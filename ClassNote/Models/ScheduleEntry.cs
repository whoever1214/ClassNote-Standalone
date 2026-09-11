namespace ClassNote.Models;

/// <summary>
/// 每周课表条目：表示每周固定某一天、某个时间段的一节课。
/// 定时记录功能依据课表条目在到点自动开始 / 结束录音。
/// 不含"某一天一次性"概念——全部条目按"周几"每周循环。
/// </summary>
public class ScheduleEntry
{
    /// <summary>全局唯一标识（本地存储用）。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>星期序号：0=周一 … 6=周日（周一为首日，便于课程表展示顺序）。</summary>
    public int WeekdayIndex { get; set; }

    /// <summary>开始时刻（当日零点起的分钟数，0–1439）。</summary>
    public int StartMin { get; set; }

    /// <summary>结束时刻（当日零点起的分钟数，1–1440，须大于 StartMin）。</summary>
    public int EndMin { get; set; }

    /// <summary>课程名称（必填，如"数学"）；允许课表自定义名称如"自习"。</summary>
    public string Course { get; set; } = "";

    /// <summary>备注 / 小标题（可选，如"第一章 函数"）。</summary>
    public string? Title { get; set; }

    /// <summary>是否参与定时记录（false 表示临时停用但不删除）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>本周几的展示文案（周一到周日）。</summary>
    public string WeekdayName => Weekdays[WeekdayIndex];

    public static readonly string[] Weekdays = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    public static int WeekdayIndexOf(DayOfWeek day)
        => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    public static DayOfWeek DayOfWeekOf(int weekdayIndex)
        => weekdayIndex == 6 ? DayOfWeek.Sunday : (DayOfWeek)(weekdayIndex + 1);

    /// <summary>某天中本节课的开始时间。</summary>
    public DateTime StartOn(DateTime date) => date.Date.AddMinutes(StartMin);

    /// <summary>某天中本节课的结束时间。</summary>
    public DateTime EndOn(DateTime date) => date.Date.AddMinutes(EndMin);

    public override string ToString() => $"{WeekdayName} {FormatClock(StartMin)}–{FormatClock(EndMin)} {Course}";

    /// <summary>把分钟数格式化为 "HH:mm"（支持 1440 = "24:00"）。</summary>
    public static string FormatClock(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, 1440);
        return $"{(minutes / 60):00}:{(minutes % 60):00}";
    }
}
