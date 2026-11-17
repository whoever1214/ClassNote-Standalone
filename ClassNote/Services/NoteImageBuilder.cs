using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ClassNote.Services;

/// <summary>选图并加载后的结果：真正要发给模型的图片 + 可读的取舍统计。</summary>
public sealed record NoteImagesResult(IReadOnlyList<NoteImage> Images, SelectionStats Stats)
{
    public bool IsEmpty => Images.Count == 0;
}

/// <summary>
/// 把"库里的截图行"变成"可发送的图片"：<b>取哈希 → 选图 → 解码 → 缩放重编码</b>。
///
/// 缓存策略：pHash 落库（<c>screenshots.phash</c>），第二次起不再解码原图——
/// 只解码最终要发送的那几十张。第一次（老库升级后）会解码全部截图一次，约 30–60ms/张。
///
/// 失败即降级：单张读不到/解码失败只跳过该张，绝不让整篇笔记因为一张图失败。
/// </summary>
public static class NoteImageBuilder
{
    /// <summary>取哈希 + 选图（不加载图片数据）。用于"只想知道会送几张"的场合与单测。</summary>
    public static SelectionResult SelectCandidates(
        Guid sessionId, ScreenshotSelectionOptions options, LocalRepository? repository = null)
    {
        var repo = repository ?? LocalRepository.Instance;
        var rows = repo.ListScreenshotsDetailed(sessionId);
        var candidates = new List<ScreenshotCandidate>(rows.Count);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ImagePath) || !File.Exists(row.ImagePath))
                continue;   // 图片被删/没落盘：静默跳过（库里仍有行，OcrText 还可用）

            ulong hash;
            ImageStats stats = default;
            if (!PerceptualHash.TryParseHex(row.Phash, out hash))
            {
                try
                {
                    var bytes = File.ReadAllBytes(row.ImagePath);
                    hash = PerceptualHash.FromImageBytes(bytes, out stats);
                    repo.SetScreenshotHash(sessionId, row.SeqNo, PerceptualHash.ToHex(hash));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NoteImageBuilder] 截图 {row.SeqNo} 哈希失败: {ex.Message}");
                    continue;
                }
            }

            candidates.Add(new ScreenshotCandidate(
                row.SeqNo, row.Timestamp, row.Type, row.ImagePath, hash, stats));
        }

        return ScreenshotSelector.Select(candidates, options.DedupThreshold, options.MaxImages);
    }

    /// <summary>
    /// 选图并加载成可发送的图片。返回的 <c>Stats</c> 已按"加载后仍然可用"的口径修正
    /// （解码失败/近空白的图片会在这一步被丢弃）。
    /// </summary>
    public static NoteImagesResult Build(
        Guid sessionId, ScreenshotSelectionOptions options,
        LocalRepository? repository = null, IProgress<string>? progress = null)
    {
        var selection = SelectCandidates(sessionId, options, repository);
        if (selection.Kept.Count == 0)
            return new NoteImagesResult(Array.Empty<NoteImage>(), selection.Stats);

        progress?.Report($"已从 {selection.Stats.Total} 张截图里筛出 {selection.Kept.Count} 张关键帧，正在准备图片…");

        var images = new List<NoteImage>(selection.Kept.Count);
        int failed = 0, blank = 0;
        foreach (var candidate in selection.Kept)
        {
            try
            {
                using var stream = new FileStream(candidate.ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var source = new Bitmap(stream);
                var pixelStats = MeasureOnly(source);
                if (pixelStats.IsNearlyBlank)
                {
                    blank++;
                    continue;
                }

                var (jpeg, width, height) = EncodeJpeg(source, options.MaxEdgePx, options.JpegQuality);
                images.Add(new NoteImage(candidate.SeqNo, candidate.TimestampSeconds, candidate.Type,
                    jpeg, width, height));
            }
            catch (Exception ex)
            {
                failed++;
                System.Diagnostics.Debug.WriteLine($"[NoteImageBuilder] 截图 {candidate.SeqNo} 加载失败: {ex.Message}");
            }
        }

        // 全部被判定为空白时，宁可送一张（否则"有截图"会静默变成"没有截图"）
        if (images.Count == 0 && selection.Kept.Count > 0)
        {
            var first = selection.Kept[0];
            try
            {
                using var stream = new FileStream(first.ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var source = new Bitmap(stream);
                var (jpeg, width, height) = EncodeJpeg(source, options.MaxEdgePx, options.JpegQuality);
                images.Add(new NoteImage(first.SeqNo, first.TimestampSeconds, first.Type, jpeg, width, height));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NoteImageBuilder] 兜底取图失败: {ex.Message}");
            }
        }

        var finalStats = selection.Stats with
        {
            DroppedBlank = selection.Stats.DroppedBlank + blank,
            DroppedDuplicate = selection.Stats.DroppedDuplicate + failed,
            Kept = images.Count,
        };
        return new NoteImagesResult(images, finalStats);
    }

    /// <summary>只算"有没有用"的统计（复用哈希实现里的同一套口径，不重复实现）。</summary>
    private static ImageStats MeasureOnly(Bitmap bitmap)
    {
        PerceptualHash.FromBitmap(bitmap, out var stats);
        return stats;
    }

    /// <summary>按长边上限缩放并重新编码为 JPEG。不放大（小图原样保留）。</summary>
    internal static (byte[] Jpeg, int Width, int Height) EncodeJpeg(Image source, int maxEdgePx, int quality)
    {
        int width = source.Width, height = source.Height;
        if (maxEdgePx > 0)
        {
            int longest = Math.Max(width, height);
            if (longest > maxEdgePx)
            {
                double scale = (double)maxEdgePx / longest;
                width = Math.Max(1, (int)Math.Round(width * scale));
                height = Math.Max(1, (int)Math.Round(height * scale));
            }
        }

        using var resized = new Bitmap(width, height);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, width, height);
        }

        using var ms = new MemoryStream();
        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        resized.Save(ms, jpegEncoder, parameters);
        return (ms.ToArray(), width, height);
    }
}
