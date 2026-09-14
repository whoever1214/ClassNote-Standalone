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
/// </summary>
internal static class OcrTextNormalizer
{
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

    /// <summary>公式行里需要去掉两侧空格的符号（括号、逗号、运算符）。</summary>
    private static readonly Regex SpaceAroundSymbol = new(@"\s*([()\[\]{},;=+\-*/<>])\s*", RegexOptions.Compiled);

    /// <summary>
    /// 规范化整段 OCR 文本（保持行结构；只改字符级噪声，不增删内容）。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var normalizedNewlines = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedNewlines.Split('\n');
        var sb = new StringBuilder(text.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(NormalizeLine(lines[i]));
        }

        return sb.ToString().Trim();
    }

    /// <summary>规范化一行（供单测直接覆盖）。</summary>
    internal static string NormalizeLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";

        string merged = MergeCharacterSpaces(line);
        if (LooksLikeFormula(merged))
            merged = NormalizeFormulaLine(merged);

        return CollapseSpaces(merged).Trim();
    }

    /// <summary>这一行是不是公式/代码（决定要不要做全角转半角与符号紧排）。</summary>
    internal static bool LooksLikeFormula(string line)
        => !string.IsNullOrEmpty(line) && FormulaHint.IsMatch(line);

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

    /// <summary>把连续空格压成一个（保留单个空格，保住"1 7 3 5 9 4 8"这类序列）。</summary>
    private static string CollapseSpaces(string line)
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

    /// <summary>全角标点（中文标点与全角符号），不含全角字母数字。</summary>
    private static bool IsFullwidthPunct(char c)
        => c is '，' or '。' or '、' or '；' or '：' or '？' or '！'
            or '（' or '）' or '［' or '］' or '｛' or '｝'
            or '“' or '”' or '‘' or '’' or '「' or '」' or '『' or '』'
            or '【' or '】' or '《' or '》' or '〈' or '〉' or '…' or '—' or '·'
            or '＋' or '－' or '＝' or '＊' or '／' or '＜' or '＞' or '～' or '％' or '＃' or '＠' or '＆' or '｜';
}
