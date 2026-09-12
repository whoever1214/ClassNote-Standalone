using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// OCR 素材去重：同一张幻灯片停留期间会被反复截屏，OCR 文本几乎完全相同。
/// 这些重复不是知识、只拉长提示词输入（prefill 更慢、token 更贵），但折叠必须保守——
/// 只要有一点内容不同就原样保留。
/// </summary>
public class NoteSegmenterOcrDedupTests
{
    private static OcrBlock Block(string content, int seconds = 0, string type = "new_slide")
        => new($"[{type} @ {NoteSegmenter.FormatTimestamp(seconds)}]", content, seconds);

    [Fact]
    public void Collapse_RemovesAdjacentIdenticalBlocks()
    {
        var blocks = new List<OcrBlock>
        {
            Block("一元二次方程\n求根公式", 10),
            Block("一元二次方程\n求根公式", 20),
            Block("一元二次方程\n求根公式", 30),
            Block("判别式 Δ=b²-4ac", 40),
        };

        var collapsed = NoteSegmenter.CollapseDuplicateOcr(blocks);

        Assert.Equal(2, collapsed.Count);
        Assert.Equal(10, collapsed[0].StartSeconds); // 保留第一条的时间标记
        Assert.Contains("判别式", collapsed[1].Content);
    }

    [Fact]
    public void Collapse_IgnoresWhitespaceAndFullWidthSpaceDifferences()
    {
        var blocks = new List<OcrBlock>
        {
            Block("定理  内容", 1),
            Block("定理\u3000内容", 2),
            Block("定理\n内容", 3),
        };

        Assert.Single(NoteSegmenter.CollapseDuplicateOcr(blocks));
    }

    [Fact]
    public void Collapse_KeepsBlocksThatDifferByOneCharacter()
    {
        // "相似但不相同"的两张幻灯片必须都保留：合并掉就是真的丢内容
        var blocks = new List<OcrBlock>
        {
            Block("第 1 页：定义", 1),
            Block("第 2 页：定义", 2),
        };

        Assert.Equal(2, NoteSegmenter.CollapseDuplicateOcr(blocks).Count);
    }

    [Fact]
    public void Collapse_OnlyFoldsAdjacentDuplicates()
    {
        // A B A：中间隔了别的内容，说明是"回到上一张幻灯片"，不能折叠
        var blocks = new List<OcrBlock>
        {
            Block("A", 1),
            Block("B", 2),
            Block("A", 3),
        };

        Assert.Equal(3, NoteSegmenter.CollapseDuplicateOcr(blocks).Count);
    }

    [Fact]
    public void Build_ProducesSameSegments_WithDuplicatedOcr()
    {
        // 折叠后进入分段的素材不应改变段数（内容更短，段数只会 ≤ 原值）
        string transcript = string.Concat(Enumerable.Repeat("这是课堂讲解内容。", 200)); // 约 1800 字
        string ocr = string.Join("\n",
            Enumerable.Range(0, 40).Select(i => $"[new_slide @ {NoteSegmenter.FormatTimestamp(i * 10)}]\n同一张幻灯片的文字"));

        var plan = NoteSegmenter.Build("数学", transcript, ocr);

        Assert.NotEmpty(plan.Segments);
        // 所有段里的 OCR 素材加起来只应出现一次正文
        int occurrences = plan.Segments.Sum(s => s.OcrText.Split("同一张幻灯片的文字").Length - 1);
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Collapse_EmptyInput_IsSafe()
    {
        Assert.Empty(NoteSegmenter.CollapseDuplicateOcr(new List<OcrBlock>()));
        Assert.Single(NoteSegmenter.CollapseDuplicateOcr(new List<OcrBlock> { Block("唯一") }));
    }
}
