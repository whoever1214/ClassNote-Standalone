using ClassNote.Models;
using ClassNote.Views;
using Xunit;

namespace ClassNote.Tests.Views;

/// <summary>
/// 每周课表网格几何（<see cref="ScheduleGridLayout"/>）单测。
/// 核心不变量：同一列内任意两个课块**不得相交**（BUG-2：相邻 10 分钟的两节课会部分重叠），
/// 且块高按课程真实时长等比换算。
/// </summary>
public class ScheduleGridLayoutTests
{
    private const int RangeStart = 5 * 60;    // 05:00
    private const int RangeEnd = 22 * 60;     // 22:00
    private const double HourPx = 32;

    private static IReadOnlyList<ScheduleBlockBar> Bars(params (int Start, int End)[] entries)
        => ScheduleGridLayout.ComputeDayBars(entries, RangeStart, RangeEnd, HourPx);

    /// <summary>断言同一列内没有任何两个课块矩形相交。</summary>
    private static void AssertNoOverlap(IReadOnlyList<ScheduleBlockBar> bars)
    {
        var drawn = bars.Where(b => b.IsVisible).OrderBy(b => b.Top).ToList();
        for (var i = 1; i < drawn.Count; i++)
        {
            var prevBottom = drawn[i - 1].Top + drawn[i - 1].Height;
            Assert.True(prevBottom <= drawn[i].Top + 0.001,
                $"课块重叠：上一块底部 {prevBottom:F2} > 下一块顶部 {drawn[i].Top:F2}");
        }
    }

    [Fact]
    public void AdjacentTenMinuteEntries_DoNotOverlapAndStayVisible()
    {
        // 三节相邻的 10 分钟短课：时间轴上每节仅约 5.3px，最容易被"最小高度"顶到下一节里
        var bars = Bars((10 * 60, 10 * 60 + 10), (10 * 60 + 10, 10 * 60 + 20), (10 * 60 + 20, 10 * 60 + 30));

        AssertNoOverlap(bars);
        Assert.All(bars, b => Assert.True(b.IsVisible, "短课块也应有可见高度"));
        // 每块都必须落在自己的 10 分钟时间格内（下一格的顶边之上）
        var cellPx = 10 * HourPx / 60.0;
        var firstTop = bars[0].Top;
        Assert.True(bars[1].Top >= firstTop + cellPx - 0.001, "第二节课块不得越到第一节的时间格内");
        Assert.True(bars[0].Top + bars[0].Height <= bars[1].Top, "第一节底部不得压到第二节顶边");
    }

    [Fact]
    public void FortyFiveMinuteClassWithTenMinuteBreak_DoesNotOverlapNext()
    {
        // 用户报告场景：08:00–08:45 与 08:55–09:40（课间 10 分钟）
        var bars = Bars((8 * 60, 8 * 60 + 45), (8 * 60 + 55, 9 * 60 + 40));

        AssertNoOverlap(bars);
        Assert.All(bars, b => Assert.True(b.IsVisible));
    }

    [Fact]
    public void BackToBackLongClasses_DoNotOverlap()
    {
        var bars = Bars((8 * 60, 9 * 60 + 30), (9 * 60 + 30, 11 * 60));

        AssertNoOverlap(bars);
        Assert.All(bars, b => Assert.True(b.IsVisible));
    }

    [Fact]
    public void HeightIsProportionalToDuration()
    {
        var bars = Bars((8 * 60, 8 * 60 + 10), (10 * 60, 10 * 60 + 45), (14 * 60, 15 * 60 + 30));

        var h10 = bars[0].Height;
        var h45 = bars[1].Height;
        var h90 = bars[2].Height;

        // 45 分钟 ≈ 24px、90 分钟 ≈ 48px（内缩后分别为 19px / 43px）
        Assert.InRange(h45, 18.0, 20.0);
        Assert.InRange(h90, 42.0, 44.0);
        Assert.True(h90 > h45 + 20, "90 分钟的块应明显高于 45 分钟的块");
        // 10 分钟短课：后面长时间空闲 → 撑到可读下限；真正退化成细条的场景见"相邻短课"用例
        Assert.InRange(h10, ScheduleGridLayout.PreferredMinHeight, 20.0);

        // 相邻的 10 分钟短课（无空闲）→ 细条，且绝不越过下一节顶边
        var tight = Bars((10 * 60, 10 * 60 + 10), (10 * 60 + 10, 10 * 60 + 20));
        Assert.InRange(tight[0].Height, 1.0, 6.0);
    }

    [Fact]
    public void ShortClassWithFreeTime_ExpandsToReadableMinimum()
    {
        // 10:00–10:10 之后长时间空闲 → 撑到可读下限高度（18px），而不是细条
        var bars = Bars((10 * 60, 10 * 60 + 10));

        Assert.True(bars[0].IsVisible);
        Assert.InRange(bars[0].Height, ScheduleGridLayout.PreferredMinHeight, 60.0);
    }

    [Fact]
    public void EntriesOutsideTimeline_AreNotDrawn()
    {
        var bars = Bars((4 * 60, 4 * 60 + 45), (22 * 60 + 30, 23 * 60));

        Assert.All(bars, b => Assert.False(b.IsVisible, "时间轴（05:00–22:00）外的条目不绘制"));
    }

    [Fact]
    public void EntryStraddlingRangeStart_IsClippedWithoutOverlap()
    {
        // 04:30–05:30（跨时间轴起点）与 05:40–06:30
        var bars = Bars((4 * 60 + 30, 5 * 60 + 30), (5 * 60 + 40, 6 * 60 + 30));

        AssertNoOverlap(bars);
        Assert.All(bars, b => Assert.True(b.IsVisible));
        Assert.True(bars[0].Top >= 0, "裁剪后的块不得超出画布顶部");
    }

    [Fact]
    public void DegenerateSameSlotEntries_NeverRenderOverlappingBlocks()
    {
        // 数据退化（同一天同一时段两条）时也不允许画出相交色块
        var bars = Bars((10 * 60, 10 * 60 + 30), (10 * 60, 10 * 60 + 30));

        AssertNoOverlap(bars);
        Assert.True(bars.Count(b => b.IsVisible) <= 1);
    }

    [Fact]
    public void AllBars_StayInsideCanvas_AndKeepGap()
    {
        var bars = Bars((5 * 60, 5 * 60 + 1), (5 * 60 + 10, 5 * 60 + 50), (6 * 60, 12 * 60),
            (12 * 60, 12 * 60 + 1), (13 * 60, 13 * 60 + 45), (21 * 60, 21 * 60 + 55), (21 * 60 + 59, 22 * 60));
        var canvasHeight = (RangeEnd - RangeStart) * HourPx / 60.0;

        AssertNoOverlap(bars);
        Assert.All(bars.Where(b => b.IsVisible), b =>
        {
            Assert.True(b.Top >= 0, "块顶不得超出画布顶部");
            Assert.True(b.Height > 0);
        });
        // 除最后一块（尾部无后续节次，可撑到可读下限并被画布裁掉）外都应落在画布高度内
        Assert.All(bars.Take(bars.Count - 1).Where(b => b.IsVisible),
            b => Assert.True(b.Top + b.Height <= canvasHeight + 0.001));
    }

    [Fact]
    public void AnyDurationAndGap_NeverProducesOverlap()
    {
        // 穷举任意时长（1–240 分钟）与任意课间（0–60 分钟）的组合：绝不重叠
        for (var duration = 1; duration <= 240; duration++)
        {
            for (var gap = 0; gap <= 60; gap += 5)
            {
                var start = 8 * 60;
                var bars = Bars((start, start + duration), (start + duration + gap, start + duration + gap + duration));
                AssertNoOverlap(bars);
            }
        }
    }

    [Fact]
    public void SampleWeek_HasNoOverlapInAnyDay()
    {
        var entries = new List<ScheduleEntry>();
        void Add(int day, int s, int e, string course, bool enabled = true)
            => entries.Add(new ScheduleEntry
            {
                WeekdayIndex = day, StartMin = s, EndMin = e, Course = course, Enabled = enabled,
            });
        Add(0, 8 * 60, 8 * 60 + 45, "数学");
        Add(0, 9 * 60, 9 * 60 + 45, "英语", enabled: false);
        Add(0, 10 * 60 + 15, 11 * 60, "物理");
        Add(1, 8 * 60, 9 * 60 + 30, "语文");
        Add(1, 13 * 60 + 30, 14 * 60 + 15, "化学");
        Add(2, 8 * 60, 8 * 60 + 45, "数学");
        Add(2, 9 * 60, 10 * 60, "生物");
        Add(3, 14 * 60, 15 * 60 + 30, "历史");
        Add(4, 8 * 60, 9 * 60 + 30, "英语");
        Add(4, 15 * 60 + 30, 17 * 60, "自习", enabled: false);
        Add(5, 10 * 60, 10 * 60 + 10, "短课A");
        Add(5, 10 * 60 + 10, 10 * 60 + 20, "短课B");
        Add(5, 10 * 60 + 20, 10 * 60 + 30, "短课C");
        Add(6, 8 * 60, 8 * 60 + 45, "数学");
        Add(6, 8 * 60 + 55, 9 * 60 + 40, "英语");

        for (var day = 0; day < 7; day++)
        {
            var dayEntries = entries.Where(e => e.WeekdayIndex == day)
                .OrderBy(e => e.StartMin).Select(e => (e.StartMin, e.EndMin)).ToList();
            var bars = ScheduleGridLayout.ComputeDayBars(dayEntries, RangeStart, RangeEnd, HourPx);
            AssertNoOverlap(bars);
            Assert.Equal(dayEntries.Count, bars.Count);
        }
    }
}
