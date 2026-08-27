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
                    // 保留标题/列表的相对结构。
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
                            col.Item().Text(trimmed[4..]).FontSize(12).Bold().FontColor("#333333");
                        else if (trimmed.StartsWith("## "))
                            col.Item().Text(trimmed[3..]).FontSize(14).Bold().FontColor("#1565C0");
                        else if (trimmed.StartsWith("# "))
                            col.Item().Text(trimmed[2..]).FontSize(16).Bold().FontColor("#0D47A1");
                        // 列表
                        else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                            col.Item().PaddingLeft(12).Text("•  " + trimmed[2..]).FontSize(11).FontColor("#333333");
                        else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^d+[.)]s+"))
                            col.Item().PaddingLeft(12).Text(trimmed).FontSize(11).FontColor("#333333");
                        // 引用
                        else if (trimmed.StartsWith("> "))
                            col.Item().PaddingLeft(12).Text(trimmed[2..]).Italic().FontColor("#666666");
                        else
                            col.Item().Text(trimmed).FontSize(11).FontColor("#222222");
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
