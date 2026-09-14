using System.Text;

namespace ClassNote.Services;

/// <summary>
/// 笔记生成的提示词构造与模型输出解析（纯函数，便于单测）。
///
/// 提示词只有两处：单段整理（唯一一段素材时的完整生成）、分段整理（每段产出 JSON）、
/// 合并（多段 JSON 合成最终笔记）。三者共享 <see cref="VerbatimRules"/>，
/// 保证"核心知识点原封不动"的口径在分段前后一致——否则分段会把保真要求稀释掉。
/// </summary>
internal static class NotePromptBuilder
{
    /// <summary>单段（一次性生成完整笔记）的 system prompt。</summary>
    internal const string NoteSystemPrompt =
        "你是一名资深助教，负责把课堂录音转写和课件截图 OCR 整理成课堂笔记。请用 Markdown 输出，只保留三部分：课程标题、核心知识点、重点及易错点。\n\n" +
        "整理原则：\n" +
        "1. 以知识点为核心：逐条提炼本节课的核心知识点（核心知识点尽量不做修改、原封不动），涵盖概念定义、公式定理、关键结论、解题模板与解题技巧，按知识逻辑分层组织。\n" +
        "2. 核心知识点尽量不做修改、原封不动地记录：解题模板、解题技巧、公式定理、定义、老师反复强调的结论，一律照抄【课堂录音转写】与【课件/板书截图 OCR】中的原始表述，不得改写、概括、精简或换词；不得省略中间步骤与限定条件。若原文是口语，先按原意整理成通顺句子，但技术内容（术语、公式、数字、符号、条件）必须与原文逐字一致。宁可保留原文，也不要改写。\n" +
        "3. 忽略非教学内容：课堂开头的寒暄问候、点名、课堂管理、设备调试等与知识点无关的白话一律不写入笔记。\n" +
        "4. 不要例题与解析：删除所有例题、习题及其解答过程（但老师给出的解题模板、通用解题步骤与解题技巧属于核心知识点，须原样保留）。\n" +
        "5. 不要师生问答：删除提问与回答的对话形式内容（其中的知识点按原则 1、2 归纳进核心知识点）。\n" +
        "6. 重点及易错点：单独成节，收录老师强调的重点、考试提示、易错点与易混淆概念，注明原文的说法。\n" +
        "7. 去重与排序：同一话题合并归纳，重要程度越高越靠前；但合并仅限于重复内容，不得因此改写或丢失技术细节。";

    /// <summary>分段整理的 system prompt：素材可能是课程中段，不得补全、不得假设结尾。</summary>
    internal const string SegmentSystemPrompt =
        "你是一名资深助教，正在分段整理一节课堂的录音转写与课件截图 OCR。" +
        "你拿到的可能只是整节课的中间片段：不要假设能看到开头或结尾，不要补全缺失内容，只整理当前片段中确实出现的内容。" +
        "请用 JSON 输出，只包含 coreKnowledge 与 pitfalls 两个字段。\n\n" +
        VerbatimRules + "\n\n" +
        OcrNoiseRules + "\n" +
        "输出格式（严格遵守，只输出 JSON，不要 Markdown 代码块，不要任何解释文字）：\n" +
        "{\"coreKnowledge\":\"## 核心知识点\\n- 知识点：原文或按原意整理后的表述（技术内容逐字保留）\\n\",\"pitfalls\":\"- 易错点：原文的说法\\n\"}";

    /// <summary>合并阶段的 system prompt：分段 JSON 是事实源，原文是校对依据。</summary>
    internal const string MergeSystemPrompt =
        "你是一名资深助教。下面给出同一节课各分段的整理结果，请合并成一份完整的课堂笔记，用 Markdown 输出。" +
        "只保留三部分：课程标题、核心知识点、重点及易错点。\n\n" +
        "合并要求：\n" +
        "1. 完整保留：本课程所有分段中的核心知识点都必须进入最终笔记，不得整体省略、不得只留摘要。\n" +
        "2. 逐字保真：各分段结果里的核心知识点已经是原文的表述，合并时原封不动地照抄，不得改写、概括、精简或换词；术语、公式、数字、符号、限定条件必须与分段结果逐字一致，中间步骤不得省略。**公式的 $...$ 分隔符必须原样保留**，不要为了「整齐」把公式拆开或去掉分隔符。\n" +
        "3. 去重：只合并完全重复的内容，保留信息最完整的那份表述；合并仅限于重复，不得因此丢失技术细节。\n" +
        "4. 结构：按知识逻辑组织，重要程度越高越靠前；不要出现例题与解析、师生问答、课堂管理等与知识点无关的内容。\n" +
        "5. 校对：若提供了原始素材且发现分段结果遗漏了其中的核心知识点、定义、公式或限定条件，用原始素材中的原文补齐（照抄原文，不改写）。\n" +
        "6. 结尾只输出笔记正文，不要任何说明、前言或总结性致辞。\n" +
        "7. OCR 素材：核对时若遇到原始素材摘录里逐字拆开、明显识别错误的内容，能确定含义的按正常写法写出，" +
        "确定不了的原样保留并注明「OCR 不清」，不要猜符号。";

    /// <summary>
    /// 多来源素材（麦克风与系统声音分轨录制）专用：合并阶段的分辨要求。
    /// 各分段结果里已经带了「现场」「课件」这类来源标记，合并时不许把这些标记洗掉——
    /// 洗掉之后最终笔记就看不出知识点是谁给的了，等于前面白分轨。
    /// </summary>
    internal const string MultiSourceMergeRule =
        "7. 保留来源标记：各分段结果里的「现场」「课件」等来源标记必须原样保留；" +
        "不同来源的同名知识点不要合并成一条，来源不同就是两条信息。\n";

    /// <summary>
    /// 一条结构化笔记条目。**分段调用一律产出条目列表**，这样后端才能做确定性处理
    /// （按标题去重、排序、回校），而不是把整篇 Markdown 交给模型反复改写。
    /// </summary>
    /// <param name="Title">条目标题（去重的依据）。</param>
    /// <param name="Body">条目正文：原文表述，技术内容逐字保留。</param>
    /// <param name="Kind"><c>knowledge</c> = 核心知识点；<c>pitfall</c> = 重点/易错点。</param>
    /// <param name="Source">来源词（「现场」「课件」）；单来源录音为空串。</param>
    /// <param name="SegmentIndex">产出该条目的分段序号（1 起，单段为 1）。</param>
    internal sealed record NoteItem(string Title, string Body, string Kind, string Source, int SegmentIndex)
    {
        /// <summary>是否为易错点条目。</summary>
        internal bool IsPitfall => string.Equals(Kind, "pitfall", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>分段调用的条目 schema（JSON）：一次产出条目数组，供后端结构化合并。</summary>
    internal const string SegmentItemsSchema =
        "{\"items\":[" +
        "{\"title\":\"条目短标题（用于去重，同一知识点在不同片段请用同一标题）\"," +
        "\"body\":\"条目正文：原文或按原意整理后的表述，技术内容逐字保留\"," +
        "\"kind\":\"knowledge 或 pitfall\"," +
        "\"source\":\"本条来自哪一侧：现场 或 课件；没有分来源时留空字符串\"}" +
        "]}";

    /// <summary>
    /// OCR 素材的处理口径。
    ///
    /// 背景：内置 OCR 对中文是"一字一词"的结果，拼行时会在每个字之间插空格，公式符号也常被认错。
    /// 软件已经在送模型之前做了一轮**字符级**规范化（<see cref="OcrTextNormalizer"/>），
    /// 但残余噪声仍在。面对残余噪声，模型有两种错误做法，实测都发生过：
    /// 照抄乱码（把「m 主 n」写进笔记），或猜一个"看起来合理"的符号
    /// （把 `f(n-1)+1` 猜成 `f(n-1)`、把乱码约束顺成意思相反的公式）。
    /// 因此这里把口径说死：能确定就整理成正常写法，确定不了就保留原样并标注。
    /// </summary>
    internal const string OcrNoiseRules =
        "OCR 素材的处理口径：\n" +
        "1. 课件截图 OCR 里字符常被空格逐字拆开（如「用 f （ n ） 代表」），要按正常写法整理；已是正常写法的不要改动。\n" +
        "2. 明显的识别错误（例如 min 被认成「m 主 n」、下标 i 被认成「工」、减号被认成汉字「一」）：" +
        "能根据上下文确定含义的，按正确写法写出；**确定不了的一律原样保留，并在该处注明「OCR 不清」**，" +
        "禁止猜一个看起来合理的符号——宁可留一个看得见的疑点，也不要写一个看不出错的错公式。\n";

    /// <summary>
    /// 来源标注口径（只用于带 <c>source</c> 字段的条目 schema）。
    ///
    /// 【课件/板书截图 OCR】的内容不属于任何一路录音，模型只能猜；实测出现过同一次输出里
    /// 同为 OCR 来源的条目有的标「课件」、有的标「现场」。这里给出确定口径，避免同一批素材两套说法。
    /// </summary>
    internal const string OcrSourceRules =
        "来源标注口径：来自【课件/板书截图 OCR】的内容，source 一律填「课件」；" +
        "只有能确认是教室现场板书时才填「现场」；判断不了就留空字符串。\n";

    /// <summary>分段整理的 system prompt（条目 schema）。</summary>
    internal const string SegmentItemsSystemPrompt =
        "你是一名资深助教，正在分段整理一节课堂的录音转写与课件截图 OCR。" +
        "你拿到的可能只是整节课的中间片段：不要假设能看到开头或结尾，不要补全缺失内容，只整理当前片段中确实出现的内容。" +
        "请用 JSON 输出，只包含一个 items 数组，每个元素含 title / body / kind / source 四个字段。\n\n" +
        VerbatimRules + "\n\n" +
        OcrNoiseRules + "\n" +
        OcrSourceRules + "\n" +
        "输出格式（严格遵守，只输出 JSON，不要 Markdown 代码块，不要任何解释文字）：\n" +
        SegmentItemsSchema;

    /// <summary>
    /// 最终一遍（全局排序 + 连贯性润色）的 system prompt。
    ///
    /// 允许模型输出润色后的正文，但**后端会逐条回校**（<see cref="NoteItemVerifier"/>）：
    /// 任一条正文在润色稿里找不到原文，就整体退回"按原条目确定性拼装"。
    /// 因此这里可以要求润色，但必须把"只改表述、不动内容"说到最死。
    /// </summary>
    internal const string FinalPassSystemPrompt =
        "你是一名资深助教。下面给出同一节课已去重后的全部笔记条目（编号 1..N），" +
        "请做一次全局逻辑排序与连贯性润色，使整篇笔记读起来通顺、层次清楚。\n\n" +
        "硬性要求（违反任一条，本次结果会被系统整体丢弃）：\n" +
        "1. 条目数量必须与输入完全一致：不得新增、删除、合并、拆分任何条目。\n" +
        "2. **每条正文必须与对应输入的正文逐字一致**：术语、公式、数字、符号、限定条件、步骤一律照抄。" +
        "只允许在正文前后添加承上启下的短句（如「接下来」「在此基础上」），不得改写正文本身，不得省略任何一句。**公式的 $...$ 分隔符必须原样保留**。\n" +
        "3. order 必须是 1..N 的完整排列；易错点条目排在相应知识点附近，但不必连续。\n" +
        "4. 过渡句里不得引入素材里没有的新知识、新结论、新数字。\n" +
        "5. 只输出 JSON，不要 Markdown 代码块，不要任何解释文字。\n\n" +
        "输出格式：\n" +
        "{\"items\":[{\"index\":2,\"transition\":\"\",\"body\":\"这一条的原文正文\"}," +
        "{\"index\":1,\"transition\":\"在此基础上\",\"body\":\"这一条的原文正文\"}]}";

    /// <summary>最终一遍的 user prompt：去重后的条目清单。</summary>
    internal static string BuildFinalPassPrompt(string course, IReadOnlyList<NoteItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("课程：").Append(course).AppendLine();
        sb.Append("共 ").Append(items.Count).AppendLine(" 条（已按标题去重）：");
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.Append("[").Append(i + 1).Append("] ");
            if (!string.IsNullOrWhiteSpace(item.Title)) sb.Append(item.Title).Append(" — ");
            sb.AppendLine(item.Body);
        }
        sb.AppendLine();
        sb.AppendLine("请按 system 要求输出排序与润色结果（JSON）。每条正文必须与上面逐字一致。");
        return sb.ToString();
    }

    /// <summary>最终一遍输出的一个元素。</summary>
    internal sealed record PolishedItem(int Index, string Transition, string Body);

    /// <summary>
    /// 解析最终一遍的输出：<c>{"items":[{"index":…,"transition":…,"body":…}]}</c>。
    /// 校验 index 必须构成 1..N 的**完整排列**（不缺、不重）、body 非空；
    /// 任一不满足返回 null，由调用方退回确定性拼装——绝不接受缺项的排序结果。
    /// </summary>
    internal static List<PolishedItem>? ParseFinalPlan(string? raw, int itemCount)
    {
        if (string.IsNullOrWhiteSpace(raw) || itemCount <= 0) return null;

        string text = StripCodeFence(raw!);
        int open = text.IndexOf('{');
        int close = text.LastIndexOf('}');
        if (open < 0 || close <= open) return null;

        FinalPlanJson? parsed;
        try
        {
            parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<FinalPlanJson>(text[open..(close + 1)]);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
        if (parsed?.Items == null || parsed.Items.Count != itemCount) return null;

        var seen = new bool[itemCount + 1];
        var result = new List<PolishedItem>(itemCount);
        foreach (var rawItem in parsed.Items)
        {
            if (rawItem == null) return null;
            int idx = rawItem.Index ?? 0;
            if (idx < 1 || idx > itemCount) return null;
            if (seen[idx]) return null;   // 重复：说明有条目被写了两次，另一个多半被丢了
            seen[idx] = true;

            string body = (rawItem.Body ?? "").Trim();
            if (body.Length == 0) return null;
            result.Add(new PolishedItem(idx, (rawItem.Transition ?? "").Trim(), body));
        }
        return result;
    }

    /// <summary>
    /// 按最终一遍的条目顺序装配笔记。正文取自**模型给的那一份**
    /// （调用方必须先用 <see cref="NoteItemVerifier"/> 校验通过才会走这里）。
    /// </summary>
    internal static string AssembleFromPolished(string course, IReadOnlyList<NoteItem> items,
        IReadOnlyList<PolishedItem> polished)
    {
        var kb = new StringBuilder();
        var pf = new StringBuilder();

        foreach (var entry in polished)
        {
            var item = items[entry.Index - 1];
            var target = item.IsPitfall ? pf : kb;

            if (target.Length > 0) target.AppendLine();
            if (!string.IsNullOrWhiteSpace(entry.Transition)) target.AppendLine(entry.Transition);
            if (!string.IsNullOrWhiteSpace(item.Title)) target.Append("**").Append(item.Title).Append("**：");
            target.AppendLine(entry.Body.Trim());
        }

        return ComposeNote(course, kb, pf);
    }

    /// <summary>按条目原顺序拼装（不做任何改写）——润色未通过校验时的确定性兜底。</summary>
    internal static string AssembleFromItems(string course, IReadOnlyList<NoteItem> items)
    {
        var kb = new StringBuilder();
        var pf = new StringBuilder();

        foreach (var item in items)
        {
            var target = item.IsPitfall ? pf : kb;
            if (target.Length > 0) target.AppendLine();
            if (!string.IsNullOrWhiteSpace(item.Title)) target.Append("**").Append(item.Title).Append("**：");
            target.AppendLine(item.Body.Trim());
        }

        return ComposeNote(course, kb, pf);
    }

    /// <summary>拼成最终笔记：标题 + 核心知识点 + （有则）重点及易错点。</summary>
    private static string ComposeNote(string course, StringBuilder knowledge, StringBuilder pitfalls)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(course);
        sb.AppendLine();
        sb.AppendLine("## 核心知识点");
        sb.AppendLine();
        sb.AppendLine(knowledge.Length > 0 ? knowledge.ToString().Trim() : "（本轮未整理出核心知识点）");
        if (pitfalls.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 重点及易错点");
            sb.AppendLine();
            sb.AppendLine(pitfalls.ToString().Trim());
        }
        return sb.ToString().Trim();
    }

    /// <summary>去重结果：保留下来的条目 + 被合并掉的条数（用于如实报告"合并了 N 条重复内容"）。</summary>
    internal sealed record DedupResult(IReadOnlyList<NoteItem> Items, int MergedCount);

    /// <summary>
    /// **确定性去重**：仅当"归一化标题 + 归一化正文"双双完全一致时才合并（保留先出现的那条）。
    ///
    /// 刻意不做相似度合并：类似「洛必达法则适用条件」与「洛必达条件」这样的标题是否同一知识点，
    /// 程序无法可靠判定，而按相似度合并会**静默丢掉一条正文**。宁可留重复让用户看到，
    /// 也不做无法解释的合并。
    /// </summary>
    internal static DedupResult Deduplicate(IReadOnlyList<NoteItem> items)
    {
        var kept = new List<NoteItem>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int merged = 0;

        foreach (var item in items)
        {
            // 键包含 kind：同一条内容被一段归为知识点、另一段归为易错点时必须都留下，
            // 否则会丢掉一种归类（两类在最终笔记里进的是不同章节）
            string key = (item.IsPitfall ? "pitfall" : "knowledge")
                         + "\u0001" + NormalizeForCompare(item.Title)
                         + "\u0001" + NormalizeForCompare(item.Body);
            if (!seen.Add(key))
            {
                merged++;
                continue;
            }
            kept.Add(item);
        }
        return new DedupResult(kept, merged);
    }

    /// <summary>
    /// 归一化：全角转半角、去掉空白与常见标点，再转小写。
    /// 只用于"完全一致"判定，因此可以激进；它不参与任何相似度计算。
    /// </summary>
    internal static string NormalizeForCompare(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            char ch = c;
            // 全角 → 半角（ASCII 可见区与全角空格）
            if (ch == '\u3000') continue;
            if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0);

            if (char.IsWhiteSpace(ch)) continue;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string StripCodeFence(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
            text = text.Trim();
        }
        return text;
    }

    private sealed class FinalPlanJson
    {
        [Newtonsoft.Json.JsonProperty("items")]
        public List<FinalPlanItemJson?>? Items { get; set; }
    }

    private sealed class FinalPlanItemJson
    {
        [Newtonsoft.Json.JsonProperty("index")]
        public int? Index { get; set; }

        [Newtonsoft.Json.JsonProperty("transition")]
        public string? Transition { get; set; }

        [Newtonsoft.Json.JsonProperty("body")]
        public string? Body { get; set; }
    }

    /// <summary>共享的保真规则：单段、分段、合并三处口径一致。</summary>
    private const string VerbatimRules =
        "核心要求：\n" +
        "1. 保真优先（核心知识点尽量不做修改、原封不动地记录）：解题模板、解题技巧、公式定理、定义、限定条件、老师反复强调的结论，必须照抄素材中的原始表述，不得改写、概括、换词或省略中间步骤；口语可整理成通顺句子，但技术内容（术语、公式、数字、符号、条件）必须与原文逐字一致。宁可保留原文，也不要改写。\n" +
        "2. 不要例题与解析：删除例题、习题及其解答过程（老师的解题模板、通用解题步骤与解题技巧本身属于核心知识点，须原样保留）。\n" +
        "3. 不要师生问答：不要记录问答对话形式，其中出现的知识点按第 1 条整理进核心知识点。\n" +
        "4. 忽略非教学内容：寒暄问候、点名、闲聊、设备调试、课堂管理等与知识点无关的内容一律不写入。\n" +
        "5. 宁多勿漏：素材中出现的知识点不要因为内容零碎而丢弃；技术细节、公式、数字、符号与限定条件宁可保留原文，也不要因求简洁而丢失或改写。\n" +
        MathFormatRules;

    /// <summary>
    /// 数学公式的书写规则（v0.7 追加）。
    ///
    /// 背景：笔记页用 MathJax 渲染公式，而它只认 <c>$...$</c> 这类分隔符；
    /// 模型过去把公式当普通文字写（例如 <c>f(n) = min(f(n-1)+1)</c>），于是**一个公式都排不出来**。
    /// 这条规则要求"补分隔符"，同时把"内容一个字都不改"说到死——
    /// 它必须与 <see cref="VerbatimRules"/> 的保真口径一致，绝不能变成"允许改写公式"。
    ///
    /// ⚠️ 与之配套的渲染侧约束（详见 <see cref="NoteHtmlRenderer"/>）：MathJax 只把
    /// <c>\(...\)</c> / <c>\[...\]</c> 当分隔符，**普通括号与方括号不是分隔符**——
    /// 历史上正是这一条写错，导致笔记里每一对括号都被渲染成行间公式。
    /// </summary>
    private const string MathFormatRules =
        "6. 数学公式用 $...$ 包裹（只补分隔符，内容一个字都不改）：状态转移方程、递推式、下标与上下标、求和/取最值等表达式，写成 $f(n) = \\min(f(n-1)+1, f(n-5)+1)$ 这样的形式，行间公式用 $$...$$。\n" +
        "   · 公式内部的写法**照抄原文**：原文写 d[i][j] 就写 $d[i][j]$，不要擅自改成 d_{i,j}；原文的括号、逗号、符号一律保留。\n" +
        "   · 若课件 OCR 把一个公式的上下标/分式拆成了多行（例如 d 单独一行、i 一行、j 一行），只有在能明确判断它们属于同一个公式时才合并成一行；判断不了就原样保留，不要猜。\n" +
        "   · 绝对不要把普通句子、括号说明、编号包进 $...$；正文里的圆括号与方括号是普通文字，不是公式分隔符。";

    /// <summary>
    /// JSON 模式下必须显式设置 max_tokens：DeepSeek 文档明确要求，否则 JSON 字符串可能
    /// 被从中截断，直接导致该段解析失败。8192 对单段整理与合并都够用。
    /// </summary>
    internal const int MaxOutputTokens = 8192;

    /// <summary>
    /// 单段调用的 user prompt。素材在单次上限内时走这条路径，与分段路径的规则口径一致。
    /// </summary>
    /// <param name="multiSource">
    /// 转写是否是多来源分区文本（麦克风 + 系统声音分轨录制）。为 true 时加入"分辨谁在说话"的要求；
    /// 单来源录音不加这条，避免给模型多余的结构暗示。
    /// </param>
    internal static string BuildSingleNotePrompt(string course, string transcript, string ocrText,
        bool multiSource = false)
    {
        var sb = new StringBuilder();
        sb.Append("课程：").Append(course).AppendLine();
        sb.AppendLine();
        sb.AppendLine("【课堂录音转写】");
        sb.AppendLine(transcript);
        sb.AppendLine();
        sb.AppendLine("【课件/板书截图 OCR】");
        sb.AppendLine(ocrText);
        sb.AppendLine();
        sb.AppendLine("请按以下要求整理成课堂笔记（Markdown），只保留三部分：课程标题、核心知识点、重点及易错点：");
        sb.AppendLine("- 推荐结构：# 课程标题 → ## 核心知识点 → ## 重点及易错点；除这三部分外不要再加其它章节；");
        sb.AppendLine("- 核心知识点尽量不做修改、原封不动记录：解题模板、解题技巧、公式定理、定义、限定条件、老师反复强调的结论，必须照抄上面【课堂录音转写】与【课件/板书截图 OCR】的原始表述，不要改写、不要概括、不要换词、不要省略步骤；");
        sb.AppendLine("- 不要例题与解析：删除例题、习题及其解答（老师的解题模板、通用解题步骤与解题技巧属于核心知识点，须原样保留）；");
        sb.AppendLine("- 不要师生问答：不要以问答形式记录，其中出现的知识点按上面的要求归纳进「核心知识点」；");
        sb.AppendLine("- 只记录与知识点相关的内容：课堂开头的寒暄、点名、闲聊、课堂管理等与课程无关的片段一律忽略，不要写入笔记；");
        sb.AppendLine("- 重要内容可标注「重点」；易错点、易混淆概念与考试提示集中放入「重点及易错点」；");
        sb.AppendLine("- OCR 文本整理：课件截图 OCR 里字符常被空格逐字拆开，按正常写法整理；" +
                      "明显的识别错误能确定含义的按正确写法写出，确定不了的一律原样保留并注明「OCR 不清」，不要猜符号；");
        if (multiSource)
        {
            // 分轨录制（麦克风 + 系统声音）时才叮嘱：素材里带了两个来源分区，
            // 模型需要据此分辨"老师现场讲的"与"课件/网课里播的"
            sb.AppendLine("- 分辨来源：转写按【麦克风（教室现场）】与【系统声音（课件/网课播放）】分区给出，" +
                          "两侧各自独立转写、没有逐句对齐关系；凡涉及来源的表述必须用「现场」「课件」这类来源词标明，" +
                          "不要混为一谈；两边同一内容保留信息更完整的一份并注明来源，不要当成两个知识点；" +
                          "【课件/板书截图 OCR】里的内容按「课件」处理；");
        }
        sb.AppendLine("- 宁多勿漏：技术细节、公式、数字、符号与限定条件宁可保留原文，也不要因求简洁而丢失或改写。");
        return sb.ToString();
    }

    /// <summary>
    /// 分段整理的 user prompt。<paramref name="segment"/> 为 null 时表示"重试"：
    /// 不重复铺陈分段说明，直接要求按同样规则输出 JSON。
    /// </summary>
    internal static string BuildSegmentPrompt(string course, NoteSegment segment, bool isRetry = false,
        bool multiSource = false, bool itemsSchema = false)
    {
        var sb = new StringBuilder();
        if (!isRetry)
        {
            sb.Append("课程：").Append(course).AppendLine();
            sb.Append("这是第 ").Append(segment.Index).Append(" / ").Append(segment.Total).Append(" 段素材");
            if (segment.TimestampHint != null)
                sb.Append("（约 ").Append(segment.TimestampHint).Append("）");
            sb.AppendLine("。");
            if (segment.Total > 1)
                sb.AppendLine("注意：这不是完整的一节课，只是其中一个片段；相邻片段可能有少量重叠内容，重叠部分按第 1 条原样记录即可。");
            sb.AppendLine();
        }

        sb.AppendLine("【课堂录音转写】");
        sb.AppendLine(segment.Transcript.Length > 0 ? segment.Transcript : "（本段无转写内容）");
        sb.AppendLine();
        sb.AppendLine("【课件/板书截图 OCR】");
        sb.AppendLine(segment.OcrText.Length > 0 ? segment.OcrText : "（本段无截图内容）");
        sb.AppendLine();
        if (itemsSchema)
        {
            sb.AppendLine("请整理本段素材，只输出 JSON，形如：");
            sb.AppendLine(SegmentItemsSchema);
            sb.AppendLine("- title：条目短标题（同一知识点在不同片段请用同一标题，便于后端去重）；");
            sb.AppendLine("- body：条目正文（含解题模板、解题技巧、公式定理、定义与限定条件），尽量照抄原文表述；");
            sb.AppendLine("- kind：knowledge（核心知识点）或 pitfall（易错点/考试提示/易混淆概念）；");
            if (multiSource)
            {
                sb.AppendLine("- source：本条来自哪一侧，填「现场」或「课件」；两侧独立转写、没有逐句对齐关系；");
            }
            else
            {
                sb.AppendLine("- source：无分来源时留空字符串；");
            }
            sb.AppendLine("- 不要输出例题与解析、师生问答；不要输出 JSON 之外的任何文字。");
            return sb.ToString();
        }

        sb.AppendLine("请整理本段素材，只输出 JSON，字段为 coreKnowledge 与 pitfalls：");
        sb.AppendLine("- coreKnowledge：本段的核心知识点（含解题模板、解题技巧、公式定理、定义与限定条件），可用 Markdown 标题与列表分层，每条尽量照抄原文表述；");
        if (multiSource)
        {
            sb.AppendLine("- 分辨来源：转写按【麦克风（教室现场）】与【系统声音（课件/网课播放）】分区给出，" +
                          "两侧各自独立转写、没有逐句对齐关系；涉及来源的表述必须用「现场」「课件」这类来源词标明；");
        }
        sb.AppendLine("- pitfalls：本段的易错点、易混淆概念与考试提示，没有则留空字符串；");
        sb.AppendLine("- 不要输出例题与解析、师生问答；不要输出 JSON 之外的任何文字。");
        return sb.ToString();
    }

    /// <summary>按素材是否为分轨录制选取合并阶段的 system prompt。</summary>
    internal static string MergeSystemPromptFor(bool multiSource)
        => multiSource ? MergeSystemPrompt + "\n" + MultiSourceMergeRule : MergeSystemPrompt;

    /// <summary>合并阶段的 user prompt：分段结果 + 原素材摘录（用于校对补齐）。</summary>
    /// <param name="multiSource">
    /// 素材是否来自分轨录制（麦克风 + 系统声音）。为 true 时用带"保留来源标记"要求的
    /// system prompt，避免合并阶段把「现场」「课件」标记洗掉。
    /// </param>
    internal static string BuildMergePrompt(string course, IReadOnlyList<string> segmentJsons,
        string? transcriptExcerpt, string? ocrExcerpt, bool multiSource = false)
    {
        var sb = new StringBuilder();
        sb.Append("课程：").Append(course).AppendLine();
        sb.AppendLine();
        sb.Append("共 ").Append(segmentJsons.Count).AppendLine(" 个分段的整理结果：");
        for (int i = 0; i < segmentJsons.Count; i++)
        {
            sb.AppendLine();
            sb.Append("── 分段 ").Append(i + 1).AppendLine(" ──");
            sb.AppendLine(segmentJsons[i]);
        }

        if (!string.IsNullOrWhiteSpace(transcriptExcerpt) || !string.IsNullOrWhiteSpace(ocrExcerpt))
        {
            sb.AppendLine();
            sb.AppendLine("【原始素材摘录（仅用于校对补齐，不要逐句照搬）】");
            if (!string.IsNullOrWhiteSpace(transcriptExcerpt))
            {
                sb.AppendLine("转写：");
                sb.AppendLine(transcriptExcerpt);
            }
            if (!string.IsNullOrWhiteSpace(ocrExcerpt))
            {
                sb.AppendLine("截图 OCR：");
                sb.AppendLine(ocrExcerpt);
            }
        }

        sb.AppendLine();
        sb.AppendLine("请按 system 中的要求合并成最终笔记（Markdown），第一行是「# " + course + "」标题。不要输出任何说明文字。");
        return sb.ToString();
    }

    /// <summary>
    /// 解析分段返回的条目 JSON（<c>{"items":[…]}</c>），赋上分段序号。
    ///
    /// **容错优先**：条目级容错——单个元素缺字段就跳过该元素，而不是整段判失败；
    /// 若 items 解析不出任何条目，返回空列表，由调用方退回 Markdown 解析路径
    /// （旧 schema / 纯文本回复 / 数组被截断都能兜住，不至于丢掉整段内容）。
    /// </summary>
    internal static List<NoteItem> ParseSegmentItems(string? raw, int segmentIndex)
    {
        var result = new List<NoteItem>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        string text = StripCodeFence(raw!);
        int open = text.IndexOf('{');
        int close = text.LastIndexOf('}');
        if (open < 0 || close <= open) return result;

        SegmentItemsJson? parsed = null;
        try
        {
            parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<SegmentItemsJson>(text[open..(close + 1)]);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            // 数组被截断 / 结构损坏：退回 Markdown 解析路径
            return result;
        }
        if (parsed?.Items == null) return result;

        foreach (var rawItem in parsed.Items)
        {
            if (rawItem == null) continue;
            string title = (rawItem.Title ?? "").Trim();
            string body = (rawItem.Body ?? "").Trim();
            if (title.Length == 0 && body.Length == 0) continue;

            result.Add(new NoteItem(
                Title: title,
                Body: body,
                Kind: string.IsNullOrWhiteSpace(rawItem.Kind) ? "knowledge" : rawItem.Kind.Trim(),
                Source: (rawItem.Source ?? "").Trim(),
                SegmentIndex: segmentIndex));
        }
        return result;
    }

    /// <summary>
    /// 解析分段返回的 JSON。模型偶尔会用 Markdown 代码块包裹，或前后带解释文字，
    /// 这里做容错：截取首个 { 到末个 } 再反序列化；失败时退回"整段文本作为核心知识点"。
    /// </summary>
    internal static (string CoreKnowledge, string Pitfalls) ParseSegmentJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");

        string text = raw.Trim();
        // 去掉 ```json ... ``` 包裹
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
            text = text.Trim();
        }

        int open = text.IndexOf('{');
        int close = text.LastIndexOf('}');
        if (open >= 0 && close > open)
        {
            string json = text[open..(close + 1)];
            SegmentJson? parsed = null;
            try
            {
                parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<SegmentJson>(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // 解析失败：落到下面的兜底
            }

            if (parsed != null)
            {
                // 只要 JSON 解析成功就采信它（"{}" 表示这一段的整理结果为空）。
                // 不能再回退成"整段文本当核心知识点"，否则会把 "{}" 这种原始输出写进笔记。
                return ((parsed.CoreKnowledge ?? "").Trim(), (parsed.Pitfalls ?? "").Trim());
            }
        }

        // 兜底：模型完全没给 JSON（纯文本回复），把它当核心知识点，
        // 宁可原文进笔记，也不要丢掉这一段的全部内容
        return (text, "");
    }

    /// <summary>解析合并结果，取 Markdown 正文（剥掉模型可能加的代码块包裹）。</summary>
    internal static string CleanMarkdown(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
            text = text.Trim();
        }
        return text;
    }

    /// <summary>把分段结果机械拼成完整笔记：合并阶段失败时的兜底，保证保真内容不丢。</summary>
    internal static string AssembleNote(string course, IReadOnlyList<(string Core, string Pitfalls)> parts)
    {
        var core = new StringBuilder();
        var pitfalls = new StringBuilder();
        foreach (var (c, p) in parts)
        {
            if (!string.IsNullOrWhiteSpace(c)) core.AppendLine(c.Trim());
            if (!string.IsNullOrWhiteSpace(p)) pitfalls.AppendLine(p.Trim());
        }

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(course);
        sb.AppendLine();
        sb.AppendLine("## 核心知识点");
        sb.AppendLine();
        sb.AppendLine(core.Length > 0 ? core.ToString().Trim() : "（本轮未整理出核心知识点）");
        if (pitfalls.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 重点及易错点");
            sb.AppendLine();
            sb.AppendLine(pitfalls.ToString().Trim());
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// "末段超限"的显式提示。措辞必须与事实一致：这些内容**没有被丢弃**——它们全部并入了
    /// 最后一段，风险是那一段的输入量超出模型上限、可能被模型截断。
    /// （旧文案写的是"未能纳入笔记"，与实现相反，会让用户以为内容已经丢了。）
    /// 同时不再建议"调大单段上限"：那是个没有界面入口的常量，等于让用户去拧一个不存在的旋钮。
    /// </summary>
    internal static string OversizedNotice(int oversizedChars) =>
        $"> ⚠️ 本次素材超出分段上限，约 {oversizedChars} 字的转写被并入最后一段、未再切分" +
        "（该段输入可能超出模型上限而被截断）。建议把课程录音切成多次会话记录后重新生成。";

    /// <summary>
    /// 有分段整段失败时的显式提示：让"笔记里少了一段"这件事出现在笔记正文里，
    /// 而不是只留在进度日志里。
    /// </summary>
    internal static string FailureNotice(int failedSegments, int totalSegments) =>
        $"> ⚠️ 共 {totalSegments} 段中有 {failedSegments} 段整理失败，这部分内容未能进入笔记。";

    private sealed class SegmentJson
    {
        [Newtonsoft.Json.JsonProperty("coreKnowledge")]
        public string? CoreKnowledge { get; set; }

        [Newtonsoft.Json.JsonProperty("pitfalls")]
        public string? Pitfalls { get; set; }
    }

    /// <summary>分段条目 schema 的响应体。</summary>
    private sealed class SegmentItemsJson
    {
        [Newtonsoft.Json.JsonProperty("items")]
        public List<SegmentItemJson?>? Items { get; set; }
    }

    private sealed class SegmentItemJson
    {
        [Newtonsoft.Json.JsonProperty("title")]
        public string? Title { get; set; }

        [Newtonsoft.Json.JsonProperty("body")]
        public string? Body { get; set; }

        [Newtonsoft.Json.JsonProperty("kind")]
        public string? Kind { get; set; }

        [Newtonsoft.Json.JsonProperty("source")]
        public string? Source { get; set; }
    }
}
