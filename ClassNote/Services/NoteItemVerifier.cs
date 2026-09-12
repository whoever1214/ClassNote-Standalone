using System;
using System.Collections.Generic;
using System.Text;

namespace ClassNote.Services;

/// <summary>
/// 润色结果的回校校验器：**逐条验证"模型输出的文字里是否还能找到这条正文"**。
///
/// 背景：分段整理产出的每条内容都是原文表述（"原封不动"是产品要求），但最后一遍
/// "全局排序 + 连贯性润色"是整条链路里幻觉风险最高的一步——长输入下模型最典型的失败
/// 就是悄悄删掉几条、把两条揉成一条、或顺手改写正文。若不做校验，这些损失都不可见。
///
/// 做法：对每条正文抽若干"探针"（连续 n 个归一化字符），要求探针在润色结果里都能命中。
/// 命中不了就判定该条**可能被改写或丢失**，由调用方回退到"按原条目确定性拼装"。
/// 探针长度随正文长度缩放：短正文（如"0/0 型"）用短探针才可能命中，
/// 长正文用长探针才能验出"只留了开头、后面被删掉"这种截断式改写。
///
/// 判定是**保守且单向**的：它只用来拒绝可疑结果，不会用来"修好"结果——
/// 校验不通过就整体回退，绝不把半可信的润色稿交给用户。
/// </summary>
internal static class NoteItemVerifier
{
    /// <summary>第一条验证失败的条目（用于诊断信息）。</summary>
    internal record VerificationReport(bool AllVerified, int ItemCount, int FailedIndex, string FailedTitle)
    {
        internal static VerificationReport Ok(int itemCount) => new(true, itemCount, -1, "");
    }

    /// <summary>过短正文（少于该长度）无法可靠抽取探针，跳过校验而不是误判为失败。</summary>
    private const int MinVerifiableLength = 6;

    /// <summary>
    /// 校验"润色后的条目列表"是否完整、逐字地保留了每一条正文。
    /// </summary>
    /// <param name="polished">模型润色后的条目正文（同一顺序，条数应一致）。</param>
    /// <param name="items">原始条目（正文为原文，来自分段整理结果）。</param>
    internal static VerificationReport Verify(IReadOnlyList<string>? polished,
        IReadOnlyList<NotePromptBuilder.NoteItem> items)
    {
        if (items.Count == 0) return VerificationReport.Ok(0);

        // 条数对不上是**最危险的信号**：多半是模型合并/丢弃了条目，直接拒绝，
        // 不做"按位对齐能对几条算几条"的猜测
        if (polished == null || polished.Count != items.Count)
            return new VerificationReport(false, items.Count, 0,
                items.Count > 0 ? TitleOf(items[0]) : "");

        string haystack = NotePromptBuilder.NormalizeForCompare(string.Join("\n", polished));

        for (int i = 0; i < items.Count; i++)
        {
            string body = NotePromptBuilder.NormalizeForCompare(items[i].Body);
            if (body.Length < MinVerifiableLength) continue; // 太短，无法可靠判定

            int probe = ProbeLength(body.Length);
            if (!BodySurvives(haystack, body, probe))
                return new VerificationReport(false, items.Count, i, TitleOf(items[i]));
        }

        return VerificationReport.Ok(items.Count);
    }

    /// <summary>
    /// 探针长度：约正文的 1/3，夹在 [8, 24] 之间。
    /// 太短会放过"改了一部分"的情况，太长会让轻微标点差异就判失败。
    /// </summary>
    private static int ProbeLength(int bodyLength)
    {
        int probe = bodyLength / 3;
        if (probe < 8) probe = Math.Min(8, bodyLength);
        if (probe > 24) probe = 24;
        return probe;
    }

    /// <summary>
    /// 在正文里取头/中/尾三个位置的探针，并在**同一份润色文本**里寻找。
    /// 三处探针必须**全部命中**才算这条正文活着：
    /// 只命中头部的，多半是"后面被删掉或改写了"（截断式改写），这正是要拦的情况。
    /// </summary>
    private static bool BodySurvives(string haystack, string body, int probe)
    {
        int[] starts =
        {
            0,
            Math.Max(0, (body.Length - probe) / 2),
            Math.Max(0, body.Length - probe),
        };

        int checkedCount = 0;
        foreach (int start in starts)
        {
            if (start + probe > body.Length) continue;
            checkedCount++;
            string candidate = body.Substring(start, probe);
            if (!haystack.Contains(candidate, StringComparison.Ordinal))
                return false;
        }

        // 正文过短导致一个探针都取不到：已由 MinVerifiableLength 拦在前面，这里只兜底
        return checkedCount > 0;
    }

    private static string TitleOf(NotePromptBuilder.NoteItem item)
        => string.IsNullOrWhiteSpace(item.Title) ? "（无标题）" : item.Title;

    /// <summary>校验失败时写进笔记尾部的提示（必须让用户知道"这次没用润色稿"以及原因）。</summary>
    internal static string FallbackNotice(int itemCount, VerificationReport report)
    {
        var sb = new StringBuilder();
        sb.Append("> ⚠️ 最终润色稿未通过完整性校验（第 ")
          .Append(report.FailedIndex + 1).Append(" 条「").Append(report.FailedTitle)
          .Append("」在润色结果中找不到原文），已改用**按原条目顺序拼装**的版本。");
        sb.Append("共 ").Append(itemCount).Append(" 条内容均来自分段整理结果，未做改写。");
        return sb.ToString();
    }
}
