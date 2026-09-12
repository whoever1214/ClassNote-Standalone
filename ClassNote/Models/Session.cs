using System.ComponentModel;

namespace ClassNote.Models;

public class Session : INotifyPropertyChanged
{
    public Guid Id { get; set; }
    public string Course { get; set; } = "";
    public string? Title { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Duration { get; set; }
    public string Status { get; set; } = "recording";

    /// <summary>
    /// 开始时间的星期几（如「周一」）。仅用于界面展示，数据库不存该字段，
    /// 记录列表里与 yyyy-MM-dd HH:mm 拼在一起让用户一眼看出是哪天。
    /// </summary>
    public string WeekdayText => WeekdayTextOf(StartTime);

    /// <summary>把日期换算为中文星期几；周日的 DayOfWeek=0，落到表格末位。</summary>
    public static string WeekdayTextOf(DateTime time) => time.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    private bool _isSelected;

    /// <summary>最近记录列表中的勾选状态（用于多选 / 全选 / 批量导出 / 批量删除）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
