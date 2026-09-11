namespace ClassNote.Views;

/// <summary>课程块在"某一天"时间轴画布上的竖向几何（像素）：距画布顶部的位置与高度。</summary>
/// <param name="Top">块顶边距画布顶部的像素。</param>
/// <param name="Height">块高度像素；0 表示该条目不绘制（完全落在时间轴之外或数据退化）。</param>
public readonly record struct ScheduleBlockBar(double Top, double Height)
{
    /// <summary>是否可绘制（高度为 0 的退化块不绘制）。</summary>
    public bool IsVisible => Height > 0;
}

/// <summary>
/// 每周课表网格的课程块几何计算（纯函数：无 UI / 无存储依赖，可单测）。
///
/// 规则（顺序即优先级）：
///   1. 高度按课程真实时长等比换算（<see cref="HourPx"/> 像素 / 小时），与时间轴刻度一致；
///   2. 相邻两节的块之间至少保留 <see cref="MinGap"/> 间隙——**本块底部绝不越过下一节的顶边**，
///      因此再紧凑的课表（如相邻 10 分钟的短课）也不会出现"上一节色块盖住下一节"的视觉重叠；
///   3. 空闲时间足够时，短课块最多撑到 <see cref="PreferredMinHeight"/>（可读性下限）；
///      空闲不足时（相邻节次）以"不越界"优先，块退化为细条。
///
/// 内缩与间隙都按该节时长的比例收缩（上限 <see cref="MaxInset"/> / <see cref="MinGap"/>），
/// 保证极短课程（10 分钟以内）也仍有可见高度，而不是被固定的内缩量吃掉。
/// </summary>
public static class ScheduleGridLayout
{
    /// <summary>块相对时间格上下最多内缩的像素（极短课程按比例减小）。</summary>
    public const double MaxInset = 3.0;

    /// <summary>相邻两节课块之间保留的最小间隙（像素）。</summary>
    public const double MinGap = 2.0;

    /// <summary>可读性下限高度：空闲时间足够时短课块撑到该高度。</summary>
    public const double PreferredMinHeight = 18.0;

    /// <summary>块内文字的上下内缩比例与间隙比例（相对该节总高度）。</summary>
    private const double InsetRatio = 0.20;
    private const double GapRatio = 0.12;

    /// <summary>
    /// 计算某一天全部课程块的竖向几何。返回数组与 <paramref name="entries"/> 一一对应
    /// （调用方按索引取用即可）；<see cref="ScheduleBlockBar.IsVisible"/> 为 false 的条目不绘制。
    /// </summary>
    /// <param name="entries">当天的条目起止时刻（分钟），需按开始时刻升序排列。</param>
    /// <param name="rangeStart">时间轴起点（分钟）。</param>
    /// <param name="rangeEnd">时间轴终点（分钟）。</param>
    /// <param name="hourPx">每小时对应的像素高度。</param>
    public static IReadOnlyList<ScheduleBlockBar> ComputeDayBars(
        IReadOnlyList<(int StartMin, int EndMin)> entries, int rangeStart, int rangeEnd, double hourPx)
    {
        var bars = new ScheduleBlockBar[entries.Count];
        if (hourPx <= 0 || rangeEnd <= rangeStart)
            return bars;

        double Px(int minute) => (minute - rangeStart) * hourPx / 60.0;

        // 只绘制与 [rangeStart, rangeEnd] 有交集的条目；其余留空（定时触发不受影响）
        var visible = new List<int>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].EndMin > rangeStart && entries[i].StartMin < rangeEnd)
                visible.Add(i);

        for (var k = 0; k < visible.Count; k++)
        {
            var index = visible[k];
            var (startMin, endMin) = entries[index];

            var cellTop = Px(Math.Max(startMin, rangeStart));
            var cell = Px(Math.Min(endMin, rangeEnd)) - cellTop;   // 该节在时间轴上的真实高度
            if (cell <= 0)
                continue;

            // 内缩/间隙按时长等比收缩：极短课程不被固定内缩吃掉
            var inset = Math.Min(MaxInset, cell * InsetRatio);
            var gap = Math.Min(MinGap, cell * GapRatio);

            var top = cellTop + inset;
            var height = Math.Max(cell - inset - gap, PreferredMinHeight);

            // 下一节的顶边是本块的硬上界：最小高度也不越界（下一节自身还会再内缩 inset，间隙只会更大）
            if (k + 1 < visible.Count)
            {
                var nextTop = Px(Math.Max(entries[visible[k + 1]].StartMin, rangeStart));
                height = Math.Min(height, nextTop - gap - top);
            }

            // 高度非正说明数据退化（同一天同一时刻有重叠条目）：不绘制，避免产生重叠色块
            if (height <= 0)
                continue;

            bars[index] = new ScheduleBlockBar(top, height);
        }
        return bars;
    }
}
