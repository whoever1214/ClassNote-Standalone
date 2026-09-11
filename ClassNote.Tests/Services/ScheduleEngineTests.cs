using ClassNote.Models;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// ScheduleEngine 触发判定单测：到点触发 / 一天一次 / 宽限期补触发 /
/// 课后与宽限外不触发 / 停用与星期过滤 / 事后打开应用不触发 / 跨周再次触发。
/// </summary>
public class ScheduleEngineTests
{
    /// <summary>找到某个周一作为"今天"（保证 WeekdayIndex==0）。</summary>
    private static DateTime Monday() => NextWeekday(new DateTime(2026, 3, 2), DayOfWeek.Monday);

    private static DateTime NextWeekday(DateTime from, DayOfWeek target)
    {
        var d = from;
        while (d.DayOfWeek != target) d = d.AddDays(1);
        return d.Date;
    }

    private static ScheduleEntry Entry(int startMin, int endMin, string course = "数学",
        int weekdayIndex = 0, bool enabled = true, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        WeekdayIndex = weekdayIndex,
        StartMin = startMin,
        EndMin = endMin,
        Course = course,
        Enabled = enabled,
    };

    private static DateTime ClockTime(int hour, int minute = 0) => Monday().AddHours(hour).AddMinutes(minute);

    [Fact]
    public void NoDue_BeforeStart()
    {
        var clock = ClockTime(7, 59);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 8 * 60 + 45);

        var due = engine.Evaluate(new[] { entry });

        Assert.Empty(due);
    }

    [Fact]
    public void Due_AtStart_ReturnsOccurrence()
    {
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 8 * 60 + 45);

        var due = engine.Evaluate(new[] { entry });

        var occ = Assert.Single(due);
        Assert.Equal(entry.Id, occ.Entry.Id);
        Assert.False(occ.IsLate);
        Assert.Equal(clock.Date.AddHours(8), occ.Start);
        Assert.Equal(clock.Date.AddHours(8).AddMinutes(45), occ.End);
    }

    [Fact]
    public void Due_OncePerDay_EvenIfStillInWindow()
    {
        // 应用在 7:50 启动，8:01 首次评估（8:00 到点的课应触发），之后当天不再重复
        var clock = ClockTime(7, 50);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 10 * 60);

        clock = ClockTime(8, 1);
        Assert.Single(engine.Evaluate(new[] { entry }));

        // 同一窗口内再次评估 → 不再触发
        clock = ClockTime(8, 5);
        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void Due_LateWithinGrace_IsLate()
    {
        // 唤醒滞后 5 分钟（< 10 分钟宽限）→ 补触发并标记迟到
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 8 * 60 + 45);

        clock = ClockTime(8, 5);
        var occ = Assert.Single(engine.Evaluate(new[] { entry }));

        Assert.True(occ.IsLate);
    }

    [Fact]
    public void NoDue_LateBeyondGrace()
    {
        // 唤醒滞后 11 分钟（> 10 分钟宽限）→ 静默跳过
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 9 * 60);

        clock = ClockTime(8, 11);
        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void NoDue_AfterClassEnded()
    {
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 8 * 60 + 45);

        clock = ClockTime(9, 0);
        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void NoDue_DisabledEntry()
    {
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 9 * 60, enabled: false);

        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void NoDue_WrongWeekday()
    {
        var clock = ClockTime(8, 0); // 周一
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 9 * 60, weekdayIndex: 2); // 周三的课

        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void NoDue_AppStartedAfterClassBegan()
    {
        // 8:20 才打开应用（8:00 的课已开始 20 分钟）→ 不应补录（避免误录）
        var clock = ClockTime(8, 20);
        var engine = new ScheduleEngine(() => clock); // 应用在 8:20 启动
        var entry = Entry(8 * 60, 9 * 60);

        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void Due_AppStartedBeforeClass_EvenIfEngineEvaluatesLater()
    {
        // 应用 7:50 启动，8:05 才首次评估（进程繁忙/定时器延迟）→ 仍在宽限内 → 补触发
        var clock = ClockTime(7, 50);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 9 * 60);

        clock = ClockTime(8, 5);
        var occ = Assert.Single(engine.Evaluate(new[] { entry }));
        Assert.True(occ.IsLate);
    }

    [Fact]
    public void Due_NextWeek_SameWeekdayFiresAgain()
    {
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 9 * 60);

        Assert.Single(engine.Evaluate(new[] { entry }));

        // 跳到下周一 8:00
        clock = clock.AddDays(7);
        Assert.Single(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void MultipleEntries_FireAtOwnStartTimes()
    {
        // 应用运行中：模拟 10 秒级 tick，各课在自己的到点时刻被触发一次；
        // 停用条目与未来的条目不触发。
        var clock = ClockTime(7, 50);
        var engine = new ScheduleEngine(() => clock);
        var math = Entry(8 * 60, 9 * 60, course: "数学");
        var english = Entry(9 * 60 + 30, 10 * 60 + 30, course: "英语");
        var disabled = Entry(8 * 60 + 5, 9 * 60, course: "自习", enabled: false);

        var fired = new List<string>();
        var entries = new[] { math, english, disabled };

        // 8:00:10 → 数学到点
        clock = ClockTime(8, 0).AddSeconds(10);
        fired.AddRange(engine.Evaluate(entries).Select(o => o.Entry.Course));
        // 8:05（仍在数学课中）→ 数学不再重复触发
        clock = ClockTime(8, 5);
        fired.AddRange(engine.Evaluate(entries).Select(o => o.Entry.Course));
        // 9:30:10 → 英语到点
        clock = ClockTime(9, 30).AddSeconds(10);
        fired.AddRange(engine.Evaluate(entries).Select(o => o.Entry.Course));

        Assert.Equal(new[] { "数学", "英语" }, fired);
    }

    [Fact]
    public void EndBoundary_Excluded()
    {
        var clock = ClockTime(8, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(8 * 60, 9 * 60);

        // 恰在结束时刻评估 → 不再触发
        clock = ClockTime(9, 0);
        Assert.Empty(engine.Evaluate(new[] { entry }));
    }

    [Fact]
    public void MidnightSpanningEntry_StillFiresBeforeMidnight()
    {
        // 22:00–24:00 的课：应用 21:00 运行中，22:00:10 tick 应触发
        var clock = ClockTime(21, 0);
        var engine = new ScheduleEngine(() => clock);
        var entry = Entry(22 * 60, 24 * 60);

        clock = ClockTime(22, 0).AddSeconds(10);
        var occ = Assert.Single(engine.Evaluate(new[] { entry }));
        Assert.Equal(24 * 60, occ.Entry.EndMin);

        // 23:30 再次评估（课程未结束）→ 一天只触发一次
        clock = ClockTime(23, 30);
        Assert.Empty(engine.Evaluate(new[] { entry }));
    }
}
