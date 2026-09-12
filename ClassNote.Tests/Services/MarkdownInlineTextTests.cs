using System;
using ClassNote.Models;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// PDF 导出的行内文本处理测试。
///
/// 旧实现把 markdown 原文逐行打印进 PDF，于是导出件里会出现字面的 <c>**加粗**</c>、<c>$公式$</c>、
/// 反引号，且编号列表因识别正则的转义丢失（<c>^d+[.)]s+</c>）**从来没被识别到过**。
/// 这组测试锁住"标记被消化、文字一字不改"。
/// </summary>
public class MarkdownInlineTextTests
{
    private static string Plain(string line)
    {
        var runs = MarkdownInlineText.ParseLine(line);
        var sb = new System.Text.StringBuilder();
        foreach (var run in runs)
            sb.Append(run.Text);
        return sb.ToString();
    }

    [Fact]
    public void BoldMarkers_AreConsumed_ContentKept()
    {
        var runs = MarkdownInlineText.ParseLine("**动态规划定义**：一种思维方式");

        Assert.Equal(2, runs.Count);
        Assert.Equal("动态规划定义", runs[0].Text);
        Assert.True(runs[0].Bold);
        Assert.Equal("：一种思维方式", runs[1].Text);
        Assert.False(runs[1].Bold);
        Assert.Equal("动态规划定义：一种思维方式", Plain("**动态规划定义**：一种思维方式"));
    }

    [Fact]
    public void UnderscoreBold_IsAlsoConsumed()
    {
        Assert.Equal("重点", Plain("__重点__"));
    }

    [Fact]
    public void MathDelimiters_AreStripped_ContentKept()
    {
        // QuestPDF 排不了 LaTeX：脱掉 $ 分隔符、保留公式原文，比印出美元符有用
        Assert.Equal("f(n) = min(f(n-1)+1)", Plain("$f(n) = min(f(n-1)+1)$"));
        Assert.Equal("E=mc^2", Plain("$$E=mc^2$$"));
        Assert.Equal("转移方程：d[i][j] = 1 + d[i-1][j-1]",
            Plain("转移方程：$d[i][j] = 1 + d[i-1][j-1]$"));
    }

    [Fact]
    public void UnpairedDollar_IsKeptAsText()
    {
        // 价格之类的孤立 $ 不能被吞掉
        Assert.Equal("售价 5$ 起", Plain("售价 5$ 起"));
    }

    [Fact]
    public void InlineCode_IsConsumed()
    {
        var runs = MarkdownInlineText.ParseLine("调用 `Export()` 方法");
        Assert.Equal("调用 ", runs[0].Text);
        Assert.Equal("Export()", runs[1].Text);
        Assert.True(runs[1].Code);
    }

    [Fact]
    public void BareBrackets_SurviveUntouched()
    {
        // 数学下标写法 d[i][j] 必须原样保留（引用式链接语法绝不能吃掉它）
        Assert.Equal("d[i][j] 与 d[7][6]", Plain("d[i][j] 与 d[7][6]"));
    }

    [Fact]
    public void LinkMarkdown_KeepsTextDropsUrl()
    {
        Assert.Equal("课程主页", Plain("[课程主页](https://example.com)"));
    }

    [Fact]
    public void EscapedCharacters_AreRestored()
    {
        Assert.Equal("2*3 = 6", Plain(@"2\*3 = 6"));
    }

    [Theory]
    [InlineData("1. 第一点", "第一点")]
    [InlineData("2) 第二点", "第二点")]
    [InlineData("10、第十点", "第十点")]
    public void OrderedListMarkers_AreRecognised(string line, string expected)
    {
        // 旧正则 ^d+[.)]s+ 永远匹配不上，编号列表因此完全失效
        Assert.True(MarkdownInlineText.TryStripOrderedMarker(line, out var content));
        Assert.Equal(expected, content);
    }

    [Theory]
    [InlineData("3.14 是圆周率")]   // 小数，不是列表
    [InlineData("2026 年")]         // 纯数字开头
    [InlineData("d[i][j] 等于几")]  // 普通正文
    public void NonListLines_AreNotTreatedAsOrderedList(string line)
        => Assert.False(MarkdownInlineText.TryStripOrderedMarker(line, out _));

    [Fact]
    public void Export_LeaksNoMarkdownMarkers()
    {
        // 端到端（PDF 字节里检索不到标记文本）：PDF 是压缩流，不能直接 grep，
        // 因此这里验证的是"喂给 QuestPDF 的文本已经干净"这条不变式。
        var markdown = new[]
        {
            "# 数学",
            "",
            "**LCS 问题定义**：d[i][j] 等于 S1 的前 i 个字母。",
            "",
            "1. 第一步：定义状态",
            "2. 第二步：写转移方程 $d[i][j] = 1 + d[i-1][j-1]$",
        };

        foreach (var line in markdown)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var text = Plain(line.StartsWith("# ") ? line[2..]
                : line.StartsWith("1. ") || line.StartsWith("2. ") ? line[3..]
                : line);
            Assert.DoesNotContain("**", text);
            Assert.DoesNotContain("$", text);
            Assert.DoesNotContain("`", text);
        }

        var bytes = new PdfExportService().Export(new Note
        {
            Title = "数学",
            ContentMarkdown = string.Join("\n", markdown),
        });
        Assert.NotNull(bytes);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes!, 0, 5));
    }
}
