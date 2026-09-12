using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

public class NoteSegmenterTests
{
    [Fact]
    public void Build_ShortMaterial_SingleSegmentNotFlaggedAsSegmented()
    {
        var plan = NoteSegmenter.Build("高等数学", "这是一段很短的转写。", "[new_slide @ 00:00:10]\n极限的定义");

        Assert.False(plan.IsSegmented);
        Assert.Single(plan.Segments);
        Assert.Equal(0, plan.OversizedChars);
        Assert.Contains("这是一段很短的转写。", plan.Segments[0].Transcript);
        Assert.Contains("极限的定义", plan.Segments[0].OcrText);
    }

    [Fact]
    public void Build_LongTranscript_SplitsWithOverlapAndKeepsEveryCharacter()
    {
        // 20000 字 → 单段上限 6000、重叠 400 → 步长 5600 → 4 段，且能完整还原
        string transcript = BuildText(20000);
        var plan = NoteSegmenter.Build("线性代数", transcript, "");

        Assert.True(plan.IsSegmented);
        Assert.Equal(4, plan.Segments.Count);
        Assert.Equal(0, plan.OversizedChars);
        Assert.Equal(20000, plan.TotalTranscriptChars);

        // 段首与上一段尾部重叠：跨边界的知识点至少能在一个完整窗口里出现
        for (int i = 1; i < plan.Segments.Count; i++)
        {
            var prev = plan.Segments[i - 1];
            var cur = plan.Segments[i];
            Assert.True(cur.StartChar < prev.EndChar, "相邻段之间必须存在重叠");
            Assert.Equal(
                transcript.Substring(cur.StartChar, prev.EndChar - cur.StartChar),
                prev.Transcript[^(prev.EndChar - cur.StartChar)..]);
        }

        // 末段结束于转写末尾，整体无空洞
        Assert.Equal(20000, plan.Segments[^1].EndChar);
        Assert.Empty(plan.Segments[0].OcrText);
    }

    [Fact]
    public void Build_OcrEntriesAreDistributedOnceAcrossSegments()
    {
        string transcript = BuildText(12000); // 12000 / 5600 → 3 段
        var ocr = new StringBuilder();
        for (int i = 1; i <= 9; i++)
        {
            ocr.AppendLine($"[new_slide @ 00:{i:00}:00]");
            ocr.AppendLine($"第 {i} 页知识点内容 ABC{i}");
        }

        var plan = NoteSegmenter.Build("计算机网络", transcript, ocr.ToString());

        Assert.Equal(3, plan.Segments.Count);
        var all = string.Join("\n", plan.Segments.Select(s => s.OcrText));
        // 每页只出现一次：既不重复塞进每一段，也不丢页
        for (int i = 1; i <= 9; i++)
            Assert.Equal(1, CountOccurrences(all, $"第 {i} 页知识点内容 ABC{i}"));
        Assert.Equal(9, plan.Segments.Sum(s => CountOccurrences(s.OcrText, "[new_slide")));
    }

    [Fact]
    public void Build_ShortTranscriptWithManySlides_DoesNotDuplicateOcr()
    {
        // 转写很短但 PPT 很多：按序号均摊，不应把全部 OCR 重复塞进唯一一段
        string transcript = BuildText(100);
        var ocr = new StringBuilder();
        for (int i = 1; i <= 40; i++)
        {
            ocr.AppendLine($"[new_slide @ 00:{i:00}:00]");
            ocr.AppendLine($"第 {i} 页内容 ABC{i}");
        }

        var plan = NoteSegmenter.Build("数据结构", transcript, ocr.ToString());

        Assert.Single(plan.Segments);
        Assert.Equal(40, CountOccurrences(plan.Segments[0].OcrText, "[new_slide"));
    }

    [Fact]
    public void Build_NoTranscript_SingleSegmentWithOcr()
    {
        var plan = NoteSegmenter.Build("大学物理", null, "[annotation @ 12:30]\n麦克斯韦方程组");

        Assert.Single(plan.Segments);
        Assert.Equal("", plan.Segments[0].Transcript);
        Assert.Contains("麦克斯韦方程组", plan.Segments[0].OcrText);
    }

    [Fact]
    public void Build_EmptyMaterial_ReturnsOneEmptySegment()
    {
        var plan = NoteSegmenter.Build("", null, null);

        Assert.Single(plan.Segments);
        Assert.Equal("", plan.Segments[0].Transcript);
        Assert.Equal("", plan.Segments[0].OcrText);
        Assert.Equal(0, plan.TotalTranscriptChars);
    }

    [Fact]
    public void Build_BeyondSegmentCap_KeepsEverythingAndReportsOversizedTail()
    {
        // 上限 2 段 × 步长 5600 → 名义上"只装得下" 11200 字。
        // 语义必须是：剩余内容**一个都没丢**（全部并入最后一段，该段因此超限），
        // 同时把"最后一段超出了单段上限多少字"如实报出来（OversizedChars）。
        // 旧实现配套的旧文案把这一项说成"未能纳入笔记"，与事实相反——这两个断言就是为了防它复辟。
        string transcript = BuildText(50000);
        var plan = NoteSegmenter.Build("离散数学", transcript, "", maxSegments: 2);

        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(50000, plan.TotalTranscriptChars);
        Assert.True(plan.OversizedChars > 0, "末段超出单段上限时，必须如实报告超出的字数");
        Assert.Equal(50000 - (2 * 5600), plan.OversizedChars);

        // 没有任何内容被丢掉：末段一路吃到转写末尾，并且它是真正的超限段
        var last = plan.Segments[^1];
        Assert.Equal(50000, last.EndChar);
        Assert.Equal(50000 - last.StartChar, last.Transcript.Length);
        Assert.True(last.Transcript.Length > NoteSegmenter.DefaultMaxCharsPerSegment,
            "超出段数上限的剩余内容必须全部落在最后一段里（该段会超限），而不是被丢弃");
    }

    [Fact]
    public void Build_WithinSegmentCap_ReportsNoOversizedTail()
    {
        string transcript = BuildText(12000); // 12000 / 5600 → 3 段，未触及段数上限

        var plan = NoteSegmenter.Build("离散数学", transcript, "");

        Assert.Equal(3, plan.Segments.Count);
        Assert.Equal(0, plan.OversizedChars);
        Assert.True(plan.Segments[^1].Transcript.Length <= NoteSegmenter.DefaultMaxCharsPerSegment);
    }

    [Fact]
    public void Build_RespectsExplicitLimits()
    {
        string transcript = BuildText(5000);
        var plan = NoteSegmenter.Build("英语", transcript, "", maxCharsPerSegment: 1000, overlapChars: 0, maxSegments: 10);

        Assert.Equal(5, plan.Segments.Count);
        Assert.Equal(0, plan.OversizedChars);
        Assert.All(plan.Segments, s => Assert.True(s.Transcript.Length <= 1000));
    }

    [Fact]
    public void ParseOcrEntries_ReadsHeadersAndTimestamps()
    {
        const string ocr = """
        [new_slide @ 00:00:10]
        极限的定义
        [annotation @ 01:02:03]
        洛必达法则
        """;

        var entries = NoteSegmenter.ParseOcrEntries(ocr);

        Assert.Equal(2, entries.Count);
        Assert.Equal(10, entries[0].StartSeconds);
        Assert.Equal("极限的定义", entries[0].Content);
        Assert.Equal(3723, entries[1].StartSeconds);
        Assert.Contains("[annotation @ 1:02:03]", entries[1].Prefix);
    }

    [Fact]
    public void ParseOcrEntries_MergesNumericOnlyLinesIntoPreviousEntry()
    {
        // PPT 页码常单独成行：必须跟随它所属的幻灯片，否则会变成"无归属"的内容
        const string ocr = """
        [new_slide @ 00:01:00]
        第一节 绪论
        12
        [new_slide @ 00:02:00]
        第二节 方法
        """;

        var entries = NoteSegmenter.ParseOcrEntries(ocr);

        Assert.Equal(2, entries.Count);
        Assert.Contains("12", entries[0].Content);
        Assert.DoesNotContain("12", entries[1].Content);
    }

    [Fact]
    public void ParseOcrEntries_TrailingPageNumber_StaysWithItsOwnSlide()
    {
        // 回归：条目以页码结尾时，页码曾残留到下一张幻灯片里
        const string ocr = """
        [new_slide @ 00:01:00]
        第一节 绪论
        7
        [new_slide @ 00:02:00]
        第二节 方法
        8
        """;

        var entries = NoteSegmenter.ParseOcrEntries(ocr);

        Assert.Equal(2, entries.Count);
        Assert.Contains("7", entries[0].Content);
        Assert.DoesNotContain("8", entries[0].Content);
        Assert.Contains("8", entries[1].Content);
        Assert.DoesNotContain("7", entries[1].Content);
    }

    [Fact]
    public void ParseOcrEntries_NoHeaders_ReturnsWholeTextAsSingleEntry()
    {
        var entries = NoteSegmenter.ParseOcrEntries("没有标记的纯文本\n第二行");

        Assert.Single(entries);
        Assert.Contains("没有标记的纯文本", entries[0].Content);
        Assert.Equal("", entries[0].Prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void ParseOcrEntries_Blank_ReturnsEmpty(string? ocr)
    {
        Assert.Empty(NoteSegmenter.ParseOcrEntries(ocr));
    }

    [Fact]
    public void Build_TimestampHint_UsesSlideTimes()
    {
        string transcript = BuildText(50);
        const string ocr = """
        [new_slide @ 00:03:00]
        内容A
        """;

        var plan = NoteSegmenter.Build("概率论", transcript, ocr);

        Assert.Equal("03:00–03:00", plan.Segments[0].TimestampHint);
    }

    [Fact]
    public void Build_NoTimestampInfo_OmitsHint()
    {
        var plan = NoteSegmenter.Build("概率论", "转写", "无标记的 OCR 文本");

        Assert.Null(plan.Segments[0].TimestampHint);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(600, "10:00")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3723, "1:02:03")]
    public void FormatTimestamp_MatchesAppConvention(int seconds, string expected)
    {
        Assert.Equal(expected, NoteSegmenter.FormatTimestamp(seconds));
    }

    private static string BuildText(int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++) sb.Append((char)('a' + (i % 26)));
        return sb.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
