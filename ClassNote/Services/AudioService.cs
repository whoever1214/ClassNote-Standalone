using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassNote.Services;

/// <summary>
/// 录音采集服务，支持三种声音来源（v0.6.0 起固定三种）：
///   · 仅麦克风（WASAPI 采集端点，USB 外接麦首选，MME 作为回退）
///   · 仅系统声音（WASAPI 回环采集所选播放设备的输出，用于在线课程 / 设备外放）
///   · 麦克风和系统声音（两路各采集一次，在内存中按 20ms 帧合成一路）
/// 输出统一为 16kHz 单声道 PCM16 WAV（STT 管线要求）。
///
/// 采集侧一律使用设备的原生格式，再降混 + 重采样到 16kHz 单声道：共享模式下让
/// WASAPI 引擎转一次、自己再转一次是双份开销，且回环采集在多数驱动上并不接受任意
/// 请求格式（NAudio 把 IsFormatSupported 的结果直接当返回值用，无法借此探测），
/// 与其猜设备行为，不如统一按原生格式收、自己转换（见 <see cref="AudioPcmConverter"/>）。
///
/// 路由代价（v0.6.0 优化）：只有一路来源时不再经过混音器——单路直接写盘，
/// 省掉一个混音线程，以及每帧一次 int16→float→int16 的来回转换。
/// （<see cref="CaptureChannel"/> 的转换结果环形缓冲仍按原样写入，只是单路时不再有人从它读。）
/// 代价是落盘发生在**采集设备线程**上（见 <see cref="DirectRecorder"/>），
/// 对磁盘的慢写会直接作用于采集节奏；判定"值得"的前提是本地 SSD 这类低延迟存储。
/// </summary>
public class AudioService : IAudioService
{
    /// <summary>输出格式：STT 管线固定的 16kHz 单声道 PCM16。</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    /// <summary>多路合成时单路缓冲上限：超出即丢弃最旧数据，保证长时间录音的内存上限。</summary>
    private static readonly TimeSpan ChannelBuffer = TimeSpan.FromSeconds(2);

    /// <summary>合成帧长：一帧 20ms（16kHz 单声道 = 320 个采样）。</summary>
    private const int FrameSamples = 320;

    /// <summary>
    /// 系统默认播放设备的角色尝试顺序。
    /// 回环采集取的是"输出端点"，而教室 / 在线课程的声音通常走多媒体默认设备；
    /// 娱乐与通信默认设备在 Windows 上可能指向另一个端点，取错就会录成静音，
    /// 因此逐个角色尝试而不是只试一个（与麦克风侧的回退思路保持一致）。
    /// </summary>
    private static readonly Role[] DefaultRenderRoles =
    {
        Role.Multimedia,
        Role.Console,
        Role.Communications,
    };

    private readonly object _stateLock = new();
    private IActiveRecorder? _active;
    private string? _outputPath;
    private string? _lastError;
    private string? _lastWarning;

    /// <summary>本次录音实际落盘的各路文件（分轨录音时是多路，否则单路）。</summary>
    private RecordingAudio _recorded = RecordingAudio.None;

    /// <summary>
    /// 转换后的 16kHz 单声道 PCM16 数据（每批是转换器产出的一块，长度不固定、不是 20ms 定长帧）。
    /// ⚠️ 在**采集设备线程**上同步触发：订阅方必须立即返回，不要做耗时操作或编组到 UI 线程，
    /// 否则会拖慢采集、造成丢帧。单路来源下本事件与写盘在同一线程上顺序发生。
    /// </summary>
    public event EventHandler<byte[]>? AudioDataAvailable;

    /// <summary>
    /// 带来源标注的 PCM16 数据（边录边转写用）。同样在采集设备线程上同步触发、
    /// 且在写盘之后触发，订阅方必须立即返回。
    /// </summary>
    public event EventHandler<AudioFrameCapturedEventArgs>? FrameCaptured;

    /// <summary>把采集回调里的一批数据派发给转写订阅方（异常吞掉：绝不能反过来影响录音）。</summary>
    private void RaiseFrameCaptured(RecordingAudioSource source, byte[] data, int count)
    {
        try { FrameCaptured?.Invoke(this, new AudioFrameCapturedEventArgs(source, data, count)); }
        catch { /* 订阅方异常不拖累录音 */ }
    }

    /// <summary>最近一次 StartRecording 失败的原因；成功时为 null。</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// 最近一次 StartRecording 成功但发生了降级的说明（例如指定播放设备失效、回退到系统默认）；
    /// 无降级时为 null。与 <see cref="LastError"/> 分开：降级不该让录音失败，但也不能静默。
    /// </summary>
    public string? LastWarning => _lastWarning;

    // ── 设备枚举 ──────────────────────────────────────────────

    /// <summary>可用的录音设备显示名，与 GetInputDeviceIds() 一一对应。</summary>
    public string[] GetInputDevices() => GetInputDevicesCore().Select(d => d.Name).ToArray();

    /// <summary>
    /// 与 GetInputDevices() 一一对应的稳定设备标识：
    /// WASAPI 设备为其稳定 ID（形如 {0.0.1.00000000}.{GUID}），
    /// MME 回退设备为合成标识 "mme:{index}"。
    /// </summary>
    public string[] GetInputDeviceIds() => GetInputDevicesCore().Select(d => d.Id).ToArray();

    /// <summary>可用播放设备显示名（系统声音回环的来源），与 GetOutputDeviceIds() 一一对应。</summary>
    public string[] GetOutputDevices() => GetOutputDevicesCore().Select(d => d.Name).ToArray();

    /// <summary>与 GetOutputDevices() 一一对应的稳定设备标识（WASAPI 渲染端点 ID）。</summary>
    public string[] GetOutputDeviceIds() => GetOutputDevicesCore().Select(d => d.Id).ToArray();

    /// <summary>设备枚举是否降级到 MME 回退路径（该路径无回环能力，系统声音不可用）。</summary>
    public bool IsUsingMmeFallback { get; private set; }

    private List<AudioDeviceInfo> GetInputDevicesCore()
    {
        var list = new List<AudioDeviceInfo>();

        // WASAPI：稳定 ID + 友好名（USB 麦克风、OPS 一体机声卡等现代设备首选）
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (dev == null) continue;
                var name = dev.FriendlyName;
                if (string.IsNullOrWhiteSpace(name)) name = "录音设备";
                list.Add(new AudioDeviceInfo(dev.ID, name));
            }
        }
        catch { /* WASAPI 不可用时回退 MME */ }

        if (list.Count > 0)
        {
            IsUsingMmeFallback = false;
            return list;
        }

        // MME 回退：设备无稳定 ID，用 "mme:{index}" 合成
        IsUsingMmeFallback = true;
        int count;
        try { count = WaveInEvent.DeviceCount; }
        catch { return list; }
        for (int i = 0; i < count; i++)
        {
            string name;
            try { name = WaveInEvent.GetCapabilities(i).ProductName; }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(name)) name = "录音设备 " + (i + 1);
            list.Add(new AudioDeviceInfo("mme:" + i, name));
        }
        return list;
    }

    private static List<AudioDeviceInfo> GetOutputDevicesCore()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (dev == null) continue;
                var name = dev.FriendlyName;
                if (string.IsNullOrWhiteSpace(name)) name = "播放设备";
                list.Add(new AudioDeviceInfo(dev.ID, name));
            }
        }
        catch { /* 无音频子系统时不阻断设置界面 */ }
        return list;
    }

    // ── 启动 / 停止 ───────────────────────────────────────────

    /// <summary>
    /// 按稳定麦克风设备标识开始录音（单路麦克风）。
    /// 设备不可用时回退到系统默认采集设备，仍失败则返回 false 并通过 LastError
    /// 说明原因（避免"选了 USB 麦克风却录到内置麦"的静默错配）。
    /// </summary>
    public bool StartRecording(string outputPath, string deviceId)
        => StartRecording(outputPath, new RecordingConfig(AudioSourceKind.Microphone, deviceId));

    /// <summary>
    /// 按录音配置开始采集。任一必需来源启动失败即整体失败（不做"少录一路但报成功"的静默降级，
    /// 用户选定的来源必须真的被录到）。
    /// </summary>
    /// <remarks>
    /// 多路来源会被**合成一路**写入 <paramref name="outputPath"/>；
    /// 需要"分辨谁在说话"时请改用 <see cref="StartRecordingDual"/>（分轨存文件），
    /// 混成一轨之后无法再拆开，转写结果也就无从标注来源。
    /// </remarks>
    public bool StartRecording(string outputPath, RecordingConfig config)
    {
        StopRecording(); // 清理可能残留的旧录制
        _lastError = null;
        _lastWarning = null;
        _outputPath = outputPath;
        _recorded = RecordingAudio.None;
        config ??= RecordingConfig.Default;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _lastError = "未指定录音输出路径";
            _outputPath = null;
            return false;
        }

        var errors = new List<string>();   // 致命原因：录音起不来，用户必须知道为什么
        var warnings = new List<string>(); // 降级原因：录音起来了，但实际行为与配置不同，同样必须告知
        var channels = new List<CaptureChannel>();

        try
        {
            if (!TryOpenChannels(config, channels, errors, warnings))
                return Fail(channels, errors, config);

            // 落盘为 16kHz 单声道 PCM16 WAV（STT 管线要求的格式）。
            // 单路来源走直写路径（不经过合成器），两路来源才需要按帧对齐合成。
            var writer = new WaveFileWriter(outputPath, TargetFormat);
            // 单路来源下"这一路是哪一路"由配置决定（回环 = 系统声音，其余 = 麦克风）：
            // 输出文件的来源标注历来固定写 Microphone，但边录边转写需要真实来源，
            // 否则"仅系统声音"的转写会被贴上"现场"标签。
            var singleSource = config is { NeedsSystemAudio: true, NeedsMicrophone: false }
                ? RecordingAudioSource.System
                : RecordingAudioSource.Microphone;
            IActiveRecorder active = channels.Count == 1
                ? new DirectRecorder(channels[0], writer, OnMixedFrame, singleSource, RaiseFrameCaptured)
                : new ActiveRecorder(channels, writer, OnMixedFrame, singleSource, RaiseFrameCaptured);

            lock (_stateLock)
                _active = active;

            // 先让合成线程跑起来再启动采集：采集回调一旦到达就有地方落数据。
            active.Start();
            if (!StartChannels(channels)) return false;

            // 采集启动后再取一次真实格式：某些设备在 Init 之后才暴露混合格式
            RefreshFormats(channels);

            _recorded = new RecordingAudio(new[]
            {
                new RecordingAudioFile(RecordingAudioSource.Microphone, outputPath),
            });
            _lastWarning = warnings.Count > 0 ? string.Join("；", warnings) : null;
            return true;
        }
        catch (Exception ex)
        {
            StopRecording();
            errors.Add("启动采集失败: " + ex.Message);
            return Fail(channels, errors, config);
        }
    }

    /// <summary>
    /// 分轨录音：每一路来源写入**各自独立的** 16kHz 单声道 WAV。
    /// 「麦克风和系统声音」必须走这条路径——混成一轨后无法再拆开，
    /// 转写结果也就无法标注"哪句是老师说的、哪句是课件里播的"。
    /// </summary>
    public RecordingAudio StartRecordingDual(RecordingConfig config, IReadOnlyList<RecordingAudioFile> paths)
    {
        StopRecording();
        _lastError = null;
        _lastWarning = null;
        _outputPath = null;
        _recorded = RecordingAudio.None;
        config ??= RecordingConfig.Default;
        paths ??= Array.Empty<RecordingAudioFile>();

        if (paths.Count == 0)
        {
            _lastError = "未指定录音输出路径";
            return RecordingAudio.None;
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        var channels = new List<CaptureChannel>();
        var writers = new List<WaveFileWriter>();

        try
        {
            if (!TryOpenChannels(config, channels, errors, warnings))
            {
                Fail(channels, errors, config);
                return RecordingAudio.None;
            }

            if (channels.Count != paths.Count)
            {
                errors.Add($"采集路数（{channels.Count}）与输出文件数（{paths.Count}）不一致");
                Fail(channels, errors, config);
                return RecordingAudio.None;
            }

            foreach (var file in paths)
                writers.Add(new WaveFileWriter(file.Path, TargetFormat));

            var active = new DualTrackRecorder(channels, writers, SourceOrderFor(config), RaiseFrameCaptured);
            lock (_stateLock)
                _active = active;

            active.Start();
            if (!StartChannels(channels))
            {
                foreach (var w in writers) { try { w.Dispose(); } catch { } }
                return RecordingAudio.None;
            }

            RefreshFormats(channels);

            _recorded = new RecordingAudio(paths.ToArray());
            _lastWarning = warnings.Count > 0 ? string.Join("；", warnings) : null;
            return _recorded;
        }
        catch (Exception ex)
        {
            StopRecording();
            foreach (var w in writers) { try { w.Dispose(); } catch { } }
            errors.Add("启动采集失败: " + ex.Message);
            Fail(channels, errors, config);
            return RecordingAudio.None;
        }
    }

    /// <summary>
    /// 按配置打开所需来源，顺序固定为**先麦克风、后系统声音**。
    /// 顺序很重要：分轨录音时"第几路"决定它写到哪个文件、带哪个来源标注。
    /// </summary>
    private bool TryOpenChannels(RecordingConfig config, List<CaptureChannel> channels,
        List<string> errors, List<string> warnings)
    {
        if (config.NeedsMicrophone)
        {
            if (!TryOpenMicrophoneChannel(config.MicId, channels, errors))
                return false;
        }

        if (config.NeedsSystemAudio)
        {
            if (!TryOpenSystemChannel(config.OutputDeviceId, channels, errors, warnings))
                return false;
        }

        if (channels.Count == 0)
        {
            errors.Add("未选择任何声音来源");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 各路采集的来源标注，顺序必须与 <see cref="TryOpenChannels"/> 的开启顺序
    /// （先麦克风、后系统声音）一致。顺序错位会让两路转写的来源标注互换，
    /// 笔记里就会把课件视频里播的内容说成"老师现场讲的"。
    /// </summary>
    private static List<RecordingAudioSource> SourceOrderFor(RecordingConfig config)
    {
        var sources = new List<RecordingAudioSource>();
        if (config.NeedsMicrophone)
            sources.Add(RecordingAudioSource.Microphone);
        if (config.NeedsSystemAudio)
            sources.Add(RecordingAudioSource.System);
        return sources;
    }

    /// <summary>启动各采集路；任一路失败即整体失败（不静默少录一路）。</summary>
    private bool StartChannels(IReadOnlyList<CaptureChannel> channels)    {
        foreach (var ch in channels)
        {
            try
            {
                ch.Start();
            }
            catch (Exception ex)
            {
                StopRecording();
                _lastError = "采集启动失败：" + ch.Label + "：" + ex.Message;
                _recorded = RecordingAudio.None;
                return false;
            }
        }
        return true;
    }

    /// <summary>采集启动后重读设备实际格式：某些设备在 Init 之后才暴露混合格式。</summary>
    private static void RefreshFormats(IReadOnlyList<CaptureChannel> channels)
    {
        foreach (var ch in channels)
        {
            try { ch.RefreshNegotiatedFormat(); }
            catch { /* 取不到就用构造时格式，转换器仍能工作 */ }
        }
    }

    private bool Fail(IReadOnlyList<CaptureChannel> channels, List<string> errors, RecordingConfig config)
    {
        foreach (var ch in channels)
        {
            try { ch.Dispose(); } catch { }
        }
        _lastError = BuildError(config, errors);
        _lastWarning = null; // 失败时只报失败原因，别让上一轮的降级说明残留在界面上
        _outputPath = null;
        _recorded = RecordingAudio.None;
        return false;
    }

    /// <summary>把各来源的失败原因拼成一句面向用户的话（含来源名称，便于定位是哪一路）。</summary>
    private static string BuildError(RecordingConfig config, IEnumerable<string> errors)
    {
        var detail = string.Join("；", errors.Where(e => !string.IsNullOrWhiteSpace(e)));
        var prefix = config.Source switch
        {
            AudioSourceKind.System => "系统声音采集失败",
            AudioSourceKind.Both => "混合录音启动失败",
            _ => "麦克风采集失败",
        };
        return string.IsNullOrEmpty(detail) ? prefix : prefix + "：" + detail;
    }

    public void StopRecording()
    {
        IActiveRecorder? active;
        lock (_stateLock)
        {
            active = _active;
            _active = null;
        }
        active?.Stop();
    }

    public byte[] GetFileBytes() => _outputPath != null && File.Exists(_outputPath) ? File.ReadAllBytes(_outputPath) : Array.Empty<byte>();

    public string? GetOutputPath() => _outputPath;

    /// <summary>
    /// 本次录音落盘的各路文件。停止录音后仍然可读（文件名在 Start 时就已确定），
    /// 供录音落地与"分轨转写"使用。
    /// </summary>
    public RecordingAudio GetRecordedAudio() => _recorded;

    public void Dispose() => StopRecording();

    private void OnMixedFrame(byte[] buffer) => AudioDataAvailable?.Invoke(this, buffer);

    // ── 来源装配 ─────────────────────────────────────────────

    /// <summary>打开麦克风采集路：WASAPI 优先，MME 回退。</summary>
    private bool TryOpenMicrophoneChannel(string? micId, List<CaptureChannel> channels, List<string> errors)
    {
        bool mmeOnly = !string.IsNullOrWhiteSpace(micId)
                       && micId.StartsWith("mme:", StringComparison.Ordinal);

        if (!mmeOnly)
        {
            try
            {
                var (channel, fallbackReason) = OpenWasapiMicrophone(micId);
                if (channel != null)
                {
                    channels.Add(channel);
                    if (fallbackReason != null)
                        errors.Add(fallbackReason); // 记录但不阻断（已回退默认设备）
                    return true;
                }
            }
            catch (Exception ex)
            {
                errors.Add("WASAPI 麦克风启动失败: " + ex.Message);
            }
        }

        // 明确指定了 MME 设备：按索引开；否则（WASAPI 路径失败）退到 MME 的默认采集设备
        try
        {
            var mme = OpenMmeMicrophone(mmeOnly ? micId : null);
            if (mme != null)
            {
                channels.Add(mme);
                if (!mmeOnly)
                    errors.Add("WASAPI 麦克风不可用，已回退 MME 默认采集设备");
                return true;
            }
            errors.Add("MME 回退失败: 无法启动所选麦克风");
        }
        catch (Exception ex)
        {
            errors.Add("MME 回退失败: " + ex.Message);
        }
        return false;
    }

    /// <summary>
    /// WASAPI 麦克风：优先按稳定 ID 打开，失败则回退系统默认采集设备。
    /// 第二个返回值是"已回退"的说明（非空时调用方只记录不报错）。
    /// </summary>
    private static (CaptureChannel? Channel, string? FallbackReason) OpenWasapiMicrophone(string? micId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;

        if (!string.IsNullOrWhiteSpace(micId))
        {
            try { device = enumerator.GetDevice(micId); }
            catch { device = null; }
        }

        string? fallbackReason = null;
        if (device == null)
        {
            if (!string.IsNullOrWhiteSpace(micId))
                fallbackReason = "指定麦克风不可用，已回退系统默认麦克风";

            try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
            catch
            {
                try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
                catch { device = null; }
            }
            if (device == null)
                return (null, fallbackReason);
        }

        var capture = new WasapiCapture(device, useEventSync: false);
        return (CaptureChannel.Create("麦克风", capture), fallbackReason);
    }

    private static CaptureChannel? OpenMmeMicrophone(string? deviceId)
    {
        int index;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            index = 0; // 未指定设备 = 系统默认采集设备
        }
        else if (deviceId.StartsWith("mme:", StringComparison.Ordinal))
        {
            // MME 合成标识：取 "mme:{index}" 里的索引，解析不出就放弃（避免静默录错设备）
            if (!int.TryParse(deviceId.AsSpan(4), out index))
                return null;
        }
        else if (int.TryParse(deviceId, out var parsed))
        {
            index = parsed;
        }
        else
        {
            return null; // WASAPI 稳定 ID 无法映射到 MME 设备，交给默认设备兜底
        }

        int count;
        try { count = WaveInEvent.DeviceCount; }
        catch { return null; }
        if (index < 0 || index >= count)
            return null;

        var recorder = new WaveInEvent
        {
            DeviceNumber = index,
            WaveFormat = new WaveFormat(16000, 16, 1),
        };
        return CaptureChannel.Create("麦克风", recorder);
    }

    /// <summary>
    /// 打开系统声音回环采集路（WASAPI 独占能力，无回退路径）。
    /// 设备选择与麦克风侧口径一致：显式指定优先；**失效则回退到系统默认设备，并把这件事
    /// 记进 <paramref name="warnings"/>**（由 StartRecording 汇总为 LastWarning 交给界面）
    /// ——避免"录到别的设备却以为录到了"。未指定时按 <see cref="DefaultRenderRoles"/> 顺序取默认设备。
    /// </summary>
    private static bool TryOpenSystemChannel(string? outputDeviceId, List<CaptureChannel> channels,
        List<string> errors, List<string> warnings)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;

            if (!string.IsNullOrWhiteSpace(outputDeviceId))
            {
                try { device = enumerator.GetDevice(outputDeviceId); }
                catch { device = null; }
                if (device == null)
                {
                    // 不是致命错误（录音仍能起来），但必须让用户知道录的不是他选的设备
                    warnings.Add("指定播放设备不可用，已回退系统默认播放设备" +
                                 "（可能录到其它设备的声音，请检查「设置 → 录音设置」中的播放设备）");
                }
            }

            device ??= FindDefaultRenderDevice(enumerator);
            if (device == null)
            {
                errors.Add("未找到可用播放设备（无法采集系统声音）");
                return false;
            }

            var loopback = new WasapiLoopbackCapture(device);
            channels.Add(CaptureChannel.Create("系统声音", loopback));
            return true;
        }
        catch (Exception ex)
        {
            errors.Add("回环采集不可用: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 取系统默认播放设备：按角色顺序尝试，任一角色可用即返回。
    /// 只取一个角色就放弃是回环采集最常见的"录成静音"根因（默认设备在通信/娱乐角色上可能指向不同端点）。
    /// </summary>
    private static MMDevice? FindDefaultRenderDevice(MMDeviceEnumerator enumerator)
    {
        foreach (var role in DefaultRenderRoles)
        {
            try
            {
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                if (device != null)
                    return device;
            }
            catch { /* 该角色无默认端点，试下一个 */ }
        }
        return null;
    }

    private sealed record AudioDeviceInfo(string Id, string Name);

    // ══════════════════════════════════════════════════════════
    //  采集路：把任意 IWaveIn 的原生数据转换成 16kHz 单声道 PCM16
    // ══════════════════════════════════════════════════════════

    private sealed class CaptureChannel : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly object _lock = new();
        private readonly byte[] _readScratch = new byte[48 * 1024];
        private readonly WaveFormat _sourceFormat;
        private readonly AudioPcmConverter _converter;

        /// <summary>未转换的原始字节环形缓冲（不足一帧的残字节留待下次补齐）。</summary>
        private byte[] _incoming = new byte[64 * 1024];
        private int _head;
        private int _tail;

        /// <summary>转换后的 16kHz 单声道数据缓冲（混音线程消费）。</summary>
        public BufferedWaveProvider Output { get; }

        /// <summary>来源名称（错误提示里区分是哪一路）。</summary>
        public string Label { get; }

        /// <summary>
        /// 转换完成的一批 16kHz 单声道 PCM16 数据。
        /// 单路来源直接用它落盘，不经过输出缓冲与合成线程。
        /// </summary>
        public event Action<byte[]>? SamplesProduced;

        private CaptureChannel(string label, IWaveIn capture)
        {
            Label = label;
            _capture = capture;

            // 采集侧一律用设备原生格式：回环采集在多数驱动上会忽略请求格式并静默回退。
            _sourceFormat = capture.WaveFormat ?? new WaveFormat(48000, 16, 2);
            _converter = new AudioPcmConverter(_sourceFormat, TargetFormat);

            Output = new BufferedWaveProvider(TargetFormat)
            {
                BufferDuration = ChannelBuffer,
                DiscardOnBufferOverflow = true, // 长时间录音的内存上限
                ReadFully = false,              // 数据不足时返回实际字节数，由混音器补零
            };

            _capture.DataAvailable += OnDataAvailable;
        }

        public static CaptureChannel Create(string label, IWaveIn capture) => new(label, capture);

        public void Start() => _capture.StartRecording();

        /// <summary>
        /// 采集启动后重新读取设备实际格式（部分设备在 Init 之后才暴露混合格式）。
        /// 与预期格式不同时重建转换器，避免用错采样率插值。
        /// </summary>
        public void RefreshNegotiatedFormat()
        {
            var actual = _capture.WaveFormat;
            if (actual == null || actual.Equals(_sourceFormat))
                return;
            lock (_lock)
            {
                _converter.Rebind(actual);
                _head = _tail = 0; // 格式变了，残留的旧格式字节不再可用
            }
        }

        /// <summary>本次回调里已转换、等待在锁外派发给订阅者的数据块（见 <see cref="OnDataAvailable"/>）。</summary>
        private readonly List<byte[]> _pumped = new();

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
                return;
            lock (_lock)
            {
                Append(e.Buffer, e.BytesRecorded);
                Pump();
            }

            // 订阅者（单路来源下就是写盘）必须在锁外调用：磁盘 I/O 落在锁内会把其它持锁方
            // 一起拖住，而"其它持锁方"正是下一个采集回调本身，慢盘下直接表现为丢帧。
            // 订阅方异常自行负责——在这里抛出会丢掉这批已转换的数据。
            for (int i = 0; i < _pumped.Count; i++)
            {
                try { SamplesProduced?.Invoke(_pumped[i]); } catch { }
            }
            _pumped.Clear();
        }

        /// <summary>把回调数据追加到环形缓冲（空间不足时先搬移/扩容）。</summary>
        private void Append(byte[] buffer, int count)
        {
            if (_tail + count > _incoming.Length)
            {
                int used = _tail - _head;
                if (used + count > _incoming.Length)
                    Array.Resize(ref _incoming, Math.Max(used + count, _incoming.Length * 2));
                if (used > 0)
                    Array.Copy(_incoming, _head, _incoming, 0, used);
                _head = 0;
                _tail = used;
            }
            Array.Copy(buffer, 0, _incoming, _tail, count);
            _tail += count;
        }

        /// <summary>
        /// 把环形缓冲里的原始字节尽量转换并推入输出缓冲（调用方持锁）。
        /// 转换结果追加到 <see cref="_pumped"/>，由 <see cref="OnDataAvailable"/> 在锁外派发。
        /// </summary>
        private void Pump()
        {
            int blockAlign = Math.Max(1, _sourceFormat.BlockAlign);
            // 交给转换器的每块都是整帧，残帧留在缓冲里等下次补齐
            int chunkLimit = Math.Max(blockAlign, _readScratch.Length - _readScratch.Length % blockAlign);

            while (_tail - _head >= blockAlign)
            {
                int want = Math.Min(_tail - _head, chunkLimit);
                want -= want % blockAlign;
                if (want <= 0)
                    break;

                Array.Copy(_incoming, _head, _readScratch, 0, want);
                _head += want;

                var output = _converter.Process(_readScratch, want);
                if (output.Length > 0)
                {
                    Output.AddSamples(output, 0, output.Length);
                    _pumped.Add(output); // 派发见 OnDataAvailable：必须锁外，订阅者可能在做磁盘 I/O
                }
            }

            if (_head == _tail)
                _head = _tail = 0; // 空闲时归零，避免缓冲区无限增长
        }

        public void Stop()
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { /* 停止失败不影响清理 */ }
        }

        public void Dispose()
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.Dispose(); } catch { /* 后台清理失败不致命 */ }
        }
    }

    // 混音实现见 Services/Pcm16Mixer.cs：在整数域按样本相加 + 饱和截断，直接产出 PCM16 字节。
    // 旧实现实现 ISampleProvider（[-1,1] 浮点契约），再由混音线程把浮点缓冲当 PCM16 写盘——
    // 写下去的是 IEEE-754 位模式而不是音频（v0.5.0 起"麦克风和系统声音"录出来是噪声的根因）。

    // ══════════════════════════════════════════════════════════
    //  一次录音的生命周期
    // ══════════════════════════════════════════════════════════

    /// <summary>一次录音的生命周期（单路直写 / 多路合成两种实现）。</summary>
    private interface IActiveRecorder
    {
        /// <summary>开始产出数据（单路直写无需预热，为空实现）。</summary>
        void Start();

        /// <summary>停止采集、收尾写盘并释放设备。</summary>
        void Stop();
    }

    /// <summary>
    /// 单路来源的直写路径：转换后的数据在采集回调里直接落盘，不经过合成器。
    /// 只有一路时合成器没有可"对齐"的对象，却要额外付出一个后台线程和每帧一次
    /// int16 → float → int16 的来回转换——直接写盘省掉这些，同时让写入节奏严格跟随设备时钟。
    ///
    /// 代价（已知并接受）：落盘发生在**采集设备线程**上。一次慢写（磁盘忙、实时扫描、
    /// 网络盘、休眠前刷盘）会直接拖慢采集节奏，可能造成采集缓冲溢出而丢帧；旧的多路合成
    /// 路径把写盘放在独立线程上，没有这个暴露面。改动本路径前请先确认前提仍然成立：
    /// 输出目录位于本地低延迟存储（SSD）。订阅方（<see cref="AudioService.AudioDataAvailable"/>）
    /// 也因此在采集线程上被同步调用，必须立即返回。
    /// </summary>
    private sealed class DirectRecorder : IActiveRecorder
    {
        private readonly CaptureChannel _channel;
        private readonly WaveFileWriter _writer;
        private readonly Action<byte[]> _onFrame;
        private readonly RecordingAudioSource _source;
        private readonly Action<RecordingAudioSource, byte[], int>? _onSamples;
        private int _stopRequested;

        public DirectRecorder(CaptureChannel channel, WaveFileWriter writer, Action<byte[]> onFrame,
            RecordingAudioSource source, Action<RecordingAudioSource, byte[], int>? onSamples = null)
        {
            _channel = channel;
            _writer = writer;
            _onFrame = onFrame;
            _source = source;
            _onSamples = onSamples;
            _channel.SamplesProduced += OnSamplesProduced;
        }

        /// <summary>无需预热：写盘路径在调用采集 Start 之前就已就绪。</summary>
        public void Start() { }

        /// <summary>在采集回调线程上直接写盘（16kHz 单声道 PCM16，与输出格式一致）。</summary>
        private void OnSamplesProduced(byte[] data)
        {
            if (Volatile.Read(ref _stopRequested) != 0 || data.Length == 0)
                return;
            try
            {
                _onFrame(data);
                _writer.Write(data, 0, data.Length);
                // 转写订阅在**写盘之后**触发：它拿到的字节与文件内容逐字节一致；
                // 写盘抛异常时不会触发（那些字节也没进文件，两边口径一致）
                _onSamples?.Invoke(_source, data, data.Length);
            }
            catch
            {
                // 写盘失败（磁盘满等）：由 Stop 统一收尾，不在回调线程抛出
            }
        }

        public void Stop()
        {
            // 1. 停止采集并摘掉订阅。
            //    注意：设备在停止过程中冲刷出来的最后一批数据**不会**流到这里——
            //    CaptureChannel.Stop() 是先摘 DataAvailable 回调、再停止设备的，
            //    因此本标志位与摘订阅的先后顺序并不决定"尾部数据丢不丢"（丢的量级 =
            //    设备缓冲，通常几十毫秒）。这里的顺序只保证一件事：writer 关闭之后不会有新写入。
            Volatile.Write(ref _stopRequested, 1);
            try { _channel.Stop(); } catch { }
            _channel.SamplesProduced -= OnSamplesProduced;

            // 2. 关闭 writer（补写 WAV 头）
            try { _writer.Dispose(); } catch { }

            // 3. 释放采集设备放到后台线程：WasapiCapture.Dispose() 会 Join 采集线程，
            //    异常音频端点（虚拟声卡等）上可能长时间阻塞，不能占用 UI 线程。
            _ = Task.Run(() =>
            {
                try { _channel.Dispose(); } catch { }
            });
        }
    }

    /// <summary>
    /// 分轨录制：第 i 路采集写入第 i 个文件，**不做任何合成**。
    /// 「麦克风和系统声音」走这条路径，使转写结果可以按来源分别标注；
    /// 各路的转换结果长度逐块独立，因此不需要合成器的帧对齐，也就没有合成线程与 20ms 节流
    /// （代价与单路直写相同：落盘发生在采集设备线程上）。
    /// </summary>
    private sealed class DualTrackRecorder : IActiveRecorder
    {
        private readonly IReadOnlyList<CaptureChannel> _channels;
        private readonly IReadOnlyList<WaveFileWriter> _writers;
        private readonly List<Action<byte[]>> _handlers = new();
        private int _stopRequested;

        public DualTrackRecorder(IReadOnlyList<CaptureChannel> channels, IReadOnlyList<WaveFileWriter> writers,
            IReadOnlyList<RecordingAudioSource>? sources = null,
            Action<RecordingAudioSource, byte[], int>? onSamples = null)
        {
            _channels = channels;
            _writers = writers;

            for (int i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                var writer = writers[i];
                // 第 i 路的来源：由调用方按开启顺序给出；缺失时按"先麦克风后系统"的约定兜底
                var source = sources != null && i < sources.Count
                    ? sources[i]
                    : (i == 0 ? RecordingAudioSource.Microphone : RecordingAudioSource.System);

                void Handler(byte[] data)
                {
                    if (Volatile.Read(ref _stopRequested) != 0 || data.Length == 0)
                        return;
                    try
                    {
                        writer.Write(data, 0, data.Length);
                        onSamples?.Invoke(source, data, data.Length); // 写盘之后：与文件内容逐字节一致
                    }
                    catch { /* 单路写盘失败不拖累另一路，由 Stop 统一收尾 */ }
                }
                _handlers.Add(Handler);
                channel.SamplesProduced += Handler;
            }
        }

        /// <summary>无需预热：两路写盘路径在调用采集 Start 之前就已就绪。</summary>
        public void Start() { }

        public void Stop()
        {
            // 1. 停采集。先置标志（回调可能仍在跑），**停完再摘回调**，
            //    以免丢掉设备在停止过程中冲刷出来的最后一批数据。
            Volatile.Write(ref _stopRequested, 1);
            foreach (var ch in _channels)
            {
                try { ch.Stop(); } catch { }
            }
            for (int i = 0; i < _channels.Count && i < _handlers.Count; i++)
                _channels[i].SamplesProduced -= _handlers[i];

            // 2. 关闭各 writer（补写 WAV 头）。两路都必须在返回前关完：
            //    调用方紧接着就要读这些文件做落地与转写。
            foreach (var w in _writers)
            {
                try { w.Dispose(); } catch { }
            }

            // 3. 释放采集设备放到后台线程（同上：Dispose 可能阻塞，不能占用 UI 线程）
            var channels = _channels;
            _ = Task.Run(() =>
            {
                foreach (var ch in channels)
                {
                    try { ch.Dispose(); } catch { }
                }
            });
        }
    }

    private sealed class ActiveRecorder : IActiveRecorder
    {
        private readonly IReadOnlyList<CaptureChannel> _channels;
        private readonly WaveFileWriter _writer;
        private readonly Pcm16Mixer _mixerSource;
        private readonly Action<byte[]> _onFrame;
        private readonly RecordingAudioSource _source;
        private readonly Action<RecordingAudioSource, byte[], int>? _onSamples;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _mixer;

        public ActiveRecorder(IReadOnlyList<CaptureChannel> channels,
            WaveFileWriter writer, Action<byte[]> onFrame,
            RecordingAudioSource source = RecordingAudioSource.Microphone,
            Action<RecordingAudioSource, byte[], int>? onSamples = null)
        {
            _channels = channels;
            _writer = writer;
            // 混音器只依赖各路的输出缓冲（不需要设备对象），因此能在单测里喂已知 PCM16 验证结果
            _mixerSource = new Pcm16Mixer(channels.Select(ch => ch.Output).ToList());
            _onFrame = onFrame;
            _source = source;
            _onSamples = onSamples;
            _mixer = new Thread(MixLoop) { IsBackground = true, Name = "ClassNote.AudioMix" };
        }

        public void Start() => _mixer.Start();

        /// <summary>混音线程：每 20ms 拉一帧写盘（混音器在数据不足时补零，保证时长与真实时钟一致）。</summary>
        private void MixLoop()
        {
            var token = _cts.Token;
            var byteBuffer = new byte[FrameSamples * 2];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long framesWritten = 0;

            while (!token.IsCancellationRequested)
            {
                framesWritten++;
                try
                {
                    // 直接拿 PCM16 字节写盘：中间不再有 float 表示，也就没有"位模式被当音频"的机会
                    int bytes = _mixerSource.Read(byteBuffer, 0, FrameSamples);
                    if (bytes <= 0)
                        break;

                    _onFrame(byteBuffer);
                    _writer.Write(byteBuffer, 0, bytes);
                    _onSamples?.Invoke(_source, byteBuffer, bytes); // 写盘之后触发，口径与文件一致
                }
                catch
                {
                    break; // 写盘失败（磁盘满等）时停止混音，由停止流程统一收尾
                }

                // 按真实时间节流：忙等会白烧 CPU，慢一帧也只会多补一点静音
                double targetMs = framesWritten * 1000.0 * FrameSamples / TargetFormat.SampleRate;
                int sleep = (int)Math.Round(targetMs - sw.Elapsed.TotalMilliseconds);
                if (sleep > 0)
                {
                    try { Task.Delay(sleep, token).Wait(token); }
                    catch (OperationCanceledException) { break; }
                    catch (AggregateException) { break; }
                }
            }
        }

        public void Stop()
        {
            // 1. 停止采集：先摘掉回调，避免停止过程中还有新数据进来
            foreach (var ch in _channels)
            {
                try { ch.Stop(); } catch { }
            }

            // 2. 停混音线程并等待其退出：WaveFileWriter.Dispose 会补写 WAV 头，
            //    必须确保没有线程还在往里写。
            try { _cts.Cancel(); } catch { }
            try { _mixer.Join(3000); } catch { }

            // 3. 关闭 writer（补写 WAV 头）
            try { _writer.Dispose(); } catch { }

            // 4. 释放采集设备放到后台线程：WasapiCapture.Dispose() 会 Join 采集线程，
            //    异常音频端点（虚拟声卡等）上可能长时间阻塞，不能占用 UI 线程。
            var channels = _channels;
            _ = Task.Run(() =>
            {
                foreach (var ch in channels)
                {
                    try { ch.Dispose(); } catch { }
                }
            });
        }
    }
}
