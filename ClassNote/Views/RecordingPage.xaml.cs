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
        await _viewModel.StartRecordingAsync();

        if (_viewModel.StatusText == "录音中")
        {
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

    /// <summary>统一的录音收尾：手动"结束录音"与定时自动结束共用同一路径。</summary>
    private async Task StopRecordingAsync()
    {
        if (_stopping)
            return;
        _stopping = true;

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

        RecordingEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        _viewModel.Dispose();
    }
}
