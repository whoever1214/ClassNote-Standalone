using System;
using System.Collections.Generic;
using System.Text;

namespace ClassNote.Services;

/// <summary>
/// 笔记质量的**确定性守卫**（v0.8 / P1）。
///
/// 它只做"机械检查"——每一条判据都能用代码解释清楚、都能在单测里复现，
/// 不依赖任何模型、也不做主观判断。三件事：
///
/// 1. **证据降级**：模型声称某条来自「现场」（老师讲的结论），但正文在转写里找不到佐证，
///    就降级为「课件」。**口头的自我声明不算数**——这是 v0.8 的关键口径。
///    化学课实测的事故正是这类：判断题的选项原句被标成"老师讲的结论"端给用户。
/// 2. **重复检测**：合并后仍然高度重复的正文会被数出来，写进摘要让用户看得见
///    （不静默丢内容——与全项目一贯口径一致）。
/// 3. **如实报告**：所有异常都写进摘要，而不是只留在日志里。
///
/// 刻意**不**做的事：判断"这条结论在学科上对不对"。那需要学科知识库，靠规则猜只会更糟。
/// 它管的是"这条内容的**来源**说法是否可信"，这正是可核对的部分。
/// </summary>
internal static class NoteQualityGuard
{
    /// <summary>"整条正文都能在转写里找到"之外的额外宽容度（字符）。</summary>
    /// <remarks>
    /// 判据是"最长公共前缀 ≥ 这个值，或者公共前缀就是整条正文"。
    /// 之所以是前缀而不是子串：模型如实地把老师的话改写了一遍时，前缀会很长；
    /// 而它把课件上的原句**缝**进正文时，与转写的公共前缀通常很短
    /// （例如正文「金属钠着火时，可用泡沫灭火器或干砂灭火」与转写「金属钠着火的时候不能用泡沫灭火器」
    /// 只有「金属钠着火」五个字相同）。取 12 是为了避免把正常的同义改写误判成"无佐证"。
    /// </remarks>
    private const int GroundingPrefixThreshold = 12;

    /// <summary>守卫结果。</summary>
    /// <param name="Items">可能被降级过的条目（未降级时原样返回）。</param>
    /// <param name="Downgraded">被降级的条目数（声称现场但无佐证）。</param>
    /// <param name="DuplicateBodies">合并后仍重复的正文句数（仅供参考，不丢内容）。</param>
    /// <param name="ExamCount">试卷原题条目数（用于摘要说明）。</param>
    public sealed record GuardResult(
        IReadOnlyList<NotePromptBuilder.NoteItem> Items,
        int Downgraded,
        int DuplicateBodies,
        int ExamCount)
    {
        /// <summary>是否有值得写进用户可见摘要的发现。</summary>
        public bool HasFindings => Downgraded > 0 || DuplicateBodies > 0;
    }

    /// <summary>
    /// 对去重后的条目列表做守卫检查。
    /// </summary>
    /// <param name="items">去重后的条目。</param>
    /// <param name="transcript">
    /// 本段（或本节）的课堂转写。为 null/空白时**跳过证据核对**——
    /// 没有可核对的对象时不能凭空判"无佐证"（单来源录音的正常情形）。
    /// </param>
    public static GuardResult Inspect(IReadOnlyList<NotePromptBuilder.NoteItem> items, string? transcript)
    {
        if (items.Count == 0)
            return new GuardResult(items, 0, 0, 0);

        bool canCheckGrounding = !string.IsNullOrWhiteSpace(transcript);
        string normalizedTranscript = canCheckGrounding ? NormalizeForCompare(transcript!) : "";

        var result = new List<NotePromptBuilder.NoteItem>(items.Count);
        int downgraded = 0;
        int examCount = 0;

        foreach (var item in items)
        {
            if (item.IsExam) examCount++;

            // 只有"声称来自现场"才需要核对：标「课件」「双证」「冲突」的条目本就说明它们有课件一侧的依据
            bool claimsLive = string.Equals(item.Evidence, "现场", StringComparison.Ordinal);
            if (!claimsLive || !canCheckGrounding || IsGroundedInTranscript(item.Body, normalizedTranscript))
            {
                result.Add(item);
                continue;
            }

            downgraded++;
            result.Add(item with { Evidence = "课件" });
        }

        return new GuardResult(result, downgraded, CountDuplicateBodies(result), examCount);
    }

    /// <summary>
    /// 正文在转写里有没有佐证：正文归一化后的**最长公共前缀**要么覆盖整条正文，
    /// 要么达到 <see cref="GroundingPrefixThreshold"/> 个字符。
    /// </summary>
    /// <param name="body">条目正文。</param>
    /// <param name="normalizedTranscript">已归一化的转写。</param>
    internal static bool IsGroundedInTranscript(string? body, string normalizedTranscript)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(normalizedTranscript)) return false;

        string normalizedBody = NormalizeForCompare(body!);
        if (normalizedBody.Length == 0) return false;

        int common = CommonPrefixLength(normalizedBody, normalizedTranscript);
        if (common >= normalizedBody.Length) return true;      // 整条都在转写里
        return common >= GroundingPrefixThreshold;
    }

    /// <summary>两段文本的公共前缀长度（按字符）。</summary>
    internal static int CommonPrefixLength(string a, string b)
    {
        int limit = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < limit && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>合并后仍完全相同的正文条数（去重之后还出现重复，说明去重键有漏网的情况）。</summary>
    private static int CountDuplicateBodies(IReadOnlyList<NotePromptBuilder.NoteItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;
        foreach (var item in items)
        {
            string key = NormalizeForCompare(item.Body);
            if (key.Length == 0) continue;
            if (!seen.Add(key)) duplicates++;
        }
        return duplicates;
    }

    /// <summary>与去重阶段同一套归一化口径（全角转半角、去空白与标点、小写）。</summary>
    private static string NormalizeForCompare(string? text) => NotePromptBuilder.NormalizeForCompare(text);

    /// <summary>
    /// 把守卫的发现写成用户可见的一段说明（没有发现时返回 null）。
    /// 措辞必须与事实对齐：降级不等于"内容错了"，而是"来源标注被纠正"。
    /// </summary>
    /// <param name="result">守卫结果。</param>
    public static string? DescribeFindings(GuardResult result)
    {
        if (!result.HasFindings) return null;

        var sb = new StringBuilder();
        if (result.Downgraded > 0)
        {
            sb.Append($"其中 {result.Downgraded} 条原标注为「现场」的内容在课堂录音转写里找不到佐证，已按「课件」处理");
        }
        if (result.DuplicateBodies > 0)
        {
            if (sb.Length > 0) sb.Append("；");
            sb.Append($"另有 {result.DuplicateBodies} 条正文与其它条目完全重复");
        }
        sb.Append("。");
        return sb.ToString();
    }
}
