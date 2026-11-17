using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ClassNote.Services;

/// <summary>一张截图的像素统计（"有没有用"的判据之一）。</summary>
/// <param name="WhiteRatio">接近纯白（>=235 灰阶）的像素占比；&gt;0.92 基本是空白页。</param>
/// <param name="GrayStdDev">64×64 缩略图的灰阶标准差；&lt;6 说明画面几乎没有任何结构。</param>
/// <param name="Width">原始像素宽。</param>
/// <param name="Height">原始像素高。</param>
public readonly record struct ImageStats(double WhiteRatio, double GrayStdDev, int Width, int Height)
{
    /// <summary>近空白：整块底色（白板/黑屏/纯色遮挡）——对笔记没有任何价值。</summary>
    public bool IsNearlyBlank => WhiteRatio > 0.92 || GrayStdDev < 6;
}

/// <summary>
/// 感知哈希（标准 pHash）与图片"有用性"度量。**全项目唯一的哈希实现出处**：
/// 录音期间的截图分类（<see cref="ScreenshotService"/>）与课后生成笔记前的选图
/// （<see cref="ScreenshotSelector"/>）必须用同一套哈希，否则两处对"这张图变没变"的判断会互相矛盾。
///
/// 算法：缩到 32×32 灰度 → 2D DCT-II → 取左上 8×8（排除 DC）→ 与中位数比较 → 64 bit。
/// 汉明距离 0 = 视觉上完全一样；&gt;8 位（64 位里）在实践中就是"换了一屏"。
/// </summary>
public static class PerceptualHash
{
    /// <summary>哈希位数（64 位）。</summary>
    public const int HashBits = 64;

    private const int SampleSize = 32;
    private const int CoeffCount = 8;

    /// <summary>DCT-II 基矩阵（8×32，含归一化系数）。</summary>
    private static readonly double[,] Basis = BuildBasis();

    private static double[,] BuildBasis()
    {
        var d = new double[CoeffCount, SampleSize];
        for (int u = 0; u < CoeffCount; u++)
        {
            double scale = u == 0 ? Math.Sqrt(1.0 / SampleSize) : Math.Sqrt(2.0 / SampleSize);
            for (int x = 0; x < SampleSize; x++)
                d[u, x] = scale * Math.Cos((2.0 * x + 1) * u * Math.PI / (2.0 * SampleSize));
        }
        return d;
    }

    /// <summary>从已解码的位图计算哈希（内部会缩放到 32×32，不改动入参）。</summary>
    public static ulong FromBitmap(Bitmap bitmap) => FromBitmap(bitmap, out _);

    /// <summary>从已解码的位图计算哈希，同时给出"有没有用"的像素统计。</summary>
    public static ulong FromBitmap(Bitmap bitmap, out ImageStats stats)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        double[,] gray = null!;
        using (var small = new Bitmap(bitmap, new Size(SampleSize, SampleSize)))
            gray = LoadGray(small, SampleSize, SampleSize);

        // 可分离的 2D DCT-II：先按行变换，再按列变换，只算 8×8 低频
        var rows = new double[SampleSize, CoeffCount];
        for (int y = 0; y < SampleSize; y++)
            for (int v = 0; v < CoeffCount; v++)
            {
                double sum = 0;
                for (int x = 0; x < SampleSize; x++)
                    sum += gray[y, x] * Basis[v, x];
                rows[y, v] = sum;
            }

        double[] coeffs = new double[HashBits];
        int k = 0;
        for (int u = 1; u <= CoeffCount; u++)        // 排除 DC（u-1==0 且 v-1==0）
            for (int v = 1; v <= CoeffCount; v++)
            {
                double sum = 0;
                for (int y = 0; y < SampleSize; y++)
                    sum += rows[y, v - 1] * Basis[u - 1, y];
                coeffs[k++] = sum;
            }

        var sorted = (double[])coeffs.Clone();
        Array.Sort(sorted);
        double median = (sorted[(HashBits / 2) - 1] + sorted[HashBits / 2]) / 2.0;

        ulong hash = 0;
        for (int i = 0; i < HashBits; i++)
            if (coeffs[i] > median)
                hash |= 1UL << i;

        stats = ComputeStats(bitmap);
        return hash;
    }

    /// <summary>从 JPEG/PNG 字节计算哈希（解码一次，同时得到统计信息）。解码失败抛异常，由调用方决定降级方式。</summary>
    public static ulong FromImageBytes(byte[] data, out ImageStats stats)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data, writable: false);
        using var bmp = new Bitmap(ms);
        return FromBitmap(bmp, out stats);
    }

    /// <summary>汉明距离：0 = 视觉完全相同，64 = 完全相反。超过 <see cref="ScreenshotSelector.DefaultDedupThreshold"/> 视为换了一屏。</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>落库用：16 位十六进制（定长，便于 SQL 里肉眼比对）。</summary>
    public static string ToHex(ulong hash) => hash.ToString("x16");

    /// <summary>解析 <see cref="ToHex"/> 写出的字符串；空/格式不对返回 false（调用方应重新计算）。</summary>
    public static bool TryParseHex(string? hex, out ulong hash)
    {
        hash = 0;
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 16)
            return false;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out hash);
    }

    private static double[,] LoadGray(Bitmap bitmap, int width, int height)
    {
        var result = new double[height, width];
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buffer = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * stride + x * 4;
                    byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
                    result[y, x] = 0.299 * r + 0.587 * g + 0.114 * b;
                }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return result;
    }

    private static ImageStats ComputeStats(Bitmap bitmap)
    {
        const int n = 64;
        double[,] gray;
        using (var small = new Bitmap(bitmap, new Size(n, n)))
            gray = LoadGray(small, n, n);

        int white = 0;
        double sum = 0, sumSq = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double v = gray[y, x];
                if (v >= 235) white++;
                sum += v;
                sumSq += v * v;
            }

        double total = n * n;
        double mean = sum / total;
        double variance = Math.Max(0, sumSq / total - mean * mean);
        return new ImageStats(white / total, Math.Sqrt(variance), bitmap.Width, bitmap.Height);
    }
}
