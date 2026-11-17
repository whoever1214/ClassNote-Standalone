using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 结构化条目管道（v0.6.0）：分段产出条目 → 确定性去重 → 排序润色 → **逐条回校** → 拼装。
///
/// 这套测试锁的是"长文本合并不产生幻觉"这条底线：模型可以润色，但不能在无人察觉的情况下
/// 删条、揉条或改写正文。每个环节都必须是可验证的，任何一个环节失效都会被这里抓住。
/// </summary>
public class NoteItemPipelineTests
{
    private static NotePromptBuilder.NoteItem Item(string title, string body,
        string kind = "knowledge", string source = "", int segment = 1)
        => new(title, body, kind, source, segment);

    // ── 条目解析 ─────────────────────────────────────────────

    [Fact]
    public void ParseSegmentItems_ReadsItemsAndAssignsSegmentIndex()
    {
        const string raw = """
        {"items":[
          {"title":"洛必达法则适用条件","body":"仅 0/0 或 ∞/∞ 型可用","kind":"knowledge","source":"现场"},
          {"title":"常见错误","body":"直接对非未定式使用","kind":"pitfall","source":"课件"}
        ]}
        """;

        var items = NotePromptBuilder.ParseSegmentItems(raw, 3);

        Assert.Equal(2, items.Count);
        Assert.Equal("洛必达法则适用条件", items[0].Title);
        Assert.Equal("knowledge", items[0].Kind);
        Assert.Equal("现场", items[0].Source);
        Assert.All(items, i => Assert.Equal(3, i.SegmentIndex));
        Assert.True(items[1].IsPitfall);
    }

    [Fact]
    public void ParseSegmentItems_StripsCodeFence()
    {
        const string raw = """
        ```json
        {"items":[{"title":"A","body":"正文 A","kind":"knowledge","source":""}]}
        ```
        """;

        var items = NotePromptBuilder.ParseSegmentItems(raw, 1);

        Assert.Single(items);
        Assert.Equal("正文 A", items[0].Body);
    }

    [Fact]
    public void ParseSegmentItems_SkipsEmptyElementsButKeepsTheRest()
    {
        // 条目级容错：单个元素为空不该让整段失败
        const string raw = """
        {"items":[{"title":"A","body":"正文 A"},{"title":"","body":""},{"title":"B","body":"正文 B"}]}
        """;

        var items = NotePromptBuilder.ParseSegmentItems(raw, 1);

        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { "A", "B" }, items.Select(i => i.Title).ToArray());
        // 缺 kind 时按知识点归类，而不是当成易错点
        Assert.All(items, i => Assert.False(i.IsPitfall));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("模型完全拒绝结构化输出，给了一段 Markdown")]
    [InlineData("{\"items\":[{\"title\":\"A\",\"body\":\"B\"}")] // 数组被截断
    [InlineData("{\"coreKnowledge\":\"旧 schema\",\"pitfalls\":\"\"}")]
    public void ParseSegmentItems_UnusableInput_ReturnsEmptySoCallerFallsBack(string? raw)
    {
        // 返回空 => 调用方退回 Markdown 合并路径，不会因为 schema 不被支持就丢掉整段
        Assert.Empty(NotePromptBuilder.ParseSegmentItems(raw, 1));
    }

    // ── 确定性去重：只去完全一致 ────────────────────────────────

    [Fact]
    public void Deduplicate_RemovesExactDuplicatesAcrossSegments()
    {
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("极限定义", "当 x 趋于 a 时 f(x) 趋于 L", segment: 1),
            Item("导数公式", "(x^n)' = n·x^(n-1)", segment: 1),
            Item("极限定义", "当x趋于a时f(x)趋于L", segment: 2), // 仅空白差异 → 归一化后一致
            Item("导数公式", "(x^n)' = n·x^(n-1)", segment: 3),
        };

        var result = NotePromptBuilder.Deduplicate(items);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.MergedCount);
        // 保留先出现的那一条
        Assert.Equal(1, result.Items[0].SegmentIndex);
        Assert.Equal("当 x 趋于 a 时 f(x) 趋于 L", result.Items[0].Body);
    }

    [Fact]
    public void Deduplicate_SimilarTitlesWithDifferentBodies_KeepsBoth()
    {
        // 这是刻意的保守：相似标题是否同一知识点，程序判不可靠，
        // 按相似度合并会静默丢掉一条正文——宁可留重复
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("洛必达法则适用条件", "必须是 0/0 或 ∞/∞ 型", segment: 1),
            Item("洛必达条件", "分母导数不能为零", segment: 2),
        };

        var result = NotePromptBuilder.Deduplicate(items);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(0, result.MergedCount);
    }

    [Fact]
    public void Deduplicate_SameBodyDifferentKind_MergesAndKeepsTheMoreSpecificKind()
    {
        // v0.8 起键只含正文：模型在**每段独立**分类，同一句话常被一段归为知识点、
        // 另一段归为易错点；两条都留就会在最终笔记里出现两遍（化学课实测重复行占比 25.4%）。
        // 合并时保留"更具体"的归类——易错点优先，反过来会丢掉一条易错提示。
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("洛必达条件", "必须是未定式", "knowledge"),
            Item("洛必达条件", "必须是未定式", "pitfall"),
        };

        var result = NotePromptBuilder.Deduplicate(items);

        Assert.Single(result.Items);
        Assert.Equal(1, result.MergedCount);
        Assert.True(result.Items[0].IsPitfall);
    }

    [Fact]
    public void Deduplicate_SameBodyDifferentTitle_MergesBecauseBodyIsTheFingerprint()
    {
        // 标题是模型自己起的，同一内容在不同段被起不同标题时不能因此漏合并
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("碳酸钠的俗名", "纯碱或苏打", "knowledge"),
            Item("俗名", "纯碱或苏打", "knowledge"),
        };

        var result = NotePromptBuilder.Deduplicate(items);

        Assert.Single(result.Items);
        Assert.Equal("碳酸钠的俗名", result.Items[0].Title);   // 保留先出现的那条
    }

    [Fact]
    public void ParseSegmentItems_ReadsEvidenceField()
    {
        const string raw = """
        {"items":[
          {"title":"钠的保存","body":"钠保存在煤油中","kind":"knowledge","source":"现场","evidence":"双证"},
          {"title":"某真题","body":"（2025·山东卷）下列说法正确的是","kind":"exam","source":"课件","evidence":"课件"}
        ]}
        """;

        var items = NotePromptBuilder.ParseSegmentItems(raw, 1);

        Assert.Equal("双证", items[0].Evidence);
        Assert.Equal("exam", items[1].Kind);
        Assert.True(items[1].IsExam);
        Assert.False(items[0].IsExam);
    }

    [Fact]
    public void ParseSegmentItems_MissingEvidence_BecomesEmptyNotFailure()
    {
        // 本地小模型可能不认新字段：缺 evidence 不能让整条被丢掉
        const string raw = """{"items":[{"title":"A","body":"正文 A","kind":"knowledge"}]}""";

        var items = NotePromptBuilder.ParseSegmentItems(raw, 1);

        Assert.Single(items);
        Assert.Equal("", items[0].Evidence);
    }

    [Fact]
    public void AssembleFromItems_PutsExamItemsIntoTheirOwnSection()
    {
        // 这是"错误断言被当成事实"的根治办法：判断题的选项原句必须落在「习题（原题）」一节，
        // 并带一句"这些是题目，不是结论"的说明。
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("钠的保存", "钠保存在煤油中", "knowledge"),
            Item("某真题", "金属钠着火时，可用泡沫灭火器或干砂灭火", "exam"),
        };

        string note = NotePromptBuilder.AssembleFromItems("化学", items);

        Assert.Contains("## 习题（原题）", note);
        Assert.Contains("不是老师的结论", note);
        // 原题必须落在「习题」一节里，而不是被当成核心知识点
        int examSection = note.IndexOf("## 习题（原题）", StringComparison.Ordinal);
        int examBody = note.IndexOf("可用泡沫灭火器", StringComparison.Ordinal);
        Assert.True(examBody > examSection, "原题正文必须出现在习题一节之后");
    }

    [Fact]
    public void AssembleFromItems_NoExamItems_OmitsExamSection()
    {
        var items = new List<NotePromptBuilder.NoteItem> { Item("A", "正文 A", "knowledge") };

        string note = NotePromptBuilder.AssembleFromItems("课程", items);

        Assert.DoesNotContain("## 习题（原题）", note);
    }

    [Fact]
    public void Deduplicate_CollapsesTheDuplicationThatWasMeasuredInRealNotes()
    {
        // 这组数据来自化学课实测笔记的形态：同一句话在「核心知识点」与「重点及易错点」各出现一次，
        // 标题由模型各起各的。旧键（kind + 标题 + 正文）会把它们全部判为"不同条目"，
        // 于是整篇笔记 25.4% 的行是重复的。新键（只按正文）把它们收敛掉。
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("金属钠的保存", "金属钠可以放置在煤油或石蜡油中保存", "knowledge", segment: 1),
            Item("钠的存放", "金属钠可以放置在煤油或石蜡油中保存", "knowledge", segment: 2),
            Item("易错：钠的保存", "金属钠可以放置在煤油或石蜡油中保存", "pitfall", segment: 3),
            Item("高压钠灯", "高压钠灯发出的黄光透雾能力强、射程较远", "knowledge", segment: 1),
            Item("高压钠灯的特点", "高压钠灯发出的黄光透雾能力强、射程较远", "pitfall", segment: 2),
        };

        var result = NotePromptBuilder.Deduplicate(items);

        Assert.Equal(2, result.Items.Count);          // 两个知识点各剩一条
        Assert.Equal(3, result.MergedCount);
        Assert.Contains(result.Items, i => i.IsPitfall);   // "更具体"的归类被保留
    }

    [Fact]
    public void Deduplicate_PreservesOrderAndNeverReturnsNull()
    {
        var items = new List<NotePromptBuilder.NoteItem> { Item("B", "b"), Item("A", "a") };

        var result = NotePromptBuilder.Deduplicate(items);

        Assert.Equal(new[] { "B", "A" }, result.Items.Select(i => i.Title).ToArray());
        Assert.Empty(NotePromptBuilder.Deduplicate(new List<NotePromptBuilder.NoteItem>()).Items);
    }

    // ── 最终一遍输出解析 ──────────────────────────────────────

    [Fact]
    public void ParseFinalPlan_AcceptsCompletePermutation()
    {
        const string raw = "{\"items\":[" +
                           "{\"index\":2,\"transition\":\"\",\"body\":\"B 的正文\"}," +
                           "{\"index\":1,\"transition\":\"在此基础上\",\"body\":\"A 的正文\"}]}";

        var plan = NotePromptBuilder.ParseFinalPlan(raw, 2);

        Assert.NotNull(plan);
        Assert.Equal(new[] { 2, 1 }, plan!.Select(p => p.Index).ToArray());
        Assert.Equal("在此基础上", plan[1].Transition);
    }

    [Theory]
    [InlineData("{\"items\":[{\"index\":1,\"transition\":\"\",\"body\":\"A\"}]}", 2)]        // 少一条
    [InlineData("{\"items\":[{\"index\":1,\"body\":\"A\"},{\"index\":1,\"body\":\"B\"}]}", 2)] // 重复索引
    [InlineData("{\"items\":[{\"index\":1,\"body\":\"A\"},{\"index\":3,\"body\":\"B\"}]}", 2)] // 越界
    [InlineData("{\"items\":[{\"index\":1,\"body\":\"A\"},{\"index\":2,\"body\":\"\"}]}", 2)]  // 空正文
    [InlineData("{\"order\":[1,2]}", 2)]                                                       // 旧/错误结构
    [InlineData("完全不是 JSON", 2)]
    [InlineData(null, 2)]
    public void ParseFinalPlan_RejectsAnythingButACompletePermutation(string? raw, int count)
    {
        // 宁可退回确定性拼装，也不接受"缺项 / 重复 / 正文为空"的排序结果
        Assert.Null(NotePromptBuilder.ParseFinalPlan(raw, count));
    }

    // ── 逐条回校（幻觉拦截） ───────────────────────────────────

    [Fact]
    public void Verify_PassesWhenEveryBodyIsKeptVerbatim()
    {
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("A", "当 x 趋近于 a 时，函数 f(x) 的值趋近于 L，这就是极限的定义。"),
            Item("B", "洛必达法则只适用于 0/0 型或 ∞/∞ 型未定式，且分母导数不为零。"),
        };
        var polished = items.Select(i => i.Body).ToList();

        Assert.True(NoteItemVerifier.Verify(polished, items).AllVerified);
    }

    [Fact]
    public void Verify_PassesWhenTransitionsAreAddedAroundTheBody()
    {
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("A", "洛必达法则只适用于 0/0 型或 ∞/∞ 型未定式，且分母导数不为零。"),
        };
        var polished = new List<string> { "接下来看适用条件。" + items[0].Body };

        Assert.True(NoteItemVerifier.Verify(polished, items).AllVerified);
    }

    [Fact]
    public void Verify_FailsWhenAnItemBodyIsDropped()
    {
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("A", "当 x 趋近于 a 时函数值趋近于 L，这是极限的定义。"),
            Item("B", "洛必达法则只适用于 0/0 型或 ∞/∞ 型未定式，且分母导数不为零。"),
        };
        var polished = new List<string> { items[0].Body, "（这一条被模型悄悄删掉了）" };

        var report = NoteItemVerifier.Verify(polished, items);

        Assert.False(report.AllVerified);
        Assert.Equal(1, report.FailedIndex);
        Assert.Equal("B", report.FailedTitle);
    }

    [Fact]
    public void Verify_FailsWhenBodyIsTruncatedMidway()
    {
        // 截断式改写：开头还在、后半段被删 —— 只看前缀的校验会漏掉，所以探针要取中尾
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("A", "洛必达法则的使用条件是分子分母同时趋于零或同时趋于无穷大且分母导数不为零。"),
        };
        var polished = new List<string> { "洛必达法则的使用条件是分子分母同时趋于零" };

        Assert.False(NoteItemVerifier.Verify(polished, items).AllVerified);
    }

    [Fact]
    public void Verify_FailsWhenItemCountDiffers()
    {
        // 条数对不上是最危险的信号（模型揉条/丢条），直接拒绝，不做按位猜测
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("A", "第一条正文内容足够长以便校验通过"),
            Item("B", "第二条正文内容足够长以便校验通过"),
        };

        Assert.False(NoteItemVerifier.Verify(new List<string> { items[0].Body }, items).AllVerified);
        Assert.False(NoteItemVerifier.Verify(new List<string>(), items).AllVerified);
        Assert.False(NoteItemVerifier.Verify(null, items).AllVerified);
    }

    [Fact]
    public void Verify_ShortBodiesAreSkippedInsteadOfFalselyFailing()
    {
        // 过短正文（如公式片段）无法可靠抽探针，跳过校验而不是误判
        var items = new List<NotePromptBuilder.NoteItem> { Item("A", "0/0") };
        var polished = new List<string> { "0/0 型" };

        Assert.True(NoteItemVerifier.Verify(polished, items).AllVerified);
    }

    [Fact]
    public void Verify_EmptyItemList_IsTriviallyOk()
    {
        Assert.True(NoteItemVerifier.Verify(new List<string>(), new List<NotePromptBuilder.NoteItem>()).AllVerified);
    }

    [Fact]
    public void FallbackNotice_NamesTheFailingItemAndStatesContentIsIntact()
    {
        var items = new List<NotePromptBuilder.NoteItem> { Item("极限定义", "正文内容足够长以便校验") };
        var report = new NoteItemVerifier.VerificationReport(false, 1, 0, "极限定义");

        string notice = NoteItemVerifier.FallbackNotice(items.Count, report);

        Assert.Contains("极限定义", notice);
        Assert.Contains("按原条目顺序拼装", notice);
    }

    // ── 拼装 ─────────────────────────────────────────────────

    [Fact]
    public void AssembleFromItems_GroupsByKindAndKeepsBodyVerbatim()
    {
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("知识点一", "正文一", "knowledge"),
            Item("易错一", "正文二", "pitfall"),
            Item("知识点二", "正文三", "knowledge"),
        };

        string note = NotePromptBuilder.AssembleFromItems("高等数学", items);

        Assert.StartsWith("# 高等数学", note);
        Assert.Contains("## 核心知识点", note);
        Assert.Contains("## 重点及易错点", note);
        // 正文逐字保留
        Assert.Contains("正文一", note);
        Assert.Contains("正文二", note);
        Assert.Contains("正文三", note);
        // 易错点被分到第二节：其正文应出现在「重点及易错点」之后
        Assert.True(note.IndexOf("正文二", System.StringComparison.Ordinal)
                    > note.IndexOf("## 重点及易错点", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AssembleFromItems_NoPitfalls_OmitsThatSection()
    {
        string note = NotePromptBuilder.AssembleFromItems("课程", new[] { Item("A", "只有知识点") });

        Assert.DoesNotContain("## 重点及易错点", note);
    }

    [Fact]
    public void AssembleFromPolished_UsesModelOrderAndTransitionsWithOriginalBodies()
    {
        var items = new List<NotePromptBuilder.NoteItem>
        {
            Item("A", "正文 A", "knowledge"),
            Item("B", "正文 B", "knowledge"),
        };
        var plan = new List<NotePromptBuilder.PolishedItem>
        {
            new(2, "", "正文 B"),
            new(1, "在此基础上", "正文 A"),
        };

        string note = NotePromptBuilder.AssembleFromPolished("课程", items, plan);

        // 顺序按计划：B 先于 A
        Assert.True(note.IndexOf("正文 B", System.StringComparison.Ordinal)
                    < note.IndexOf("正文 A", System.StringComparison.Ordinal));
        Assert.Contains("在此基础上", note);
    }
}
