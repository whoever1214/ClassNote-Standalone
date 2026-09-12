using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassNote.Services;

/// <summary>
/// 录音采集服务，支持三种声音来源：
///   · 麦克风（WASAPI 采集端点，USB 外接麦首选，MME 作为回退）
///   · 系统声音（WASAPI 回环采集扬声器输出，用于在线课程 / 设备外放）
///   · 两者混合（各采集一路，在内存中按 20ms 帧混音成一路）
/// 输出统一为 16kHz 单声道 PCM16 WAV（STT 管线要求）。
///
/// 采集侧一律使用设备的原生格式，再降混 + 重采样到 16kHz 单声道：共享模式下让
/// WASAPI 引擎转一次、自己再转一次是双份开销，且回环采集在多数驱动上并不接受任意
/// 请求格式（NAudio 把 IsFormatSupported 的结果直接当返回值用，无法借此探测），
/// 与其猜设备行为，不如统一按原生格式收、自己转换（见 <see cref="AudioPcmConverter"/>）。
/// </summary>
public class AudioService : IAudioService
{
    /// <summary>混音输出格式：STT 管线固定的 16kHz 单声道 PCM16。</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    /// <summary>单路缓冲上限：超出即丢弃最旧数据，保证长时间录音的内存上限。</summary>
    private static readonly TimeSpan ChannelBuffer = TimeSpan.FromSeconds(2);

    /// <summary>混音帧长：一帧 20ms（16kHz 单声道 = 320 个采样）。</summary>
    private const int FrameSamples = 320;

    private readonly object _stateLock = new();
    private ActiveRecorder? _active;
    private string? _outputPath;
    private string? _lastError;

    public event EventHandler<byte[]>? AudioDataAvailable;

    /// <summary>最近一次 StartRecording 失败的原因；成功时为 null。</summary>
    public string? LastError => _lastError;

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
    public bool StartRecording(string outputPath, RecordingConfig config)
    {
        StopRecording(); // 清理可能残留的旧录制
        _lastError = null;
        _outputPath = outputPath;
        config ??= RecordingConfig.Default;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            _lastError = "未指定录音输出路径";
            _outputPath = null;
            return false;
        }

        var errors = new List<string>();
        var channels = new List<CaptureChannel>();

        try
        {
            if (config.NeedsMicrophone)
            {
                if (!TryOpenMicrophoneChannel(config.MicId, channels, errors))
                    return Fail(channels, errors, config);
            }

            if (config.NeedsSystemAudio)
            {
                if (!TryOpenSystemChannel(config.OutputDeviceId, channels, errors))
                    return Fail(channels, errors, config);
            }

            if (channels.Count == 0)
            {
                errors.Add("未选择任何声音来源");
                return Fail(channels, errors, config);
            }

            // 混音结果直接落盘为 16kHz 单声道 PCM16 WAV（STT 管线要求的格式）
            var writer = new WaveFileWriter(outputPath, TargetFormat);
            var active = new ActiveRecorder(channels, writer, OnMixedFrame);

            lock (_stateLock)
                _active = active;

            // 先让混音线程跑起来再启动采集：采集回调一旦到达就有地方落数据。
            active.Start();
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
                    return false;
                }
            }

            // 采集启动后再取一次真实格式：某些设备在 Init 之后才暴露混合格式
            foreach (var ch in channels)
            {
                try { ch.RefreshNegotiatedFormat(); }
                catch { /* 取不到就用构造时格式，转换器仍能工作 */ }
            }

            return true;
        }
        catch (Exception ex)
        {
            StopRecording();
            errors.Add("启动采集失败: " + ex.Message);
            return Fail(channels, errors, config);
        }
    }

    private bool Fail(IReadOnlyList<CaptureChannel> channels, List<string> errors, RecordingConfig config)
    {
        foreach (var ch in channels)
        {
            try { ch.Dispose(); } catch { }
        }
        _lastError = BuildError(config, errors);
        _outputPath = null;
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
        ActiveRecorder? active;
        lock (_stateLock)
        {
            active = _active;
            _active = null;
        }
        active?.Stop();
    }

    public byte[] GetFileBytes() => _outputPath != null && File.Exists(_outputPath) ? File.ReadAllBytes(_outputPath) : Array.Empty<byte>();

    public string? GetOutputPath() => _outputPath;

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

    /// <summary>打开系统声音回环采集路（WASAPI 独占能力，无回退路径）。</summary>
    private static bool TryOpenSystemChannel(string? outputDeviceId, List<CaptureChannel> channels, List<string> errors)
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
                    errors.Add("指定播放设备不可用");
            }

            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
                return;
            lock (_lock)
            {
                Append(e.Buffer, e.BytesRecorded);
                Pump();
            }
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

        /// <summary>把环形缓冲里的原始字节尽量转换并推入输出缓冲（调用方持锁）。</summary>
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
                    Output.AddSamples(output, 0, output.Length);
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

    // ══════════════════════════════════════════════════════════
    //  混音：多路 16kHz 单声道 PCM16 → 一路
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 把多路 16kHz 单声道 PCM16 按帧对齐相加（各路独立缓冲，某路暂时无数据时
    /// 只输出另一路，避免某路设备的静音期把整体数据"拖住"）。
    /// </summary>
    private sealed class MixSampleProvider : ISampleProvider
    {
        private readonly IReadOnlyList<CaptureChannel> _channels;
        private readonly byte[] _scratch = new byte[64 * 1024];

        public MixSampleProvider(IReadOnlyList<CaptureChannel> channels) => _channels = channels;

        public WaveFormat WaveFormat => TargetFormat;

        /// <summary>每次调用都返回满帧（数据不足的通道按静音处理），保证录音时长与真实时钟一致。</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0)
                return 0;

            var accumulator = new int[count];
            int neededBytes = count * 2; // 各路缓冲都是 16bit 单声道

            foreach (var ch in _channels)
            {
                int remaining = neededBytes;
                int samples = 0;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, _scratch.Length);
                    chunk -= chunk % 2;
                    if (chunk <= 0)
                        break;

                    int read = ch.Output.Read(_scratch, 0, chunk);
                    if (read <= 0)
                        break;

                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        int idx = samples + i / 2;
                        if (idx >= count)
                            break;
                        accumulator[idx] += BitConverter.ToInt16(_scratch, i);
                    }
                    samples += read / 2;
                    remaining -= read;
                }
            }

            for (int i = 0; i < count; i++)
            {
                int s = Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
                buffer[offset + i] = s / 32768f;
            }
            return count;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  一次录音的生命周期
    // ══════════════════════════════════════════════════════════

    private sealed class ActiveRecorder
    {
        private readonly IReadOnlyList<CaptureChannel> _channels;
        private readonly WaveFileWriter _writer;
        private readonly ISampleProvider _mixerSource;
        private readonly Action<byte[]> _onFrame;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _mixer;

        public ActiveRecorder(IReadOnlyList<CaptureChannel> channels,
            WaveFileWriter writer, Action<byte[]> onFrame)
        {
            _channels = channels;
            _writer = writer;
            _mixerSource = new MixSampleProvider(channels);
            _onFrame = onFrame;
            _mixer = new Thread(MixLoop) { IsBackground = true, Name = "ClassNote.AudioMix" };
        }

        public void Start() => _mixer.Start();

        /// <summary>混音线程：每 20ms 拉一帧写盘（混音器在数据不足时补零，保证时长与真实时钟一致）。</summary>
        private void MixLoop()
        {
            var token = _cts.Token;
            var floatBuffer = new float[FrameSamples];
            var byteBuffer = new byte[FrameSamples * 2];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long framesWritten = 0;

            while (!token.IsCancellationRequested)
            {
                framesWritten++;
                try
                {
                    int samples = _mixerSource.Read(floatBuffer, 0, FrameSamples);
                    if (samples <= 0)
                        break;

                    Buffer.BlockCopy(floatBuffer, 0, byteBuffer, 0, samples * 2);
                    _onFrame(byteBuffer);
                    _writer.Write(byteBuffer, 0, samples * 2);
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
