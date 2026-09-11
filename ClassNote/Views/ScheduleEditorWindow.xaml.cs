using System.Windows;
using System.Windows.Controls;
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;

namespace ClassNote.Views;

/// <summary>
/// 「课程安排」编辑窗口 —— 每周课表唯一的编辑入口。
/// 以列表形式维护条目（星期 → 时间排序），添加/修改经 ScheduleEntryDialog 单条编辑，
/// 支持就地停用/启用与删除；点「保存课表」才整体写回 SQLite（取消则全部丢弃）。
/// </summary>
public partial class ScheduleEditorWindow : Window
{
    private readonly List<ScheduleEntry> _entries;

    /// <summary>行视图模型：把条目渲染成列表行。</summary>
    private sealed class Row
    {
        public required ScheduleEntry Entry { get; init; }
        public string Course => Entry.Course;
        public string? Title => Entry.Title;
        public string WeekdayShort => ScheduleEntry.Weekdays[Entry.WeekdayIndex];
        public string TimeRangeText => $"{ScheduleEntry.FormatClock(Entry.StartMin)}–{ScheduleEntry.FormatClock(Entry.EndMin)}";
        public string ToggleLabel => Entry.Enabled ? "停用" : "启用";
        public bool DisabledBadgeVisible => !Entry.Enabled;
    }

    public ScheduleEditorWindow(IEnumerable<ScheduleEntry> entries)
    {
        InitializeComponent();
        _entries = entries.ToList();
        RefreshRows();
    }

    private static string[] CourseSuggestions => MainViewModel.DefaultCourses;

    private void RefreshRows()
    {
        var rows = _entries
            .OrderBy(e => e.WeekdayIndex)
            .ThenBy(e => e.StartMin)
            .Select(e => new Row { Entry = e })
            .ToList();
        EntryList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HelpBanner.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = rows.Count == 0
            ? ""
            : $"共 {rows.Count} 节 · 启用 {rows.Count(e => e.Entry.Enabled)} 节";
    }

    private Row? RowOf(object sender)
        => sender is FrameworkElement fe && fe.DataContext is Row row ? row : null;

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScheduleEntryDialog(null, 0, 8 * 60, _entries, CourseSuggestions)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
            return;
        if (dialog.Result is { } result)
        {
            _entries.Add(result);
            RefreshRows();
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        var entry = row.Entry;
        var dialog = new ScheduleEntryDialog(entry, entry.WeekdayIndex, entry.StartMin,
            _entries, CourseSuggestions)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
            return;
        if (dialog.Deleted)
        {
            _entries.RemoveAll(x => x.Id == entry.Id);
        }
        else if (dialog.Result is { } result)
        {
            result.Id = entry.Id; // 保持 Id：调度器"当天已触发"记忆不错乱
            _entries.RemoveAll(x => x.Id == entry.Id);
            _entries.Add(result);
        }
        RefreshRows();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            row.Entry.Enabled = !row.Entry.Enabled;
            RefreshRows();
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        var confirm = MessageBox.Show(
            $"删除「{row.Entry.Course}」（{row.WeekdayShort} {row.TimeRangeText}）这条课表？",
            "删除课表条目",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;
        _entries.Remove(row.Entry);
        RefreshRows();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 保存前二次校验：同一星期内不允许时间重叠（对话框已拦截，此处防御）
        foreach (var entry in _entries)
        {
            var clash = _entries.FirstOrDefault(x =>
                x.Id != entry.Id &&
                x.WeekdayIndex == entry.WeekdayIndex &&
                entry.StartMin < x.EndMin && x.StartMin < entry.EndMin);
            if (clash != null)
            {
                MessageBox.Show(
                    $"「{entry.Course}」与「{clash.Course}」在{entry.WeekdayName}时间重叠，" +
                    $"无法保存。请先调整时间。",
                    "课表无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        try
        {
            LocalRepository.Instance.ReplaceScheduleEntries(_entries);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存课表失败：{ex.Message}", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
