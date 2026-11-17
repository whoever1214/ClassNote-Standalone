using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClassNote.Services;

/// <summary>
/// 课件截图的**输入形态**：决定生成笔记时把截图喂给模型的方式。
///
/// - <see cref="Ocr"/>：老路径，本地 OCR 出文本再进提示词（省 token、能离线、但公式与化学式识得很碎）；
/// - <see cref="Vision"/>：图片直接进多模态模型（保真度高、要视觉模型、按图片 token 计费）；
/// - <see cref="Both"/>：两条都给（最贵，用于"先看图再按 OCR 校对"的场合）。
/// </summary>
public enum ScreenshotInputMode
{
    Ocr = 0,
    Vision = 1,
    Both = 2,
}

/// <summary>截图输入模式的存储/展示约定（与其它枚举一致：存枚举名，文案随便改）。</summary>
public static class ScreenshotInputModes
{
    /// <summary>解析设置里的字符串；缺失/损坏时回退 <see cref="ScreenshotInputMode.Vision"/>。</summary>
    public static ScreenshotInputMode FromStorage(string? stored)
        => Enum.TryParse<ScreenshotInputMode>(stored, ignoreCase: true, out var mode) ? mode : ScreenshotInputMode.Vision;

    public static string ToStorage(ScreenshotInputMode mode) => mode.ToString();

    public static string DisplayName(ScreenshotInputMode mode) => mode switch
    {
        ScreenshotInputMode.Ocr => "仅 OCR 文本（省 token / 可完全离线）",
        ScreenshotInputMode.Vision => "仅截图图片（推荐：公式与板书保真）",
        ScreenshotInputMode.Both => "图片 + OCR 文本",
        _ => "仅截图图片",
    };

    /// <summary>这个模式要不要跑本地 OCR。</summary>
    public static bool UsesOcr(this ScreenshotInputMode mode)
        => mode is ScreenshotInputMode.Ocr or ScreenshotInputMode.Both;

    /// <summary>这个模式要不要把图片送进模型。</summary>
    public static bool UsesVision(this ScreenshotInputMode mode)
        => mode is ScreenshotInputMode.Vision or ScreenshotInputMode.Both;

    /// <summary>按界面顺序列出（设置下拉直接用）。</summary>
    public static IReadOnlyList<ScreenshotInputMode> All { get; } = new[]
    {
        ScreenshotInputMode.Vision, ScreenshotInputMode.Ocr, ScreenshotInputMode.Both,
    };

    /// <summary>与 <see cref="All"/> 一一对应的下拉文案（顺序必须一致）。</summary>
    public static IReadOnlyList<string> DisplayNames { get; } =
        All.Select(DisplayName).ToList();

    /// <summary>按下拉索引取模式（越界时回退默认值）。</summary>
    public static ScreenshotInputMode FromIndex(int index)
        => index >= 0 && index < All.Count ? All[index] : ScreenshotInputMode.Vision;

    /// <summary>模式在下拉里的索引。</summary>
    public static int IndexOf(ScreenshotInputMode mode)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i] == mode) return i;
        return 0;
    }
}

/// <summary>
/// 一张要送进模型的截图。**已经是最终要发送的字节**（按设置缩放并重新编码过），
/// 因此调用方不必再关心落盘的原图有多少 MB。
/// </summary>
/// <param name="SeqNo">会话内序号（用于稳定排序与追溯）。</param>
/// <param name="TimestampSeconds">该截图对应的录音时刻（秒）。</param>
/// <param name="Type">annotation / new_slide / video。</param>
/// <param name="Jpeg">JPEG 字节（已缩放）。</param>
/// <param name="Width">缩放后的像素宽。</param>
/// <param name="Height">缩放后的像素高。</param>
public sealed record NoteImage(int SeqNo, double TimestampSeconds, string Type, byte[] Jpeg, int Width, int Height)
{
    /// <summary>给模型看的说明行：<c>[图片 3] new_slide @ 00:12:30</c>。序号从 1 起，与该次请求里的图片顺序一致。</summary>
    public string Caption(int oneBasedIndex)
        => string.Format(CultureInfo.InvariantCulture, "[图片 {0}] {1} @ {2}",
            oneBasedIndex, Type, FormatClock(TimestampSeconds));

    /// <summary>与提示词其它位置一致的时间格式：HH:mm:ss（超过 1 小时才写小时）。</summary>
    public static string FormatClock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}

/// <summary>选图与缩放的参数（全部来自设置，带安全取值）。</summary>
/// <param name="MaxImages">单节课最多送几张图（0 或负数按 1 处理）。</param>
/// <param name="MaxEdgePx">长边缩放上限（0 或负数表示不缩放）。</param>
/// <param name="JpegQuality">重新编码的 JPEG 质量（1–100）。</param>
/// <param name="DedupThreshold">pHash 汉明距离阈值：相邻帧距离不超过它就算"同一屏"，只留最后一帧。</param>
public sealed record ScreenshotSelectionOptions(
    int MaxImages = 30,
    int MaxEdgePx = 1600,
    int JpegQuality = 80,
    int DedupThreshold = ScreenshotSelector.DefaultDedupThreshold)
{
    /// <summary>从设置快照构造，并把每个参数夹到安全区间（设置文件可能被手改坏）。</summary>
    public static ScreenshotSelectionOptions FromSettings(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ScreenshotSelectionOptions(
            MaxImages: Clamp(settings.VisionMaxImagesPerNote, 1, 200, 30),
            MaxEdgePx: Clamp(settings.VisionMaxEdgePx, 0, 4096, 1600),
            JpegQuality: Clamp(settings.VisionJpegQuality, 30, 95, 80),
            DedupThreshold: Clamp(settings.VisionDedupThreshold, 0, 32, ScreenshotSelector.DefaultDedupThreshold));
    }

    private static int Clamp(int value, int min, int max, int fallback)
        => value < min || value > max ? fallback : value;
}
