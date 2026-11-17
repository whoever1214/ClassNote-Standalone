using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassNote.Services;

/// <summary>
/// 一路音频的转写分块（块文本 + 该块的起始秒）。
/// </summary>
/// <param name="Source">这一路来自哪（麦克风 / 系统声音）。</param>
/// <param name="ChunkTexts">按块号升序的块文本（与库里 <c>transcript_chunks.text</c> 一致）。</param>
/// <param name="StartSeconds">各块起始秒（块大小固定 30 秒，见 <see cref="TranscriptionChunking"/>）。</param>
public sealed record TrackTranscript(RecordingAudioSource Source, IReadOnlyList<string> ChunkTexts, IReadOnlyList<int> StartSeconds);

/// <summary>
/// 转写文本 ↔ 录音时间的对照表。
///
/// **为什么需要它**：课件截图带的是**绝对秒**（<c>screenshots.timestamp</c>），而分段用的是
/// **字符偏移**。两者之间原本没有任何桥梁，于是 <see cref="NoteSegmenter"/> 只能把 OCR 按
/// **序号均摊**到各段（见那儿的注释）——结果是"第 N 段里放的是别的时间段的幻灯片"，
/// 同一段内转写与截图讲的根本不是同一时刻的事，两源因此无法互证。
///
/// 本类补上这座桥：它知道每个块对应的字符区间，于是能把"某段的字符区间"翻译成"某段时间区间"。
///
/// ⚠️ 一律**失败即退化**：素材对不齐、缺块、库里没有块记录时，
/// <see cref="BuildForSession"/> 返回 null，调用方退回旧的"按序号均摊"行为。
/// 时间对齐只是"更好"，绝不能成为"跑不起来"的原因。
/// </summary>
public sealed class TranscriptTimeline
{
    /// <summary>一个块的文本区间与时间区间。</summary>
    /// <param name="Index">块号（0 起）。</param>
    /// <param name="TextLength">该块文本长度。</param>
    /// <param name="StartSeconds">块起始秒。</param>
    /// <param name="EndSeconds">块结束秒（下一块起点，或按单块时长推算）。</param>
    public readonly record struct ChunkSpan(int Index, int TextLength, int StartSeconds, int EndSeconds);

    /// <summary>默认块时长（秒）：与 <see cref="TranscriptionChunking"/> 的 480000 采样一致。</summary>
    private const int DefaultChunkSeconds = 30;

    private readonly List<ChunkSpan> _spans;

    /// <summary>最后一块的结束秒（时间轴总长）。</summary>
    public int TotalSeconds { get; }

    /// <summary>对照表的覆盖字符数（所有块文本长度之和）。</summary>
    public int TextLength { get; }

    private TranscriptTimeline(List<ChunkSpan> spans, int totalSeconds, int textLength)
    {
        _spans = spans;
        TotalSeconds = totalSeconds;
        TextLength = textLength;
    }

    /// <summary>是否可用（没有块信息时不能用来对齐）。</summary>
    public bool IsUsable => _spans.Count > 0 && TextLength > 0;

    /// <summary>
    /// 用某一路（生成最终文本的那一路）的块构建对照表。
    /// </summary>
    /// <param name="track">该路的块文本与起始秒。</param>
    /// <param name="expectedTextLength">该路拼接后的字符数（用于校验块记录是否与文本对得上）。</param>
    internal static TranscriptTimeline? Build(TrackTranscript track, int expectedTextLength)
    {
        if (track.ChunkTexts.Count == 0) return null;

        // 块记录必须与文本对得上，否则说明库里缺过块、文本是以别的方式拼出来的：
        // 这时任何"按字符推断时间"都是在编数据，直接放弃。
        int sum = 0;
        foreach (var text in track.ChunkTexts)
            sum += text?.Length ?? 0;
        if (expectedTextLength > 0 && sum != expectedTextLength) return null;

        var spans = BuildSpans(track.ChunkTexts, track.StartSeconds);
        return spans.Count == 0 ? null : FromSpans(spans, sum);
    }

    /// <summary>合并多路的时间轴（任一路有块信息即可用）。</summary>
    internal static TranscriptTimeline? Merge(IReadOnlyList<TranscriptTimeline?> timelines)
    {
        var spans = new List<ChunkSpan>();
        int textLength = 0;
        foreach (var timeline in timelines)
        {
            if (timeline == null || !timeline.IsUsable) continue;
            spans.AddRange(timeline._spans);
            textLength += timeline.TextLength;
        }
        if (spans.Count == 0) return null;
        return FromSpans(spans, textLength);
    }

    private static TranscriptTimeline FromSpans(List<ChunkSpan> spans, int textLength)
    {
        spans.Sort((a, b) => a.StartSeconds != b.StartSeconds
            ? a.StartSeconds.CompareTo(b.StartSeconds)
            : a.Index.CompareTo(b.Index));

        int total = spans.Count == 0 ? 0 : spans[^1].EndSeconds;
        return new TranscriptTimeline(spans, total, textLength);
    }

    /// <summary>由"块文本 + 各块起始秒"算出逐个块的文本/时间区间。</summary>
    private static List<ChunkSpan> BuildSpans(IReadOnlyList<string> chunkTexts, IReadOnlyList<int> startSeconds)
    {
        var spans = new List<ChunkSpan>(chunkTexts.Count);
        for (int i = 0; i < chunkTexts.Count; i++)
        {
            int start = i < startSeconds.Count ? startSeconds[i] : i * DefaultChunkSeconds;
            int end = i + 1 < startSeconds.Count && startSeconds[i + 1] > start
                ? startSeconds[i + 1]
                : start + DefaultChunkSeconds;
            spans.Add(new ChunkSpan(i, chunkTexts[i]?.Length ?? 0, start, end));
        }
        return spans;
    }

    /// <summary>
    /// 字符偏移 → 录音秒。偏移落在某块的文本区间内时返回该块的起始秒。
    /// 偏移超出对照表覆盖范围时返回 null（**不猜**）。
    /// </summary>
    /// <param name="offset">转写文本里的字符偏移。</param>
    public int? TimeAtOffset(int offset)
    {
        if (!IsUsable || offset < 0) return null;

        int cursor = 0;
        foreach (var span in _spans)
        {
            cursor += span.TextLength;
            if (offset < cursor) return span.StartSeconds;
        }
        return null;
    }

    /// <summary>
    /// 一段字符区间对应的时间区间。起点取不到时返回 null。
    /// </summary>
    /// <param name="startChar">区间起点（含）。</param>
    /// <param name="endChar">区间终点（不含）。</param>
    public (int StartSeconds, int EndSeconds)? TimeRangeFor(int startChar, int endChar)
    {
        int? start = TimeAtOffset(startChar);
        if (start == null) return null;

        // 终点按"最后一个字符所在处的下一时刻"算：直接用 endChar-1 的块结束时间，
        // 这样跨块的分段能覆盖到它真正读过的那段时间。
        int? end = endChar - 1 >= startChar ? TimeAtOffset(endChar - 1) : start;
        if (end == null) end = start;

        int endSeconds = EndSecondsOf(end.Value, endChar);
        if (endSeconds <= start.Value) endSeconds = start.Value + DefaultChunkSeconds;
        return (start.Value, endSeconds);
    }

    /// <summary>某个时刻所属块的结束秒（用于把"瞬时"扩展成"区间"）。</summary>
    private int EndSecondsOf(int seconds, int offset)
    {
        foreach (var span in _spans)
        {
            if (seconds >= span.StartSeconds && seconds < span.EndSeconds)
                return span.EndSeconds;
        }
        // 落在所有块之外（比如末块之后）：退化成"该时刻 + 单块时长"
        return seconds + DefaultChunkSeconds;
    }

    /// <summary>
    /// 从库里取某一会话的转写块并构建对照表。**任何异常都返回 null**（调用方据此退回旧行为）。
    ///
    /// 多路（麦克风 + 系统声音）时合并两路的时间轴：分段文本是按来源分区拼的，
    /// 两路各自的块都能提供"那段时间发生过什么"的证据，合并后覆盖更完整。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="repository">仓库（可注入，便于测试）。</param>
    public static TranscriptTimeline? BuildForSession(Guid sessionId, ITranscriptChunkStore repository)
    {
        if (repository == null) return null;

        var timelines = new List<TranscriptTimeline?>();
        foreach (var source in new[] { RecordingAudioSource.Microphone, RecordingAudioSource.System })
        {
            try
            {
                // 库里存的是逐块文本；起始秒直接由块号推算（块大小固定 30 秒）。
                // 刻意不依赖 transcript_chunks.start_seconds 字段：它对丢失的块没有约束，
                // 而块号本身是"这一段音频在时间轴上的第几个 30 秒"，语义更硬。
                var stored = repository.ListTranscriptChunks(sessionId, source);
                if (stored.Count == 0) continue;

                var texts = new List<string>(stored.Count);
                var starts = new List<int>(stored.Count);
                foreach (var (index, text) in stored.OrderBy(c => c.Index))
                {
                    texts.Add(text ?? "");
                    starts.Add(index * DefaultChunkSeconds);
                }

                int sum = texts.Sum(t => t.Length);
                timelines.Add(Build(new TrackTranscript(source, texts, starts), sum));
            }
            catch (Exception)
            {
                // 读库失败不是致命问题：时间对齐只是"更好"，不是"必须"
                return null;
            }
        }

        return Merge(timelines);
    }
}
