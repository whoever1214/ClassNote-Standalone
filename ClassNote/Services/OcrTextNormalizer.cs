using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassNote.Services;

/// <summary>
/// 课件截图 OCR 文本的规范化（**纯函数**，因此可被单测逐条锁住）。
///
/// 为什么需要它：Windows OCR 对中文是"一个字一个词"的识别结果，拼行时会在每个字之间插空格，
/// 实测一节课的 OCR 正文里 **45.5% 的字符是空白**；公式里的符号也常被认错。这些噪声有两个后果：
/// ① 白烧近一半的提示词 token；② 模型面对的是"碎片"，要么抄下乱码，要么按自己的理解"修"错
/// （实测样本里就出现过把 `f(n-1)+1` 修成 `f(n-1)` 的事故）。
///
/// 因此这里把**字符级**噪声用确定性规则处理掉，只把"语义级"判断留给模型：
/// 1. 合并逐字拆开的空格：仅汉字↔汉字、汉字↔全角标点、字母数字↔全角标点这几类边界；
/// 2. **绝不动 ASCII 记号之间的空格**——`1 7 3 5 9 4 8`（最长上升子序列的样例输入）一旦被合并成
///    `1735948` 就是彻底的语义事故；
/// 3. 公式行（含 =、min(、f( 等特征）里把全角括号/逗号/运算符转成半角，并去掉括号与运算符两侧的空格；
/// 4. 只做**极保守**的误识替换：`m 主 n` → `min`、公式行里夹在字母数字之间的 `一` → `-`（OCR 把减号认成汉字"一"）。
///    其余可疑字符（`巨`、`丿`、`刂` 之类）一律不动——猜符号正是我们要从模型手里收回来、
///    也不能用正则乱猜的那件事；它们由提示词规则要求模型"确定不了就标注 OCR 不清"。
///
/// v0.8 追加（P0-α，见 docs/方案-化学笔记质量修复.md）：
/// 5. **转写词典校正**：同一节课的转写是高置信词汇表（见 <see cref="OcrLexicon"/>）。化学课实测的
///    `Na2C03`、`古亻本`、`氵谷氵夜` 这类粉碎性错误，只要转写里出现过正确写法就能被**有据地**改回来；
/// 6. **化学式与离子符号**：把 `C02`/`H20` 这类数字字母互认（`0`↔`O`）与元素符号后的下标
///    转成 Unicode 下标（`Na2O2` → `Na₂O₂`），离子电荷转上标（`Ca2+` → `Ca²⁺`）。**只对
///    "确认为化学式"的 token 生效**——`utf8`、`resize2` 这类标识符绝不改（`ut`/`res` 不是元素符号）；
/// 7. **方程式归一**：`===`/`==`/`＝`/`→` 统一成 `→`，`t`/`↑` 统一成 `↑`；
/// 8. **疑点显式标注**：整行做完全部确定性修复后仍含生僻偏旁或形态可疑的字符，
///    在行尾追加 <see cref="UncertainSuffix"/>。**这一条是"不猜"原则的执行机构**——
///    改不了的内容必须能被看见，而不是无声地混进笔记。
/// </summary>
internal static class OcrTextNormalizer
{
    /// <summary>行尾疑点标注：本行存在无法确定性修复的可疑字符。</summary>
    public const string UncertainSuffix = "【OCR不清】";

    /// <summary>每行最多列举几个疑点字符（行尾提示用，太长反而没人看）。</summary>
    private const int MaxListedSuspects = 3;

    /// <summary>公式行的特征：出现等号、常见函数记号、C++ 代码记号或全角运算符。</summary>
    private static readonly Regex FormulaHint = new(
        @"=|min\s*\(|max\s*\(|\bf\s*[\(\[]|\bd\s*\[|\bdp\s*\[|for\s*\(|\bint\s+|\bcout\b|\bcin\b|\breturn\b|[＋－×÷＝＜＞]",
        RegexOptions.Compiled);

    /// <summary>OCR 把转置/括号认成「巨」之外最常见的误识：m 主 n = min。允许中间有空格。</summary>
    private static readonly Regex MinMisread = new(@"m\s*主\s*n", RegexOptions.Compiled);

    /// <summary>被空格拆散的 min：`m i n (`。</summary>
    private static readonly Regex MinSpaced = new(@"m\s+i\s+n\s*(?=\()", RegexOptions.Compiled);

    /// <summary>公式行里夹在字母数字之间的「一」＝减号（OCR 常把 - 认成汉字一）。</summary>
    private static readonly Regex MisreadMinus = new(@"(?<=[A-Za-z0-9])\s*一\s*(?=[A-Za-z0-9])", RegexOptions.Compiled);

    /// <summary>公式行里需要去掉两侧空格的符号（括号、逗号、运算符、平衡箭头）。</summary>
    private static readonly Regex SpaceAroundSymbol = new(@"\s*([()\[\]{},;=+\-*/<>⇌])\s*", RegexOptions.Compiled);

    /// <summary>连续等号/长箭头：`==`、`===`、`====` 都是"等号"的手写/识别变体，统一成反应箭头。</summary>
    private static readonly Regex EquationArrow = new(@"[=＝]{2,}|[→⟶➔➜]|-->|—>|-＞", RegexOptions.Compiled);

    /// <summary>
    /// 化学式候选 token：**必须以元素符号的大写字母开头**，后接字母数字。
    /// 刻意不用 `<c>[A-Za-z]</c>` 开头——那会把 `utf8`、`resize2` 这类标识符也扫进来
    /// （它们靠 <see cref="IsChemicalFormula"/> 也能被拦下，但让正则一开始就不匹配更省事、更不易错）。
    /// </summary>
    private static readonly Regex FormulaToken = new(
        @"[A-Z][A-Za-z0-9]*(?:\([A-Za-z0-9]+\)[A-Za-z0-9]*)*", RegexOptions.Compiled);

    /// <summary>字母数字串 token（用于数字/字母互认修复）。</summary>
    private static readonly Regex AlnumToken = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// 生僻偏旁/部件：**只出现在被拆开的汉字里，几乎不会出现在正常正文中**。
    ///
    /// 判据是"它不是一个能独立成词的常用汉字"。刻意排除「皿」「一」「口」「日」「十」这类
    /// 既能做偏旁、又能独立成字的字符——把它们算进来会让「溶池」与「溶液」因为 `液` 而被
    /// 误判成"字被拆开"（实测踩过）。
    /// </summary>
    private const string RareRadicalChars = "亻氵冫钅纟讠刂阝卩冂匚凵屮尢廴廾弋彐彡攴攵殳毋爿爫爻犭牜礻衤疒癶罒耒聿虍豸辶釒钅闩巛丿亠宀冖";

    /// <summary>
    /// 元素符号表（用于判断"这是不是化学式"）。判据刻意**要求出现真实的元素符号**，
    /// 否则 `utf8`、`resize2`、`version2` 这类标识符会被误改——那是比漏改严重得多的事故。
    /// </summary>
    private static readonly HashSet<string> ElementSymbols = new(StringComparer.Ordinal)
    {
        "H", "He", "Li", "Be", "B", "C", "N", "O", "F", "Ne",
        "Na", "Mg", "Al", "Si", "P", "S", "Cl", "Ar", "K", "Ca",
        "Sc", "Ti", "V", "Cr", "Mn", "Fe", "Co", "Ni", "Cu", "Zn",
        "Ga", "Ge", "As", "Se", "Br", "Kr", "Rb", "Sr", "Y", "Zr",
        "Nb", "Mo", "Tc", "Ru", "Rh", "Pd", "Ag", "Cd", "In", "Sn",
        "Sb", "Te", "I", "Xe", "Cs", "Ba", "La", "Ce", "Pr", "Nd",
        "Pm", "Sm", "Eu", "Gd", "Tb", "Dy", "Ho", "Er", "Tm", "Yb",
        "Lu", "Hf", "Ta", "W", "Re", "Os", "Ir", "Pt", "Au", "Hg",
        "Tl", "Pb", "Bi", "Po", "At", "Rn", "Fr", "Ra", "Ac", "Th",
        "Pa", "U", "Np", "Pu", "Am", "Cm", "Bk", "Cf", "Es", "Fm",
    };

    /// <summary>Unicode 下标数字。</summary>
    private static readonly char[] SubscriptDigits = { '₀', '₁', '₂', '₃', '₄', '₅', '₆', '₇', '₈', '₉' };

    /// <summary>Unicode 上标数字（离子电荷用；上标 1 由 `¹` 表达）。</summary>
    private static readonly char[] SuperscriptDigits = { '⁰', '¹', '²', '³', '⁴', '⁵', '⁶', '⁷', '⁸', '⁹' };

    /// <summary>行尾提示里最多展示的疑点数量（超出只给总数）。</summary>
    private const string SuspectListSeparator = "、";

    /// <summary>
    /// 规范化整段 OCR 文本（保持行结构；只改字符级噪声，不增删内容）。
    ///
    /// 不传词典时行为与 v0.7 完全一致（词典校正整体关闭）。
    /// </summary>
    public static string Normalize(string? text) => Normalize(text, null);

    /// <summary>
    /// 规范化整段 OCR 文本，并用同一节课的转写词典校正可确证的误识。
    /// </summary>
    /// <param name="text">原始 OCR 文本。</param>
    /// <param name="lexicon">转写词典；null 表示不启用词典校正。</param>
    public static string Normalize(string? text, OcrLexicon? lexicon)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var normalizedNewlines = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedNewlines.Split('\n');
        var sb = new StringBuilder(text.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            // 疑点证据必须在**修复之前**采集：修复成功会把偏旁消掉，
            // 于是"这一行 OCR 出过问题"这个事实也会一起消失，用户就再也看不到它了。
            bool suspiciousSource = ContainsRareRadical(lines[i]);

            string line = NormalizeLine(lines[i]);

            // 词典校正后可能产生新的可合并项（例如被拆开的字符拼回了术语），再走一次行内规范化
            if (lexicon != null)
            {
                line = RepairByLexicon(line, lexicon);
                line = CollapseSpaces(line);
            }

            // 方程式折行粘连：OCR 常把一条方程式拆到两三行，行内规则看不到完整式子。
            // 判据保守（上一行以反应记号结尾 + 下一行以元素/数字开头），普通换行不会被误合并。
            string previous = CurrentOutputLine(sb);
            if (TryMergeEquationContinuation(line, previous, out string merged))
            {
                ReplaceCurrentOutputLine(sb, AnnotateUncertain(NormalizeLine(merged), suspiciousSource: true));
                continue;
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(AnnotateUncertain(line, suspiciousSource));
        }

        return sb.ToString().Trim();
    }

    /// <summary>取已输出内容的最后一行（没有内容时返回空串）。</summary>
    private static string CurrentOutputLine(StringBuilder sb)
    {
        if (sb.Length == 0) return "";
        int nl = sb.ToString().LastIndexOf('\n');
        return nl < 0 ? sb.ToString() : sb.ToString()[(nl + 1)..];
    }

    /// <summary>用新的内容替换已输出内容的最后一行（用于方程式折行合并）。</summary>
    private static void ReplaceCurrentOutputLine(StringBuilder sb, string replacement)
    {
        if (sb.Length == 0) return;
        string all = sb.ToString();
        int nl = all.LastIndexOf('\n');
        if (nl < 0) { sb.Clear(); sb.Append(replacement); return; }
        sb.Clear();
        sb.Append(all[..(nl + 1)]).Append(replacement);
    }

    /// <summary>规范化一行（供单测直接覆盖）。</summary>
    internal static string NormalizeLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";

        string merged = MergeCharacterSpaces(line);
        if (LooksLikeFormula(merged))
            merged = NormalizeFormulaLine(merged);

        // 顺序有讲究：先把 `===`/`t` 这类**方程式记号**理顺，再处理化学式 token。
        // 反过来的话，`...O2t` 里的 `O2t` 会被当成一个候选 token、因 `t` 不是元素而整体放弃，
        // 结果 `O2` 的下标就丢了（实测 bug）。
        merged = StandardizeEquation(merged);
        merged = RepairChemicalTokens(merged);
        merged = RepairIonSymbols(merged);

        return CollapseSpaces(merged).Trim();
    }

    /// <summary>
    /// 这一行是不是公式/代码（决定要不要做全角转半角与符号紧排）。
    /// 化学方程式（含 `→`、`H2O` 这类形态）也算公式行。
    /// </summary>
    internal static bool LooksLikeFormula(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (FormulaHint.IsMatch(line)) return true;
        return line.Contains('→') || EquationArrow.IsMatch(line);
    }

    /// <summary>
    /// 合并"逐字拆开"的空格：只有边界两侧都属于可合并类型时才吃掉空格。
    /// 刻意不合并 ASCII↔ASCII（保护 `1 7 3 5 9 4 8`、`Dynamic Programming`）。
    /// </summary>
    internal static string MergeCharacterSpaces(string line)
    {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c != ' ' && c != '\u3000')
            {
                sb.Append(c);
                i++;
                continue;
            }

            int next = i;
            while (next < line.Length && (line[next] == ' ' || line[next] == '\u3000')) next++;

            char? before = sb.Length > 0 ? sb[^1] : null;
            char? after = next < line.Length ? line[next] : null;

            if (before.HasValue && after.HasValue && ShouldJoin(before.Value, after.Value))
            {
                i = next;           // 吃掉这串空格
                continue;
            }

            sb.Append(' ');
            i = next;
        }
        return sb.ToString();
    }

    /// <summary>两个字符之间的空格是否该吃掉。</summary>
    private static bool ShouldJoin(char a, char b)
    {
        // 汉字 ↔ 汉字（"动 态 规 划" → "动态规划"）
        if (IsCjk(a) && IsCjk(b)) return true;
        // 汉字 ↔ 全角标点（"规 划 （ 英 文" → "规划（英文"）
        if (IsCjk(a) && IsFullwidthPunct(b)) return true;
        if (IsFullwidthPunct(a) && IsCjk(b)) return true;
        // 全角标点 ↔ 全角标点
        if (IsFullwidthPunct(a) && IsFullwidthPunct(b)) return true;
        // 字母数字 ↔ 全角标点（"f （ n ）" → "f（n）"）
        if (IsAsciiAlnum(a) && IsFullwidthPunct(b)) return true;
        if (IsFullwidthPunct(a) && IsAsciiAlnum(b)) return true;
        return false;
    }

    /// <summary>公式行：全角符号转半角 + 去括号/运算符两侧空格 + 极保守的误识替换。</summary>
    internal static string NormalizeFormulaLine(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (char c in line)
            sb.Append(ToHalfwidth(c));
        string text = sb.ToString();

        // 误识替换必须在"去空格"之前：`m 主 n` 的识别依赖字符之间还留着空格
        text = MinMisread.Replace(text, "min");
        text = MinSpaced.Replace(text, "min");
        text = MisreadMinus.Replace(text, "-");

        // 括号与运算符两侧紧排：`f ( n ) = min ( 5 , 3 , 5 ) = 3` → `f(n)=min(5,3,5)=3`
        text = SpaceAroundSymbol.Replace(text, "$1");

        return text;
    }

    /// <summary>
    /// 化学式 token 的确定性修复：数字/字母互认 + 下标 + 离子电荷。
    ///
    /// 只处理**确认为化学式**的 token（见 <see cref="IsChemicalFormula"/>）：
    /// `Na202` → `Na₂O₂`、`Ca2+` → `Ca²⁺`、`H20` → `H₂O`；
    /// 而 `utf8`、`resize2`、`version2`、`1735948` 一律原样保留。
    /// </summary>
    internal static string RepairChemicalTokens(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        return FormulaToken.Replace(line, m =>
        {
            // 前置字母守卫：`pH7` 里的 `H7`、`utf8` 里的 `H` 都是**更大单词的碎片**，不是化学式。
            // 没有这条守卫就会把 `pH7` 改成 `pH₇`——正是"误改比漏改严重"的典型。
            if (m.Index > 0 && IsAsciiLetter(line[m.Index - 1]))
                return m.Value;

            return NormalizeFormulaToken(m.Value);
        });
    }

    /// <summary>
    /// 单个化学式 token 的确定性规范化：**先修形态，再判化学式，最后才转下标**。
    ///
    /// 顺序至关重要（实测踩坑）：`C02` 必须先被修成 `CO2`，再整体转成 `CO₂`。
    /// 如果先转下标，那个被认错的 `0` 会先变成 `₀`，修形态的规则就再也认不出它了
    /// （输出会变成 `Na₂C₀₃` 这种"半个下标"的怪东西）。
    /// 同理，`O` 在本领域既可能是元素符号、又可能是被认错的 `0`，两者只能整体一起处理。
    /// </summary>
    internal static string NormalizeFormulaToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        string repaired = RepairDigitLetterConfusion(token);
        return IsChemicalFormula(repaired) ? ToFormulaUnicode(repaired) : repaired;
    }

    /// <summary>
    /// 数字/字母互认修复（保守）：只在 `0` 夹在字母数字之间、且该 token 含字母时把 `0` 当作 `O`。
    ///
    /// 判据刻意收得很紧：必须**两侧都是字母数字**且**自带至少一个字母**。
    /// `1735948`（纯数字）与 `1000`（纯数字）因此完全不受影响。
    /// 判断"是否化学式"在 <see cref="IsChemicalFormula"/>，这里只负责形态修复。
    /// </summary>
    /// <summary>
    /// 数字/字母互认修复（保守）：把**夹在"字母与数字"之间**的 `0` 改回 `O`、`1` 改回 `l`。
    ///
    /// 判据是**位置**而不是"token 里有多少个数字"（实测踩坑：先写成"恰好一个数字"，
    /// 结果 `Na2O2`、`Na02` 这类最常见的形态全被挡在门外）。真正可靠的信号是：
    /// OCR 把 `O` 认成 `0` 时，它两侧通常一个是字母、一个是数字——
    /// `Na2O2`（a|0|2）、`CO2`（C|0|2）、`Na02`（a|0|2）全都符合。
    ///
    /// 纯数字序列（`1735948` —— 数学课的样例输入）永远不受影响：token 里没有字母时直接返回。
    /// </summary>
    internal static string RepairDigitLetterConfusion(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;

        bool hasLetter = false, hasDigit = false;
        foreach (char c in token)
        {
            if (IsAsciiLetter(c)) hasLetter = true;
            else if (c >= '0' && c <= '9') hasDigit = true;
        }
        if (!hasLetter || !hasDigit) return token;

        var sb = new StringBuilder(token.Length);
        for (int i = 1; i < token.Length - 1; i++)
        {
            char c = token[i];
            char prev = token[i - 1];
            char next = token[i + 1];

            // 字母 ↔ 数字 之间：一边是字母、另一边是数字
            bool flankedByLetterAndDigit =
                (IsAsciiLetter(prev) && next >= '0' && next <= '9')
                || (prev >= '0' && prev <= '9' && IsAsciiLetter(next));

            if (flankedByLetterAndDigit && c == '0') { sb.Append('O'); continue; }
            if (flankedByLetterAndDigit && c == '1') { sb.Append('l'); continue; }
            sb.Append(c);
        }

        if (sb.Length < token.Length) sb.Insert(0, token[0]);
        sb.Append(token[^1]);
        return sb.ToString();
    }

    /// <summary>
    /// 这个 token 是不是化学式：必须出现**真实存在的元素符号**，且全部由元素符号与数字构成
    /// （可带一层括号）。判据不放宽——放宽就会开始改标识符。
    /// </summary>
    internal static bool IsChemicalFormula(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 2) return false;
        if (!char.IsUpper(token[0])) return false;

        // 括号内内容单独判断（只支持一层，足够覆盖 Ca(OH)2 这类写法）
        int open = token.IndexOf('(');
        if (open >= 0)
        {
            int close = token.IndexOf(')', open + 1);
            if (close < 0 || token.IndexOf('(', open + 1) >= 0) return false;
            string inner = token[(open + 1)..close];
            string suffix = token[(close + 1)..];
            if (suffix.Length > 0)
            {
                foreach (char c in suffix)
                    if (!char.IsDigit(c)) return false;
            }
            return IsChemicalFormula(inner) && IsChemicalFormula(token[..open]);
        }

        int elements = 0;
        int i = 0;
        while (i < token.Length)
        {
            if (!char.IsUpper(token[i])) return false;
            string symbol = token[i].ToString();
            i++;
            if (i < token.Length && IsPlausibleSecondElementLetter(token[i]))
            {
                // 只有确实构成元素符号的两字母组合才吃第二个字母：
                // `Na`/`Cl` 是元素，`Utf` 不是（`Ut` 不在表里），于是 `utf8` 不会被当成化学式。
                string twoLetter = symbol + token[i];
                if (ElementSymbols.Contains(twoLetter))
                {
                    symbol = twoLetter;
                    i++;
                }
                else if (!IsExactElementSymbol(symbol))
                {
                    // 不是元素、也不是"元素符号 + 下一个元素"的开头 → 这个 token 不是化学式
                    return false;
                }
            }
            if (!ElementSymbols.Contains(symbol)) return false;
            elements++;

            while (i < token.Length && char.IsDigit(token[i])) i++;
        }
        return elements > 0;
    }

    /// <summary>
    /// 这个小写字母**可能**是某个元素符号的第二个字母（提前排除显然不可能的，减少无效查找）。
    /// 用"元素表里出现的次字母"推导而不是硬编码集合，元素表扩充时自动跟随。
    /// </summary>
    private static bool IsPlausibleSecondElementLetter(char c)
    {
        if (c < 'a' || c > 'z') return false;
        foreach (var symbol in ElementSymbols)
        {
            if (symbol.Length == 2 && symbol[1] == c) return true;
        }
        return false;
    }

    private static bool IsExactElementSymbol(string symbol) => ElementSymbols.Contains(symbol);

    /// <summary>
    /// 化学式的 Unicode 化：元素符号后的数字转下标，离子电荷转上标。
    /// `Na2O2` → `Na₂O₂`；`Ca2+` → `Ca²⁺`；`OH-` → `OH⁻`。
    /// </summary>
    internal static string ToFormulaUnicode(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;

        var sb = new StringBuilder(token.Length + 4);
        int i = 0;

        // 需要区分"下标"（元素后的原子个数）与"上标"（末尾电荷）
        string core = token;
        string charge = "";
        int signIndex = token.Length - 1;
        if (signIndex >= 0 && (token[signIndex] == '+' || token[signIndex] == '-'))
        {
            // 从符号往前吃掉连续数字，作为电荷数
            int digitStart = signIndex;
            while (digitStart > 0 && char.IsDigit(token[digitStart - 1])) digitStart--;
            core = token[..digitStart];
            charge = token[digitStart..];
        }

        while (i < core.Length)
        {
            char c = core[i];
            if (char.IsDigit(c))
            {
                sb.Append(SubscriptDigits[c - '0']);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }

        if (charge.Length > 0)
        {
            char sign = charge[^1];
            string digits = charge[..^1];
            foreach (char d in digits)
            {
                if (char.IsDigit(d)) sb.Append(SuperscriptDigits[d - '0']);
            }
            sb.Append(sign == '+' ? '⁺' : '⁻');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 方程式归一：连续等号统一成 `→`，反应符号 `t` 统一成 `↑`，
    /// 并在（仅）化学式 token 存在时把下标/上标补齐。
    ///
    /// 幂等：已是 `→` / `↑` 的输入不会被再次改写。
    /// </summary>
    internal static string StandardizeEquation(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        string text = EquationArrow.Replace(line, "→");

        // 反应符号：紧跟在数字/右括号后的 ASCII `t`（OCR 把 ↑ 认成 t，如 `02t`、`H2t`）
        text = Regex.Replace(text, @"(?<=[0-9\)])\s*t(?![A-Za-z0-9])", "↑");

        return text;
    }

    /// <summary>
    /// 用转写词典校正本行可确证的误识。
    ///
    /// 三条纪律（违反任一条都会让"校正"退化成"猜"）：
    /// 1. **不新增内容**：只替换，不改长度结构、不补全句子；
    /// 2. **唯一匹配才改**：候选词对应多个词典词时放弃（见 <see cref="OcrLexicon.TryResolve"/>）；
    /// 3. **没有匹配就什么都不做**：绝不使用词典之外的"猜想"。
    /// </summary>
    internal static string RepairByLexicon(string line, OcrLexicon lexicon)
    {
        if (string.IsNullOrEmpty(line) || lexicon.Count == 0) return line;

        var sb = new StringBuilder(line.Length);
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                sb.Append(RepairToken(buffer.ToString(), lexicon));
                buffer.Clear();
            }
        }

        foreach (char c in line)
        {
            if (IsAsciiAlnum(c))
            {
                buffer.Append(c);
                continue;
            }
            Flush();
            sb.Append(c);
        }
        Flush();

        string repaired = sb.ToString();
        repaired = RepairSplitCjkTerms(repaired, lexicon);
        return repaired;
    }

    /// <summary>修复单个拉丁/数字 token。</summary>
    private static string RepairToken(string token, OcrLexicon lexicon)
    {
        if (token.Length < 2) return token;

        bool suspicious = ContainsSuspectShape(token);
        if (lexicon.TryResolve(token, forgiving: suspicious || HasDigitOrLetterMix(token), allowOneEdit: suspicious,
                out string canonical))
        {
            // 词典给的是**规范写法**（如 Na2CO3）：补上与普通 token 相同的化学式处理，
            // 否则同一条化学式会因为"走了词典"而少了下标（实测 bug）。
            return NormalizeFormulaToken(canonical);
        }
        return token;
    }

    /// <summary>
    /// 修复"被拆开的汉字术语"：OCR 可能把 `溶液` 认成 `氵谷氵夜` 这样的两字碎片，
    /// 也可能把第一个字拆成"偏旁 + 余下部分"（`氵` + `谷`）。
    ///
    /// 做法是**用词典做最长匹配**：优先整词命中，其次允许把"偏旁 + 后续 1~2 字"当作一个整体
    /// 去词典里找。仍然只做唯一匹配的替换，找不到就不动。
    /// </summary>
    private static string RepairSplitCjkTerms(string line, OcrLexicon lexicon)
    {
        if (line.Length < 2) return line;

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (!IsCjk(line[i]))
            {
                sb.Append(line[i]);
                i++;
                continue;
            }

            int matched = 0;
            string replaced = "";

            // 长的优先；**同一长度上精确命中优先于编辑距离修复**——
            // 否则词典里同时有「溶液」与「溶液池」时，正文里的「溶液」会被"修"成更长的那个词，
            // 那就成了凭空造词（比留噪声严重得多）。修复只在前缀也匹配不上时才允许。
            for (int len = Math.Min(4, line.Length - i); len >= 2; len--)
            {
                string candidate = line.Substring(i, len);
                if (lexicon.Contains(candidate))
                {
                    matched = len;
                    replaced = candidate;   // 精确命中：原样保留
                    break;
                }

                // 修复由 <see cref="OcrLexicon.TryResolve"/> 内部的两条硬约束把关：
                // ① **边界锚定**——只接受首尾吻合的一次编辑（挡掉 `氵溶`→碎片「钠溶」这类
                //    "用另一个错字替换原来的错字"）；② **唯一匹配**——有歧义就不改。
                // 这里刻意**不再加"候选必须碰得到词典"的额外门槛**：实测它会挡掉合法的
                // 同形修复（`溶池` 与「溶液」只差一处，但候选的任何 2 字子串都不在词典里）。
                if (lexicon.TryResolve(candidate, forgiving: true, allowOneEdit: true, out string canonical))
                {
                    matched = len;
                    replaced = canonical;
                    break;
                }
            }

            if (matched > 0)
            {
                sb.Append(replaced);
                i += matched;
                continue;
            }

            // 整词命中与编辑距离修复都失败时：把**当前位置及其后 1 个字**当作一个被打碎的词
            // （例如 `氵` + `溶` 这种"多吐了一个偏旁"），再试一次。
            //
            // 只在**本次修复真的改变了首字**时才接受：若 `joined` 本身就是词典词，
            // 那说明这个位置本来就是正常内容，应当逐字原样输出（否则会吃掉一个字）。
            if (i + 2 <= line.Length)
            {
                string joined = line.Substring(i, 2);
                if (lexicon.TryResolve(joined, forgiving: true, allowOneEdit: true, out string canonical2)
                    && canonical2.Length > 0 && canonical2[0] != joined[0])
                {
                    sb.Append(canonical2);
                    i += 2;
                    continue;
                }
            }

            sb.Append(line[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 给整行追加疑点标注（幂等）。
    ///
    /// 触发条件：做完全部确定性修复后，本行仍含**生僻偏旁**（被拆开的汉字残留）。
    /// 注意不把"无法识别的普通汉字"算作疑点——那不是我们该管的事，
    /// 否则每一行带一个不常见字名的行都会被标红，标注本身就不再有信息量。
    /// </summary>
    internal static string AnnotateUncertain(string line) => AnnotateUncertain(line, suspiciousSource: false);

    /// <summary>
    /// 给整行追加疑点标注（幂等）。
    /// </summary>
    /// <param name="line">已规范化的行。</param>
    /// <param name="suspiciousSource">
    /// **原始行**是否含生僻偏旁。为 true 时无条件标注——这是"修复成功也要留痕"的机制：
    /// 修复会把偏旁消掉，只看结果就看不出这一行曾经坏过（实测踩过：`釒属钠` 被修成 `金属钠`
    /// 之后标记也一起消失了，用户于是永远不知道这里出过问题）。
    /// </param>
    internal static string AnnotateUncertain(string line, bool suspiciousSource)
    {
        if (string.IsNullOrEmpty(line)) return line;
        // 幂等判定必须看标记本身，而不是"行尾是不是标记"：标记后面还跟着疑点明细
        // （`【OCR不清】（氵）`），只看行尾会把同一行标注两遍（实测 bug）。
        if (line.Contains(UncertainSuffix, StringComparison.Ordinal)) return line;
        if (!suspiciousSource && !ContainsRareRadical(line)) return line;

        var suspects = CollectSuspects(line);
        string detail = suspects.Count == 0 ? "" : "（" + string.Join(SuspectListSeparator, suspects) + "）";
        return line + UncertainSuffix + detail;
    }

    /// <summary>本行疑似被拆开的字符（去重、保序、限量）。</summary>
    internal static List<string> CollectSuspects(string line)
    {
        var result = new List<string>();
        var seen = new HashSet<char>();
        foreach (char c in line)
        {
            if (!IsRareRadical(c) || !seen.Add(c)) continue;
            result.Add(c.ToString());
            if (result.Count >= MaxListedSuspects) break;
        }
        return result;
    }

    /// <summary>文本里是否含生僻偏旁（疑点标注的唯一判据）。</summary>
    internal static bool ContainsRareRadical(string text)
    {
        foreach (char c in text)
        {
            if (IsRareRadical(c)) return true;
        }
        return false;
    }

    /// <summary>
    /// 候选文本是否"碰得到词典"：候选自身是词典词，或它的任一子串是词典词（长度 ≥2）。
    ///
    /// 这是编辑距离修复的准入门槛。它的作用是区分两种"像术语"的候选：
    /// · `氵溶`——`溶` 是词「溶液」的一部分，说明这个位置**本来能认出来**，只是被多余的
    ///   `氵` 打断了，值得尝试修复；
    /// · `氯化铁`——它的任何子串都不是本课词典里的词，属于"完全词典外"，一律不动。
    ///
    /// 单字命中不算（否则任何含常见字的候选都会通过，门槛形同虚设）。
    /// </summary>
    internal static bool TouchesLexicon(string candidate, OcrLexicon lexicon)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (lexicon.Contains(candidate)) return true;

        for (int start = 0; start < candidate.Length; start++)
        {
            for (int len = 2; start + len <= candidate.Length; len++)
            {
                if (lexicon.Contains(candidate.Substring(start, len)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>该行是否已被标注疑点。</summary>
    internal static bool IsAnnotatedUncertain(string line)
        => !string.IsNullOrEmpty(line) && line.Contains(UncertainSuffix, StringComparison.Ordinal);

    /// <summary>形态可疑（含数字字母混杂或疑似被拆的偏旁），决定是否允许 1 字符编辑距离匹配。</summary>
    private static bool ContainsSuspectShape(string token)
    {
        foreach (char c in token)
        {
            if (IsRareRadical(c)) return true;
        }
        return false;
    }

    /// <summary>字母与数字混排（化学式/编号的典型形态）。</summary>
    private static bool HasDigitOrLetterMix(string token)
    {
        bool digit = false, letter = false;
        foreach (char c in token)
        {
            if (char.IsDigit(c)) digit = true;
            else if (char.IsLetter(c)) letter = true;
        }
        return digit && letter;
    }

    /// <summary>仅对公式里有意义的全角字符做半角化（正文标点保持中文习惯）。</summary>
    private static char ToHalfwidth(char c) => c switch
    {
        '（' => '(',
        '）' => ')',
        '［' => '[',
        '］' => ']',
        '｛' => '{',
        '｝' => '}',
        '，' => ',',
        '；' => ';',
        '：' => ':',
        '＋' => '+',
        '－' => '-',
        '＝' => '=',
        '＊' => '*',
        '／' => '/',
        '＜' => '<',
        '＞' => '>',
        '～' => '~',
        _ => c,
    };

    /// <summary>
    /// 离子符号的规范化：把"元素/化学式 + 电荷数字 + 正负号"合成上标写法
    /// （`Ca2+` → `Ca²⁺`、`OH-` → `OH⁻`）。
    ///
    /// 同时把全角/装饰性正负号（`＋`、`－`、`−`、`–`、`—`）归一到半角。
    /// **不猜**被认错的数字——那属于 <see cref="AnnotateUncertain"/> 的职责。
    /// </summary>
    internal static string RepairIonSymbols(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        // 先把各种破折号/全角正负号归一到 ASCII 的 + 与 -，再交给正则。
        // 顺序很重要：`−`(U+2212) 是全角区之外的独立字符，必须先换掉，
        // 否则正则认不出它（实测：先做全角转换的话 `OH−` 永远不会被匹配）。
        string text = line.Replace('＋', '+').Replace('－', '-')
                          .Replace('−', '-').Replace('–', '-').Replace('—', '-')
                          .Replace('⁻', '-').Replace('⁺', '+');

        return IonChargeRegex.Replace(text, m =>
        {
            string core = m.Groups["core"].Value;
            // core 必须含字母：挡住 `1735948-` 这类纯数字序列
            if (!core.Any(IsAsciiLetter)) return m.Value;

            string tail = m.Groups["tail"].Value;
            char sign = m.Groups["sign"].Value[0];

            // 尾巴里最后一个数字是电荷数，前面的是下标（见 IonChargeRegex 的说明）
            string subscript = tail.Length > 0 ? tail[..^1] : "";
            string charge = tail.Length > 0 ? tail[^1..] : "";

            var sb = new StringBuilder(core);
            foreach (char d in subscript)
            {
                if (d >= '0' && d <= '9') sb.Append(SubscriptDigits[d - '0']);
            }
            foreach (char d in charge)
            {
                if (d >= '0' && d <= '9') sb.Append(SuperscriptDigits[d - '0']);
            }
            sb.Append(sign == '+' ? '⁺' : '⁻');
            return sb.ToString();
        });
    }

    /// <summary>
    /// 离子电荷的形态：<c>字母串（可带括号）+ 尾部数字 + 正负号</c>（`Ca2+`、`OH-`、`SO42-`）。
    ///
    /// 关键设计：**core 只吃字母与括号，不吃数字**。
    /// 尾部数字统一归 <c>tail</c> 组，再在替换时按位置分配归属——
    /// 一旦让 core 里出现 <c>[0-9]*</c>，贪婪匹配会把 `Ca2+` 的 `2` 一起吞掉，
    /// 电荷数就永远取不到（实测踩过；事后再用负向断言补救无效，正则会在更早的位置继续尝试）。
    ///
    /// 字母串允许连续大写（`OH`、`NaOH`），因此这里用 <c>[A-Za-z]*</c> 而不是
    /// "首字母大写 + 小写若干"——后者会漏掉 `OH-`（实测）。
    ///
    /// 尾巴的数字按"**最后一个数字是电荷数，前面的是下标**"分配：
    /// · `Ca2+`  → `Ca²⁺`；`Na+` → `Na⁺`；`SO42-` → `SO⁴⁻`（下标 4、电荷 2）
    /// 这也正合中国教材对根/原子团的写法习惯。
    ///
    /// 两条例外挡住误伤：末尾 <c>(?![A-Za-z0-9])</c> 排除 `A+B` 这类算式；
    /// "core 必须含字母"排除 `1735948-` 这类纯数字序列（数学课的样例输入）。
    /// </summary>
    private static readonly Regex IonChargeRegex = new(
        @"(?<![A-Za-z0-9(])(?<core>[A-Z][A-Za-z]*(?:\([A-Za-z0-9]+\))?)(?<tail>[0-9]*)(?<sign>[+-])(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    /// <summary>
    /// 方程式折行的确定性粘连：OCR 常把一条方程式拆到两三行
    /// （`2Na2O2+2H2O===` 换行 `4NaOH+O2t`），行内规则因此看不到完整方程式。
    ///
    /// 判据刻意保守：只有当**上一行以反应记号结尾、下一行以元素符号/数字/左括号开头**时才合并，
    /// 且合并后必须仍是一条"看起来像方程式"的行。普通段落换行绝不会被误合并。
    /// </summary>
    /// <param name="line">当前行。</param>
    /// <param name="previous">已输出的上一行（可能为空）。</param>
    /// <param name="merged">合并后的结果行。</param>
    internal static bool TryMergeEquationContinuation(string line, string previous, out string merged)
    {
        merged = line;
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(line)) return false;

        char last = previous.TrimEnd()[^1];
        if (last is not ('=' or '→' or '+' or '＋' or '⇌' or '-')) return false;

        char first = line.TrimStart()[0];
        if (!char.IsUpper(first) && !char.IsDigit(first) && first != '(') return false;

        string candidate = previous.TrimEnd() + line.TrimStart();
        if (!candidate.Contains('=') && !candidate.Contains('→') && !candidate.Contains('⇌'))
            return false;   // 合并后不像方程式：宁可分开

        merged = candidate;
        return true;
    }

    /// <summary>把连续空格压成一个（保留单个空格，保住"1 7 3 5 9 4 8"这类序列）。</summary>
    internal static string CollapseSpaces(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool lastWasSpace = false;
        foreach (char c in line)
        {
            bool isSpace = c == ' ' || c == '\u3000';
            if (isSpace)
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) => c >= '\u4e00' && c <= '\u9fff';

    private static bool IsAsciiAlnum(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsRareRadical(char c) => RareRadicalChars.IndexOf(c) >= 0;

    /// <summary>该字符是不是"被拆开的汉字"会留下的生僻偏旁（供词典层判定修复是否可确证）。</summary>
    internal static bool IsRareRadicalChar(char c) => IsRareRadical(c);

    /// <summary>全角标点（中文标点与全角符号），不含全角字母数字。</summary>
    private static bool IsFullwidthPunct(char c)
        => c is '，' or '。' or '、' or '；' or '：' or '？' or '！'
            or '（' or '）' or '［' or '］' or '｛' or '｝'
            or '“' or '”' or '‘' or '’' or '「' or '」' or '『' or '』'
            or '【' or '】' or '《' or '》' or '〈' or '〉' or '…' or '—' or '·'
            or '＋' or '－' or '＝' or '＊' or '／' or '＜' or '＞' or '～' or '％' or '＃' or '＠' or '＆' or '｜';
}
