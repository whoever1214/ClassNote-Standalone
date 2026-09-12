using Markdig;

namespace ClassNote.Services;

/// <summary>
/// 笔记 Markdown → HTML 文档的**唯一实现**（NoteViewModel 与渲染校验工具共用）。
///
/// 抽成独立一处的理由：公式渲染有三段必须彼此对齐——
/// Markdig 的数学扩展吐出的分隔符、MathJax 认的分隔符、以及给公式/容器定的 CSS 选择器。
/// 三者分散在两处代码里曾经直接导致线上事故（见下），现在它们只在这一个文件里出现。
///
/// ⚠️ **历史事故（用户截图：`d[i][j]` 被渲染成「d / i / j」三行居中大字）**：
/// MathJax 的分隔符曾写成 <c>inlineMath: [['(', ')']], displayMath: [['[', ']']]</c>
/// ——本意是 LaTeX 的 <c>\(...\)</c> / <c>\[...\]</c>，但反斜杠丢了。后果是：
/// · 笔记里**每一对圆括号与方括号都被当成公式**（`f(n)`、`d[i][j]`、`S1[i-1]` 全中招）；
/// · 方括号是"行间公式"，而 `.math.display` 带 <c>display:block; text-align:center</c>，
///   于是括号里的内容被单独拎出来居中并撑开空隙——就是截图里那三段。
/// 正确写法见 <see cref="MathJaxConfig"/>：JS 字符串里要写 <c>'\\(</c>（转义后才是 <c>\(</c>），
/// 且必须与 Markdig 数学扩展实际吐出的分隔符一致（<c>$x$</c> → <c>&lt;span class="math"&gt;\(x\)&lt;/span&gt;</c>）。
/// </summary>
public static class NoteHtmlRenderer
{
    /// <summary>
    /// Markdown → HTML 管线。<see cref="MarkdownPipelineBuilder.UseMathematics"/> 会把
    /// <c>$...$</c> / <c>$$...$$</c> 变成 <c>&lt;span class="math"&gt;\(...\)&lt;/span&gt;</c> /
    /// <c>&lt;div class="math"&gt;\[...\]&lt;/div&gt;</c>，**并保护公式内容不被 Markdown 语法吃掉**
    /// （公式里的 <c>_</c>、<c>*</c> 不会变成斜体）——这是"公式必须走 <c>$...$</c>"的核心理由。
    /// <c>DisableHtml()</c> 是安全项：笔记内容可能来自模型/截图 OCR，禁止原始 HTML 透传。
    /// </summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .DisableHtml()
        .Build();

    /// <summary>
    /// MathJax 配置（放在加载脚本之前）。**只认 LaTeX 标准的 <c>\(...\)</c> 与 <c>\[...\]</c>**：
    /// · 这两个正是 Markdig 数学扩展吐出的形式，两边必须一致；
    /// · 刻意**不**把 <c>$</c>、<c>(</c>、<c>[</c> 配成分隔符——正文里的美元符号、普通括号
    ///   绝不该被当成公式（这正是历史事故的成因）。
    /// </summary>
    public const string MathJaxConfig = @"  MathJax = {
    tex: { inlineMath: [['\\(', '\\)']], displayMath: [['\\[', '\\]']] },
    svg: { fontCache: 'global' }
  };";

    /// <summary>把 Markdown 渲染为完整 HTML 文档（含 MathJax 与阅读样式）。</summary>
    public static string RenderDocument(string? markdown)
    {
        var body = Markdown.ToHtml(markdown ?? "", Pipeline);
        return BuildDocument(body);
    }

    /// <summary>把已渲染的正文 HTML 包成完整文档。</summary>
    public static string BuildDocument(string bodyHtml)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta http-equiv='Content-Security-Policy' content=""default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline' https://appassets.local; img-src data: https:;"">
<style>
  body {{
    font-family: 'Segoe UI', 'Microsoft YaHei', -apple-system, sans-serif;
    font-size: 15px;
    line-height: 1.7;
    color: #333;
    padding: 20px 26px;
    max-width: 860px;
    margin: 0 auto;
    user-select: text;
    -webkit-user-select: text;
  }}
  h1 {{ font-size: 24px; border-bottom: 2px solid #1976D2; padding-bottom: 8px; }}
  h2 {{ font-size: 20px; margin-top: 24px; color: #1565C0; }}
  h3 {{ font-size: 17px; color: #333; }}
  p, li {{ line-height: 1.8; }}
  ul, ol {{ padding-left: 24px; }}
  code {{
    background: #f5f5f5;
    padding: 2px 6px;
    border-radius: 4px;
    font-family: Consolas, 'Courier New', monospace;
    font-size: 13px;
  }}
  pre {{
    background: #f6f8fa;
    padding: 14px 16px;
    border-radius: 6px;
    overflow-x: auto;
  }}
  pre code {{ background: none; padding: 0; }}
  blockquote {{
    border-left: 4px solid #B0BEC5;
    margin: 12px 0;
    padding: 4px 16px;
    color: #555;
    background: #fafafa;
  }}
  table {{ border-collapse: collapse; margin: 12px 0; }}
  th, td {{ border: 1px solid #ddd; padding: 6px 12px; }}
  th {{ background: #f1f5f9; }}
  /* 选择器必须与 Markdig 数学扩展实际吐出的标签一致（span.math / div.math）。
     历史事故里写的是 .math.inline / .math.display——这两个类名 Markdig 根本不会生成，
     等于样式从未生效过。 */
  span.math {{ display: inline; }}
  div.math {{ display: block; text-align: center; margin: 12px 0; }}
  /* 长公式（状态转移方程常见）不要撑破版心 */
  mjx-container {{ max-width: 100%; overflow-x: auto; overflow-y: hidden; }}
  a {{ color: #1976D2; }}
  hr {{ border: none; border-top: 1px solid #e0e0e0; }}
</style>
<script>
{MathJaxConfig}
</script>
<script async src='https://appassets.local/mathjax-tex-svg.js'></script>
</head>
<body>
{bodyHtml}
</body>
</html>";
    }
}
