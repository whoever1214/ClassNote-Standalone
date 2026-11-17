using System.Drawing;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 感知哈希：截图分类（录音期间）与选图（生成笔记前）共用同一实现，
/// 因此这里的契约是"同一张图距离为 0、不同画面距离足够大"，否则去重会把不同屏当成重复。
/// </summary>
public class PerceptualHashTests
{
    private static Bitmap Solid(int w, int h, Color color)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    private static Bitmap Checker(int w, int h, int cell)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var brush = new SolidBrush(Color.Black);
        for (int y = 0; y < h; y += cell)
            for (int x = 0; x < w; x += cell)
                if (((x / cell) + (y / cell)) % 2 == 0)
                    g.FillRectangle(brush, x, y, cell, cell);
        return bmp;
    }

    [Fact]
    public void SameImage_DistanceIsZero()
    {
        using var a = Checker(400, 300, 40);
        using var b = Checker(400, 300, 40);
        ulong ha = PerceptualHash.FromBitmap(a, out _);
        ulong hb = PerceptualHash.FromBitmap(b, out _);
        Assert.Equal(0, PerceptualHash.Distance(ha, hb));
    }

    [Fact]
    public void DifferentImages_AreFarApart()
    {
        using var white = Solid(400, 300, Color.White);
        using var dark = Solid(400, 300, Color.Black);
        using var checker = Checker(400, 300, 40);

        ulong hWhite = PerceptualHash.FromBitmap(white, out _);
        ulong hDark = PerceptualHash.FromBitmap(dark, out _);
        ulong hChecker = PerceptualHash.FromBitmap(checker, out _);

        Assert.True(PerceptualHash.Distance(hWhite, hChecker) > ScreenshotSelector.DefaultDedupThreshold);
        Assert.True(PerceptualHash.Distance(hDark, hChecker) > ScreenshotSelector.DefaultDedupThreshold);
    }

    [Fact]
    public void WhiteImage_IsReportedAsNearlyBlank()
    {
        using var white = Solid(400, 300, Color.White);
        var stats = new ImageStats(0, 0, 0, 0);
        var _ = PerceptualHash.FromBitmap(white, out stats);
        Assert.True(stats.IsNearlyBlank);
        Assert.Equal(400, stats.Width);
        Assert.Equal(300, stats.Height);
    }

    [Fact]
    public void CheckerImage_IsNotBlank()
    {
        using var checker = Checker(400, 300, 40);
        var stats = new ImageStats(0, 0, 0, 0);
        var _ = PerceptualHash.FromBitmap(checker, out stats);
        Assert.False(stats.IsNearlyBlank);
    }

    [Fact]
    public void HexRoundTrips()
    {
        ulong hash = 0x0123456789abcdefUL;
        var hex = PerceptualHash.ToHex(hash);
        Assert.Equal(16, hex.Length);
        Assert.True(PerceptualHash.TryParseHex(hex, out var parsed));
        Assert.Equal(hash, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("0123")]
    public void BadHex_IsRejected(string? value)
    {
        Assert.False(PerceptualHash.TryParseHex(value, out _));
    }
}
