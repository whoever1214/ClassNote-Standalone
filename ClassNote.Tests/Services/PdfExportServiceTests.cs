using System.Text;
using ClassNote.Models;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

public class PdfExportServiceTests
{
    [Fact]
    public void Export_NullNote_ReturnsNull()
    {
        var svc = new PdfExportService();
        Assert.Null(svc.Export(null));
    }

    [Fact]
    public void Export_NullContent_ReturnsNull()
    {
        var svc = new PdfExportService();
        Assert.Null(svc.Export(new Note { ContentMarkdown = null }));
    }

    [Fact]
    public void Export_ValidNote_ProducesPdf()
    {
        var svc = new PdfExportService();
        var markdown = new string[]
        {
            "# 导数",
            "",
            "## 定义",
            "",
            "f'(x) = lim(h→0) [f(x+h)-f(x)]/h",
            "",
            "- 重点一：切线斜率",
            "- 重点二：可导与连续关系",
            "",
            "> 例题：求 y=x^2 的导数",
        };
        var note = new Note
        {
            Title = "导数章节课堂笔记",
            Summary = "本节讲解导数的定义与几何意义。",
            ContentMarkdown = string.Join("\n", markdown),
        };

        var bytes = svc.Export(note);

        // 应生成非空 PDF，且以 %PDF 魔数开头
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 100, "PDF 过小");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    // ── 列表层级与表格（P2）────────────────────────────────

    [Theory]
    [InlineData("- 一级项", 0, "一级项", false)]
    [InlineData("  - 二级项", 1, "二级项", false)]
    [InlineData("    - 三级项", 2, "三级项", false)]
    [InlineData("* 星号也是项", 0, "星号也是项", false)]
    [InlineData("1. 有序项", 0, "有序项", true)]
    [InlineData("  2) 缩进的有序项", 1, "缩进的有序项", true)]
    [InlineData("3、中文顿号也算", 0, "中文顿号也算", true)]
    public void TryParseListItem_ReadsDepthMarkerAndText(string line, int depth, string text, bool ordered)
    {
        Assert.True(PdfExportService.TryParseListItem(line, out int actualDepth, out string actualText, out bool actualOrdered));
        Assert.Equal(depth, actualDepth);
        Assert.Equal(text, actualText);
        Assert.Equal(ordered, actualOrdered);
    }

    [Theory]
    [InlineData("普通段落，不是列表")]
    [InlineData("f(n) = min(f(n-1)+1)")]      // 公式不能被当成列表
    [InlineData("-")]                          // 只有标记没有正文
    [InlineData("")]
    public void TryParseListItem_RejectsNonListLines(string line)
    {
        Assert.False(PdfExportService.TryParseListItem(line, out _, out _, out _));
    }

    [Fact]
    public void TableHelpers_RecognizeRowsAndSeparators()
    {
        Assert.True(PdfExportService.IsTableRow("| 物质 | 俗名 |"));
        Assert.False(PdfExportService.IsTableRow("普通一行"));
        Assert.False(PdfExportService.IsTableRow("| 缺右竖线"));

        Assert.True(PdfExportService.IsTableSeparator("|---|---|"));
        Assert.True(PdfExportService.IsTableSeparator("|:--|--:|"));
        Assert.False(PdfExportService.IsTableSeparator("| 碳酸钠 | 纯碱 |"));

        Assert.Equal(new[] { "物质", "俗名" }, PdfExportService.SplitTableRow("| 物质 | 俗名 |"));
    }

    [Fact]
    public void Export_NoteWithTableAndNestedList_StillProducesValidPdf()
    {
        // 表格与嵌套列表在旧实现里会退化成一条条带竖线的句子 / 同缩进的平铺行，
        // 这里只保证"排得出来"（内容不丢、不抛异常）；呈现质量由人工核对 PDF。
        var svc = new PdfExportService();
        var markdown = new[]
        {
            "# 化学",
            "",
            "## 核心知识点",
            "",
            "1. 钠的性质",
            "   - 与氧气反应条件不同产物不同",
            "   - 保存在煤油中",
            "     - 不能放在四氯化碳里",
            "",
            "| 物质 | 俗名 | 水溶性 |",
            "|---|---|---|",
            "| 碳酸钠 | 纯碱 | 放热 |",
            "| 碳酸氢钠 | 小苏打 | 吸热 |",
            "",
            "方程式：$2Na_2O_2 + 2H_2O \\to 4NaOH + O_2$",
        };
        var note = new Note { Title = "化学", ContentMarkdown = string.Join("\n", markdown) };

        var bytes = svc.Export(note);

        Assert.NotNull(bytes);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes!, 0, 5));
    }
}
