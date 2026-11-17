using System;
using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// "图片直送"的输入契约：图片必须按分段**连续切分**（每张只进一段），
/// 提示词必须如实说明本次附了几张图，且纯 OCR 模式的提示词不能出现"图片"话术。
/// </summary>
public class VisionInputTests
{
    private static NoteImage Img(int seq) => new(seq, seq * 30.0, "new_slide", new byte[] { 1, 2, 3 }, 1600, 900);

    private static NotePlan Plan(int segmentCount)
    {
        var segments = new List<NoteSegment>();
        for (int i = 1; i <= segmentCount; i++)
            segments.Add(new NoteSegment(i, segmentCount, "转写" + i, "OCR" + i, 0, 0, null));
        return new NotePlan("数学", segments, segmentCount > 1, 0, 100);
    }

    [Fact]
    public void SplitImages_SingleSegment_GetsAll()
    {
        var images = Enumerable.Range(1, 7).Select(Img).ToList();
        var split = LlmService.SplitImages(Plan(1), images);

        Assert.Single(split);
        Assert.Equal(7, split[0].Count);
    }

    [Fact]
    public void SplitImages_MultiSegment_PartitionsWithoutLoss()
    {
        var images = Enumerable.Range(1, 10).Select(Img).ToList();
        var split = LlmService.SplitImages(Plan(3), images);

        Assert.Equal(3, split.Count);
        var flattened = split.SelectMany(s => s).ToList();

        // 每张图恰好出现一次，且顺序保持（分段是连续切分，不是按模数抽）
        Assert.Equal(images.Count, flattened.Count);
        Assert.Equal(images.Select(i => i.SeqNo), flattened.Select(i => i.SeqNo));
    }

    [Fact]
    public void SplitImages_MultiSegment_Contiguous()
    {
        var images = Enumerable.Range(1, 9).Select(Img).ToList();
        var split = LlmService.SplitImages(Plan(3), images);

        Assert.Equal(new[] { 1, 2, 3 }, split[0].Select(i => i.SeqNo).ToArray());
        Assert.Equal(new[] { 4, 5, 6 }, split[1].Select(i => i.SeqNo).ToArray());
        Assert.Equal(new[] { 7, 8, 9 }, split[2].Select(i => i.SeqNo).ToArray());
    }

    [Fact]
    public void SplitImages_NoImages_ReturnsEmptyPerSegment()
    {
        var split = LlmService.SplitImages(Plan(2), null);
        Assert.Equal(2, split.Count);
        Assert.All(split, s => Assert.Empty(s));
    }

    [Fact]
    public void SinglePrompt_WithImages_AnnouncesCountAndDropsEmptyOcrBlock()
    {
        var prompt = NotePromptBuilder.BuildSingleNotePrompt("数学", "老师讲了极限", "", imageCount: 3);

        Assert.Contains("随附 3 张图片", prompt);
        // 规则正文里会提到「【课件/板书截图 OCR】」这个词（"照抄上面…的原始表述"），
        // 但**不能出现该标题单独成行**的空 OCR 区块
        var lines = prompt.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.DoesNotContain("【课件/板书截图 OCR】", lines);
    }

    [Fact]
    public void SinglePrompt_BothMode_KeepsBothBlocks()
    {
        var prompt = NotePromptBuilder.BuildSingleNotePrompt("数学", "老师讲了极限", "OCR 文本内容", imageCount: 2);

        Assert.Contains("随附 2 张图片", prompt);
        Assert.Contains("【课件/板书截图 OCR】", prompt);
        Assert.Contains("OCR 文本内容", prompt);
    }

    [Fact]
    public void SinglePrompt_OcrOnly_UnchangedBehaviour()
    {
        var prompt = NotePromptBuilder.BuildSingleNotePrompt("数学", "老师讲了极限", "OCR 文本内容");

        Assert.DoesNotContain("张图片", prompt);
        Assert.Contains("【课件/板书截图 OCR】", prompt);
    }

    [Fact]
    public void SegmentPrompt_WithImages_AnnouncesLocalCount()
    {
        var segment = new NoteSegment(2, 5, "本段转写", "", 0, 0, "00:10–00:20");
        var prompt = NotePromptBuilder.BuildSegmentPrompt("物理", segment, imageCount: 4);

        Assert.Contains("本段随附 4 张图片", prompt);
        var lines = prompt.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.DoesNotContain("【课件/板书截图 OCR】", lines);
    }

    [Fact]
    public void SystemPrompt_CarriesVisionRules()
    {
        Assert.Contains("看不清", NotePromptBuilder.NoteSystemPrompt);
        Assert.Contains("[图片 K]", NotePromptBuilder.VisionRules);
    }

    [Fact]
    public void ImageModes_FlagsAreConsistent()
    {
        Assert.True(ScreenshotInputMode.Vision.UsesVision());
        Assert.False(ScreenshotInputMode.Vision.UsesOcr());
        Assert.True(ScreenshotInputMode.Ocr.UsesOcr());
        Assert.False(ScreenshotInputMode.Ocr.UsesVision());
        Assert.True(ScreenshotInputMode.Both.UsesOcr());
        Assert.True(ScreenshotInputMode.Both.UsesVision());
    }

    [Fact]
    public void ImageModes_StorageRoundTrip_AndDefault()
    {
        Assert.Equal(ScreenshotInputMode.Ocr, ScreenshotInputModes.FromStorage("Ocr"));
        Assert.Equal(ScreenshotInputMode.Both, ScreenshotInputModes.FromStorage("both"));
        Assert.Equal(ScreenshotInputMode.Vision, ScreenshotInputModes.FromStorage(null));
        Assert.Equal(ScreenshotInputMode.Vision, ScreenshotInputModes.FromStorage("不认识的字符串"));
    }

    [Fact]
    public void SelectionOptions_ClampBadSettings()
    {
        var settings = new AppSettingsData
        {
            VisionMaxImagesPerNote = -3,
            VisionMaxEdgePx = 99999,
            VisionJpegQuality = 5,
            VisionDedupThreshold = 999,
        };

        var options = ScreenshotSelectionOptions.FromSettings(settings);

        Assert.Equal(30, options.MaxImages);
        Assert.Equal(1600, options.MaxEdgePx);
        Assert.Equal(80, options.JpegQuality);
        Assert.Equal(ScreenshotSelector.DefaultDedupThreshold, options.DedupThreshold);
    }
}