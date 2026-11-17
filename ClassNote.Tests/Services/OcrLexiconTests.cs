using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 转写词典（<see cref="OcrLexicon"/>）的回归测试。
///
/// 这里锁住的不是"能修多少"，而是**不能多修**：
/// 词典校正的失败模式不是漏改，而是把正常内容改成词典里恰好存在的另一个词。
/// 因此每条"允许修复"的断言旁边，都配了一条"禁止修复"的断言。
/// </summary>
public class OcrLexiconTests
{
    /// <summary>一节化学课的转写样子（术语在口语里会反复出现）。</summary>
    private const string ChemistryTranscript =
        "【麦克风（教室现场）】\n" +
        "今天讲碳酸钠和碳酸氢钠，碳酸钠俗名纯碱，碳酸氢钠俗名小苏打。\n" +
        "碳酸钠溶于水放热，碳酸氢钠的溶解度比碳酸钠小。\n" +
        "过氧化钠和二氧化碳反应生成碳酸钠和氧气，碳酸钠溶液显碱性。\n" +
        "碳酸钠溶液能使酚酞变红。\n" +
        "【系统声音（课件/网课播放）】\n" +
        "碳酸钠、碳酸氢钠的性质比较。";

    [Fact]
    public void FromTranscript_CollectsRepeatedCjkTerms()
    {
        var lexicon = OcrLexicon.FromTranscript(ChemistryTranscript);

        Assert.True(lexicon.Contains("碳酸钠"));
        Assert.True(lexicon.Contains("碳酸氢钠"));
        // 「溶液」在转写里出现两次以上，因此进入词典
        Assert.True(lexicon.Contains("溶液"));
    }

    [Fact]
    public void FromTranscript_DropsMidSentenceOneOffTerms()
    {
        // 只出现一次、且落在句子内部（两侧都是汉字）的 n-gram 可能是转写本身的偶发噪声，
        // 不当"可信词"。注意判据是**"出现≥2次 或 落在自然边界上"**，
        // 因此这里必须把它放在句子中间才能验证"单次且无边界证据不采信"。
        var lexicon = OcrLexicon.FromTranscript("今天课程内容先说碳酸钠接着讲别的");

        Assert.False(lexicon.Contains("碳酸钠"));
    }

    [Fact]
    public void FromTranscript_SkipsSectionLabelLines()
    {
        // 分区标注行是管道自己加的标记，不是课堂内容
        var lexicon = OcrLexicon.FromTranscript("【麦克风（教室现场）】\n碳酸钠溶于水，碳酸钠显碱性。");

        Assert.False(lexicon.Contains("教室现场"));
        Assert.True(lexicon.Contains("碳酸钠"));
    }

    [Fact]
    public void FromTranscript_AlwaysCarriesBuiltInChemistryTerms()
    {
        // 内置化学式只用于**消歧**：候选词归一后命中它时才允许替换
        var lexicon = OcrLexicon.FromTranscript(null);

        Assert.True(lexicon.Contains("Na2CO3"));
        Assert.True(lexicon.Contains("NaHCO3"));
    }

    [Fact]
    public void FromTranscript_NullTranscript_YieldsEmptyLexicon()
    {
        var lexicon = OcrLexicon.FromTranscript(null);
        Assert.False(lexicon.Contains("碳酸钠"));
    }

    [Fact]
    public void TryResolve_ExactMatchWins()
    {
        var lexicon = OcrLexicon.FromTranscript(ChemistryTranscript);

        Assert.True(lexicon.TryResolve("碳酸钠", forgiving: false, allowOneEdit: false, out var canonical));
        Assert.Equal("碳酸钠", canonical);
    }

    [Fact]
    public void TryResolve_MapsDigitLetterConfusionOntoTheFormula()
    {
        // `Na2C03`：OCR 把 O 认成 0。混淆等价类（0↔o）是这条修复的唯一依据。
        var lexicon = OcrLexicon.FromTranscript(ChemistryTranscript);

        Assert.True(lexicon.TryResolve("Na2C03", forgiving: true, allowOneEdit: false, out var canonical));
        Assert.Equal("Na2CO3", canonical);
    }

    [Fact]
    public void TryResolve_WithoutForgiving_KeepsTheCorruptedForm()
    {
        var lexicon = OcrLexicon.FromTranscript(ChemistryTranscript);

        Assert.False(lexicon.TryResolve("Na2C03", forgiving: false, allowOneEdit: false, out _));
    }

    [Fact]
    public void FromTranscript_DropsCrossWordFragmentsButKeepsRealTerms()
    {
        // 真实转写：术语通常至少有一次落在自然边界上（句首/标点旁），跨词碎片则不。
        var lexicon = OcrLexicon.FromTranscript("溶液显碱性。碳酸钠俗名纯碱。碳酸氢钠是小苏打。");

        Assert.True(lexicon.Contains("溶液"));
        Assert.True(lexicon.Contains("碳酸钠"));
        Assert.True(lexicon.Contains("碳酸氢钠"));
        // 两端都落在句子内部的跨词碎片不会进入词典
        Assert.False(lexicon.Contains("酸钠溶"));
        Assert.False(lexicon.Contains("钠溶"));
    }

    [Fact]
    public void TryResolve_ExactMatchIsNotTreatedAsAGuess()
    {
        // 精确命中是最高优先级：它没有"猜"的成分，不应当被歧义检查一票否决
        var lexicon = OcrLexicon.FromTranscript("碳酸钠俗名纯碱。碳酸钠溶液显碱性。");

        Assert.True(lexicon.TryResolve("碳酸钠", forgiving: true, allowOneEdit: true, out var canonical));
        Assert.Equal("碳酸钠", canonical);
    }

    [Fact]
    public void TryResolve_AmbiguousCandidate_RefusesToGuess()
    {
        // `碳酸锂` 不是词典词，但可能在 1 个编辑距离内同时命中两个真实术语——
        // 这时**必须拒绝**。猜错等于在笔记里写一个看不出错的错化学式。
        var lexicon = OcrLexicon.FromTranscript("碳酸钠俗名纯碱。碳酸钾是钾肥。");

        Assert.False(lexicon.TryResolve("碳酸锂", forgiving: true, allowOneEdit: true, out _));
    }

    [Fact]
    public void TryResolve_UnknownTerm_ReturnsFalse()
    {
        var lexicon = OcrLexicon.FromTranscript(ChemistryTranscript);

        Assert.False(lexicon.TryResolve("完全无关的词汇", forgiving: true, allowOneEdit: true, out _));
    }

    [Fact]
    public void EditDistanceWithinOne_HandlesReplaceInsertDelete()
    {
        Assert.True(OcrLexicon.EditDistanceWithinOne("碳酸钠", "碳酸钾"));   // 替换
        Assert.True(OcrLexicon.EditDistanceWithinOne("溶液", "溶液池"));      // 插入
        Assert.True(OcrLexicon.EditDistanceWithinOne("溶液池", "溶液"));      // 删除
        Assert.True(OcrLexicon.EditDistanceWithinOne("碳酸钠", "碳酸"));      // 差一个字（删除）
        Assert.False(OcrLexicon.EditDistanceWithinOne("碳酸钠", "氯化钠溶液")); // 差了 4 个编辑
    }

    [Fact]
    public void IsBoundaryAnchoredSingleEdit_OnlyAcceptsProvableSplitRepairs()
    {
        // 自身：命中
        Assert.True(OcrLexicon.IsBoundaryAnchoredSingleEdit("溶液", "溶液"));
        // 多吐了一个偏旁（`氵`）：长度差 1，去掉偏旁后逐字相同 → 可确证。三个位置都成立
        Assert.True(OcrLexicon.IsBoundaryAnchoredSingleEdit("氵溶液", "溶液"));
        Assert.True(OcrLexicon.IsBoundaryAnchoredSingleEdit("溶氵液", "溶液"));
        Assert.True(OcrLexicon.IsBoundaryAnchoredSingleEdit("溶液氵", "溶液"));

        // 词首字被读成偏旁（`氵液` ← 「溶液」）：除首字外逐字相同，且首字确实是偏旁 → 可确证修复
        Assert.True(OcrLexicon.IsBoundaryAnchoredSingleEdit("氵液", "溶液"));

        // `氵溶` 与「溶液」其实是**两处**差异（氵↔溶、溶↔液 正好错位）
        Assert.False(OcrLexicon.IsBoundaryAnchoredSingleEdit("氵溶", "溶液"));
        // 末位形近误识（液 → 池）：差异两侧都不是偏旁 → 拒绝（不做字形相似度猜测）
        Assert.False(OcrLexicon.IsBoundaryAnchoredSingleEdit("溶池", "溶液"));
        // 去掉一个偏旁之后仍然对不上 → 拒绝（这条锁住"只有偏旁调整才叫可确证"）
        Assert.False(OcrLexicon.IsBoundaryAnchoredSingleEdit("氵氵", "溶液"));
    }

    [Fact]
    public void Tokenize_SplitsLatinRunsAndSingleCjkCharacters()
    {
        var tokens = OcrLexicon.Tokenize("Na2CO3 与 碳酸钠");

        Assert.Contains("Na2CO3", tokens);
        Assert.Contains("碳", tokens);
        Assert.Contains("酸", tokens);
        Assert.DoesNotContain("碳酸钠", tokens); // 汉字逐字切分：术语靠计数阶段重组
    }

    [Fact]
    public void NormalizeKey_IsCaseAndWidthInsensitive()
    {
        Assert.Equal(OcrLexicon.NormalizeKey("na2co3"), OcrLexicon.NormalizeKey("Ｎａ２ＣＯ３"));
    }
}
