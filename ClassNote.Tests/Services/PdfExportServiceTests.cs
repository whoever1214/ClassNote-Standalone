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
}
