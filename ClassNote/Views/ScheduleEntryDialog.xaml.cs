using System.Windows;
using System.Windows.Media;
using ClassNote.Models;

namespace ClassNote.Views;

/// <summary>
/// 单条课表条目的添加/编辑对话框：课程（下拉选择，不支持手输）、星期、起止时间、是否启用。
/// 保存时校验：课程已选择、时间合法、同一星期内不与其它条目重叠。
/// </summary>
public partial class ScheduleEntryDialog : Window
{
    private readonly ScheduleEntry? _editing;
    private readonly List<ScheduleEntry> _existing;
    private readonly string[] _weekdays;

    /// <summary>保存成功后的条目（新增或更新后的副本）；取消或删除时为 null。</summary>
    public ScheduleEntry? Result { get; private set; }

    public bool Deleted { get; private set; }

    public ScheduleEntryDialog(ScheduleEntry? editing, int initialWeekday, int initialStartMin,
        IReadOnlyList<ScheduleEntry> existing, string[] courseSuggestions)
    {
        InitializeComponent();
        _editing = editing;
        _existing = existing.ToList();
        _weekdays = ScheduleEntry.Weekdays;

        // 课程候选：内置 9 门 + 现有条目的课程，去重（课程名只能从这里选，避免手输出现
        // "数学 " / "数学" 之类同课不同名的脏数据）
        var courses = new List<string>();
        if (courseSuggestions != null)
            courses.AddRange(courseSuggestions.Where(c => !string.IsNullOrWhiteSpace(c)));
        if (courses.Count == 0)
            courses.AddRange(ViewModels.MainViewModel.DefaultCourses);   // 防御：调用方未给候选时兜底
        foreach (var e in _existing)
            if (!string.IsNullOrWhiteSpace(e.Course) && !courses.Contains(e.Course))
                courses.Add(e.Course);
        CourseBox.ItemsSource = courses;

        WeekdayBox.ItemsSource = _weekdays;

        if (_editing != null)
        {
            HeaderTitle.Text = "编辑课表条目";
            DeleteButton.Visibility = Visibility.Visible;
            SelectCourse(courses, _editing.Course);
            WeekdayBox.SelectedIndex = _editing.WeekdayIndex;
            StartBox.Text = ScheduleEntry.FormatClock(_editing.StartMin);
            EndBox.Text = ScheduleEntry.FormatClock(_editing.EndMin);
            EnabledBox.IsChecked = _editing.Enabled;
            if (_editing.Title is { Length: > 0 })
                TitleBox.Text = _editing.Title;
        }
        else
        {
            CourseBox.SelectedIndex = CourseBox.Items.Count > 0 ? 0 : -1;
            WeekdayBox.SelectedIndex = Math.Clamp(initialWeekday, 0, 6);
            var start = Normalize(initialStartMin);
            StartBox.Text = ScheduleEntry.FormatClock(start);
            EndBox.Text = ScheduleEntry.FormatClock(start + 45);
            UpdateDurationHint();
        }

        StartBox.TextChanged += (_, _) => UpdateDurationHint();
        EndBox.TextChanged += (_, _) => UpdateDurationHint();
    }

    /// <summary>
    /// 把课程名设为下拉框的选中项。历史录入的课程名若不在候选里，临时补进列表后再选中
    /// （保持"只能选择"：补进去的仍是列表项，而不是手输文本）。
    /// </summary>
    private void SelectCourse(List<string> courses, string course)
    {
        var idx = courses.IndexOf(course);
        if (idx < 0)
        {
            courses.Add(course);
            CourseBox.ItemsSource = null;
            CourseBox.ItemsSource = courses;   // 刷新 ItemsSource 使补充项可选中
            idx = courses.Count - 1;
        }
        CourseBox.SelectedIndex = idx;
    }

    /// <summary>把任意分钟数规范到当天范围内（0–1439）。</summary>
    private static int Normalize(int minutes)
    {
        minutes %= 1440;
        return minutes < 0 ? minutes + 1440 : minutes;
    }

    private void UpdateDurationHint()
    {
        var (s, e) = ParseTimes();
        DurationHint.Text = s >= 0 && e > s
            ? $"本节时长 {e - s} 分钟（{ScheduleEntry.FormatClock(s)} – {ScheduleEntry.FormatClock(e)}）"
            : "请填写合法的开始与结束时间（如 08:00 / 09:40）。";
    }

    private (int Start, int End) ParseTimes()
    {
        int start = ParseClock(StartBox.Text);
        int end = ParseClock(EndBox.Text);
        return (start, end);
    }

    /// <summary>解析 "HH:mm" / "H:mm"（也容忍 8:5 → 08:05）；非法返回 -1。</summary>
    public static int ParseClock(string text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 2)
            return -1;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
            return -1;
        if (h < 0 || h > 24 || m < 0 || m > 59)
            return -1;
        if (h == 24 && m != 0)
            return -1;
        return h * 60 + m;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 课程名只能从下拉列表选择：以选中项为准（不再读取手输文本）
        var course = (CourseBox.SelectedItem as string)?.Trim() ?? "";
        if (course.Length == 0)
        {
            ShowError("请选择课程名称。");
            return;
        }
        if (WeekdayBox.SelectedIndex < 0)
        {
            ShowError("请选择星期。");
            return;
        }
        var weekday = WeekdayBox.SelectedIndex;
        var (start, end) = ParseTimes();
        if (start < 0 || end < 0 || end <= start)
        {
            ShowError("时间无效：结束时间必须晚于开始时间（如 08:00–09:40）。");
            return;
        }

        // 重叠校验：同一星期内不允许与其它条目时间重叠（跨零点条目也参与判断）
        foreach (var other in _existing)
        {
            if (_editing != null && other.Id == _editing.Id)
                continue;
            if (other.WeekdayIndex != weekday)
                continue;
            if (start < other.EndMin && other.StartMin < end)
            {
                ShowError($"与「{other.Course}」（{ScheduleEntry.FormatClock(other.StartMin)}–" +
                          $"{ScheduleEntry.FormatClock(other.EndMin)}）时间重叠。");
                return;
            }
        }

        Result = new ScheduleEntry
        {
            Id = _editing?.Id ?? Guid.NewGuid(),
            WeekdayIndex = weekday,
            StartMin = start,
            EndMin = end,
            Course = course,
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked != false,
        };
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        Deleted = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
