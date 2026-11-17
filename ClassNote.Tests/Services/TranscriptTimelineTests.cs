using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 时间轴（<see cref="TranscriptTimeline"/>）与"截图按时间归属"的回归测试（P0-β）。
///
/// 这组测试锁住两件事：
/// 1. **对齐本身**：截图必须落进时间上真正覆盖它的那一段（而不是按序号均摊）；
/// 2. **退化行为**：拿不到块信息 / 块与文本对不上 / 时间轴不可用时，必须精确退回旧行为，
///    绝不能因为"对齐失败"就把截图弄丢或让分段崩掉。
/// </summary>
public class TranscriptTimelineTests
{
    /// <summary>造一路转写：每块固定 <paramref name="charsPerChunk"/> 个字，便于推算字符↔时间。</summary>
    private static TrackTranscript Track(int chunks, int charsPerChunk, RecordingAudioSource source = RecordingAudioSource.Microphone)
    {
        var texts = new List<string>();
        var starts = new List<int>();
        for (int i = 0; i < chunks; i++)
        {
            texts.Add(new string('字', charsPerChunk));
            starts.Add(i * 30);
        }
        return new TrackTranscript(source, texts, starts);
    }

    [Fact]
    public void Build_MapsCharacterOffsetsToChunkTimes()
    {
        // 4 块 × 每块 100 字 → 每 100 字推进 30 秒
        var timeline = TranscriptTimeline.Build(Track(4, 100), expectedTextLength: 400);

        Assert.NotNull(timeline);
        Assert.Equal(0, timeline!.TimeAtOffset(0));
        Assert.Equal(0, timeline.TimeAtOffset(99));
        Assert.Equal(30, timeline.TimeAtOffset(100));
        Assert.Equal(60, timeline.TimeAtOffset(250));
        Assert.Equal(90, timeline.TimeAtOffset(399));
    }

    [Fact]
    public void Build_RefusesWhenChunkRecordsDoNotMatchTheText()
    {
        // 块文本总长 400 而实际文本 350：说明库里缺过块、文本是别的方式拼的。
        // 这时任何"按字符推断时间"都是在编数据 → 必须拒绝。
        var timeline = TranscriptTimeline.Build(Track(4, 100), expectedTextLength: 350);

        Assert.Null(timeline);
    }

    [Fact]
    public void TimeAtOffset_ReturnsNullBeyondCoverage()
    {
        // 超出对照表范围时**不猜**：宁可让调用方退回均摊，也不要凭"大概是末尾"硬塞
        var timeline = TranscriptTimeline.Build(Track(2, 100), expectedTextLength: 200);

        Assert.Null(timeline!.TimeAtOffset(500));
        Assert.Null(timeline.TimeAtOffset(-1));
    }

    [Fact]
    public void TimeRangeFor_CoversWholeChunksTouchedByTheSegment()
    {
        var timeline = TranscriptTimeline.Build(Track(4, 100), expectedTextLength: 400)!;

        // 段覆盖第 1~2 块（字符 100..300）→ 时间 30..90
        var range = timeline.TimeRangeFor(100, 300);

        Assert.NotNull(range);
        Assert.Equal(30, range!.Value.StartSeconds);
        Assert.Equal(90, range.Value.EndSeconds);
    }

    [Fact]
    public void AssignOcr_PutsEachScreenshotIntoTheSegmentThatCoversItsTime()
    {
        // 3 段，每段转写 200 字（对应 60 秒），时间轴每 100 字 = 30 秒
        var timeline = TranscriptTimeline.Build(Track(6, 100), expectedTextLength: 600)!;
        var windows = new List<(int Start, int End)> { (0, 200), (200, 400), (400, 600) };

        var blocks = new List<OcrBlock>
        {
            new("[new_slide @ 00:00:05]", "第一页", 5),
            new("[new_slide @ 00:01:35]", "第四页", 95),    // 95 秒 → 第 2 段（60..120）
            new("[new_slide @ 00:02:05]", "第五页", 125),   // 125 秒 → 第 3 段（120..180）
        };

        var buckets = NoteSegmenter.AssignOcr(blocks, windows, 3, timeline);

        Assert.Equal(new[] { "第一页" }, buckets[0].Select(b => b.Content));
        Assert.Equal(new[] { "第四页" }, buckets[1].Select(b => b.Content));
        Assert.Equal(new[] { "第五页" }, buckets[2].Select(b => b.Content));
    }

    [Fact]
    public void AssignOcr_WithoutTimeline_FallsBackToRoundRobin()
    {
        var windows = new List<(int Start, int End)> { (0, 100), (100, 200), (200, 300) };
        var blocks = new List<OcrBlock>
        {
            new("[new_slide @ 00:00:05]", "A", 5),
            new("[new_slide @ 00:01:35]", "B", 95),
            new("[new_slide @ 00:02:05]", "C", 125),
        };

        var buckets = NoteSegmenter.AssignOcr(blocks, windows, 3, timeline: null);

        // 旧的"按序号均摊"：3 张图 → 一段一张
        Assert.Single(buckets[0]);
        Assert.Single(buckets[1]);
        Assert.Single(buckets[2]);
    }

    [Fact]
    public void AssignOcr_ReflowsScreenshotsThatWouldOverflowOneSegment()
    {
        // 老师连续翻页：3 张都很长的截图挤在同一段时间里，而转写均匀分成 3 段。
        // 直接按时间分配会把它们全压进第 1 段、撑破单段输入上限 → 必须有容量回填。
        var timeline = TranscriptTimeline.Build(Track(6, 100), expectedTextLength: 600)!;
        var windows = new List<(int Start, int End)> { (0, 200), (200, 400), (400, 600) };
        string big = new('内', 4000);
        var blocks = new List<OcrBlock>
        {
            new("[new_slide @ 00:00:05]", big, 5),
            new("[new_slide @ 00:00:10]", big, 10),
            new("[new_slide @ 00:00:15]", big, 15),
        };

        var buckets = NoteSegmenter.AssignOcr(blocks, windows, 3, timeline);

        // 一张都没丢，且没有任何一段超过单段上限
        Assert.Equal(3, buckets.Sum(b => b.Count));
        foreach (var bucket in buckets)
            Assert.True(bucket.Sum(b => b.Content.Length) <= NoteSegmenter.DefaultMaxCharsPerSegment,
                "任何一段都不应超过单段上限");
    }

    [Fact]
    public void AssignOcr_KeepsScreenshotsTakenAfterTheLastSegment()
    {
        // 录音结束后又截了一张图（时间超出所有段）→ 归入最后一节有时间区间的段，绝不丢弃
        var timeline = TranscriptTimeline.Build(Track(2, 100), expectedTextLength: 200)!;
        var windows = new List<(int Start, int End)> { (0, 100), (100, 200) };

        var blocks = new List<OcrBlock> { new("[new_slide @ 00:59:00]", "收尾页", 3540) };

        var buckets = NoteSegmenter.AssignOcr(blocks, windows, 2, timeline);

        Assert.Equal("收尾页", buckets[1].Single().Content);
    }

    [Fact]
    public void BuildForSession_ReturnsNullWhenRepositoryThrows()
    {
        var timeline = TranscriptTimeline.BuildForSession(Guid.NewGuid(), new ThrowingChunkStore());

        Assert.Null(timeline);
    }

    /// <summary>读库失败时的替身：时间轴必须安静退化，而不是把异常抛进笔记管线。</summary>
    private sealed class ThrowingChunkStore : ITranscriptChunkStore
    {
        public List<(int Index, string Text)> ListTranscriptChunks(Guid sessionId, RecordingAudioSource source)
            => throw new InvalidOperationException("库不可用");

        public void SaveTranscriptChunk(Guid sessionId, RecordingAudioSource source, int chunkIndex, int startSeconds, string text)
            => throw new InvalidOperationException("库不可用");
    }
}
