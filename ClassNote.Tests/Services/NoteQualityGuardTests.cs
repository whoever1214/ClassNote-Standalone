using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 质量守卫（<see cref="NoteQualityGuard"/>）的回归测试（P1）。
///
/// 它锁住的是一条口径：**模型对"这条来自哪里"的自我声明不算数，后端要能核对**。
/// 化学课实测的事故正是这一条没做——试卷判断题的选项原句被标成"老师讲的结论"端给用户。
///
/// 同时锁住"不误伤"：正常的同义改写绝不能因为措辞与转写不同就被降级。
/// </summary>
public class NoteQualityGuardTests
{
    private static NotePromptBuilder.NoteItem Item(string body, string evidence, string kind = "knowledge")
        => new("标题", body, kind, "现场", 1, evidence);

    [Fact]
    public void Inspect_DowngradesLiveClaimThatTheTranscriptDoesNotSupport()
    {
        // 这是化学课的真实事故形态：判断题的选项原句被标成「现场」，
        // 而老师实际说的是相反的结论（"不能用泡沫灭火器"）。
        string transcript = "金属钠着火的时候不能用泡沫灭火器，只能用干砂。";
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("金属钠着火时，可用泡沫灭火器或干砂灭火", "现场"),
        };

        var result = NoteQualityGuard.Inspect(items, transcript);

        Assert.Equal(1, result.Downgraded);
        Assert.Equal("课件", result.Items[0].Evidence);
        Assert.True(result.HasFindings);
    }

    [Fact]
    public void Inspect_KeepsLiveClaimThatIsGroundedInTheTranscript()
    {
        // 正文整条都在转写里 → 有佐证，不得降级
        string transcript = "碳酸钠俗名纯碱或者苏打，碳酸氢钠俗名小苏打。";
        var items = new List<NotePromptBuilder.NoteItem> { Item("碳酸钠俗名纯碱", "现场") };

        var result = NoteQualityGuard.Inspect(items, transcript);

        Assert.Equal(0, result.Downgraded);
        Assert.Equal("现场", result.Items[0].Evidence);
    }

    [Fact]
    public void Inspect_DoesNotPunishReasonableRewording()
    {
        // 正常的同义改写：与转写的公共前缀很长（≥ 阈值）→ 视为有佐证。
        // 误伤这条会让「现场」标签彻底失去意义（模型几乎不会逐字复述口语）。
        string transcript = "过氧化钠和二氧化碳反应生成碳酸钠和氧气，所以可以做供氧剂。";
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("过氧化钠和二氧化碳反应生成碳酸钠和氧气，可作供氧剂", "现场"),
        };

        var result = NoteQualityGuard.Inspect(items, transcript);

        Assert.Equal(0, result.Downgraded);
    }

    [Fact]
    public void Inspect_SkipsGroundingCheckWhenThereIsNoTranscript()
    {
        // 没有可核对的对象时不能凭空判"无佐证"（单来源录音 / 只有课件的正常情形）
        var items = new List<NotePromptBuilder.NoteItem> { Item("任意内容", "现场") };

        var result = NoteQualityGuard.Inspect(items, transcript: null);

        Assert.Equal(0, result.Downgraded);
        Assert.Equal("现场", result.Items[0].Evidence);
    }

    [Fact]
    public void Inspect_NeverTouchesItemsThatDoNotClaimLiveEvidence()
    {
        string transcript = "完全无关的内容。";
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("只在课件上出现的一行字", "课件"),
            Item("两侧都有的内容", "双证"),
            Item("两侧冲突的内容", "冲突"),
            Item("没标证据的内容", ""),
        };

        var result = NoteQualityGuard.Inspect(items, transcript);

        Assert.Equal(0, result.Downgraded);
        Assert.Equal(new[] { "课件", "双证", "冲突", "" }, result.Items.Select(i => i.Evidence).ToArray());
    }

    [Fact]
    public void Inspect_CountsExamItems()
    {
        string transcript = "老师在讲碳酸钠的性质。碳酸钠的性质很重要。";
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("碳酸钠的性质很重要", "现场"),
            Item("（2025·山东卷）用 Na2O2 和水制备少量 O2", "课件", kind: "exam"),
        };

        var result = NoteQualityGuard.Inspect(items, transcript);

        Assert.Equal(1, result.ExamCount);
    }

    [Fact]
    public void Inspect_ReportsDuplicateBodiesWithoutDroppingThem()
    {
        // 去重之后仍有完全相同的正文：如实数出来（不静默丢弃），写进摘要让用户看得见
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("同一句话", "课件"),
            Item("同一句话", "课件"),
        };

        var result = NoteQualityGuard.Inspect(items, transcript: null);

        Assert.Equal(1, result.DuplicateBodies);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public void CommonPrefixLength_IsCharacterWise()
    {
        Assert.Equal(3, NoteQualityGuard.CommonPrefixLength("金属钠着火了", "金属钠不能用水"));
        Assert.Equal(0, NoteQualityGuard.CommonPrefixLength("甲乙", "丙丁"));
        Assert.Equal(2, NoteQualityGuard.CommonPrefixLength("甲乙丙", "甲乙"));
    }

    [Fact]
    public void DescribeFindings_SaysWhatChangedAndIsNullWhenNothingHappened()
    {
        var clean = NoteQualityGuard.Inspect(new List<NotePromptBuilder.NoteItem> { Item("内容", "课件") }, null);
        Assert.Null(NoteQualityGuard.DescribeFindings(clean));

        var found = NoteQualityGuard.Inspect(
            new List<NotePromptBuilder.NoteItem> { Item("完全对不上的说法", "现场") },
            "转写里说的是另一回事，完全不同。");

        string? text = NoteQualityGuard.DescribeFindings(found);
        Assert.NotNull(text);
        Assert.Contains("找不到佐证", text);
        Assert.Contains("课件", text);
    }
}
