using System;
using System.IO;
using System.Text;
using ClassNote.Models;
using ClassNote.Services;
using Markdig;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 笔记公式渲染链路的回归测试。
///
/// 现场（用户截图）：笔记里写的是 `d[i][j]`，页面却渲染成「d / i / j」三行居中大字。
/// 根因是 MathJax 的分隔符被配成了圆括号与方括号：
/// <code>tex: { inlineMath: [['(', ')']], displayMath: [['[', ']']] }</code>
/// 本意应是 LaTeX 的 <c>\(...\)</c> / <c>\[...\]</c>（反斜杠在字符串里丢了）。
/// 后果远不止下标：**笔记里每一对圆括号/方括号都被当成公式**，行间公式还会被 CSS 居中并撑开空隙。
///
/// 这组测试从两侧锁住它：
/// 1. 生成 HTML 的那一侧（Markdig）对普通方括号/圆括号不得产出公式节点；
/// 2. 页面模板里的 MathJax 配置必须使用正确分隔符，且不得再出现裸括号形式。
/// </summary>
public class NoteMathRenderingTests
{
    /// <summary>与 NoteViewModel / PdfExportService 完全一致的管线。</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .DisableHtml()
        .Build();

    private static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    [Fact]
    public void PlainBrackets_AreNotMath()
    {
        // 用户笔记里的原句：d[i][j] 必须原样保留为普通文本，不得变成公式节点
        var html = Render("第一步还是定义问题：d[i][j] 等于 S1 的前 i 个字母。");

        Assert.DoesNotContain("math", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("d[i][j]", html);
    }

    [Fact]
    public void PlainParentheses_AreNotMath()
    {
        var html = Render("f(n) = min(f(n-1)+1, f(n-5)+1)");

        Assert.DoesNotContain("math", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("f(n)", html);
    }

    [Fact]
    public void DollarDelimitedMath_BecomesMathNode()
    {
        // LLM 用 $...$ 写的公式：Markdig 数学扩展应把它变成可被 MathJax 处理的节点
        var html = Render("转移方程：$d_{i,j} = d_{i-1,j-1} + 1$。");

        Assert.Contains("math", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("d_{i,j}", html);          // 公式内容不能被 Markdown 吃掉（下划线不能变斜体）
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void DollarMath_SubscriptUnderscoreSurvives()
    {
        // 这是 $...$ 相对"裸文本"的核心价值：公式里的 _ 与 * 不会被当成 Markdown 强调标记
        var html = Render("$a_i * b_j$");

        Assert.DoesNotContain("<em>", html);
        Assert.Contains("a_i * b_j", html);
    }

    [Fact]
    public void DisplayMath_BecomesBlockMath()
    {
        var html = Render("$$\nf(n) = \\min_{1 \\le k \\le 3}(f(n-k) + 1)\n$$");

        Assert.Contains("math", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("f(n)", html);
    }

    /// <summary>
    /// 把真实笔记（含公式/括号/下划线）经**生产同款**管线渲染后导出，便于人工核对与留痕。
    /// 断言写死"不得出现公式节点"的部分由上面几条覆盖，这条只保证不抛异常且内容不丢。
    /// </summary>
    [Fact]
    public void RealisticNote_RendersWithoutLosingContent()
    {
        var markdown = string.Join("\n", new[]
        {
            "# 数学",
            "",
            "## 核心知识点",
            "",
            "**LCS 问题定义（状态定义）**：第一步还是定义问题：d[i][j] 等于 S1 的前 i 个字母和 S2 的前 j 个字母。",
            "",
            "**LCS 状态转移**：如果 S1[i-1] == S2[j-1]，那么 $d[i][j] = 1 + d[i-1][j-1]$。",
            "",
            "**硬币问题**：$f(n) = \\min(f(n-1)+1, f(n-5)+1, f(n-11)+1)$",
        });

        var html = Render(markdown);

        Assert.Contains("LCS 问题定义", html);
        Assert.Contains("d[i][j]", html);              // 普通文本原样保留
        Assert.Contains("math", html, StringComparison.OrdinalIgnoreCase); // 真公式被识别
        Assert.Contains("f(n)", html);

        // 落盘留证（与 TestResults 目录的既有做法一致）
        var dump = Path.Combine(Path.GetTempPath(), "classnote-math-render.html");
        File.WriteAllText(dump, html, Encoding.UTF8);
        Assert.True(File.Exists(dump));
    }

    // ── 模板侧（MathJax 配置与 CSS 选择器）──────────────────────
    //
    // 这一组锁的是"事故本身"：配置里绝不能再出现裸括号形式的分隔符。
    // 生成 HTML 的那侧已经由上面的测试保证不会误产出公式节点，
    // 但模板侧的配置错一次就会把**所有**括号文本变成公式（历史事故即是如此）。

    [Fact]
    public void MathJaxConfig_UsesLatexDelimiters_NotBareBrackets()
    {
        var config = NoteHtmlRenderer.MathJaxConfig;

        // 行内与行间都必须是 LaTeX 标准写法（Markdig 数学扩展吐出的正是这一对）
        Assert.Contains(@"['\\(', '\\)']", config);
        Assert.Contains(@"['\\[', '\\]']", config);

        // 历史事故的三种错误写法，一个都不许回来
        Assert.DoesNotContain("['(', ')']", config);
        Assert.DoesNotContain("['[', ']']", config);
        Assert.DoesNotContain("[['$', '$']]", config);   // 美元符号不该被当公式分隔符
    }

    [Fact]
    public void Document_CssMatchesMarkdigClassNames()
    {
        // Markdig 产出的是 span.math / div.math；写 .math.inline / .math.display 等于样式从未生效。
        // 断言只针对**规则本身**（带花括号），这样文档里解释历史事故的注释不会误伤。
        var html = NoteHtmlRenderer.RenderDocument("$x$ 与 $$y$$");

        Assert.Contains("span.math {", html);
        Assert.Contains("div.math {", html);
        Assert.DoesNotContain(".math.inline {", html);
        Assert.DoesNotContain(".math.display {", html);
        Assert.Contains("mjx-container", html);   // 长公式的横向滚动兜底
    }

    [Fact]
    public void Document_KeepsMathJaxScriptAndSecurityPolicy()
    {
        var html = NoteHtmlRenderer.RenderDocument("# 标题");

        Assert.Contains("https://appassets.local/mathjax-tex-svg.js", html); // 离线 MathJax
        Assert.Contains("Content-Security-Policy", html);                    // CSP 仍在
        Assert.Contains("script-src 'unsafe-inline' https://appassets.local", html);
    }
}
