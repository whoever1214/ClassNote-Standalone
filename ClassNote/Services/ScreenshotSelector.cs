using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassNote.Services;

/// <summary>选图算法的输入：一帧截图的"身份 + 指纹 + 有用性统计"。</summary>
/// <param name="SeqNo">会话内序号（升序即时间顺序）。</param>
/// <param name="TimestampSeconds">录音时刻（秒）。</param>
/// <param name="Type">annotation / new_slide / video。</param>
/// <param name="ImagePath">原图路径（返回给调用方去加载）。</param>
/// <param name="Hash">pHash（见 <see cref="PerceptualHash"/>）。</param>
/// <param name="Stats">像素统计（判断是否近空白）。</param>
public sealed record ScreenshotCandidate(
    int SeqNo, double TimestampSeconds, string Type, string ImagePath, ulong Hash, ImageStats Stats);

/// <summary>选图结果统计：既给用户看"省了多少"，也是回归测试的断言依据。</summary>
public sealed record SelectionStats(
    int Total, int DroppedBlank, int DroppedDuplicate, int DroppedOverflow, int Kept)
{
    /// <summary>"共 86 张，去重掉 60 张、空白 2 张、超出上限 4 张，保留 20 张"。</summary>
    public string Describe()
        => string.Format("共 {0} 张截图：去重 {1}、空白 {2}、超出上限 {3}，送模型 {4} 张",
            Total, DroppedDuplicate, DroppedBlank, DroppedOverflow, Kept);
}

public sealed record SelectionResult(IReadOnlyList<ScreenshotCandidate> Kept, SelectionStats Stats);

/// <summary>
/// 截图留存/送模算法（**纯函数，可单测**）。
///
/// 背景（用真实数据量出来的，见 docs/截图选图-实测.md）：一节课平均 67.6 张、最多 125 张，
/// 其中 73% 的相邻帧 pHash 距离 ≤8（同一屏继续写字），逐帧全送既昂贵又几乎没有新信息。
///
/// 三条规则：
/// 1. **空白丢弃**：近空白页（白板/黑屏/纯色）对笔记没有价值；
/// 2. **变化点切分 + 簇尾保留**：与上一帧距离 ≤ 阈值视为"同一屏"，只保留这一簇的**最后一帧**
///    —— 它包含该屏最终状态（批注是逐步加上去的，首帧反而信息最少）；
/// 3. **上限**：超过 <c>maxImages</c> 时，优先保留首帧、末帧与所有 new_slide 簇尾，其余按时间均匀抽样。
/// </summary>
public static class ScreenshotSelector
{
    /// <summary>默认去重阈值（汉明距离，64 位哈希）。实测：≤8 覆盖 73% 的相邻帧。</summary>
    public const int DefaultDedupThreshold = 8;

    /// <summary>单节课默认最多送几张。</summary>
    public const int DefaultMaxImages = 30;

    /// <summary>
    /// 选出要送进模型的截图（按 <see cref="ScreenshotCandidate.SeqNo"/> 升序返回）。
    /// 永不抛异常，永不返回 null；输入为空时返回空结果。
    /// </summary>
    public static SelectionResult Select(
        IReadOnlyList<ScreenshotCandidate>? candidates,
        int dedupThreshold = DefaultDedupThreshold,
        int maxImages = DefaultMaxImages)
    {
        if (candidates == null || candidates.Count == 0)
            return new SelectionResult(Array.Empty<ScreenshotCandidate>(),
                new SelectionStats(0, 0, 0, 0, 0));

        if (dedupThreshold < 0) dedupThreshold = 0;
        if (dedupThreshold > PerceptualHash.HashBits) dedupThreshold = PerceptualHash.HashBits;
        if (maxImages < 1) maxImages = 1;

        var ordered = candidates
            .Where(c => c != null)
            .OrderBy(c => c.SeqNo)
            .ToList();

        // 1. 空白页
        var nonBlank = new List<ScreenshotCandidate>(ordered.Count);
        int blank = 0;
        foreach (var c in ordered)
        {
            if (c.Stats.IsNearlyBlank)
            {
                blank++;
                continue;
            }
            nonBlank.Add(c);
        }

        // 全空白时退回原始序列（宁可给一张空白图，也不要让"这一节有没有图"的语义变成"没有"）
        if (nonBlank.Count == 0)
        {
            nonBlank = ordered;
            blank = 0;
        }

        // 2. 变化点切分：与上一帧比，超过阈值就是新的一屏
        var clusters = new List<List<ScreenshotCandidate>>();
        var current = new List<ScreenshotCandidate> { nonBlank[0] };
        for (int i = 1; i < nonBlank.Count; i++)
        {
            var prev = nonBlank[i - 1];
            var cur = nonBlank[i];
            if (PerceptualHash.Distance(prev.Hash, cur.Hash) > dedupThreshold)
            {
                clusters.Add(current);
                current = new List<ScreenshotCandidate>();
            }
            current.Add(cur);
        }
        clusters.Add(current);

        // 簇尾代表：该屏的最终状态
        var representatives = clusters.Select(c => c[c.Count - 1]).ToList();
        int duplicate = nonBlank.Count - representatives.Count;

        // 3. 上限
        int overflow = 0;
        if (representatives.Count > maxImages)
        {
            var capped = CapPreservingKeyFrames(representatives, maxImages);
            overflow = representatives.Count - capped.Count;
            representatives = capped;
        }

        return new SelectionResult(representatives,
            new SelectionStats(ordered.Count, blank, duplicate, overflow, representatives.Count));
    }

    /// <summary>
    /// 截断到 <paramref name="maxImages"/> 张：首帧、末帧、new_slide 簇尾优先，其余按时间均匀抽样。
    /// 抽样的作用不是"保真"，而是让笔记不至于只覆盖前半节课——丢掉的都是同一屏的不同时刻。
    /// </summary>
    private static List<ScreenshotCandidate> CapPreservingKeyFrames(
        IReadOnlyList<ScreenshotCandidate> items, int maxImages)
    {
        var mustKeep = new List<ScreenshotCandidate> { items[0], items[items.Count - 1] };
        for (int i = 1; i < items.Count - 1; i++)
            if (string.Equals(items[i].Type, "new_slide", StringComparison.OrdinalIgnoreCase))
                mustKeep.Add(items[i]);

        // 去重（首/末可能是同一个元素）
        mustKeep = mustKeep.Distinct().ToList();

        if (mustKeep.Count > maxImages)
        {
            // 关键帧本身就超限：只按时间均匀抽样关键帧，首帧一定留下
            var evenly = SampleEvenly(mustKeep, maxImages);
            return evenly.OrderBy(c => c.SeqNo).ToList();
        }

        int remaining = maxImages - mustKeep.Count;
        var others = items.Where(i => !mustKeep.Contains(i)).ToList();
        var rnd = SampleEvenly(others, remaining);

        var result = mustKeep.Concat(rnd).OrderBy(c => c.SeqNo).ToList();
        return result;
    }

    /// <summary>按时间均匀取 <paramref name="count"/> 个（保持原顺序，不重复）。</summary>
    private static List<ScreenshotCandidate> SampleEvenly(IReadOnlyList<ScreenshotCandidate> items, int count)
    {
        if (count <= 0 || items.Count == 0) return new List<ScreenshotCandidate>();
        if (count >= items.Count) return items.ToList();

        var picked = new List<ScreenshotCandidate>(count);
        double step = (double)items.Count / count;
        for (int i = 0; i < count; i++)
        {
            int idx = Math.Min(items.Count - 1, (int)Math.Round(i * step));
            picked.Add(items[idx]);
        }

        // 浮点取整可能撞车：补齐（保持不重复）
        for (int i = 0; picked.Count < count && i < items.Count; i++)
            if (!picked.Contains(items[i]))
                picked.Add(items[i]);

        return picked.Take(count).OrderBy(c => c.SeqNo).ToList();
    }
}
