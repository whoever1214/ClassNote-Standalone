using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;

namespace ClassNote.Views;

public partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>
    /// Raised when the user clicks "开始记录" — carries the new session ID, course name and selected microphone.
    /// </summary>
    public event EventHandler<(Guid SessionId, string Course, string? MicName)>? StartRecordingRequested;

    /// <summary>
    /// Raised when the user double-clicks a session in the list — carries the session ID.
    /// </summary>
    public event EventHandler<Guid>? SessionSelected;

    public MainPage()
    {
        InitializeComponent();

        var apiService = new ApiService();
        _viewModel = new MainViewModel(apiService);
        DataContext = _viewModel;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            await _viewModel.LoadSessionsAsync();
            EnsurePolling();
        };
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>
    /// Refresh once; keeps polling automatically while any session is still
    /// being processed (ends only when everything reaches a terminal state).
    /// Silent refresh: no loading animation, so UI stays calm during polling.
    /// </summary>
    private async Task RefreshAsync()
    {
        await _viewModel.LoadSessionsAsync(showLoading: false);
        EnsurePolling();
    }

    private void EnsurePolling()
    {
        if (_viewModel.HasPendingSessions)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = Window.GetWindow(this) };
        settings.ShowDialog();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // 1. 弹出配置界面：确认课程、输入标题、选择麦克风
        var mics = new AudioService().GetInputDevices();
        var setup = new RecordingSetupWindow(_viewModel.SelectedCourse, _viewModel.Courses, mics)
        {
            Owner = Window.GetWindow(this),
        };

        if (setup.ShowDialog() != true || setup.Result == null)
            return; // 用户取消

        // 2. 用配置结果创建会话
        _viewModel.SelectedCourse = setup.Result.Course;
        var sessionId = await _viewModel.StartRecordingAsync(setup.Result.Title);
        if (sessionId.HasValue)
        {
            await _viewModel.LoadSessionsAsync();
            StartRecordingRequested?.Invoke(this, (sessionId.Value, setup.Result.Course, setup.Result.MicName));
        }
    }

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is Session session)
        {
            SessionSelected?.Invoke(this, session.Id);
        }
    }
}
