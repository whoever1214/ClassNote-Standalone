using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClassNote.Services;
using ClassNote.ViewModels;

namespace ClassNote.Views;

public partial class RecordingPage : Page
{
    private readonly RecordingViewModel _viewModel;
    private readonly DispatcherTimer _uiTimer;

    /// <summary>定时自动结束时刻（课表触发时传入；到点自动停止录音）。</summary>
    private readonly DateTime? _autoStopAt;

    /// <summary>自动停止是否已触发（避免与手动"结束录音"并发执行两次收尾）。</summary>
    private bool _stopping;

    /// <summary>
    /// Raised when recording ends (user clicked "结束录音" or scheduled auto-stop fired).
    /// </summary>
    public event EventHandler? RecordingEnded;

    /// <summary>当前是否处于录音中（调度器用它判断是否已在录音）。</summary>
    public bool IsRecording => _viewModel.StatusText == "录音中";

    public RecordingPage(Guid sessionId, string course, RecordingConfig? config = null,
        DateTime? autoStopAt = null)
    {
        InitializeComponent();

        CourseLabel.Text = course;
        _autoStopAt = autoStopAt;

        if (_autoStopAt is DateTime end)
        {
            ScheduleBadge.Visibility = Visibility.Visible;
            ScheduleBadgeText.Text = $"定时记录 · {end:HH:mm} 自动结束";
        }

        // 单机模式：在页面内组装本地依赖（无服务端）
        var api = new ApiService();
        _viewModel = new RecordingViewModel(
            sessionId,
            api,
            new AudioService(),
            new ScreenshotService(),
            new UploadService("", api),
            config
        );
        DataContext = _viewModel;

        SourceLabel.Text = "声音来源：" + _viewModel.SourceDisplayName;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        // 平滑呼吸闪烁：透明度在 1 ↔ 0.22 之间切换（替代原先生硬的红↔透明切换）
        _uiTimer.Tick += (_, _) =>
            RecordingDot.Opacity = RecordingDot.Opacity >= 0.99 ? 0.22 : 1.0;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        UiWatchdog.SetPhase($"开始录音（{CourseLabel.Text}）");
        await _viewModel.StartRecordingAsync();

        if (_viewModel.StatusText == "录音中")
        {
            UiWatchdog.SetPhase($"录音中（{CourseLabel.Text}）");
            RecordingDot.Fill = Brushes.Red;
            _uiTimer.Start();
            UploadProgressBar.Visibility = Visibility.Visible;
            UploadStatusText.Visibility = Visibility.Visible;
            StartAutoStopWatcher();
        }
        else
        {
            RecordingDot.Fill = Brushes.Gray;
            UploadProgressBar.Visibility = Visibility.Collapsed;
            UploadStatusText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 定时触发：启动秒级看门狗，到 <see cref="_autoStopAt"/> 时自动结束录音
    /// （与手动点"结束录音"走同一收尾路径）。
    /// </summary>
    private void StartAutoStopWatcher()
    {
        if (_autoStopAt == null)
            return;
        var watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watcher.Tick += async (_, _) =>
        {
            if (_stopping || _viewModel.StatusText != "录音中")
                return;
            if (DateTime.Now >= _autoStopAt.Value)
            {
                watcher.Stop();
                await StopRecordingAsync();
            }
        };
        watcher.Start();
    }

    private async void EndButton_Click(object sender, RoutedEventArgs e)
    {
        await StopRecordingAsync();
    }

    /// <summary>
    /// 供课表调度调用：本节已到下课时间（或下一节已经开始）时，从外部结束这一段录音。
    ///
    /// 与「结束录音」按钮、自动下课看门狗走**同一条**收尾路径，因此三种入口不会互相踩。
    /// 返回 true 表示录音已停止；false 表示本来就停了。
    ///
    /// 存在的理由：v1.0.x 起课表允许一节课接一节课，但"到点自动下课"的看门狗是
    /// <see cref="DispatcherTimer"/>——UI 线程一旦被占住（历史版本里 `Dispose` 会阻塞数秒），
    /// 看门狗就会被推迟，这段录音于是跨进下一节课；下一节到点时被判定"正在录音"而整个跳过，
    /// 表现就是"下一节的内容记到上一节名下，且下一节记录丢失"。
    /// 由调度器在**下一节真正要开始时**兜底结束，是这条链路上最后一道保险。
    /// </summary>
    public async Task<bool> RequestStopAsync()
    {
        if (_stopping || _viewModel.StatusText != "录音中")
            return false;
        await StopRecordingAsync();
        return true;
    }

    /// <summary>统一的录音收尾：手动"结束录音"与定时自动结束共用同一路径。</summary>
    private async Task StopRecordingAsync()
    {
        if (_stopping)
            return;
        _stopping = true;

        UiWatchdog.SetPhase("结束录音收尾");
        EndButton.IsEnabled = false;
        EndButton.Content = "正在停止...";

        _uiTimer.Stop();
        RecordingDot.Fill = Brushes.Gray;
        RecordingDot.Opacity = 1.0;
        UploadProgressBar.Visibility = Visibility.Collapsed;
        UploadStatusText.Visibility = Visibility.Collapsed;
        TranscriptionStatusText.Visibility = Visibility.Collapsed;

        // 快速收尾后立即返回主页；音频转写等在后台完成
        await _viewModel.StopRecordingAsync();

        UiWatchdog.SetPhase("返回主页（笔记在后台生成）");
        RecordingEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        // DispatcherTimer 必须在它自己的线程上停；先把计时器停掉，剩下的释放动作才可安全地挪到后台。
        _viewModel.StopElapsedTimer();

        // ⚠️ 本页每次导航离开都会走到这里（包括"点结束录音 → 立刻回主页"），
        // 而 Dispose 里存在**同步等待**：音频设备释放的 Join，以及（历史实现里）
        // 每路转写工作线程 2 秒的 WaitForCompletion —— 两路就是 4 秒。
        // 这些一律放到后台线程，绝不能在 UI 线程上跑：
        //   · UI 线程被占住 → 课表调度与"到点自动下课"看门狗（都是 DispatcherTimer）一起被推迟，
        //     录音于是跨进下一节课，下一节到点被判定"正在录音"而整节跳过；
        //   · 持续 5 秒以上，Windows 会给窗口挂上"未响应"，用户只能杀掉进程（现场表现即"卡死"）。
        // 这里只做资源释放：音频已落盘、会话已结束、处理管线由 _backgroundProcessing 独立推进，
        // 不依赖本方法先跑完。
        var viewModel = _viewModel;
        _ = Task.Run(() =>
        {
            try { viewModel.Dispose(); }
            catch { /* 释放失败不致命：进程退出时会一并回收 */ }
        });
    }
}
