using ClassNote.Services;
using System.IO;
using System.Windows.Threading;
using System.Windows.Input;

namespace ClassNote.ViewModels;
public class RecordingViewModel : BaseViewModel
{
    private readonly IAudioService _audio;
    private readonly IScreenshotService _screenshot;
    private readonly IUploadService _upload;
    private readonly IApiService _api;
    private readonly INoteProcessor _processor;
    private readonly Guid _sessionId;

    private string _statusText = "准备中";
    private int _screenshotCount;
    private string? _lastWarning;
    private double _elapsedSeconds;
    private string _selectedMic = "";
    private string _selectedMicId = "";
    private string[] _availableMics = Array.Empty<string>();
    private string[] _availableMicIds = Array.Empty<string>();
    private string[] _availableOutputs = Array.Empty<string>();
    private string[] _availableOutputIds = Array.Empty<string>();
    private DispatcherTimer? _elapsedTimer;
    private System.Diagnostics.Stopwatch? _elapsedClock;
    private Task? _backgroundProcessing;
    private IncrementalTranscriptionSession? _incremental;
    private ClassroomResourceGovernor? _governor;
    private readonly BackgroundOcrQueue? _ocrQueue;

    /// <summary>本次录音的采集配置（来源 + 设备）。</summary>
    private RecordingConfig _config = RecordingConfig.Default;

    public RecordingViewModel(Guid sessionId, IApiService api, IAudioService audio,
        IScreenshotService screenshot, IUploadService upload,
        RecordingConfig? config = null, INoteProcessor? processor = null,
        BackgroundOcrQueue? ocrQueue = null)
    {
        _sessionId = sessionId;
        _api = api;
        _audio = audio;
        _screenshot = screenshot;
        _upload = upload;

        if (processor == null)
        {
            // 单机模式的默认装配（v0.7）：
            // · STT 用进程级共享实例 —— 模型 241MB / 加载 6–33s，绝不能每个会话重来一次；
            // · 转写装配器负责"复用课堂期间已算好的块、只补缺的块"；
            // · OCR 队列让截图在录音期间就识别完，处理管线只读现成结果。
            var stt = SenseVoiceSttService.Shared;
            // OCR 引擎由设置在「内置 Windows OCR / 内网 PaddleOCR 服务」之间切换；
            // 队列与处理管线**共用同一个实例**，否则主备切换的冷却与计数会在两处各算一遍。
            var ocr = OcrServiceFactory.Create(AppSettings.Instance.Snapshot());
            _ocrQueue = ocrQueue ?? new BackgroundOcrQueue(ocr,
                (sid, seqNo, text) => LocalRepository.Instance.SetScreenshotOcr(sid, seqNo, text));
            _processor = new NoteProcessor(stt, ocr, new LlmService(),
                new TranscriptAssembler(LocalRepository.Instance, stt), _ocrQueue);
        }
        else
        {
            _processor = processor;
            _ocrQueue = ocrQueue;
        }

        _screenshot.ScreenshotCaptured += OnScreenshotCaptured;
        AvailableMics = _audio.GetInputDevices();
        AvailableMicIds = _audio.GetInputDeviceIds();
        // 播放设备也要枚举：来源包含系统声音时，用户需要确认"到底在录哪个设备"
        // （选错播放设备是回环录成静音的典型成因）。Mock/异常实现返回 null 时退化为空数组。
        AvailableOutputs = _audio.GetOutputDevices() ?? Array.Empty<string>();
        AvailableOutputIds = _audio.GetOutputDeviceIds() ?? Array.Empty<string>();

        var desired = config ?? RecordingConfig.Default;
        _config = desired;
        SourceDisplayName = AudioSourceKinds.ToDisplayName(desired.Source);

        // 优先按稳定设备 ID 匹配（消除名称/枚举顺序不一致导致的"选 USB 麦克风却录到内置麦"）
        if (!string.IsNullOrEmpty(desired.MicId))
        {
            int idx = Array.IndexOf(AvailableMicIds, desired.MicId);
            if (idx >= 0)
            {
                SelectedMicId = desired.MicId;
                SelectedMic = AvailableMics[idx];
            }
        }
        // ID 缺失或已失效（设备被拔出/重装驱动）时，退回按显示名匹配
        if (SelectedMicId.Length == 0 && !string.IsNullOrEmpty(desired.MicName))
        {
            int idx = Array.IndexOf(AvailableMics, desired.MicName);
            if (idx >= 0 && idx < AvailableMicIds.Length)
            {
                SelectedMicId = AvailableMicIds[idx];
                SelectedMic = desired.MicName;
            }
        }
        if (SelectedMicId.Length == 0 && AvailableMics.Length > 0)
        {
            SelectedMicId = AvailableMicIds.Length > 0 ? AvailableMicIds[0] : "";
            SelectedMic = AvailableMics[0];
        }

        // 设备已拔出/被改名时更新配置，避免采集服务再去解析一个失效 ID
        _config = _config with
        {
            MicId = string.IsNullOrEmpty(SelectedMicId) ? null : SelectedMicId,
        };
    }

    /// <summary>本次录音的声音来源（界面展示用）。</summary>
    public string SourceDisplayName { get; }

    /// <summary>本次录音是否使用麦克风（决定录音页是否显示麦克风选择）。</summary>
    public bool UsesMicrophone => _config.NeedsMicrophone;

    /// <summary>本次录音是否采集系统声音（决定录音页是否显示"在录哪个播放设备"的说明）。</summary>
    public bool UsesSystemAudio => _config.NeedsSystemAudio;

    /// <summary>
    /// 本次录音使用的播放设备显示名（系统声音来源）；未指定 = 系统默认设备。
    /// 让用户在录音过程中确认"到底在录哪个设备"——选错播放设备是回环录成静音的典型成因。
    /// </summary>
    public string OutputDeviceDisplayName
    {
        get
        {
            var id = _config.OutputDeviceId;
            if (string.IsNullOrEmpty(id))
                return "系统默认播放设备";
            int idx = Array.IndexOf(AvailableOutputIds, id);
            return idx >= 0 && idx < AvailableOutputs.Length
                ? AvailableOutputs[idx]
                : "已失效的播放设备（实际会回退到系统默认）";
        }
    }

    /// <summary>
    /// 录制对象说明：**只要本次采集包含系统声音就显示**（含"麦克风和系统声音"），
    /// 既避免用户以为"没在录"，也告诉他声音取的是哪个播放设备。
    /// ⚠️ 可见性条件必须与 RecordingPage.xaml 一致（绑 UsesSystemAudio）——曾经绑的是
    /// "UsesMicrophone 的反值"，导致下面"两路"这句永远显示不出来。
    /// </summary>
    public string SystemSourceNotice => UsesMicrophone
        ? $"正在录制麦克风与系统声音：两路分开录制、分别转写，笔记里会标明「现场」与「课件」；" +
          $"系统声音来自「{OutputDeviceDisplayName}」，请让课件 / 视频在该设备上出声。"
        : $"正在录制系统声音：来源设备「{OutputDeviceDisplayName}」，请让课件 / 视频在该设备上出声。";

    /// <summary>启动录音时发生的降级说明（如所选播放设备失效被回退）；无降级时为空串。</summary>
    public string RecordingWarning => _lastWarning ?? "";

    /// <summary>是否有降级说明需要显示。</summary>
    public bool HasRecordingWarning => !string.IsNullOrEmpty(_lastWarning);

    /// <summary>录音页那段提示区域是否需要显示：来源含系统声音，或本次启动有降级要告知。</summary>
    public bool HasAudioNotice => UsesSystemAudio || HasRecordingWarning;

    /// <summary>
    /// 记录启动录音时的降级说明。刻意不写进 <see cref="StatusText"/>：那是录音页判断
    /// "是否在录音"的控制量（== "录音中"），改动它会让页面对录音状态产生误判。
    /// </summary>
    private void SetRecordingWarning(string? warning)
    {
        _lastWarning = string.IsNullOrWhiteSpace(warning) ? null : warning;
        OnPropertyChanged(nameof(RecordingWarning));
        OnPropertyChanged(nameof(HasRecordingWarning));
        OnPropertyChanged(nameof(HasAudioNotice));
    }

    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public int ScreenshotCount { get => _screenshotCount; set { _screenshotCount = value; OnPropertyChanged(); } }

    private string _transcriptionStatus = "";
    private bool _isYielding;

    /// <summary>
    /// 课堂转写进度文案（"课堂转写：现场 12:30 / 课件 11:50"）。
    /// 刻意与 <see cref="StatusText"/> 分开：后者是录音页判断"是否在录音"的控制量（== "录音中"），
    /// 往里塞进度文案会让页面对录音状态产生误判。
    /// </summary>
    public string TranscriptionStatus
    {
        get => _transcriptionStatus;
        private set { _transcriptionStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTranscriptionStatus)); }
    }

    public bool HasTranscriptionStatus => _transcriptionStatus.Length > 0;

    /// <summary>是否因为"前台在放 PPT / 视频"而主动让路（录音页据此提示用户"转写在给放映让路"）。</summary>
    public bool IsYielding
    {
        get => _isYielding;
        private set { _isYielding = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 后台处理管线任务（录音停止后启动：STT → OCR → LLM）。
    /// 页面在 quick-stop 后即可返回主页，此任务在后台独立完成。
    /// </summary>
    public Task? BackgroundProcessingTask => _backgroundProcessing;
    public double ElapsedSeconds { get => _elapsedSeconds; set { _elapsedSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedDisplay)); } }
    public string ElapsedDisplay => TimeSpan.FromSeconds(ElapsedSeconds).ToString(@"hh\:mm\:ss");
    public string SelectedMic
    {
        get => _selectedMic;
        set
        {
            _selectedMic = value;
            OnPropertyChanged();
            // 下拉切换时同步设备 ID（与 AvailableMicIds 一一对应）
            int idx = Array.IndexOf(AvailableMics, value);
            if (idx >= 0 && idx < AvailableMicIds.Length)
                SelectedMicId = AvailableMicIds[idx];
        }
    }
    public string SelectedMicId
    {
        get => _selectedMicId;
        set
        {
            _selectedMicId = value;
            OnPropertyChanged();
            // 下拉切换后同步采集配置，避免界面显示与真正录制的设备不一致
            if (_config.NeedsMicrophone)
                _config = _config with { MicId = string.IsNullOrEmpty(value) ? null : value };
        }
    }
    public string[] AvailableMics { get => _availableMics; set { _availableMics = value; OnPropertyChanged(); } }
    public string[] AvailableMicIds { get => _availableMicIds; set { _availableMicIds = value; OnPropertyChanged(); } }
    public string[] AvailableOutputs { get => _availableOutputs; set { _availableOutputs = value; OnPropertyChanged(); } }
    public string[] AvailableOutputIds { get => _availableOutputIds; set { _availableOutputIds = value; OnPropertyChanged(); } }

    public Task StartRecordingAsync()
    {
        // 只在需要麦克风时校验麦克风是否存在：系统声音来源不依赖采集端点
        if (_config.NeedsMicrophone && string.IsNullOrEmpty(SelectedMicId))
        {
            StatusText = "录音启动失败：未检测到可用麦克风";
            return Task.CompletedTask;
        }

        // 边录边转写：必须在采集启动**之前**挂上（否则开头几百毫秒的音频无人接收，
        // 之后所有块的时间轴与内容都会整体错位）。
        StartIncrementalTranscription();

        // 分轨录制：麦克风与系统声音**各写一个文件**（不是合成一路），
        // 这样两路转写结果能分别标注来源，模型才分得清"现场讲的"与"课件里播的"。
        // 单路来源仍走原路径（一个文件）。
        var tempDir = Path.GetTempPath();
        bool dual = _config.NeedsMicrophone && _config.NeedsSystemAudio;

        if (dual)
        {
            var files = new List<RecordingAudioFile>
            {
                // 顺序即"第几路"，必须与 AudioService.TryOpenChannels 的开启顺序一致（先麦克风后系统声音）
                new(RecordingAudioSource.Microphone, Path.Combine(tempDir, $"classnote_{_sessionId}_mic.wav")),
                new(RecordingAudioSource.System, Path.Combine(tempDir, $"classnote_{_sessionId}_system.wav")),
            };
            var recorded = _audio.StartRecordingDual(_config, files);
            if (recorded.IsEmpty)
            {
                AbortIncrementalTranscription();
                StatusText = _audio.LastError != null
                    ? "录音启动失败：" + _audio.LastError
                    : "录音启动失败";
                return Task.CompletedTask;
            }
        }
        else if (!_audio.StartRecording(Path.Combine(tempDir, $"classnote_{_sessionId}.wav"), _config))
        {
            AbortIncrementalTranscription();
            StatusText = _audio.LastError != null
                ? "录音启动失败：" + _audio.LastError
                : "录音启动失败";
            return Task.CompletedTask;
        }

        _screenshot.Start();
        // 计时用**墙钟**而不是"定时器 tick 次数"：
        // 旧实现每秒把 ElapsedSeconds 加一，可 UI 线程一旦被截图落库/进度刷新占住，
        // DispatcherTimer 的 tick 就会被推迟——实测一段 21.9 分钟（1314 秒）的录音
        // 只记成了 1262 秒（少 4%），而这个数字既写进会话时长、又用作截图的时间戳，
        // 于是笔记里的 `[new_slide @ 20:50]` 标记也整体偏早。用 Stopwatch 后与实际音频长度一致。
        _elapsedClock = System.Diagnostics.Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            ElapsedSeconds = _elapsedClock?.Elapsed.TotalSeconds ?? ElapsedSeconds + 1;
            RefreshTranscriptionStatus();
        };
        _elapsedTimer.Start();

        // 录音起来了、但发生了降级（例如所选播放设备失效、回退到系统默认设备）时必须让用户看见：
        // 否则他会一直以为录的是自己配的那个设备，等发现录成静音时已经下课了。
        // ⚠️ 不能塞进 StatusText：那是录音页判断"是否在录音"的控制量（== "录音中"），会被误判。
        SetRecordingWarning(_audio.LastWarning);

        StatusText = "录音中";
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync()
    {
        // 快速收尾（毫秒级）：停止采集 → 摘掉转写订阅 → 告知转写输入完结。
        // 归档音频、结束会话、处理管线（STT → OCR → LLM）全部在后台执行，立即返回，不阻塞 UI。
        //
        // v0.7 改动：以前这里 awaited 了"复制音频文件"（90 分钟单路 172MB、双路 344MB）
        // 与两次网络/DB 收尾，用户点"结束录音"后要盯着转圈 1–10 秒；现在这些都在后台，
        // 且与转写管线并行（管线读的是 %TEMP% 里的原始文件，不依赖归档副本）。
        RecordingAudio? audio;
        try
        {
            audio = StopCapture();
        }
        catch (Exception ex)
        {
            StatusText = $"停止失败: {ex.Message}";
            return Task.CompletedTask;
        }

        _backgroundProcessing = Task.Run(() => FinalizeAsync(audio));
        StatusText = "已停止";
        return Task.CompletedTask;
    }

    /// <summary>
    /// 同步收尾（毫秒级）：停采集、关音频文件（补写 WAV 头）、停截图、摘掉转写订阅。
    /// 必须在返回前关完文件——后面的转写要读它们。
    /// </summary>
    private RecordingAudio? StopCapture()
    {
        try
        {
            _audio.StopRecording();
        }
        catch { /* 音频停止失败不影响后续流程 */ }

        _screenshot.Stop();
        _elapsedTimer?.Stop();

        // 摘掉转写订阅：停止之后不应再收到任何帧（采集侧也在 Stop 里摘回调，这里是第二道保险）
        try { _audio.FrameCaptured -= OnFrameCaptured; } catch { }

        // 告知"不会再有新数据"：工作线程把尾部不足一块的部分补算完就退出，**不阻塞本方法**
        _incremental?.SignalInputComplete();
        _ocrQueue?.Complete();

        // 分轨录音时这里是两路文件；停止后文件已写完（各 writer 在 Stop 里关闭）
        return _audio.GetRecordedAudio();
    }

    /// <summary>
    /// 后台收尾：归档音频 → 结束会话 → 处理管线。失败不抛出，仅更新状态。
    /// </summary>
    private async Task FinalizeAsync(RecordingAudio? audio)
    {
        try
        {
            await _upload.FlushQueueAsync();
        }
        catch { /* 队列刷新失败不影响结束 */ }

        try
        {
            await UploadAudioAsync(audio);
        }
        catch (Exception ex)
        {
            StatusText = $"音频保存失败: {ex.Message}";
        }

        try
        {
            await _upload.FlushAudioQueueAsync();
        }
        catch { /* 音频队列补发失败不影响结束 */ }

        try
        {
            await _api.EndSessionAsync(_sessionId, (int)Math.Round(_elapsedSeconds));
        }
        catch (Exception ex)
        {
            StatusText = $"结束会话失败: {ex.Message}";
        }

        await ProcessAsync(audio);
    }

    /// <summary>
    /// 执行本地处理管线（STT → OCR → LLM）。失败不抛出，仅更新状态。
    /// </summary>
    private async Task ProcessAsync(RecordingAudio? audio)
    {
        try
        {
            await _processor.ProcessAsync(_sessionId, audio,
                new Progress<string>(msg => StatusText = msg));
        }
        catch (Exception ex)
        {
            StatusText = $"笔记生成失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 将本次录音落地到本地数据目录（直接复制，无网络上传）。
    /// 分轨录音按来源分别落地，文件名保留 mic / system 以便事后辨认。
    /// </summary>
    private async Task UploadAudioAsync(RecordingAudio? audio)
    {
        if (audio == null || audio.IsEmpty)
            return;

        var uploaded = await _upload.UploadAudioTracksAsync(_sessionId, audio);
        if (!uploaded)
        {
            // 落地失败时退回逐文件重试（含离线队列语义）
            foreach (var file in audio.Files)
            {
                if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path))
                    continue;
                var name = file.Source == RecordingAudioSource.System ? "system.wav" : "mic.wav";
                await _upload.EnqueueAudioAsync(_sessionId, file.Path, name);
            }
        }
    }

    // ── 边录边转写（v0.7）──────────────────────────────────

    /// <summary>
    /// 启动课堂增量转写：
    /// · 采集前挂上帧订阅（漏接开头会导致后面所有块错位）；
    /// · 起资源调度器（检测到全屏放映 / 视频 / 电池时自动降速，见 ClassroomResourceGovernor）；
    /// · 预热 STT 模型（把 6–33 秒的加载挪到课堂这一段本来就有空闲的时间里）。
    /// </summary>
    private void StartIncrementalTranscription()
    {
        var mode = ClassroomTranscriptionModes.FromStorage(AppSettings.Instance.Snapshot().ClassroomTranscription);
        if (mode == ClassroomTranscriptionMode.Off)
            return;

        var sources = new List<RecordingAudioSource>();
        if (_config.NeedsMicrophone) sources.Add(RecordingAudioSource.Microphone);
        if (_config.NeedsSystemAudio) sources.Add(RecordingAudioSource.System);
        if (sources.Count == 0)
            return;

        try
        {
            _governor = new ClassroomResourceGovernor(
                new SystemResourceProbe(() => _screenshot.IsVideoMode), mode);
            _governor.Start();

            _incremental = new IncrementalTranscriptionSession(
                _sessionId, sources, LocalRepository.Instance,
                SenseVoiceSttService.Shared.TranscribeChunkFromWindow, _governor);
            IncrementalTranscriptionHub.Register(_sessionId, _incremental);

            _audio.FrameCaptured += OnFrameCaptured;

            // 预热：模型加载是纯 CPU 的一次性开销，放在这里比放在"点结束"那一刻好得多
            _ = SenseVoiceSttService.Shared.PrewarmAsync();
        }
        catch (Exception ex)
        {
            // 增量转写起不来不是录音失败的理由：退化成课后整文件转写即可
            System.Diagnostics.Debug.WriteLine($"[RecordingViewModel] 启动增量转写失败: {ex.Message}");
            AbortIncrementalTranscription();
        }
    }

    /// <summary>
    /// 录音**没起来**时的中止：摘订阅并连注册表一起清掉。
    /// 与 <see cref="Dispose"/> 的区别：这里不会再有任何处理管线来接管，留着注册表条目就是悬挂引用。
    /// </summary>
    private void AbortIncrementalTranscription()
    {
        try { _audio.FrameCaptured -= OnFrameCaptured; } catch { }
        _governor?.Dispose();
        _governor = null;
        _incremental = null;
        IncrementalTranscriptionHub.Unregister(_sessionId);
    }

    /// <summary>采集线程回调：只做一次拷贝入队，绝不做推理/IO（那会拖慢采集并丢帧）。</summary>
    private void OnFrameCaptured(object? sender, AudioFrameCapturedEventArgs e)
        => _incremental?.HandleFrame(e.Source, e.Data, e.Count);

    /// <summary>刷新课堂转写进度文案（秒级定时器调用，在 UI 线程上）。</summary>
    private void RefreshTranscriptionStatus()
    {
        if (_incremental == null)
            return;

        try
        {
            var parts = _incremental.Snapshot()
                .Select(p => $"{SourceShortName(p.Source)} {FormatClock(p.SecondsCovered)}")
                .ToArray();
            var budget = _governor?.Current;
            string suffix = budget is { IsYielding: true } ? $"（{budget.Reason}，已让路）" : "";
            TranscriptionStatus = parts.Length == 0
                ? ""
                : "课堂转写：" + string.Join(" / ", parts) + suffix;
            IsYielding = budget?.IsYielding == true || budget?.IsPaused == true;
        }
        catch
        {
            // 进度只是展示：任何异常都不该影响录音
        }
    }

    private static string SourceShortName(RecordingAudioSource source)
        => source == RecordingAudioSource.System ? "课件" : "现场";

    private static string FormatClock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private async void OnScreenshotCaptured(object? sender, ScreenshotResult result)
    {
        try
        {
            ScreenshotCount++;
            int seqNo = ScreenshotCount;
            await _upload.UploadScreenshotAsync(_sessionId, seqNo, ElapsedSeconds,
                result.ApiType, result.ImageData, result.UrlFound);

            // 图片落库后立刻排队做 OCR：让"识别截图文字"在课堂上就完成，
            // 而不是像旧实现那样排在 STT 后面、等用户已经开始等笔记了才开跑。
            _ocrQueue?.Enqueue(_sessionId, seqNo, result.ImageData);
        }
        catch
        {
            // 截图保存失败已由 UploadService 内部处理
        }
    }

    public byte[] GetAudioData() => _audio.GetFileBytes();

    public void Dispose()
    {
        try { _audio.FrameCaptured -= OnFrameCaptured; } catch { }
        _incremental?.Dispose();
        _governor?.Dispose();
        _ocrQueue?.Dispose();
        _audio.Dispose();
        _screenshot.Dispose();
        _upload.Dispose();
        _elapsedTimer?.Stop();
    }
}
