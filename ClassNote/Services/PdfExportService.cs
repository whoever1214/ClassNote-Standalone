using System.IO;
using System.Linq;
using System.Text;
using ClassNote.Models;
using Markdig;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ClassNote.Services;

/// <summary>
/// 本地 PDF 导出服务：用 QuestPDF（纯 .NET）在客户端渲染课堂笔记为 PDF，
/// 替代原服务端 reportlab 渲染，无需任何后端。
/// </summary>
public interface IPdfExportService
{
    /// <summary>将笔记渲染为 PDF 字节流；返回 null 表示笔记尚未生成。</summary>
    byte[]? Export(Note? note);
}

public sealed class PdfExportService : IPdfExportService
{
    // 自定义注册的中文字体名称
    private const string CjkFont = "ClassNoteCJK";

    // 首次使用前设置许可证（Community 免费版）+ 注册中文字体
    static PdfExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        RegisterCjkFont();
    }

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .DisableHtml()
        .Build();

    public byte[]? Export(Note? note)
    {
        if (note?.ContentMarkdown == null)
            return null;

        var title = string.IsNullOrWhiteSpace(note.Title) ? "课堂笔记" : note.Title;

        using var ms = new MemoryStream();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(48);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontFamily(CjkFont).FontSize(11).LineHeight(1.6f));

                page.Header().Column(col =>
                {
                    col.Item().Text("ClassNote · 课堂笔记")
                        .FontSize(9f).FontColor("#888888");
                    col.Item().PaddingBottom(8).BorderBottom(1.5f).BorderColor("#1976D2")
                        .Text(title).FontSize(22).Bold().FontColor("#1A1A1A");
                });

                page.Content().Column(col =>
                {
                    // 摘要
                    if (!string.IsNullOrWhiteSpace(note.Summary))
                    {
                        col.Item().PaddingTop(4).PaddingBottom(8).Text("摘要")
                            .FontSize(14).Bold().FontColor("#1565C0");
                        col.Item().Text(note.Summary).FontSize(10.5f).FontColor("#444444");
                    }

                    col.Item().PaddingTop(4).PaddingBottom(8).Text("正文")
                        .FontSize(14).Bold().FontColor("#1565C0");

                    // QuestPDF 不渲染 HTML，故以 markdown 原文按行近似排版，
                    // 保留标题/列表的相对结构；**行内标记必须自己处理掉**——
                    // 旧实现直接打印原文，于是 PDF 里会出现字面的 `**加粗**`、`$公式$`、反引号，
                    // 且编号列表因识别正则转义丢失（`^d+[.)]s+`）永远匹配不上、形同虚设。
                    var lines = note.ContentMarkdown
                        .Replace("\r\n", "\n").Replace('\r', '\n')
                        .Split('\n');

                    foreach (var line in lines)
                    {
                        var trimmed = line.TrimEnd();
                        if (string.IsNullOrWhiteSpace(trimmed))
                        {
                            col.Item().Height(6);
                            continue;
                        }

                        // 标题
                        if (trimmed.StartsWith("### "))
                            WriteRichLine(col.Item(), trimmed[4..], 12, "#333333");
                        else if (trimmed.StartsWith("## "))
                            WriteRichLine(col.Item(), trimmed[3..], 14, "#1565C0");
                        else if (trimmed.StartsWith("# "))
                            WriteRichLine(col.Item(), trimmed[2..], 16, "#0D47A1");
                        // 无序列表
                        else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                            WriteRichLine(col.Item().PaddingLeft(12), "•  " + trimmed[2..], 11, "#333333");
                        // 有序列表（1. / 2) / 1、 三种写法；旧正则转义写错，从来没识别到过）
                        else if (MarkdownInlineText.TryStripOrderedMarker(trimmed, out var ordered))
                            WriteRichLine(col.Item().PaddingLeft(12), ordered, 11, "#333333");
                        // 引用
                        else if (trimmed.StartsWith("> "))
                            WriteRichLine(col.Item().PaddingLeft(12), trimmed[2..], 11, "#666666", italic: true);
                        else
                            WriteRichLine(col.Item(), trimmed, 11, "#222222");
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("第 ").FontSize(9).FontColor("#999999");
                    x.CurrentPageNumber().FontSize(9).FontColor("#999999");
                    x.Span(" 页").FontSize(9).FontColor("#999999");
                });
            });
        }).GeneratePdf(ms);

        return ms.ToArray();
    }

    /// <summary>
    /// 把一行 Markdown 写成 QuestPDF 富文本：**行内标记（加粗/反引号/公式分隔符）在这里被消化掉**，
    /// 文字内容原样保留。这样 PDF 与笔记页看到的是同一份内容，只是少了标记符号。
    /// </summary>
    private static void WriteRichLine(IContainer container, string markdown, float fontSize, string color,
        bool italic = false)
    {
        container.Text(text =>
        {
            text.DefaultTextStyle(style => style.FontSize(fontSize).FontColor(color));

            var runs = MarkdownInlineText.ParseLine(markdown);
            if (runs.Count == 0)
            {
                text.Span(" ");
                return;
            }

            foreach (var run in runs)
            {
                var span = text.Span(run.Text);
                if (run.Bold)
                    span.Bold();
                if (italic)
                    span.Italic();
                if (run.Code)
                    span.FontFamily("Consolas");
            }
        });
    }

    /// <summary>从系统字体目录注册一个中文字体，绑定到自定义名称 ClassNoteCJK。</summary>
    private static void RegisterCjkFont()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var candidates = new[]
        {
            "simhei.ttf",   // 黑体（首选，纯 TTF 文件，QuestPDF 可稳定加载）
            "msyh.ttc",     // 微软雅黑
            "msyhbd.ttc",
            "simsun.ttc",   // 宋体
            "simkai.ttf",   // 楷体
            "Deng.ttf",     // 等线
            "malgun.ttf",
        };

        foreach (var fileName in candidates)
        {
            var path = Path.Combine(fontsDir, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                // 使用自定义名称注册，便于后续统一引用
                FontManager.RegisterFontWithCustomName(CjkFont, File.OpenRead(path));
                return;
            }
            catch
            {
                // 注册失败（如 TTC 集合加载异常）则尝试下一个
            }
        }
    }
}
