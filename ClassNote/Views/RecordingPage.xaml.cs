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

    public RecordingPage(Guid sessionId, string course, string? micName = null)
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
            micName
        );
        DataContext = _viewModel;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _uiTimer.Tick += (_, _) =>
            RecordingDot.Fill = RecordingDot.Fill == Brushes.Red ? Brushes.Transparent : Brushes.Red;
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
        UploadStatusText.Text = "正在保存并生成笔记...";

        _uiTimer.Stop();
        RecordingDot.Fill = Brushes.Gray;

        await _viewModel.StopRecordingAsync();

        EndButton.Content = "已完成";
        UploadProgressBar.Visibility = Visibility.Collapsed;
        UploadStatusText.Visibility = Visibility.Collapsed;

        RecordingEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        _viewModel.Dispose();
    }
}
