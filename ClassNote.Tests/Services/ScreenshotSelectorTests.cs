using System;
using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 选图算法（真实数据实测：一节课平均 67.6 张、73% 的相邻帧是同一屏的重复）。
/// 这里的测试守的是三条不可退让的语义：
/// 1. 同一屏多帧只留**最后一帧**（批注逐步加上去，尾帧信息最全）；
/// 2. 上限生效时首帧/末帧/new_slide 必须优先保住（不能让笔记只覆盖前半节课）；
/// 3. 任何情况下都不丢"所有图都被判为空白"这一极端场景的最后一张。
/// </summary>
public class ScreenshotSelectorTests
{
    private static readonly ImageStats Normal = new(0.10, 40, 1920, 1080);
    private static readonly ImageStats Blank = new(0.99, 2, 1920, 1080);

    private static ScreenshotCandidate Cand(int seq, ulong hash, string type = "annotation", ImageStats? stats = null)
        => new(seq, seq * 10.0, type, $@"C:\shots\{seq:0000}.jpg", hash, stats ?? Normal);

    private static ulong Far(ulong baseHash) => baseHash ^ 0xFFFFFFFFFFFFFFFFUL; // 距离 64

    /// <summary>第 i 帧的"明显不同"的哈希：乘上奇常数打散，相邻帧汉明距离期望 32（远大于阈值 8）。</summary>
    private static ulong Distinct(int i) => unchecked((ulong)i * 0x9E3779B97F4A7C15UL) | 0x8000000000000000UL;

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var result = ScreenshotSelector.Select(Array.Empty<ScreenshotCandidate>());
        Assert.Empty(result.Kept);
        Assert.Equal(0, result.Stats.Total);
    }

    [Fact]
    public void SameScreen_KeepsOnlyLastFrame()
    {
        // 5 帧几乎一样（距离 2），应当只保留第 5 帧
        ulong h = 0x0000_0000_0000_00FFUL;
        var candidates = new[]
        {
            Cand(1, h), Cand(2, h ^ 1), Cand(3, h ^ 3), Cand(4, h ^ 1), Cand(5, h),
        };

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 30);

        Assert.Single(result.Kept);
        Assert.Equal(5, result.Kept[0].SeqNo);
        Assert.Equal(4, result.Stats.DroppedDuplicate);
    }

    [Fact]
    public void ChangedScreen_StartsNewCluster()
    {
        ulong a = 0x0000_0000_0000_00FFUL;
        var candidates = new[]
        {
            Cand(1, a), Cand(2, a ^ 1),          // 第 1 屏
            Cand(3, Far(a)), Cand(4, Far(a) ^ 1),// 第 2 屏
        };

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 30);

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(new[] { 2, 4 }, result.Kept.Select(c => c.SeqNo).ToArray());
    }

    [Fact]
    public void BlankFrames_AreDropped()
    {
        var candidates = new[]
        {
            Cand(1, 0x1UL, stats: Blank),
            Cand(2, 0x100UL),
        };

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 30);

        Assert.Single(result.Kept);
        Assert.Equal(2, result.Kept[0].SeqNo);
        Assert.Equal(1, result.Stats.DroppedBlank);
    }

    [Fact]
    public void AllBlank_StillKeepsOneImage()
    {
        var candidates = new[]
        {
            Cand(1, 0x1UL, stats: Blank),
            Cand(2, 0x2UL, stats: Blank),
        };

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 30);

        // 全空白时退回原始序列：语义是"这一节有截图"，不能因为都空白就变成"没有"
        Assert.NotEmpty(result.Kept);
    }

    [Fact]
    public void Cap_KeepsFirstAndLastAndRespectsLimit()
    {
        var candidates = Enumerable.Range(1, 100)
            .Select(i => Cand(i, Distinct(i)))
            .ToArray();

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 10);

        Assert.Equal(10, result.Kept.Count);
        Assert.Contains(result.Kept, c => c.SeqNo == 1);
        Assert.Contains(result.Kept, c => c.SeqNo == 100);
        Assert.Equal(result.Kept.Select(c => c.SeqNo).OrderBy(x => x), result.Kept.Select(c => c.SeqNo));
    }

    [Fact]
    public void Cap_PrefersNewSlideFrames()
    {
        var candidates = Enumerable.Range(1, 60)
            .Select(i => Cand(i, Distinct(i), i % 20 == 0 ? "new_slide" : "annotation"))
            .ToArray();

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 8);

        Assert.Equal(8, result.Kept.Count);
        // 20/40/60 都是 new_slide，必须进保留集
        Assert.Contains(result.Kept, c => c.SeqNo == 20);
        Assert.Contains(result.Kept, c => c.SeqNo == 40);
    }

    [Fact]
    public void NegativeThresholdAndZeroCap_AreClamped()
    {
        var candidates = new[] { Cand(1, 0x1UL), Cand(2, Far(0x1UL)) };

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: -5, maxImages: 0);

        Assert.Single(result.Kept);   // maxImages 按 1 处理
    }

    [Fact]
    public void Stats_AddUp()
    {
        ulong h = 0xAAUL;
        var candidates = new[]
        {
            Cand(1, h, stats: Blank),
            Cand(2, h), Cand(3, h ^ 1),
            Cand(4, Far(h)),
        };

        var result = ScreenshotSelector.Select(candidates, dedupThreshold: 8, maxImages: 30);

        Assert.Equal(4, result.Stats.Total);
        Assert.Equal(1, result.Stats.DroppedBlank);
        Assert.Equal(1, result.Stats.DroppedDuplicate); // 第 2、3 帧同屏，留第 3 帧
        Assert.Equal(2, result.Stats.Kept);
    }
}
