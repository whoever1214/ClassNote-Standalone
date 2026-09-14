using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// OCR 文本规范化的回归测试。
///
/// 这些断言锁住的是两件互相冲突的事：**要合并逐字空格，但绝不能碰数字/字母之间的空格**。
/// 后者是硬约束——最长上升子序列的样例输入 `1 7 3 5 9 4 8` 被合并成 `1735948`，
/// 就是比留噪声严重得多的语义事故（实测素材里真的有这一行）。
/// </summary>
public class OcrTextNormalizerTests
{
    [Fact]
    public void Normalize_MergesCharactersSplitBySpaces()
    {
        Assert.Equal("动态规划", OcrTextNormalizer.Normalize("动 态 规 划"));
    }

    [Fact]
    public void Normalize_PreservesDigitSequenceSpacing()
    {
        // 一节课的 OCR 里真的出现过这一行：它是"最长上升子序列"的样例输入
        string input = "1 7 3 5 9 4 8";
        Assert.Equal(input, OcrTextNormalizer.Normalize(input));
        Assert.DoesNotContain("1735948", OcrTextNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_DoesNotGlueEnglishWords()
    {
        Assert.Equal("Dynamic Programming", OcrTextNormalizer.Normalize("Dynamic Programming"));
    }

    [Fact]
    public void Normalize_MergesCjkWithFullwidthPunctuation()
    {
        Assert.Equal("规划（英文：Dynamic", OcrTextNormalizer.Normalize("规 划 （ 英 文 ： Dynamic"));
    }

    [Fact]
    public void Normalize_FormulaLine_ConvertsFullwidthSymbolsAndTightensOperators()
    {
        // 真实素材的样子：公式被逐字拆开、括号是全角、减号被认成汉字「一」
        string input = "f （ n ） = min （ f （ n 一 1 ） + 1 ， f （ n-5 ） + 1 ）";

        string result = OcrTextNormalizer.Normalize(input);

        Assert.Equal("f(n)=min(f(n-1)+1,f(n-5)+1)", result);
    }

    [Fact]
    public void Normalize_RepairsTheMinMisreadThatBrokeTheSmallModel()
    {
        // 这一段是让本地小模型把整段乱码抄进笔记的元凶
        string input = "3 · 从 1 ～ n 循 环 ， 根 据 公 式 f （ n ） = m 主 n （ f （ n 一 1 ） + 1 ）";

        string result = OcrTextNormalizer.Normalize(input);

        Assert.Contains("min(", result);
        Assert.DoesNotContain("主", result);
        Assert.Contains("f(n-1)+1", result);
    }

    [Fact]
    public void Normalize_KeepsProsePunctuationAsChinese()
    {
        // 非公式行不做全角转半角：中文正文的标点习惯不能被打乱
        string result = OcrTextNormalizer.Normalize("他 说 ： “ 今 天 讲 动 态 规 划 ” 。");

        Assert.Contains("：", result);
        Assert.Contains("“", result);
        Assert.DoesNotContain(":", result);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        string input = "动 态 规 划 f （ n ） = m 主 n （ 1 ， 2 ） 1 7 3 5 9 4 8";

        string once = OcrTextNormalizer.Normalize(input);
        string twice = OcrTextNormalizer.Normalize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_PreservesLineStructureButTrimsBlankEdges()
    {
        string result = OcrTextNormalizer.Normalize("\n动 态 规 划\n\n1 7 3 5\n");

        Assert.Equal("动态规划\n\n1 7 3 5", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \u3000 ")]
    public void Normalize_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal("", OcrTextNormalizer.Normalize(input));
    }

    [Fact]
    public void LooksLikeFormula_DistinguishesFormulaFromProse()
    {
        Assert.True(OcrTextNormalizer.LooksLikeFormula("f ( n ) = 1"));
        Assert.True(OcrTextNormalizer.LooksLikeFormula("min ( 5 , 3 )"));
        Assert.True(OcrTextNormalizer.LooksLikeFormula("for ( int i = 0 ;"));
        Assert.False(OcrTextNormalizer.LooksLikeFormula("今 天 讲 动 态 规 划"));
        Assert.False(OcrTextNormalizer.LooksLikeFormula("1 7 3 5 9 4 8"));
    }

    [Fact]
    public void Normalize_UnknownMisreads_AreLeftAloneInsteadOfGuessed()
    {
        // 「巨」「丿」这类无法确定的误识**不能猜**：猜错等于在笔记里写一个看不出错的错公式，
        // 这正是提示词规则要求"确定不了就标注 OCR 不清"的原因。
        const string input = "f 巨 一 1 ] + 1";

        string result = OcrTextNormalizer.Normalize(input);

        Assert.Contains("巨", result);
    }
}
