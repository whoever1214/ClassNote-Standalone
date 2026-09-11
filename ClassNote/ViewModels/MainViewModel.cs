using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly IApiService _api;

    public ObservableCollection<Session> RecentSessions { get; } = new();

    /// <summary>内置课程候选（课程下拉/课表编辑器共用）。</summary>
    public static string[] DefaultCourses { get; } =
        { "语文", "数学", "英语", "物理", "化学", "生物", "历史", "政治", "地理" };

    public string[] Courses => DefaultCourses;

    private string _selectedCourse = "数学";
    public string SelectedCourse
    {
        get => _selectedCourse;
        set { _selectedCourse = value; OnPropertyChanged(); }
    }

    private bool? _selectAll;

    /// <summary>
    /// 全选 / 取消全选（三态：全部勾选 true、部分勾选 null、未勾选 false）。
    /// 由最近记录列表中的"全选"复选框驱动。
    /// </summary>
    public bool? SelectAll
    {
        get => _selectAll;
        set
        {
            if (_selectAll == value) return;
            _selectAll = value;
            OnPropertyChanged();
            if (value.HasValue)
            {
                foreach (var s in RecentSessions)
                    s.IsSelected = value.Value;
            }
            UpdateSelectAllState();
        }
    }

    /// <summary>当前勾选的记录数量。</summary>
    public int SelectedCount => RecentSessions.Count(s => s.IsSelected);

    /// <summary>是否存在勾选的记录（用于启用批量导出 / 删除按钮）。</summary>
    public bool HasSelection => SelectedCount > 0;

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

            // 保留刷新前的勾选状态（自动轮询期间不丢失用户选择）
            var selectedIds = RecentSessions.Where(s => s.IsSelected).Select(s => s.Id).ToHashSet();
            foreach (var old in RecentSessions)
                old.PropertyChanged -= OnSessionPropertyChanged;
            RecentSessions.Clear();
            foreach (var s in sessions.Take(20))
            {
                s.IsSelected = selectedIds.Contains(s.Id);
                s.PropertyChanged += OnSessionPropertyChanged;
                RecentSessions.Add(s);
            }
            UpdateSelectAllState();
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

    /// <summary>
    /// 批量导出勾选会话的笔记为 PDF 到指定文件夹（跳过尚未生成笔记的会话）。
    /// 返回（成功导出数量, 因无笔记而跳过的数量）。
    /// </summary>
    public async Task<(int Exported, int Skipped)> ExportSelectedPdfAsync(string folderPath)
    {
        var selected = RecentSessions.Where(s => s.IsSelected).ToList();
        var exported = 0;
        var skipped = 0;

        foreach (var s in selected)
        {
            var bytes = await _api.ExportNotePdfAsync(s.Id);
            if (bytes == null || bytes.Length == 0)
            {
                skipped++;
                continue;
            }

            var title = string.IsNullOrWhiteSpace(s.Title) ? "课堂笔记" : s.Title;
            var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
            var path = UniqueFilePath(folderPath, safeTitle + ".pdf");
            await File.WriteAllBytesAsync(path, bytes);
            exported++;
        }

        return (exported, skipped);
    }

    /// <summary>批量删除勾选的会话（含录音、截图、笔记）。</summary>
    public async Task DeleteSelectedAsync()
    {
        var selected = RecentSessions.Where(s => s.IsSelected).ToList();
        foreach (var s in selected)
            await _api.DeleteSessionAsync(s.Id);
    }

    /// <summary>同一文件夹下重名文件自动追加序号，避免覆盖（如 "笔记 (2).pdf"）。</summary>
    private static string UniqueFilePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName)!;
        if (!File.Exists(path)) return path;

        var name = Path.GetFileNameWithoutExtension(fileName) ?? fileName;
        var ext = Path.GetExtension(fileName) ?? "";
        for (var i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{name} ({i}){ext}")!;
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>单条记录勾选状态变化时，同步刷新计数与"全选"三态。</summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Session.IsSelected))
            return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        UpdateSelectAllState();
    }

    /// <summary>根据当前勾选情况重算"全选"复选框的三态值。</summary>
    private void UpdateSelectAllState()
    {
        var total = RecentSessions.Count;
        var selected = SelectedCount;
        bool? next = total == 0 || selected == 0
            ? false
            : selected == total ? true : null;

        if (_selectAll == next) return;
        _selectAll = next;
        OnPropertyChanged(nameof(SelectAll));
    }
}