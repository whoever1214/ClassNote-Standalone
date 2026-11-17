using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassNote.Services;

/// <summary>
/// 分段计划：把一次会话的转写与 OCR 素材切成若干"喂给 LLM 的窗口"。
/// </summary>
/// <param name="Course">课程名（原样进入标题）。</param>
/// <param name="Segments">分段窗口（顺序即时间顺序；单段时只含一个窗口）。</param>
/// <param name="IsSegmented">是否需要分段（素材在单次调用上限内时为 false）。</param>
/// <param name="OversizedChars">
/// 因段数上限而被并入最后一段、未再切分的转写字符数（0 = 最后一段也没超单段上限）。
/// 注意语义：这些内容**没有被丢弃**，它们全部进入了最后一段——只是那一段会超出单段上限，
/// 有可能被模型侧截断。因此本值表达的是"最后一段超出上限的量"，而不是"丢掉的内容"。
/// </param>
/// <param name="TotalTranscriptChars">转写总字符数（用于展示）。</param>
public sealed record NotePlan(
    string Course,
    IReadOnlyList<NoteSegment> Segments,
    bool IsSegmented,
    int OversizedChars,
    int TotalTranscriptChars);

/// <summary>一个 LLM 窗口：一段转写 + 归属该段的截图 OCR 片段。</summary>
/// <param name="Index">1 起的段序号。</param>
/// <param name="Total">总段数。</param>
/// <param name="Transcript">该段的转写文本（相邻段之间带重叠，避免跨段的知识点被截断）。</param>
/// <param name="OcrText">该段的截图 OCR 文本（带 [类型 @ 时间] 标记）。</param>
/// <param name="StartChar">该段在完整转写中的起始偏移。</param>
/// <param name="EndChar">该段在完整转写中的结束偏移（不含）。</param>
/// <param name="TimestampHint">该段大致覆盖的录音时间范围，如 "00:00–00:40"；无时间信息时为 null。</param>
public sealed record NoteSegment(
    int Index,
    int Total,
    string Transcript,
    string OcrText,
    int StartChar,
    int EndChar,
    string? TimestampHint);

/// <summary>截图 OCR 的一个条目（连续且只有数字的行会并入前一条目，避免页码丢失所属内容）。</summary>
/// <param name="Prefix">来源标记行，形如 <c>[new_slide @ 00:09:00]</c>。</param>
/// <param name="Content">该条目的正文。</param>
/// <param name="StartSeconds">截图时刻（秒）。</param>
/// <param name="SeqNo">截图序号（0 = 未知）。按时间对齐时用于保持稳定顺序。</param>
public sealed record OcrBlock(string Prefix, string Content, int StartSeconds, int SeqNo = 0);

/// <summary>
/// 笔记素材分段器：纯计算，不发网络请求。
///
/// 需求背景：一节课的转写可轻松超过单次调用的输入上限，早期实现直接 Truncate()
/// 把超出部分静默丢弃。在"核心知识点原封不动记录"的提示词要求下，被丢弃的内容
/// 就是永久丢失，因此改为按窗口分段、逐段整理、最后合并。
/// </summary>
public static class NoteSegmenter
{
    /// <summary>单个窗口的素材字符上限（转写 + OCR）。约为模型单次输出能力的 1/3，给"原封不动"留出余量。</summary>
    public const int DefaultMaxCharsPerSegment = 6000;

    /// <summary>相邻段之间的重叠字符数：让跨段边界的概念/公式至少在一个完整窗口内出现。</summary>
    public const int DefaultOverlapChars = 400;

    /// <summary>段数上限（成本与耗时的护栏）。超出部分会被标记为未处理，而不是静默丢弃。</summary>
    public const int DefaultMaxSegments = 40;

    /// <summary>合并阶段每组包含的分段数：分段很多时先分组合并，再逐级合并，避免合并输入再次超限。</summary>
    public const int DefaultMergeGroupSize = 3;

    private const int MinSegmentChars = 1000;

    /// <summary>截图标记行：[new_slide @ 00:12:30] / [annotation @ 12:30]。</summary>
    private static readonly Regex OcrHeaderRegex = new(
        @"^\s*\[(?<type>[^\]@\r\n]{1,32})@\s*(?:(?<h>\d{1,2}):)?(?<m>\d{1,2}):(?<s>\d{1,2})\s*\]\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>只有数字的行（PPT 页码、幻灯片编号）——判断为上一张幻灯片的附属内容。</summary>
    private static readonly Regex NumericOnlyRegex = new(@"^\s*\d{1,4}\s*$", RegexOptions.Compiled);

    /// <summary>
    /// 生成分段计划。永不抛异常，也永不返回空计划（素材为空时返回单个空窗口，
    /// 由调用方决定"没有内容"的处理方式）。
    /// </summary>
    /// <param name="maxCharsPerSegment">
    /// 单窗口字符上限。小于 <see cref="MinSegmentChars"/> 时按该下限处理（静默的下限保护，
    /// 因为本方法约定永不抛异常）。
    /// </param>
    /// <param name="overlapChars">相邻窗口重叠字符数；负数按 0，超过单窗口上限一半时按一半处理。</param>
    /// <param name="maxSegments">
    /// 段数上限（成本护栏），小于 1 时按 1 处理。超出上限的转写**不会被丢弃**，
    /// 而是全部并入最后一段，其量记录在 <see cref="NotePlan.OversizedChars"/>。
    /// </param>
    /// <param name="timeline">
    /// 转写 ↔ 录音时间的对照表（见 <see cref="TranscriptTimeline"/>）。提供时截图按**时间戳归属**到
    /// 对应段落；为 null 或不可用时退回"按序号均摊"——那条路会把别的时间段的幻灯片塞进本段，
    /// 使两源在同一段内无法互证（这正是 P0-β 要修的问题）。**失败即退化，绝不抛异常。**
    /// </param>
    public static NotePlan Build(
        string course,
        string? transcript,
        string? ocrText,
        int maxCharsPerSegment = DefaultMaxCharsPerSegment,
        int overlapChars = DefaultOverlapChars,
        int maxSegments = DefaultMaxSegments,
        TranscriptTimeline? timeline = null)
    {
        transcript ??= "";
        ocrText ??= "";
        course ??= "";

        if (maxCharsPerSegment < MinSegmentChars) maxCharsPerSegment = MinSegmentChars;
        if (overlapChars < 0) overlapChars = 0;
        if (overlapChars > maxCharsPerSegment / 2) overlapChars = maxCharsPerSegment / 2;
        if (maxSegments < 1) maxSegments = 1;

        var blocks = CollapseDuplicateOcr(ParseOcrEntries(ocrText));

        // 1. 段数：由转写字符数决定（OCR 通常远小于转写，且按序号均摊到各段）
        int stride = Math.Max(1, maxCharsPerSegment - overlapChars);
        int needed = string.IsNullOrEmpty(transcript)
            ? 1
            : (int)Math.Ceiling(transcript.Length / (double)stride);

        int segCount = Math.Min(Math.Max(needed, 1), maxSegments);
        bool segmented = needed > 1;
        // 段数被上限截住时剩余内容并**不丢弃**，而是全部并入最后一段（见下面的窗口计算）。
        // 因此这里记的是"最后一段将超出单段上限多少字"——用于如实告知可能被模型截断的量。
        int oversizedChars = needed > segCount
            ? Math.Max(0, transcript.Length - (segCount * stride))
            : 0;

        // 2. 转写窗口（最后一段吃掉剩余内容；被段数上限截断时同样"吃满到最后"，
        //    宁可这一段超限、并由 OversizedChars 如实告知，也不静默丢内容）
        var windows = new List<(int Start, int End)>(segCount);
        for (int i = 0; i < segCount; i++)
        {
            int start = i * stride;
            if (start > transcript.Length) start = transcript.Length;
            bool isLast = i == segCount - 1;
            int end = isLast ? transcript.Length : Math.Min(transcript.Length, start + maxCharsPerSegment);
            windows.Add((start, end));
        }

        // 3. OCR 条目按序号均摊到各窗口（与时间对齐解耦，避免"转写很短而 PPT 很多"
        //    时按时间切分把大量 OCR 重复塞进每一段）
        var buckets = AssignOcr(blocks, windows, segCount, timeline, maxCharsPerSegment);

        // 4. 组装窗口
        //    多来源素材（分轨录音，转写里带【麦克风…】/【系统声音…】标注）需要特殊处理：
        //    若只按字符切，靠后的段会只剩后一个来源，模型在那几段里**完全看不到另一侧内容**。
        //    因此按来源先拆开、各自按同一套窗口均摊，保证每段都带两侧标注。
        var sections = SplitLabeledSections(transcript);
        bool multiSource = sections.Count > 1;

        var segments = new List<NoteSegment>(segCount);
        // 多来源时一次性算好各段文本（每段都要带上两侧标注，不能按整体字符切）
        var sectionWindows = multiSource ? RenderSectionWindows(sections, segCount) : null;
        for (int i = 0; i < segCount; i++)
        {
            string text = sectionWindows != null
                ? sectionWindows[i]
                : SliceTranscript(transcript, windows[i]);

            string ocr = RenderOcr(buckets[i]);
            var (start, end) = windows[i];
            segments.Add(new NoteSegment(
                Index: i + 1,
                Total: segCount,
                Transcript: text,
                OcrText: ocr,
                StartChar: start,
                EndChar: end,
                TimestampHint: BuildTimestampHint(buckets[i])));
        }

        return new NotePlan(course, segments, segmented, oversizedChars, transcript.Length);
    }

    /// <summary>按 [start, end) 切一段转写；越界时返回空串。</summary>
    private static string SliceTranscript(string transcript, (int Start, int End) window)
        => window.Start >= transcript.Length ? "" : transcript.Substring(window.Start, window.End - window.Start);

    /// <summary>
    /// 把截图条目分配到各段。
    ///
    /// 两条路：
    /// · **按时间戳归属**（<paramref name="timeline"/> 可用时）：先算出每段覆盖的录音时间区间
    ///   （用该段转写字符区间反查），再把每张截图放进时间上包含它的那一段。这样同一段里的
    ///   转写与课件讲的是同一时刻的事，两源才能互证——这正是 P1 交叉核验的前提。
    /// · **按序号均摊**（旧行为，时间轴不可用时的兜底）：按时间排序后等分。
    ///
    /// 无论走哪条路，都会做一次**容量回填**：截图在时间上常常成簇（老师连续翻页），
    /// 而转写在时间上均匀，直接按时间分配会把某一页压到超过单段上限。因此把越界的条目
    /// 按剩余容量分配到还有空间的段里——宁可换一段，也不要撑破那一段的输入上限。
    /// </summary>
    /// <param name="blocks">已折叠重复的截图条目。</param>
    /// <param name="windows">各段的转写字符窗口（顺序即时间顺序）。</param>
    /// <param name="segCount">段数。</param>
    /// <param name="timeline">时间对照表（可为 null）。</param>
    internal static List<OcrBlock>[] AssignOcr(
        IReadOnlyList<OcrBlock> blocks, IReadOnlyList<(int Start, int End)> windows, int segCount,
        TranscriptTimeline? timeline,
        int maxCharsPerSegment = DefaultMaxCharsPerSegment)
    {
        var buckets = new List<OcrBlock>[segCount];
        for (int i = 0; i < segCount; i++) buckets[i] = new List<OcrBlock>();
        if (blocks.Count == 0 || segCount == 0) return buckets;

        // 稳定顺序：先按时间，其次按序号（同一秒内的多张截图保持原有先后）
        var order = new List<OcrBlock>(blocks);
        order.Sort((a, b) => a.StartSeconds != b.StartSeconds
            ? a.StartSeconds.CompareTo(b.StartSeconds)
            : a.SeqNo.CompareTo(b.SeqNo));

        if (segCount == 1 || timeline == null || !timeline.IsUsable)
        {
            for (int i = 0; i < order.Count; i++)
            {
                int seg = (int)((long)i * segCount / order.Count);
                if (seg >= segCount) seg = segCount - 1;
                buckets[seg].Add(order[i]);
            }
            return buckets;
        }

        // 各段的时间区间（取不到的段跳过，其截图由后面的容量兜底分配）
        var ranges = new (int Start, int End)?[segCount];
        for (int i = 0; i < segCount; i++)
            ranges[i] = timeline.TimeRangeFor(windows[i].Start, windows[i].End);

        // 单张截图允许占用的上限：单段上限的六成，给"这一页文字特别多"和别的截图留出余地。
        // 用单段上限而不是"平均值"是刻意的：平均值会被段数摊薄，段数一多就没人愿意收留大截图。
        int overflow = Math.Max(1, maxCharsPerSegment * 6 / 10);

        var pending = new List<OcrBlock>();
        foreach (var block in order)
        {
            int target = SegmentForTime(ranges, block.StartSeconds);
            if (target < 0)
            {
                pending.Add(block);   // 落在所有段之外：交给容量分配
                continue;
            }
            if (UsedChars(buckets[target]) + block.Content.Length > overflow)
            {
                pending.Add(block);   // 这一段装不下了：换一段，别撑破它的输入上限
                continue;
            }
            buckets[target].Add(block);
        }

        // 容量回填：按"剩余容量最多"的段依次放，保证既不丢内容也不撑破任何一段
        foreach (var block in pending)
        {
            int best = 0;
            for (int i = 1; i < segCount; i++)
            {
                if (UsedChars(buckets[i]) < UsedChars(buckets[best]))
                    best = i;
            }
            buckets[best].Add(block);
        }

        return buckets;
    }

    /// <summary>某个时刻落在哪一段的时间区间内；都不在时返回 -1。</summary>
    private static int SegmentForTime(IReadOnlyList<(int Start, int End)?> ranges, int seconds)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            if (range == null) continue;
            if (seconds >= range.Value.Start && seconds < range.Value.End)
                return i;
        }

        // 末段之后（录音结束后又截了一张图）归到最后一节有时间区间的段
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            if (ranges[i] != null && seconds >= ranges[i]!.Value.End) return i;
        }
        return -1;
    }

    private static int UsedChars(List<OcrBlock> bucket)
    {
        int sum = 0;
        foreach (var block in bucket)
            sum += block.Content.Length;
        return sum;
    }

    /// <summary>
    /// 把带来源标注的转写拆成 (标注行, 正文) 列表。
    /// 只在**行首**出现的已知标注行才算分区起点，避免正文里偶然提到同样的字串就误切。
    /// 没有任何标注时返回空列表（调用方据此走普通按字符切分）。
    /// </summary>
    internal static List<(string Label, string Body)> SplitLabeledSections(string transcript)
    {
        var result = new List<(string Label, string Body)>();
        if (string.IsNullOrEmpty(transcript)) return result;

        var labels = new[] { TranscriptSections.MicrophoneLabel, TranscriptSections.SystemLabel };
        var lines = transcript.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? currentLabel = null;
        var body = new System.Text.StringBuilder();

        void Flush()
        {
            if (currentLabel != null)
                result.Add((currentLabel, body.ToString().Trim()));
            body.Clear();
        }

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            string? matched = labels.FirstOrDefault(l => string.Equals(trimmed, l, StringComparison.Ordinal));
            if (matched != null)
            {
                Flush();
                currentLabel = matched;
                continue;
            }
            if (currentLabel != null)
                body.AppendLine(line);
        }
        Flush();

        // 只有一个分区不算"多来源"（可能只是正文里恰好有一行标注）
        return result.Count > 1 ? result : new List<(string Label, string Body)>();
    }

    /// <summary>
    /// 把各来源分区按序号均摊到 <paramref name="segCount"/> 段，每段都带上全部来源的标注行
    /// （该来源在本段没有内容时输出"（本段无内容）"占位，让模型明确知道"这一侧这段确实没有"）。
    /// </summary>
    private static List<string> RenderSectionWindows(List<(string Label, string Body)> sections, int segCount)
    {
        var windows = new List<System.Text.StringBuilder>(segCount);
        for (int i = 0; i < segCount; i++) windows.Add(new System.Text.StringBuilder());

        foreach (var (label, body) in sections)
        {
            int stride = Math.Max(1, (int)Math.Ceiling(body.Length / (double)segCount));
            for (int i = 0; i < segCount; i++)
            {
                int start = Math.Min(i * stride, body.Length);
                int end = Math.Min(start + stride, body.Length);
                string part = end > start ? body[start..end].Trim() : "";

                if (windows[i].Length > 0) windows[i].AppendLine().AppendLine();
                windows[i].Append(label).AppendLine();
                windows[i].Append(part.Length > 0 ? part : "（本段无内容）");
            }
        }

        return windows.Select(w => w.ToString()).ToList();
    }

    /// <summary>
    /// 解析 OCR 文本为条目。行为与 <see cref="LlmService"/> 早期把 OCR 拼成一段时一致：
    /// "[类型 @ 时间]" 作为条目标记；标记之后若紧跟"只有数字的行"，并入当前条目（PPT 页码）。
    /// 无法解析时整段作为单条目返回。
    /// </summary>
    public static List<OcrBlock> ParseOcrEntries(string? ocrText)
    {
        var result = new List<OcrBlock>();
        if (string.IsNullOrWhiteSpace(ocrText)) return result;

        string text = ocrText.Replace("\r\n", "\n").Replace('\r', '\n');
        var markers = OcrHeaderRegex.Matches(text);

        if (markers.Count == 0)
        {
            string content = text.Trim();
            if (content.Length > 0) result.Add(new OcrBlock("", content, 0));
            return result;
        }

        int firstStart = markers[0].Index;
        string preamble = text[..firstStart].Trim();
        if (preamble.Length > 0) result.Add(new OcrBlock("", preamble, 0));

        for (int i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            int bodyStart = m.Index + m.Length;
            int bodyEnd = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            var body = text[bodyStart..bodyEnd];

            var keep = new List<string>();
            var pendingNumeric = new List<string>();

            // 数字行（PPT 页码）是"上一行内容的附属"，因此只在遇到下一条正文或条目结束时并入，
            // 绝不允许跨条目残留——否则页码会跑到下一张幻灯片的文字里
            void FlushPending()
            {
                if (pendingNumeric.Count == 0) return;
                keep.AddRange(pendingNumeric);
                pendingNumeric.Clear();
            }

            var lines = body.Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (NumericOnlyRegex.IsMatch(line))
                {
                    pendingNumeric.Add(line.Trim());
                    continue;
                }
                FlushPending();
                keep.Add(line.TrimEnd());
            }
            FlushPending();

            int seconds = ParseSeconds(m.Groups["h"].Value, m.Groups["m"].Value, m.Groups["s"].Value);
            string type = m.Groups["type"].Value.Trim();
            string content = string.Join("\n", keep).Trim();
            if (content.Length > 0) result.Add(new OcrBlock($"[{type} @ {FormatTimestamp(seconds)}]", content, seconds));
        }

        return result;
    }

    /// <summary>
    /// 折叠内容重复的 OCR 条目：同一张幻灯片在屏幕上停留期间会被反复截屏
    /// （批注/光标/动画都会触发变化检测），OCR 出来的文字几乎一模一样。
    /// 这些重复既不是知识点、也不提供信息，却会按比例拉长提示词输入（prefill 更慢、token 更贵）。
    ///
    /// 判定刻意保守：**只折叠"归一化后完全相同"的相邻条目**（去空白、统一全/半角空格）。
    /// 只要有一个字符不同就原样保留——宁可留一点冗余，也不要把两张相似但不相同的幻灯片合并掉。
    /// </summary>
    internal static List<OcrBlock> CollapseDuplicateOcr(IReadOnlyList<OcrBlock> blocks)
    {
        if (blocks.Count <= 1)
            return new List<OcrBlock>(blocks);

        var result = new List<OcrBlock>(blocks.Count);
        string? lastKey = null;
        foreach (var block in blocks)
        {
            var key = NormalizeForComparison(block.Content);
            if (key.Length > 0 && key == lastKey)
                continue; // 与上一条内容相同：丢弃（保留第一条的时间标记）
            lastKey = key;
            result.Add(block);
        }
        return result;
    }

    /// <summary>比对用的归一化：去掉所有空白字符（换行/空格/全角空格差异不构成"不同内容"）。</summary>
    private static string NormalizeForComparison(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";
        var sb = new StringBuilder(content.Length);
        foreach (var ch in content)
        {
            if (!char.IsWhiteSpace(ch) && ch != '\u3000')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string RenderOcr(List<OcrBlock> blocks)    {
        if (blocks.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (b.Prefix.Length > 0) sb.AppendLine(b.Prefix);
            sb.AppendLine(b.Content);
        }
        return sb.ToString().TrimEnd();
    }

    private static string? BuildTimestampHint(List<OcrBlock> blocks)
    {
        if (blocks.Count == 0) return null;
        int min = int.MaxValue;
        int max = int.MinValue;
        foreach (var b in blocks)
        {
            if (b.StartSeconds < min) min = b.StartSeconds;
            if (b.StartSeconds > max) max = b.StartSeconds;
        }
        if (min == int.MaxValue) return null;
        // 全部无时间信息（0）时不展示提示，避免误导
        if (min == 0 && max == 0) return null;
        return $"{FormatTimestamp(min)}–{FormatTimestamp(max)}";
    }

    private static int ParseSeconds(string h, string m, string s)
    {
        int hours = string.IsNullOrEmpty(h) ? 0 : int.Parse(h);
        int minutes = string.IsNullOrEmpty(m) ? 0 : int.Parse(m);
        int seconds = string.IsNullOrEmpty(s) ? 0 : int.Parse(s);
        if (minutes > 59 && hours == 0)
        {
            // "[@ 90:30]" 这类写法按 90 分钟理解
            hours = minutes / 60;
            minutes %= 60;
        }
        return (hours * 3600) + (minutes * 60) + seconds;
    }

    /// <summary>秒 → mm:ss（超过 1 小时为 h:mm:ss），与 App 其它时间展示保持一致。</summary>
    public static string FormatTimestamp(int seconds)
    {
        if (seconds < 0) seconds = 0;
        int h = seconds / 3600;
        int m = (seconds % 3600) / 60;
        int s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}
