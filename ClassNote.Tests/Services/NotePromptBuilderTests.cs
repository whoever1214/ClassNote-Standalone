using System.Collections.Generic;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 提示词与模型输出解析的回归测试。
/// 这些断言锁住的是"分段改造不能稀释保真要求"这条底线：三处提示词必须都带
/// 逐字保真规则、都必须禁止例题与师生问答，JSON 契约必须与解析器一致。
/// </summary>
public class NotePromptBuilderTests
{
    [Fact]
    public void NoteSystemPrompt_KeepsOnlyThreeSectionsAndVerbatimRule()
    {
        string prompt = NotePromptBuilder.NoteSystemPrompt;

        Assert.Contains("只保留三部分：课程标题、核心知识点、重点及易错点", prompt);
        Assert.Contains("原封不动", prompt);
        Assert.Contains("逐字一致", prompt);
        Assert.Contains("不要例题与解析", prompt);
        Assert.Contains("不要师生问答", prompt);
        // 早期那句"控制篇幅、避免流水账"与保真要求直接冲突，不得复活
        Assert.DoesNotContain("避免流水账", prompt);
        Assert.DoesNotContain("宁精勿滥", prompt);
    }

    [Fact]
    public void SegmentSystemPrompt_DeclaresExactlyTheFieldsTheParserReads()
    {
        string prompt = NotePromptBuilder.SegmentSystemPrompt;

        // 提示词声明的字段名与 ParseSegmentJson 读取的 JsonProperty 必须一致，
        // 否则模型输出会静默解析失败、退回"整段当核心知识点"
        Assert.Contains("coreKnowledge", prompt);
        Assert.Contains("pitfalls", prompt);
        // 分段路径同样要带上保真要求与两条删除规则
        Assert.Contains("原封不动", prompt);
        Assert.Contains("不要例题与解析", prompt);
        Assert.Contains("不要师生问答", prompt);
        // 中段素材不得被模型当成完整课程去补全
        Assert.Contains("不要假设能看到开头或结尾", prompt);
    }

    [Fact]
    public void MergeSystemPrompt_RequiresCompletenessAndVerbatim()
    {
        string prompt = NotePromptBuilder.MergeSystemPrompt;

        Assert.Contains("完整保留", prompt);
        Assert.Contains("逐字保真", prompt);
        Assert.Contains("只保留三部分：课程标题、核心知识点、重点及易错点", prompt);
        Assert.Contains("去重", prompt);
        // 合并阶段也必须明确禁止例题与师生问答，否则前面的删除要求会被"merge 时又写回来"
        Assert.Contains("不要出现例题与解析、师生问答", prompt);
        // 逐字保真时不得夹带"摘要式压缩"的措辞
        Assert.DoesNotContain("精炼", prompt);
    }

    [Fact]
    public void AllThreePrompts_CarryTheSameVerbatimRules()
    {
        // 分段改造的核心风险：保真要求只在单段提示词里，分段/合并路径被稀释
        foreach (var prompt in new[]
                 {
                     NotePromptBuilder.NoteSystemPrompt,
                     NotePromptBuilder.SegmentSystemPrompt,
                     NotePromptBuilder.MergeSystemPrompt,
                 })
        {
            Assert.Contains("原封不动", prompt);
        }

        // JSON 模式要求提示词里出现 "json" 字样并给出格式示例（DeepSeek 文档的硬要求）
        Assert.Contains("JSON", NotePromptBuilder.SegmentSystemPrompt);
        Assert.Contains("coreKnowledge", NotePromptBuilder.SegmentSystemPrompt);
    }

    [Fact]
    public void SegmentItemsSystemPrompt_CarriesOcrNoiseAndSourceRules()
    {
        string prompt = NotePromptBuilder.SegmentItemsSystemPrompt;

        // OCR 噪声兜底口径：能确定就整理，确定不了必须原样保留并标注，禁止猜符号
        Assert.Contains("OCR 不清", prompt);
        Assert.Contains("禁止猜", prompt);
        // 来源口径：OCR 内容一律「课件」，避免同一次输出里同为 OCR 却有的标现场有的标课件
        Assert.Contains("source 一律填「课件」", prompt);
        // 原有契约不能被挤掉
        Assert.Contains("coreKnowledge", NotePromptBuilder.SegmentSystemPrompt);
        Assert.Contains("items", prompt);
    }

    [Fact]
    public void SegmentSystemPrompt_AlsoCarriesTheOcrNoiseRules()
    {
        // 旧 schema 路径同样吃 OCR 素材，噪声口径不能只在条目 schema 里
        Assert.Contains("OCR 不清", NotePromptBuilder.SegmentSystemPrompt);
    }

    [Fact]
    public void MergeSystemPrompt_MentionsOcrNoiseHandlingForExcerpts()
    {
        // 合并阶段会带上原始素材摘录（含 OCR），校对时同样会遇到噪声
        Assert.Contains("OCR 不清", NotePromptBuilder.MergeSystemPrompt);
    }

    [Fact]
    public void BuildSingleNotePrompt_TellsModelHowToHandleOcrNoise()
    {
        string prompt = NotePromptBuilder.BuildSingleNotePrompt("高等数学", "转写", "OCR");

        Assert.Contains("OCR 不清", prompt);
    }

    [Fact]
    public void MaxOutputTokens_IsSetForJsonMode()
    {
        // JSON 模式必须显式给 max_tokens，否则输出可能被截断成不完整 JSON
        Assert.True(NotePromptBuilder.MaxOutputTokens >= 4096);
    }

    [Fact]
    public void BuildSingleNotePrompt_IncludesMaterialAndStructureRules()
    {
        string prompt = NotePromptBuilder.BuildSingleNotePrompt("高等数学", "转写正文ABC", "OCR正文XYZ");

        Assert.Contains("课程：高等数学", prompt);
        Assert.Contains("【课堂录音转写】", prompt);
        Assert.Contains("转写正文ABC", prompt);
        Assert.Contains("【课件/板书截图 OCR】", prompt);
        Assert.Contains("OCR正文XYZ", prompt);
        Assert.Contains("## 重点及易错点", prompt);
        Assert.Contains("除这三部分外不要再加其它章节", prompt);
        Assert.DoesNotContain("师生问答（问什么、答什么）完整保留", prompt);
    }

    [Fact]
    public void BuildSingleNotePrompt_MultiSource_AddsSpeakerDisambiguation()
    {
        string transcript = TranscriptSections.Render(new[]
        {
            new TranscriptTrack(RecordingAudioSource.Microphone, "老师现场讲解极限的定义"),
            new TranscriptTrack(RecordingAudioSource.System, "课件上写着洛必达法则的适用条件"),
        });

        // 不带 multiSource：不应出现分辨要求
        string plain = NotePromptBuilder.BuildSingleNotePrompt("高等数学", transcript, "OCR");
        Assert.DoesNotContain("分辨来源", plain);

        // 带 multiSource：必须叮嘱分辨来源，且要求用「现场」「课件」标明
        string prompt = NotePromptBuilder.BuildSingleNotePrompt("高等数学", transcript, "OCR", multiSource: true);
        Assert.Contains("分辨来源", prompt);
        Assert.Contains("现场", prompt);
        Assert.Contains("课件", prompt);
        // 素材本身的两侧标注必须原样出现在提示词里
        Assert.Contains(TranscriptSections.MicrophoneLabel, prompt);
        Assert.Contains(TranscriptSections.SystemLabel, prompt);
    }

    [Fact]
    public void BuildSegmentPrompt_MultiSource_AsksToLabelSource()
    {
        var segment = new NoteSegment(1, 2, "本段转写", "本段OCR", 0, 6000, null);

        string plain = NotePromptBuilder.BuildSegmentPrompt("高等数学", segment);
        Assert.DoesNotContain("分辨来源", plain);

        string prompt = NotePromptBuilder.BuildSegmentPrompt("高等数学", segment, multiSource: true);
        Assert.Contains("分辨来源", prompt);
        Assert.Contains("「现场」「课件」", prompt);
    }

    [Fact]
    public void MergeSystemPromptFor_MultiSource_ForbidsDroppingSourceMarkers()
    {
        string plain = NotePromptBuilder.MergeSystemPromptFor(multiSource: false);
        Assert.DoesNotContain("保留来源标记", plain);

        // 分段结果里已经写了「现场」「课件」，合并时洗掉就等于前面白分轨
        string multi = NotePromptBuilder.MergeSystemPromptFor(multiSource: true);
        Assert.Contains("保留来源标记", multi);
        Assert.Contains("不同来源的同名知识点不要合并成一条", multi);
    }

    [Fact]
    public void BuildSegmentPrompt_AnnouncesPositionAndPartialNature()
    {
        var segment = new NoteSegment(2, 5, "本段转写", "本段OCR", 5600, 12000, "10:00–20:00");

        string prompt = NotePromptBuilder.BuildSegmentPrompt("大学物理", segment);

        Assert.Contains("第 2 / 5 段", prompt);
        Assert.Contains("10:00–20:00", prompt);
        Assert.Contains("这不是完整的一节课", prompt);
        Assert.Contains("原样记录", prompt);
        Assert.Contains("本段转写", prompt);
        Assert.Contains("本段OCR", prompt);
    }

    [Fact]
    public void BuildSegmentPrompt_Retry_OmitsPreambleButKeepsMaterial()
    {
        var segment = new NoteSegment(1, 1, "转写", "OCR", 0, 2, null);

        string retry = NotePromptBuilder.BuildSegmentPrompt("课程", segment, isRetry: true);

        Assert.DoesNotContain("这是第", retry);
        Assert.Contains("【课堂录音转写】", retry);
        Assert.Contains("coreKnowledge", retry);
    }

    [Fact]
    public void BuildSegmentPrompt_EmptyMaterial_MarksPlaceholders()
    {
        var segment = new NoteSegment(1, 2, "", "", 0, 0, null);

        string prompt = NotePromptBuilder.BuildSegmentPrompt("课程", segment);

        Assert.Contains("（本段无转写内容）", prompt);
        Assert.Contains("（本段无截图内容）", prompt);
    }

    [Fact]
    public void BuildMergePrompt_ListsEverySegmentWithExcerpts()
    {
        var segmentJsons = new List<string> { "{\"coreKnowledge\":\"A\"}", "{\"coreKnowledge\":\"B\"}" };

        string prompt = NotePromptBuilder.BuildMergePrompt("数据结构", segmentJsons, "转写摘录", "OCR摘录");

        Assert.Contains("共 2 个分段的整理结果", prompt);
        Assert.Contains("分段 1", prompt);
        Assert.Contains("分段 2", prompt);
        Assert.Contains("转写摘录", prompt);
        Assert.Contains("OCR摘录", prompt);
        Assert.Contains("# 数据结构", prompt);
    }

    [Fact]
    public void ParseSegmentJson_ReadsBothFields()
    {
        var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson(
            "{\"coreKnowledge\":\"## 核心知识点\\n- 极限\",\"pitfalls\":\"- 易错：洛必达条件\"}");

        Assert.Contains("极限", core);
        Assert.Contains("洛必达条件", pitfalls);
    }

    [Fact]
    public void ParseSegmentJson_StripsMarkdownCodeFence()
    {
        const string raw = """
        ```json
        {"coreKnowledge":"内容A","pitfalls":""}
        ```
        """;

        var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson(raw);

        Assert.Equal("内容A", core);
        Assert.Equal("", pitfalls);
    }

    [Fact]
    public void ParseSegmentJson_ToleratesSurroundingProse()
    {
        const string raw = "好的，以下是整理结果：\n{\"coreKnowledge\":\"内容B\",\"pitfalls\":\"易错点C\"}\n希望有帮助。";

        var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson(raw);

        Assert.Equal("内容B", core);
        Assert.Equal("易错点C", pitfalls);
    }

    [Fact]
    public void ParseSegmentJson_PlainText_FallsBackToCoreKnowledge()
    {
        // 本地小模型可能完全无视 JSON 要求：宁可把整段当核心知识点，也不能丢掉这一段
        var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson("## 核心知识点\n- 原封不动的原文");

        Assert.Contains("原封不动的原文", core);
        Assert.Equal("", pitfalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSegmentJson_UnusableInput_ReturnsEmpty(string? raw)
    {
        var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson(raw!);

        Assert.Equal("", core);
        Assert.Equal("", pitfalls);
    }

    [Fact]
    public void ParseSegmentJson_EmptyJsonObject_TreatedAsEmptyResult()
    {
        // "{}" 是合法 JSON 但两个字段都为空——属于"这一段的整理结果为空"，
        // 不应回退成把原始输出（"{}" 本身）当成笔记内容
        var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson("{}");

        Assert.Equal("", core);
        Assert.Equal("", pitfalls);
    }

    [Fact]
    public void CleanMarkdown_StripsCodeFence()
    {
        Assert.Equal("# 标题\n正文", NotePromptBuilder.CleanMarkdown("```markdown\n# 标题\n正文\n```"));
        Assert.Equal("# 标题", NotePromptBuilder.CleanMarkdown("# 标题"));
        Assert.Equal("", NotePromptBuilder.CleanMarkdown("   "));
    }

    [Fact]
    public void AssembleNote_MechanicallyKeepsEverySegment()
    {
        var parts = new List<(string Core, string Pitfalls)>
        {
            ("## 核心知识点\n- 知识点一", "- 易错点一"),
            ("## 核心知识点\n- 知识点二", ""),
        };

        string note = NotePromptBuilder.AssembleNote("操作系统", parts);

        Assert.StartsWith("# 操作系统", note);
        Assert.Contains("## 核心知识点", note);
        Assert.Contains("知识点一", note);
        Assert.Contains("知识点二", note);
        Assert.Contains("## 重点及易错点", note);
        Assert.Contains("易错点一", note);
    }

    [Fact]
    public void AssembleNote_NoPitfalls_OmitsSection()
    {
        string note = NotePromptBuilder.AssembleNote("课程", new List<(string, string)> { ("内容", "") });

        Assert.DoesNotContain("## 重点及易错点", note);
    }

    [Fact]
    public void AssembleNote_AllSegmentsFailed_SaysSoInsteadOfEmptyNote()
    {
        string note = NotePromptBuilder.AssembleNote("课程", new List<(string, string)> { ("", "") });

        Assert.Contains("（本轮未整理出核心知识点）", note);
    }

    [Fact]
    public void OversizedNotice_SaysContentWasKeptNotDropped()
    {
        string notice = NotePromptBuilder.OversizedNotice(12345);

        Assert.Contains("12345", notice);
        // 措辞必须与实现一致：内容被并入最后一段（可能被模型截断），而不是"丢了"
        Assert.Contains("并入最后一段", notice);
        Assert.DoesNotContain("未能纳入笔记", notice);
        // 不得再建议用户"调大单段上限"——那是个没有界面入口的常量
        Assert.DoesNotContain("调大单段上限", notice);
    }

    [Fact]
    public void FailureNotice_ReportsFailedAndTotalSegmentCount()
    {
        string notice = NotePromptBuilder.FailureNotice(2, 5);

        Assert.Contains("5 段", notice);
        Assert.Contains("2 段", notice);
        Assert.Contains("未能进入笔记", notice);
    }
}
