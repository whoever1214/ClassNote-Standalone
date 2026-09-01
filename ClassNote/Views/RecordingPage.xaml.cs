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

    /// <summary>
    /// Raised when recording ends (user clicked "结束录音" and stop completed).
    /// </summary>
    public event EventHandler? RecordingEnded;

    public RecordingPage(Guid sessionId, string course, string? micName = null, string? micId = null)
    {
        InitializeComponent();

        CourseLabel.Text = course;

        // 单机模式：在页面内组装本地依赖（无服务端）
        var api = new ApiService();
        _viewModel = new RecordingViewModel(
            sessionId,
            api,
            new AudioService(),
            new ScreenshotService(),
            new UploadService("", api),
            micName,
            micId
        );
        DataContext = _viewModel;

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
        }
        else
        {
            RecordingDot.Fill = Brushes.Gray;
            UploadProgressBar.Visibility = Visibility.Collapsed;
            UploadStatusText.Visibility = Visibility.Collapsed;
        }
    }

    private async void EndButton_Click(object sender, RoutedEventArgs e)
    {
        EndButton.IsEnabled = false;
        EndButton.Content = "正在停止...";

        _uiTimer.Stop();
        RecordingDot.Fill = Brushes.Gray;
        RecordingDot.Opacity = 1.0;
        UploadProgressBar.Visibility = Visibility.Collapsed;
        UploadStatusText.Visibility = Visibility.Collapsed;

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
