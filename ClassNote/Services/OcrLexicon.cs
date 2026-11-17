using System;
using System.Collections.Generic;
using System.Text;

namespace ClassNote.Services;

/// <summary>
/// 转写词典：把**课堂转写**当成"同一节课的高置信词汇表"，用来校正课件截图 OCR。
///
/// 为什么需要它（这是本类存在的全部理由）：
/// 截图 OCR 的错误不是"少一个字"，而是**把内容粉碎**——化学课的板书实测出现了
/// `Na2C03`（碳酸钠）、`古亻本`（碳酸钠的"固"？）、`氵谷氵夜`（溶液）这类结果。
/// 面对这种输入，模型只有两条烂路：照抄乱码（现在提示词要求的"逐字保真"就是这么干的），
/// 或者猜一个"看起来合理"的符号（历史上把 `f(n-1)+1` 猜成 `f(n-1)` 的事故）。
///
/// 但同一节课的**转写里有正确答案**：老师嘴里说的是"碳酸钠""纯碱""溶液"，
/// 语音转写对术语与化学式的正确率远高于截图 OCR。于是这里把转写抽成一份受约束的词表，
/// 让"OCR 候选词 → 词典词"变成一次**有据可依的替换**，而不是猜。
///
/// ⚠️ 纪律（与 <see cref="OcrTextNormalizer"/> 的"不猜符号"口径一致，必须靠单测锁住）：
/// · 只有当候选词与词典词**唯一匹配**时才替换；有歧义（多个候选）一律不动；
/// · **没有匹配就什么都不做**，绝不根据"看起来像"生成词典里没有的词；
/// · 词典只收 ① 中文理科术语（≥2 字、转写中出现 ≥2 次）② 类化学式/缩写的拉丁 token
///   （如 Na2CO3、LCS、DP）。单个汉字、纯数字、超高频虚词不进词典。
/// </summary>
public sealed class OcrLexicon
{
    /// <summary>空词典（未提供转写时的行为等于"不启用词典校正"）。</summary>
    public static readonly OcrLexicon Empty = new(new List<string>());

    /// <summary>中文术语的最小字数：单字不进词典（它可能只是被拆开的一个偏旁）。</summary>
    private const int MinCjkTermLength = 2;

    /// <summary>中文术语的最大字数：够覆盖「碳酸氢钠」「过氧化钠」这类词，又不会把整句收进来。</summary>
    private const int MaxCjkTermLength = 6;

    /// <summary>术语在转写中至少出现这么多次才值得当"可信词"。</summary>
    private const int MinTermOccurrences = 2;

    /// <summary>词典条目数上限：纯属护栏，防止异常转写把词典撑到影响性能。</summary>
    private const int MaxTerms = 4000;

    /// <summary>拉丁 token 的形态：字母开头，可带数字与大小写（Na2CO3 / LCS / DP / pH）。</summary>
    private static readonly System.Text.RegularExpressions.Regex LatinTermRegex =
        new(@"^[A-Za-z][A-Za-z0-9]{1,15}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>拉丁 token 长度上限（排除长英文单词：它们是转写噪声，不是术语）。</summary>
    private const int MaxLatinTermLength = 12;

    /// <summary>常见化学式/试剂：**只用于消歧**（候选词归一后命中它时才允许改写），
    /// 绝不用它们去无中生有地"补全"板书内容。</summary>
    private static readonly string[] BuiltInChemistryTerms =
    {
        "Na2O2", "Na2O", "Na2CO3", "NaHCO3", "NaOH", "NaCl", "Na2SO4", "NaNO3",
        "CaH2", "Ca(OH)2", "CaCO3", "CaO", "CaCl2",
        "CO2", "CO", "H2O", "H2O2", "H2", "O2", "O3",
        "MnO2", "HCl", "H2SO4", "HNO3", "NH3", "NH4Cl",
        "KMnO4", "KCl", "KOH", "K2CO3", "KHCO3",
        "Fe2O3", "Fe3O4", "FeO", "Al2O3", "Al(OH)3",
        "SiO2", "CuO", "CuSO4", "MgO", "MgCl2", "ZnSO4", "BaCl2", "BaSO4",
        "SO2", "SO3", "P2O5", "AgNO3", "NaAlO2",
    };

    /// <summary>词典条目（保留首次出现顺序，行为因此可复现）。</summary>
    private readonly List<string> _terms;

    /// <summary>归一化键 → 条目（用于 O(1) 精确查找）。</summary>
    private readonly Dictionary<string, string> _byKey;

    /// <summary>
    /// 词典里出现过的**全部字符**（含归一化形式）。
    ///
    /// 用途是"某个字是否属于本课词汇"这个判断（见 <c>OcrTextNormalizer.ContainsUnknownChar</c>）：
    /// 它决定要不要对一段候选文本尝试编辑距离修复。逐条扫描词典是 O(条目数 × 词长)，
    /// 而它在一行里会被调用很多次，所以预先摊平成集合。
    /// </summary>
    private readonly HashSet<char> _chars = new();

    public int Count => _terms.Count;

    private OcrLexicon(List<string> terms)
    {
        _terms = terms;
        _byKey = new Dictionary<string, string>(terms.Count, StringComparer.Ordinal);
        foreach (var term in terms)
        {
            _byKey[NormalizeKey(term)] = term;
            foreach (char c in term)
                _chars.Add(c);
        }
    }

    /// <summary>该字符是否出现在词典的任何一个词里。</summary>
    public bool ContainsChar(char c) => _chars.Contains(c);

    /// <summary>词典全部条目（只读，供诊断与测试核对）。</summary>
    public IReadOnlyList<string> Terms => _terms;

    /// <summary>
    /// 从课堂转写构建词典。传 null / 空白得到 <see cref="Empty"/>（词典校正整体关闭）。
    /// </summary>
    /// <param name="transcript">
    /// 该会话的转写文本（可含 <c>【麦克风（教室现场）】</c> 这类分区标注行——它们不参与统计）。
    /// </param>
    /// <param name="extraTerms">
    /// 额外术语（例如用户词表）。与内置化学式合并，同样只用于消歧。
    /// </param>
    public static OcrLexicon FromTranscript(string? transcript, IEnumerable<string>? extraTerms = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var boundaryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<string>();

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            foreach (var rawLine in transcript.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine.Trim();
                // 分区标注行是管道自己加的标记，不是课堂内容
                if (line.StartsWith("【", StringComparison.Ordinal) && line.EndsWith("】", StringComparison.Ordinal))
                    continue;
                if (line.Length == 0)
                    continue;

                var lineTerms = new HashSet<string>(StringComparer.Ordinal);
                var boundaryTerms = new HashSet<string>(StringComparer.Ordinal);
                CountLineTerms(line,
                    term =>
                    {
                        if (counts.TryGetValue(term, out int n)) counts[term] = n + 1;
                        else { counts[term] = 1; ordered.Add(term); }
                        lineTerms.Add(term);
                    },
                    term => boundaryTerms.Add(term));

                // 边界出现次数：该 n-gram 有多少次是**落在自然边界上**的（段首/段尾）。
                // 这是区分"真术语"与"跨词碎片"最有效的信号：
                // 「碳酸钠」在「碳酸钠溶液显碱性」里出现在句首，而碎片「酸钠溶」永远不在边界上。
                foreach (var term in boundaryTerms)
                    boundaryCounts[term] = boundaryCounts.TryGetValue(term, out int b) ? b + 1 : 1;
            }
        }

        var accepted = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        // **长的优先**：短语优先于其内部片段进入词典。
        foreach (var term in ordered.OrderByDescending(x => x.Length))
        {
            if (accepted.Count >= MaxTerms)
                break;
            // 采信门槛：拉丁 token 出现一次即可（化学式不会因为只念一遍就不是那个式子）；
            // 中文术语要求"出现过 ≥2 次**或**至少一次落在自然边界上"。
            // 边界那一条很关键：真正的术语通常至少有一次出现在句首或标点之后，
            // 而跨词碎片几乎永远落在句子中间（实测：「碳酸钠溶液」出现在句首，碎片「酸钠溶」没有）。
            bool isLatin = IsLatinTerm(term);
            if (!isLatin && counts[term] < MinTermOccurrences
                && !(boundaryCounts.TryGetValue(term, out int bc) && bc > 0))
                continue;
            if (seenKeys.Add(NormalizeKey(term)))
                accepted.Add(term);
        }

        foreach (var extra in extraTerms ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(extra))
                continue;
            string term = extra.Trim();
            if (!IsCjkTerm(term) && !IsLatinTerm(term))
                continue;
            if (seenKeys.Add(NormalizeKey(term)))
                accepted.Add(term);
        }

        foreach (var builtin in BuiltInChemistryTerms)
        {
            if (seenKeys.Add(NormalizeKey(builtin)))
                accepted.Add(builtin);
        }

        return accepted.Count == 0 ? Empty : new OcrLexicon(accepted);
    }

    /// <summary>
    /// 候选与词典词是否构成一次**可确证的"字被拆开"修复**。
    ///
    /// 判据（三条同时成立）：
    /// 1. 两者只差**一处**编辑；
    /// 2. 差异**必须涉及一个生僻偏旁**——被拆开的字总会留下一两个偏旁（`氵`、`冫`、`钅`…），
    ///    这是"字形损坏"的机械证据，而不是"我觉得这两个字长得像"；
    /// 3. 去掉该偏旁后，其余部分**必须逐字相同**。
    ///
    /// 为什么把判据收到这么紧（实测踩过的坑）：
    /// · `氵溶` → 「溶液」：差异是 `氵`（偏旁），其余相同 → **可以修**；
    /// · `氵溶` → 词典里的跨词碎片「钠溶」：差异是 `氵`→`钠`（`钠` 不是偏旁）→ 拒绝。
    ///   放行它就会把 `氵` 改写成 `钠`，也就是**用一个错字替换另一个错字**；
    /// · `溶池` → 「溶液」（末位形近误识）：差异两侧都不是偏旁 → 拒绝。
    ///   字形相似度修复不在本能力范围内，这类内容交由【OCR不清】如实标记，不猜。
    /// </summary>
    /// <param name="candidate">OCR 候选（已归一化）。</param>
    /// <param name="lexiconKey">词典词的归一化键。</param>
    internal static bool IsBoundaryAnchoredSingleEdit(string candidate, string lexiconKey)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(lexiconKey)) return false;

        // 归一化到同一口径：调用方可能传原始词（单测）也可能传折叠后的键（TryResolve），
        // 两边必须落在同一个表示上，否则同一对词会给出不同结论（实测踩过这个坑）。
        candidate = FoldAscii(NormalizeKey(candidate));
        lexiconKey = FoldAscii(NormalizeKey(lexiconKey));

        if (candidate == lexiconKey) return true;
        if (Math.Abs(candidate.Length - lexiconKey.Length) > 1) return false;

        // ① 等长：候选只比词典词多/少**一个生僻偏旁**，其余部分逐字相同。
        //
        // 这里刻意不做"字符串编辑距离"式的判断，而是做**结构性**判断：把候选分成
        // "偏旁 + 前半 + 后半"，要求去掉偏旁后能逐字对上词典词。这样：
        // · `氵溶液` vs「溶液」：去掉 `氵` → 完全一致 → 可确证修复；
        // · `氵氵` vs「溶液」：去掉任何一个 `氵` 都对不上（`氵`≠`溶液`）→ 拒绝；
        // · `溶池` vs「溶液」：去不掉任何字符后对上（`池` 不是偏旁）→ 拒绝（不做字形猜测）。
        if (candidate.Length == lexiconKey.Length + 1)
            return MinusOneRadicalEquals(candidate, lexiconKey);

        // ② 长度相等：候选少了一个偏旁（`溶液` ← OCR 把偏旁丢了）
        if (candidate.Length == lexiconKey.Length - 1)
            return MinusOneRadicalEquals(lexiconKey, candidate);

        // ③ 等长：候选把词首字读成了偏旁（OCR 丢了部首，只留下 `氵` 顶在原来的位置）。
        //
        // **判据刻意不对称**：只接受"候选是偏旁、词典词是正常汉字"这一个方向。
        // 反方向（候选正常、词典词是偏旁）不接受——`氵溶` vs 碎片「钠溶」正是反方向，
        // 放行它就会把 `氵` 改写成 `钠`（用一个错字换另一个错字）。
        // 而 `氵液` vs「溶液」能被修成「溶液」，是因为证据更强：除首字外**逐字相同**，
        // 且首字确实是偏旁。修复后的文本只会变成词典里真实存在的写法，不会凭空造词。
        if (candidate.Length == lexiconKey.Length)
        {
            if (candidate.Length == 0) return false;
            if (IsRareRadicalChar(candidate[0]) && !IsRareRadicalChar(lexiconKey[0]))
            {
                if (string.Equals(candidate.Substring(1), lexiconKey.Substring(1), StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="longer"/> 去掉**恰好一个生僻偏旁**之后，是否与 <paramref name="shorter"/> 逐字相同。
    /// 偏旁可以在任意位置（前缀、中间、后缀）。
    /// </summary>
    private static bool MinusOneRadicalEquals(string longer, string shorter)
    {
        if (longer.Length != shorter.Length + 1) return false;

        for (int cut = 0; cut < longer.Length; cut++)
        {
            if (!IsRareRadicalChar(longer[cut])) continue;
            string withoutCut = longer.Remove(cut, 1);
            if (string.Equals(withoutCut, shorter, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>该字符是不是"被拆开的汉字"会留下的生僻偏旁（与规范化器共用同一份清单）。</summary>
    private static bool IsRareRadicalChar(char c) => OcrTextNormalizer.IsRareRadicalChar(c);

    /// <summary>词典里是否存在该（精确）条目。</summary>
    public bool Contains(string term) => !string.IsNullOrEmpty(term) && _byKey.ContainsKey(NormalizeKey(term));

    /// <summary>
    /// 统计一行里可能的术语：CJK 段按 n-gram 统计、拉丁串整体统计。
    ///
    /// 中文没有词边界，所以**必须按 n-gram 统计**而不是"逐字 token"（最初的实现就是逐字切分，
    /// 结果词典里一个中文术语都没有）。但 n-gram 同样会切出「酸钠溶」这类**跨词碎片**，
    /// 因此只把"至少一端落在自然边界上"的 n-gram 计入——见 <see cref="CountCjkTermsAtBoundaries"/>。
    ///
    /// 拉丁 token 则按连续字母数字串整体统计。
    /// </summary>
    /// <param name="line">转写的一行。</param>
    /// <param name="count">计数回调（全部 n-gram）。</param>
    /// <param name="countBoundary">计数回调（仅落在自然边界上的 n-gram）。</param>
    private static void CountLineTerms(string line, Action<string> count, Action<string> countBoundary)
    {
        var latin = new StringBuilder();
        var cjk = new StringBuilder();

        void FlushLatin()
        {
            if (latin.Length == 0) return;
            string token = latin.ToString();
            latin.Clear();
            if (IsLatinTerm(token))
            {
                count(token);
                countBoundary(token);   // 拉丁串本身就是"成词"的，天然算边界证据
            }
        }

        void FlushCjk()
        {
            if (cjk.Length == 0) return;
            string run = cjk.ToString();
            cjk.Clear();
            CountCjkNgrams(run, count, countBoundary);
        }

        foreach (char c in line)
        {
            if (IsCjk(c))
            {
                FlushLatin();
                cjk.Append(c);
                continue;
            }
            if (IsAsciiAlnum(c))
            {
                FlushCjk();
                latin.Append(c);
                continue;
            }
            FlushLatin();
            FlushCjk();
        }
        FlushLatin();
        FlushCjk();
    }

    /// <summary>
    /// 在一个 CJK 段里抽取全部 2..6 字 n-gram，并标记哪些**落在自然边界上**
    /// （段首、段尾；段内其余位置都紧邻汉字，不算边界）。
    ///
    /// 中文没有词边界，所以统计必须按 n-gram 做；但 n-gram 会切出「酸钠溶」这类跨词碎片，
    /// 边界标记就是用来区分"真术语"与"碎片"的信号（见 <see cref="ShouldDropAsSubstring"/>）。
    /// </summary>
    /// <param name="run">连续汉字段。</param>
    /// <param name="count">全部 n-gram 的计数回调。</param>
    /// <param name="countBoundary">落在边界上的 n-gram 的计数回调。</param>
    private static void CountCjkNgrams(string run, Action<string> count, Action<string> countBoundary)
    {
        int maxLen = Math.Min(MaxCjkTermLength, run.Length);
        for (int start = 0; start < run.Length; start++)
        {
            int limit = Math.Min(maxLen, run.Length - start);
            for (int len = MinCjkTermLength; len <= limit; len++)
            {
                string term = run.Substring(start, len);
                count(term);
                if (start == 0 || start + len == run.Length)
                    countBoundary(term);
            }
        }
    }

    /// <summary>
    /// 查一个 OCR 候选词对应的词典词。
    ///
    /// 分两级，都是"唯一匹配才返回"：
    /// 1. <paramref name="forgiving"/> 为 true 时，按键的**混淆等价类**匹配
    ///    （<c>0↔o</c>、<c>1↔l/i</c>、<c>5↔s</c>、<c>2↔z</c>）——这是 `Na202` 被认成
    ///    `Na2O2` 之类"数字/字母互认"错误的兜底；
    /// 2. 允许 1 个字符的编辑距离（仅在候选词**确定可疑**时由调用方开启）。
    ///
    /// 任一情形下若有多个词典词并列匹配，返回 false——有歧义就不改，宁可留疑点。
    /// </summary>
    /// <param name="candidate">OCR 里的候选词（未归一）。</param>
    /// <param name="forgiving">是否启用混淆等价类（数字/字母互认）匹配。</param>
    /// <param name="allowOneEdit">是否允许 1 个字符的编辑距离。</param>
    /// <param name="canonical">命中的词典词。</param>
    public bool TryResolve(string candidate, bool forgiving, bool allowOneEdit, out string canonical)
    {
        canonical = "";

        // ① 精确（归一化后）
        if (_byKey.TryGetValue(NormalizeKey(candidate), out var exact))
        {
            canonical = exact;
            return true;
        }

        // ② 混淆等价类：把候选词折叠进同一等价类再比
        if (forgiving)
        {
            string folded = FoldConfusions(NormalizeKey(candidate));
            string? hit = null;
            int hits = 0;
            foreach (var (key, term) in _byKey)
            {
                if (!string.Equals(FoldConfusions(key), folded, StringComparison.Ordinal))
                    continue;
                hits++;
                hit = term;
                if (hits > 1)
                    return false; // 有歧义：不改
            }
            if (hits == 1)
            {
                canonical = hit!;
                return true;
            }
        }

        // ③ 1 个字符的编辑距离（只对可疑候选启用，代价 O(词典条数 × 词长²)）
        if (allowOneEdit)
        {
            const int maxLenDelta = 1;
            string target = NormalizeKey(candidate);
            string? hit = null;
            int hits = 0;
            foreach (var term in _terms)
            {
                string key = NormalizeKey(term);
                if (Math.Abs(key.Length - target.Length) > maxLenDelta)
                    continue;
                // 边界锚定：只接受"首尾吻合的一次编辑"。这一条挡掉的是词典里的跨词碎片
                // （`氵溶` 命中碎片「钠溶」——首字不符），同时放行真正的字形修复
                // （`氵容` → 「溶液」）。少了它就会"用另一个错字替换原来的错字"。
                if (!IsBoundaryAnchoredSingleEdit(target, key))
                    continue;
                if (EditDistanceWithinOne(key, target))
                {
                    hits++;
                    hit = term;
                    if (hits > 1)
                        return false;
                }
            }
            if (hits == 1)
            {
                canonical = hit!;
                return true;
            }
        }

        return false;
    }

    // ── 判定与归一 ──────────────────────────────────────

    /// <summary>中文术语：≥2 个汉字，且不含空白/标点/拉丁字符。</summary>
    internal static bool IsCjkTerm(string token)
    {
        if (token.Length < MinCjkTermLength) return false;
        foreach (char c in token)
        {
            if (!IsCjk(c)) return false;
        }
        return true;
    }

    /// <summary>拉丁术语：字母开头、长度受限、只含字母数字。</summary>
    internal static bool IsLatinTerm(string token)
    {
        if (token.Length < 2 || token.Length > MaxLatinTermLength) return false;
        return LatinTermRegex.IsMatch(token);
    }

    /// <summary>
    /// 词典键归一：全角转半角、去空白、转小写。**不做混淆折叠**——
    /// 折叠是 <see cref="FoldAscii"/> 的事，两者的差别正是"精确"与"宽容"两级匹配的边界，
    /// 混在一起就没法表达"精确命中优先"。
    /// </summary>
    internal static string NormalizeKey(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            char ch = c;
            if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0);
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// ASCII 折叠：把 OCR 常互认的字符折进同一类，用于"宽容"匹配与**可确证修复**的判定。
    /// 数字与字母互认是解析化学式最常见的失败模式（0↔O、1↔l/I、5↔S、2↔Z）。
    ///
    /// 只动 ASCII：汉字（含生僻偏旁）必须逐字保留——它们是判定"字是否被拆开"的证据。
    /// </summary>
    internal static string FoldAscii(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            sb.Append(c switch
            {
                '0' or 'o' => 'o',
                '1' or 'l' or 'i' => 'i',
                '5' or 's' => 's',
                '2' or 'z' => 'z',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// 混淆折叠：<see cref="FoldAscii"/> 的别名（历史命名，保留以兼容既有调用点与测试）。
    /// </summary>
    internal static string FoldConfusions(string key) => FoldAscii(key);

    /// <summary>编辑距离是否 ≤1（只要布尔结果，不做完整矩阵）。</summary>
    internal static bool EditDistanceWithinOne(string a, string b)
    {
        if (a == b) return true;
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > 1) return false;

        if (la == lb)
        {
            int diff = 0;
            for (int i = 0; i < la; i++)
            {
                if (a[i] == b[i]) continue;
                if (++diff > 1) return false;
            }
            return true; // 0 或 1 处不同（0 已在上面返回，这里即 1 处替换）
        }

        // 长度差 1：短串必须是长串删一个字符的结果
        string shorter = la < lb ? a : b;
        string longer = la < lb ? b : a;
        int i2 = 0, j2 = 0, skips = 0;
        while (i2 < shorter.Length && j2 < longer.Length)
        {
            if (shorter[i2] == longer[j2]) { i2++; j2++; continue; }
            if (++skips > 1) return false;
            j2++;
        }
        return true;
    }

    /// <summary>把文本切成 token：拉丁/数字串各成一体，汉字一字一 token（其余字符丢弃）。</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var buffer = new StringBuilder();
        foreach (char c in text)
        {
            if (IsCjk(c))
            {
                if (buffer.Length > 0) { tokens.Add(buffer.ToString()); buffer.Clear(); }
                tokens.Add(c.ToString());
            }
            else if (IsAsciiAlnum(c))
            {
                buffer.Append(c);
            }
            else
            {
                if (buffer.Length > 0) { tokens.Add(buffer.ToString()); buffer.Clear(); }
            }
        }
        if (buffer.Length > 0) tokens.Add(buffer.ToString());
        return tokens;
    }

    private static bool IsCjk(char c) => c >= '\u4e00' && c <= '\u9fff';

    private static bool IsAsciiAlnum(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
