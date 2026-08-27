using System.Collections.ObjectModel;
using System.Windows.Input;
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly IApiService _api;

    public ObservableCollection<Session> RecentSessions { get; } = new();

    public string[] Courses { get; } =
        { "语文", "数学", "英语", "物理", "化学", "生物", "历史", "政治", "地理" };

    private string _selectedCourse = "数学";
    public string SelectedCourse
    {
        get => _selectedCourse;
        set { _selectedCourse = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// True while any visible session is still being processed (not yet in a
    /// terminal state). Used to drive auto-refresh polling on the main page.
    /// </summary>
    public bool HasPendingSessions =>
        RecentSessions.Any(s => s.Status is "recording" or "processing");

    public ICommand StartCommand { get; }

    public ICommand RefreshCommand { get; }

    public MainViewModel(IApiService api)
    {
        _api = api;
        StartCommand = new RelayCommand(async () =>
        {
            var id = await StartRecordingAsync();
            if (id.HasValue)
                await LoadSessionsAsync();
        });
        RefreshCommand = new RelayCommand(() => LoadSessionsAsync());
    }

    public async Task LoadSessionsAsync(bool showLoading = true)
    {
        if (showLoading)
            IsLoading = true;
        ErrorMessage = "";
        try
        {
            var sessions = await _api.ListSessionsAsync();
            RecentSessions.Clear();
            foreach (var s in sessions.Take(20))
                RecentSessions.Add(s);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            if (showLoading)
                IsLoading = false;
            OnPropertyChanged(nameof(HasPendingSessions));
        }
    }

    public async Task<Guid?> StartRecordingAsync(string? title = null)
    {
        ErrorMessage = "";
        try
        {
            var id = await _api.CreateSessionAsync(SelectedCourse, title);
            return id;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"启动记录失败: {ex.Message}";
            return null;
        }
    }
}
