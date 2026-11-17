using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// LaTeX → Unicode 近似排版的回归测试（P2）。
///
/// 它锁住两条相反的要求：
/// · **能转的要转**（否则 PDF 里印的是 <c>\min</c>、<c>\alpha</c> 这种源码）；
/// · **转不了的必须原样保留**（宁可让用户看见一个陌生命令，也不要转出错误的内容）。
///
/// 特别是"半下标"那种情况：`d_{i,j}` 里逗号没有下标映射，此时**整体放弃**转下标，
/// 而不是把 i、j 转成下标、把逗号留在正文里——半转的结果比不转更容易误读。
/// </summary>
public class LatexToUnicodeTests
{
    [Theory]
    [InlineData(@"\min(f(n-1)+1)", "min(f(n-1)+1)")]
    [InlineData(@"\le", "≤")]
    [InlineData(@"a \times b", "a × b")]
    [InlineData(@"\alpha + \beta", "α + β")]
    [InlineData(@"\Omega", "Ω")]
    [InlineData(@"\infty", "∞")]
    [InlineData(@"x \to y", "x → y")]
    public void Convert_MapsCommonCommandsToUnicode(string input, string expected)
    {
        Assert.Equal(expected, LatexToUnicode.Convert(input));
    }

    [Theory]
    [InlineData("x^2", "x²")]
    [InlineData("a_i", "aᵢ")]
    [InlineData(@"d_{i-1}", "dᵢ₋₁")]
    [InlineData("m_1", "m₁")]
    public void Convert_TurnsScriptsIntoUnicodeScripts(string input, string expected)
    {
        Assert.Equal(expected, LatexToUnicode.Convert(input));
    }

    [Fact]
    public void Convert_PartiallyMappableSubscript_IsLeftAlone()
    {
        // 逗号没有下标映射：整体放弃（`d_{i,j}` 保持原样），而不是转出"半下标"
        string result = LatexToUnicode.Convert("d_{i,j}");

        Assert.Equal("d_{i,j}", result);
    }

    [Theory]
    [InlineData(@"\frac{1}{2}", "1/2")]
    [InlineData(@"\frac{n}{2}", "n/2")]
    [InlineData(@"\frac{a+b}{c+d}", "a+b/(c+d)")]
    public void Convert_FlattensFractionsIntoReadableText(string input, string expected)
    {
        Assert.Equal(expected, LatexToUnicode.Convert(input));
    }

    [Theory]
    [InlineData(@"\sqrt{x}", "√(x)")]
    [InlineData(@"\sqrt{b^2-4ac}", "√(b²-4ac)")]
    public void Convert_RendersRoots(string input, string expected)
    {
        Assert.Equal(expected, LatexToUnicode.Convert(input));
    }

    [Theory]
    [InlineData(@"\left( x \right)", "( x )")]
    [InlineData(@"a \quad b", "a b")]
    public void Convert_DropsLayoutOnlyCommands(string input, string expected)
    {
        // 这些命令只影响排版，丢掉后语义不变
        Assert.Equal(expected, LatexToUnicode.Convert(input));
    }

    [Fact]
    public void Convert_UnknownCommand_IsKeptVerbatim()
    {
        // 认不出来的命令原样保留——绝不猜它是什么意思
        string result = LatexToUnicode.Convert(@"\weirdcmd{x}");

        Assert.Contains(@"\weirdcmd", result);
        Assert.Contains("x", result);
    }

    [Fact]
    public void Convert_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", LatexToUnicode.Convert(null));
        Assert.Equal("", LatexToUnicode.Convert("   "));
    }

    [Theory]
    [InlineData(@"$x^2$", "x²")]
    [InlineData(@"$$E=mc^2$$", "E=mc²")]
    [InlineData(@"转移方程：$d_{i-1}$", "转移方程：dᵢ₋₁")]
    public void ConvertInlineMath_ReplacesDollarDelimitedFormulas(string input, string expected)
    {
        Assert.Equal(expected, LatexToUnicode.ConvertInlineMath(input));
    }

    [Fact]
    public void ConvertInlineMath_LeavesProseAndUnpairedDollarsAlone()
    {
        // 价格之类的孤立 $ 不能被吞掉
        Assert.Equal("售价 5$ 起", LatexToUnicode.ConvertInlineMath("售价 5$ 起"));
        Assert.Equal("普通一句话", LatexToUnicode.ConvertInlineMath("普通一句话"));
    }

    [Fact]
    public void Convert_IsIdempotentForAlreadyUnicodeText()
    {
        // 笔记里可能已经是 Unicode（P0-α 会把化学式转成下标）：再跑一次不得改坏
        const string unicode = "Na₂O₂ 与 Ca²⁺";

        Assert.Equal(unicode, LatexToUnicode.Convert(unicode));
    }
}
