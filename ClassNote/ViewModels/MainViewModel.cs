using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.ViewModels;

public class MainViewModel : BaseViewModel
{
    /// <summary>「最近记录」课程筛选框里代表"不限课程"的选项。</summary>
    public const string AllCourses = "全部课程";

    private readonly IApiService _api;

    /// <summary>
    /// 从本地库读出、参与筛选的全部会话（最近在前）。勾选状态挂在这些对象上，
    /// 因此切换筛选条件不会丢失用户已勾选的记录。
    /// 私有：外部只通过 <see cref="FilteredSessions"/> 看到筛选后的结果，
    /// 避免直接改动主集合后可见列表与筛选条件不一致（列表必须始终与筛选条件一致）。
    /// </summary>
    private ObservableCollection<Session> RecentSessions { get; } = new();

    /// <summary>「最近记录」列表绑定的集合：全部记录按 CourseFilter 过滤后的结果，最近在前。</summary>
    public ObservableCollection<Session> FilteredSessions { get; } = new();

    /// <summary>内置课程候选（课程下拉/课表编辑器共用）。</summary>
    public static string[] DefaultCourses { get; } =
        { "语文", "数学", "英语", "物理", "化学", "生物", "历史", "政治", "地理" };

    public string[] Courses => DefaultCourses;

    private string _selectedCourse = "数学";

    /// <summary>上一次使用的课程（打开「开始记录」配置窗时的默认选中项）。</summary>
    public string SelectedCourse
    {
        get => _selectedCourse;
        set { _selectedCourse = value; OnPropertyChanged(); }
    }

    /// <summary>「最近记录」的课程筛选候选：全部课程 + 内置课程 + 库里出现过的自定义课程名。</summary>
    public ObservableCollection<string> CourseFilters { get; } = new() { AllCourses };

    private string _courseFilter = AllCourses;

    /// <summary>
    /// 当前课程筛选条件。"全部课程"表示不限课程；默认按时间倒序展示（列表本身即最近在前）。
    /// </summary>
    public string CourseFilter
    {
        get => _courseFilter;
        set
        {
            if (string.Equals(_courseFilter, value, StringComparison.Ordinal)) return;
            _courseFilter = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>是否正处于筛选态（用于界面提示与空状态文案）。</summary>
    public bool IsFiltered => !string.Equals(_courseFilter, AllCourses, StringComparison.Ordinal);

    private bool? _selectAll;

    /// <summary>
    /// 全选 / 取消全选（三态：全部勾选 true、部分勾选 null、未勾选 false）。
    /// 只作用于当前筛选后可见的记录，避免误删被筛掉的记录。
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
                foreach (var s in FilteredSessions)
                    s.IsSelected = value.Value;
            }
            UpdateSelectAllState();
        }
    }

    /// <summary>当前可见记录中勾选的数量。</summary>
    public int SelectedCount => FilteredSessions.Count(s => s.IsSelected);

    /// <summary>是否存在勾选的记录（用于启用批量导出 / 删除按钮）。</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>筛选后可见的记录数（界面空状态与计数用）。</summary>
    public int VisibleCount => FilteredSessions.Count;

    /// <summary>本地库中是否有任何记录（用于区分"一条都没有"和"筛选后为空"两种空状态）。</summary>
    public bool HasAnySessions => RecentSessions.Count > 0;

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
    /// True while any session is still being processed (not yet in a
    /// terminal state). Used to drive auto-refresh polling on the main page.
    /// 判定基于全部已加载记录而非筛选结果，否则筛掉进行中的记录会让轮询提前停止。
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
            RebuildCourseFilters();
            ApplyFilter();
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
            OnPropertyChanged(nameof(HasAnySessions));
        }
    }

    /// <summary>
    /// 按当前 CourseFilter 重建可见列表（保持 RecentSessions 的最近在前顺序）。
    /// 同一批 Session 实例被复用，勾选状态跨筛选保留。
    /// </summary>
    private void ApplyFilter()
    {
        FilteredSessions.Clear();
        foreach (var s in RecentSessions)
        {
            if (!IsFiltered || string.Equals(s.Course, _courseFilter, StringComparison.Ordinal))
                FilteredSessions.Add(s);
        }

        UpdateSelectAllState();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(IsFiltered));
    }

    /// <summary>
    /// 刷新筛选下拉候选：保留「全部课程」在首位，其后是内置课程，最后补上库里出现过的自定义课程名。
    /// 已选中的筛选项始终保留，避免刷新过程中筛选条件被静默重置。
    /// </summary>
    private void RebuildCourseFilters()
    {
        var wanted = new List<string> { AllCourses };
        wanted.AddRange(DefaultCourses);
        foreach (var course in RecentSessions.Select(s => s.Course)
                     .Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (!wanted.Contains(course))
                wanted.Add(course);
        }
        if (IsFiltered && !wanted.Contains(_courseFilter))
            wanted.Add(_courseFilter);

        if (wanted.Count == CourseFilters.Count && wanted.SequenceEqual(CourseFilters))
            return;

        CourseFilters.Clear();
        foreach (var c in wanted)
            CourseFilters.Add(c);
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
        var selected = FilteredSessions.Where(s => s.IsSelected).ToList();
        var exported = 0;
        var skipped = 0;

        foreach (var s in selected)
        {
            var path = await ExportSessionPdfAsync(s, folderPath);
            if (path == null)
                skipped++;
            else
                exported++;
        }

        return (exported, skipped);
    }

    /// <summary>
    /// 导出单条记录的笔记为 PDF 到指定文件夹。返回写入的文件路径；
    /// 该记录尚未生成笔记（或导出内容为空）时返回 null。
    /// </summary>
    public async Task<string?> ExportSessionPdfAsync(Session session, string folderPath)
    {
        var bytes = await _api.ExportNotePdfAsync(session.Id);
        if (bytes == null || bytes.Length == 0)
            return null;

        var title = string.IsNullOrWhiteSpace(session.Title) ? "课堂笔记" : session.Title!;
        var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = "课堂笔记";
        var path = UniqueFilePath(folderPath, safeTitle + ".pdf");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>批量删除勾选的会话（含录音、截图、笔记）。</summary>
    public async Task DeleteSelectedAsync()
    {
        var selected = FilteredSessions.Where(s => s.IsSelected).ToList();
        foreach (var s in selected)
            await _api.DeleteSessionAsync(s.Id);
    }

    /// <summary>删除单条会话（含录音、截图、笔记）。</summary>
    public Task DeleteSessionAsync(Session session) => _api.DeleteSessionAsync(session.Id);

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

    /// <summary>根据当前可见记录的勾选情况重算"全选"复选框的三态值。</summary>
    private void UpdateSelectAllState()
    {
        var total = FilteredSessions.Count;
        var selected = SelectedCount;
        bool? next = total == 0 || selected == 0
            ? false
            : selected == total ? true : null;

        if (_selectAll == next) return;
        _selectAll = next;
        OnPropertyChanged(nameof(SelectAll));
    }
}
