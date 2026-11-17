using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 化学式/方程式与疑点标注的回归测试（P0-α）。
///
/// 这一组测试的存在理由：**误改比漏改严重得多**。把 `utf8` 改成 `utf₈`、
/// 把 `1735948` 的下标补齐、把「银行」"修"成别的词，都会在用户看不见的地方弄坏内容。
/// 所以每条"应当改"的断言都配了"绝不许改"的断言。
/// </summary>
public class OcrChemistryNormalizeTests
{
    [Theory]
    [InlineData("Na2O2", "Na₂O₂")]
    [InlineData("Na2CO3", "Na₂CO₃")]
    [InlineData("Ca(OH)2", "Ca(OH)₂")]
    [InlineData("H2O", "H₂O")]
    [InlineData("KMnO4", "KMnO₄")]
    public void RepairChemicalTokens_TurnsFormulaDigitsIntoSubscripts(string input, string expected)
    {
        Assert.Equal(expected, OcrTextNormalizer.RepairChemicalTokens(input));
    }

    [Theory]
    [InlineData("utf8")]          // `Ut` 不是元素符号
    [InlineData("version2")]      // 小写开头
    [InlineData("resize2")]
    [InlineData("1735948")]       // 纯数字：最长上升子序列的样例输入
    [InlineData("1000")]
    [InlineData("pH7")]           // `pH` 不是"大写开头 + 元素"的形态
    public void RepairChemicalTokens_LeavesNonFormulasAlone(string input)
    {
        Assert.Equal(input, OcrTextNormalizer.RepairChemicalTokens(input));
    }

    [Fact]
    public void IsChemicalFormula_RequiresRealElementSymbols()
    {
        Assert.True(OcrTextNormalizer.IsChemicalFormula("Na2O2"));
        Assert.True(OcrTextNormalizer.IsChemicalFormula("Ca(OH)2"));
        Assert.True(OcrTextNormalizer.IsChemicalFormula("NaOH"));
        Assert.False(OcrTextNormalizer.IsChemicalFormula("Utf8"));
        Assert.False(OcrTextNormalizer.IsChemicalFormula("Version2"));
        Assert.False(OcrTextNormalizer.IsChemicalFormula("1735948"));
    }

    [Fact]
    public void RepairDigitLetterConfusion_FixesZeroFlankedByLetterAndDigit()
    {
        // OCR 把 O 认成 0 时，它两侧通常是"一个字母 + 一个数字"
        Assert.Equal("CO2", OcrTextNormalizer.RepairDigitLetterConfusion("C02"));
        Assert.Equal("NaO2", OcrTextNormalizer.RepairDigitLetterConfusion("Na02"));
        // `CC14` 是四氯化碳 CCl4（OCR 把 l 认成了 1）：两侧是字母与数字 → 修
        Assert.Equal("CCl4", OcrTextNormalizer.RepairDigitLetterConfusion("CC14"));
    }

    [Fact]
    public void RepairDigitLetterConfusion_LeavesNormalTokensAlone()
    {
        // `Na202` 里的 0 两侧都是数字，形态上无法判断它是不是被认错的 O → 放行（保守）。
        // 这个能力边界是刻意的：猜错会写出一个看不出错的错化学式，
        // 正确写法应由转写词典（转写里老师念对了）来兜。
        Assert.Equal("Na202", OcrTextNormalizer.RepairDigitLetterConfusion("Na202"));
        Assert.Equal("Na2O2", OcrTextNormalizer.RepairDigitLetterConfusion("Na2O2"));
        Assert.Equal("H2O", OcrTextNormalizer.RepairDigitLetterConfusion("H2O"));
        Assert.Equal("CCl4", OcrTextNormalizer.RepairDigitLetterConfusion("CCl4"));
        // 纯数字序列绝不动（数学课的样例输入）
        Assert.Equal("1735948", OcrTextNormalizer.RepairDigitLetterConfusion("1735948"));
        Assert.Equal("1000", OcrTextNormalizer.RepairDigitLetterConfusion("1000"));
        // 数字在词尾不算"夹在字母与数字之间"
        Assert.Equal("pH7", OcrTextNormalizer.RepairDigitLetterConfusion("pH7"));
        Assert.Equal("H20", OcrTextNormalizer.RepairDigitLetterConfusion("H20"));
    }

    [Fact]
    public void RepairDigitLetterConfusion_LeavesTrailingAndLeadingZerosAlone()
    {
        // 判据是"0 夹在字母数字之间"：词尾/词首的 0 没有任何依据可判，
        // `H20` 只能原样保留（它要变成 H₂O 需要词典或模型来判断）
        Assert.Equal("H20", OcrTextNormalizer.RepairDigitLetterConfusion("H20"));
        Assert.Equal("02", OcrTextNormalizer.RepairDigitLetterConfusion("02"));
    }

    [Fact]
    public void RepairDigitLetterConfusion_DoesNotTouchPureDigits()
    {
        Assert.Equal("1735948", OcrTextNormalizer.RepairDigitLetterConfusion("1735948"));
        Assert.Equal("1000", OcrTextNormalizer.RepairDigitLetterConfusion("1000"));
    }

    [Theory]
    [InlineData("Ca2+", "Ca²⁺")]
    [InlineData("OH-", "OH⁻")]
    [InlineData("Na+", "Na⁺")]
    public void ToFormulaUnicode_TurnsChargeIntoSuperScript(string input, string expected)
    {
        Assert.Equal(expected, OcrTextNormalizer.ToFormulaUnicode(input));
    }

    [Theory]
    [InlineData("2Na2O2+2H2O===4NaOH+O2t", "2Na₂O₂+2H₂O→4NaOH+O₂↑")]
    [InlineData("4Na + O2 == 2Na2O", "4Na+O₂→2Na₂O")] // 公式行的运算符两侧空格由既有规则紧排
    [InlineData("CaH2+2H2O===Ca(OH)2+2H2t", "CaH₂+2H₂O→Ca(OH)₂+2H₂↑")]
    public void NormalizeLine_StandardizesChemicalEquations(string input, string expected)
    {
        Assert.Equal(expected, OcrTextNormalizer.NormalizeLine(input));
    }

    [Fact]
    public void NormalizeLine_IsIdempotentForEquations()
    {
        string once = OcrTextNormalizer.NormalizeLine("2Na2O2+2H2O===4NaOH+O2t");
        string twice = OcrTextNormalizer.NormalizeLine(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_WithLexicon_RepairsFormulaUsingTranscriptVocabulary()
    {
        // 这正是化学板书实测的形态：`Na2C03`（O 认成 0）、`NaHC03`
        var lexicon = OcrLexicon.FromTranscript("碳酸钠碳酸钠碳酸氢钠碳酸氢钠");

        string result = OcrTextNormalizer.Normalize("Na2C03 和 NaHC03 都 能 与 酸 反 应", lexicon);

        Assert.Contains("Na₂CO₃", result);
        Assert.Contains("NaHCO₃", result);
        Assert.DoesNotContain("Na2C03", result);
    }

    /// <summary>
    /// 构造一个**完全可控**的词典：只用显式给出的化学术语，不从转写里推断。
    /// 这样测试锁定的就是"词典校正"这一层本身的行为，不会因为 n-gram 统计的细节而漂移。
    /// </summary>
    private static OcrLexicon ControlledLexicon(params string[] terms)
        => OcrLexicon.FromTranscript(null, terms);

    [Fact]
    public void Normalize_WithLexicon_RemovesStraySplitRadicalInTerm()
    {
        // 真实场景：OCR 把「溶液」拆成"偏旁 + 整词"（多吐了一个 `氵`），
        // 而同一节课的词典里有「溶液」。长度差 1 且多出来的是偏旁 → **可确证**的修复。
        var lexicon = ControlledLexicon("溶液");

        string result = OcrTextNormalizer.Normalize("配 制 氵溶 液 待 用", lexicon);

        // 规范化早期会把"逐字拆开"的空格合并，因此 `氵溶` 与 `液` 会连成 `氵溶液`
        Assert.Contains("溶液", result);
        Assert.DoesNotContain("氵", result);
    }

    [Fact]
    public void Normalize_WithLexicon_RefusesShapeSimilarityGuess()
    {
        // 能力边界（如实记录）：末位形近误识（`溶池` vs「溶液」）**不做修复**——
        // 判断"池"与"液"长得像需要字形相似度模型，靠一条字符串规则去猜就会开始改正常内容。
        // 这类疑点交由【OCR不清】标记。
        var lexicon = ControlledLexicon("溶液");

        string result = OcrTextNormalizer.Normalize("配 制 溶池 待 用", lexicon);

        Assert.Contains("溶池", result);
    }

    [Fact]
    public void Normalize_WithLexicon_RefusesToGuessWhenCandidateIsNotOneEditAway()
    {
        // `溶液池` 与词典词「溶液」差 2 处（多一个"池"且不是掐头去尾）……
        // 真正要锁的是：候选与词典词不构成"边界锚定的一次编辑"时，**原样保留**。
        var lexicon = ControlledLexicon("溶液");

        string result = OcrTextNormalizer.Normalize("配 制 氵谷氵夜 待 用", lexicon);

        Assert.DoesNotContain("溶液", result);
        Assert.Contains("氵", result);
    }

    [Fact]
    public void Normalize_WithLexicon_DoesNotInventTermsThatAreNotInTheDictionary()
    {
        // 词典里没有「氯化铁」：即便它"看起来像"化学术语，也绝不能凭空补出来
        var lexicon = ControlledLexicon("溶液");

        string result = OcrTextNormalizer.Normalize("氯 化 铁 是 红 褐 色 固 体", lexicon);

        Assert.Contains("氯化铁", result);
    }

    [Fact]
    public void Normalize_WithoutLexicon_KeepsV07Behaviour()
    {
        // 不传词典 = 词典校正整体关闭（旧调用点行为不变）；
        // 化学式规范化与词典无关，因此仍然生效（`Na2C03` 里的 0 被还原成 O、再转下标）
        string input = "Na2C03 和 NaHC03";

        string result = OcrTextNormalizer.Normalize(input);

        Assert.Contains("Na₂CO₃", result);
        Assert.Contains("NaHCO₃", result);
    }

    [Fact]
    public void AnnotateUncertain_FlagsLinesWithRareRadicals()
    {
        string result = OcrTextNormalizer.AnnotateUncertain("金 属 钠 放 在 煤 氵由 中");

        // 标记之后还可以跟疑点明细（`【OCR不清】（氵）`），所以断言用 Contains 而不是 EndsWith
        Assert.Contains(OcrTextNormalizer.UncertainSuffix, result);
        Assert.Contains("氵", result);
        Assert.StartsWith("金 属 钠 放 在 煤 氵由 中", result);
    }

    [Fact]
    public void AnnotateUncertain_LeavesNormalProseAlone()
    {
        // 常用字（一、口、日、十）是完全正常的汉字，不是"被拆开的偏旁"
        const string input = "金属钠着火时不可用水灭火";

        Assert.Equal(input, OcrTextNormalizer.AnnotateUncertain(input));
    }

    [Fact]
    public void AnnotateUncertain_IsIdempotent()
    {
        string once = OcrTextNormalizer.AnnotateUncertain("煤 氵由");
        string twice = OcrTextNormalizer.AnnotateUncertain(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_KeepsUncertainMarkerEvenWhenTheOnlySuspectWasRepaired()
    {
        // 修复成功也要留痕：`氵溶液` 会被修成 `溶液`，但"这一行 OCR 出过问题"必须仍然可见。
        // 否则用户看到的就是一条干净行，永远不知道这里曾经读错（实测踩过这个缺口）。
        string result = OcrTextNormalizer.Normalize("配 制 氵溶 液 待 用", ControlledLexicon("溶液"));

        Assert.Contains("溶液", result);
        Assert.Contains(OcrTextNormalizer.UncertainSuffix, result);
    }

    [Fact]
    public void Normalize_AnnotatesOnlyTheLineThatNeedsIt()
    {
        string result = OcrTextNormalizer.Normalize("金属钠可以保存在煤氵由中\n碳酸钠俗称纯碱");

        var lines = result.Split('\n');
        Assert.Contains(OcrTextNormalizer.UncertainSuffix, lines[0]);
        Assert.Equal("碳酸钠俗称纯碱", lines[1]);
    }

    [Theory]
    [InlineData("Ca2+", "Ca²⁺")]
    [InlineData("OH-", "OH⁻")]
    [InlineData("Na+", "Na⁺")]
    [InlineData("SO42-", "SO₄²⁻")]
    public void RepairIonSymbols_TurnsChargeIntoSuperScript(string input, string expected)
    {
        Assert.Equal(expected, OcrTextNormalizer.RepairIonSymbols(input));
    }

    [Fact]
    public void RepairIonSymbols_NormalizesFullwidthAndUnicodeSigns()
    {
        // OCR 常把 − / + 读成全角或 Unicode 减号
        Assert.Equal("Ca²⁻", OcrTextNormalizer.RepairIonSymbols("Ca2－"));
        Assert.Equal("OH⁻", OcrTextNormalizer.RepairIonSymbols("OH−"));
    }

    [Fact]
    public void RepairIonSymbols_LeavesProseAndArithmeticAlone()
    {
        // 破折号（单独一个 ——）后面没有字母数字，不构成离子
        Assert.Equal("这是一句话-", OcrTextNormalizer.RepairIonSymbols("这是一句话—"));
        // 算式：电荷号后面还接着字母 → 不碰
        Assert.Equal("A+B", OcrTextNormalizer.RepairIonSymbols("A+B"));
        Assert.Equal("x-y", OcrTextNormalizer.RepairIonSymbols("x-y"));
        // 纯数字序列带减号（数学课的样例输入形态）也不能被当成离子
        Assert.Equal("1735948-", OcrTextNormalizer.RepairIonSymbols("1735948-"));
    }

    [Fact]
    public void TryMergeEquationContinuation_JoinsSplitEquationLines()
    {
        bool merged = OcrTextNormalizer.TryMergeEquationContinuation(
            "4NaOH+O2t", "2Na2O2+2H2O===", out string result);

        Assert.True(merged);
        Assert.Equal("2Na2O2+2H2O===4NaOH+O2t", result);
    }

    [Fact]
    public void TryMergeEquationContinuation_RefusesProseAndNonEquationContinuations()
    {
        // 上一行不以反应记号结尾 → 不合并
        Assert.False(OcrTextNormalizer.TryMergeEquationContinuation("碳酸钠", "金属钠可以保存在煤油中", out _));
        // 普通段落：上一行以"-"结尾但合并后不像方程式 → 不合并
        Assert.False(OcrTextNormalizer.TryMergeEquationContinuation("C02", "今天讲-", out _));
    }

    [Fact]
    public void Normalize_JoinsSplitEquationAcrossLines()
    {
        string result = OcrTextNormalizer.Normalize("2Na2O2+2H2O===\n4NaOH+O2t");

        Assert.Contains("2Na₂O₂+2H₂O→4NaOH+O₂↑", result);
        Assert.DoesNotContain("===", result);
    }

    [Fact]
    public void Normalize_DigitSequenceSample_IsNeverGluedOrSubscripted()
    {
        // 数学课那条硬约束在化学规则加入之后必须依然成立
        Assert.Equal("1 7 3 5 9 4 8", OcrTextNormalizer.Normalize("1 7 3 5 9 4 8"));
    }
}
