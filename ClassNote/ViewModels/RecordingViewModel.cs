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
    private double _elapsedSeconds;
    private string _selectedMic = "";
    private string[] _availableMics = Array.Empty<string>();
    private DispatcherTimer? _elapsedTimer;
    private Task? _backgroundProcessing;

    public RecordingViewModel(Guid sessionId, IApiService api, IAudioService audio,
        IScreenshotService screenshot, IUploadService upload, string? micName = null,
        INoteProcessor? processor = null)
    {
        _sessionId = sessionId;
        _api = api;
        _audio = audio;
        _screenshot = screenshot;
        _upload = upload;
        _processor = processor ?? new NoteProcessor(
            new SenseVoiceSttService(), new WindowsOcrService(), new LlmService());
        _screenshot.ScreenshotCaptured += OnScreenshotCaptured;
        AvailableMics = _audio.GetInputDevices();
        if (micName != null && AvailableMics.Contains(micName))
            SelectedMic = micName;
        else if (AvailableMics.Length > 0)
            SelectedMic = AvailableMics[0];
    }

    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public int ScreenshotCount { get => _screenshotCount; set { _screenshotCount = value; OnPropertyChanged(); } }

    /// <summary>
    /// 后台处理管线任务（录音停止后启动：STT → OCR → LLM）。
    /// 页面在 quick-stop 后即可返回主页，此任务在后台独立完成。
    /// </summary>
    public Task? BackgroundProcessingTask => _backgroundProcessing;
    public double ElapsedSeconds { get => _elapsedSeconds; set { _elapsedSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedDisplay)); } }
    public string ElapsedDisplay => TimeSpan.FromSeconds(ElapsedSeconds).ToString(@"hh\:mm\:ss");
    public string SelectedMic { get => _selectedMic; set { _selectedMic = value; OnPropertyChanged(); } }
    public string[] AvailableMics { get => _availableMics; set { _availableMics = value; OnPropertyChanged(); } }

    public Task StartRecordingAsync()
    {
        var audioPath = Path.Combine(Path.GetTempPath(), $"classnote_{_sessionId}.wav");
        var micIndex = Array.IndexOf(AvailableMics, SelectedMic);

        if (!_audio.StartRecording(audioPath, micIndex >= 0 ? micIndex : 0))
        {
            StatusText = "录音启动失败";
            return Task.CompletedTask;
        }

        _screenshot.Start();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedSeconds++;
        _elapsedTimer.Start();
        StatusText = "录音中";
        return Task.CompletedTask;
    }

    public async Task StopRecordingAsync()
    {
        // 快速收尾：停止采集 → 音频/截图入库 → 结束会话。
        // 处理管线（STT → OCR → LLM）在后台执行，立即返回，不阻塞 UI。
        string? audioPath = null;
        try
        {
            audioPath = await StopRecordingCoreAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"停止失败: {ex.Message}";
            return;
        }

        // 后台处理音频（转写 / OCR / 生成笔记），完成后会话状态自动变为 completed
        _backgroundProcessing = Task.Run(() => ProcessAsync(audioPath));
        StatusText = "已停止";
    }

    /// <summary>执行快速收尾，返回本次录音的音频路径（可能为 null）。</summary>
    private async Task<string?> StopRecordingCoreAsync()
    {
        try
        {
            _audio.StopRecording();
        }
        catch { /* 音频停止失败不影响后续流程 */ }

        _screenshot.Stop();
        _elapsedTimer?.Stop();

        try
        {
            await _upload.FlushQueueAsync();
        }
        catch { /* 队列刷新失败不影响结束 */ }

        // 落地音频到本地持久目录（供转录管线使用）
        var audioPath = _audio.GetOutputPath();
        try
        {
            await UploadAudioAsync();
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

        await _api.EndSessionAsync(_sessionId, (int)Math.Round(_elapsedSeconds));
        return audioPath;
    }

    /// <summary>
    /// 执行本地处理管线（STT → OCR → LLM）。失败不抛出，仅更新状态。
    /// </summary>
    private async Task ProcessAsync(string? audioPath)
    {
        try
        {
            await _processor.ProcessAsync(_sessionId, audioPath,
                new Progress<string>(msg => StatusText = msg));
        }
        catch (Exception ex)
        {
            StatusText = $"笔记生成失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 将本次录音落地到本地数据目录（直接复制，无网络上传），
    /// 失敗则不保留录音路径，影响后续 STT。
    /// </summary>
    private async Task UploadAudioAsync()
    {
        var audioPath = _audio.GetOutputPath();
        if (string.IsNullOrEmpty(audioPath))
            return;
        if (!File.Exists(audioPath) || new FileInfo(audioPath).Length == 0)
            return;

        var uploaded = await _upload.UploadAudioAsync(_sessionId, audioPath, "audio.wav");
        if (!uploaded)
        {
            await _upload.EnqueueAudioAsync(_sessionId, audioPath, "audio.wav");
        }
    }

    private async void OnScreenshotCaptured(object? sender, ScreenshotResult result)
    {
        try
        {
            ScreenshotCount++;
            await _upload.UploadScreenshotAsync(_sessionId, ScreenshotCount, ElapsedSeconds,
                result.ApiType, result.ImageData, result.UrlFound);
        }
        catch
        {
            // 截图保存失败已由 UploadService 内部处理
        }
    }

    public byte[] GetAudioData() => _audio.GetFileBytes();

    public void Dispose()
    {
        _audio.Dispose();
        _screenshot.Dispose();
        _upload.Dispose();
        _elapsedTimer?.Stop();
    }
}
