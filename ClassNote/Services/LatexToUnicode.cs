using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassNote.Services;

/// <summary>
/// LaTeX 数学公式 → Unicode 近似排版（**纯函数**，便于单测逐条锁住）。
///
/// 为什么需要它：笔记页用 MathJax 排版公式，而 PDF 走 QuestPDF——**它不能排 LaTeX**。
/// 现在导出时只是把 <c>$</c> 分隔符脱掉、LaTeX 原文照印，于是 PDF 里会出现
/// <c>f(n) = \min(f(n-1)+1)</c> 这种带反斜杠命令的字面文本，对用户毫无价值。
///
/// 本类把常见 LaTeX 记法转成 Unicode：`\min` → min、`_{i,j}` → ᵢⱼ 样式的下标、`\le` → ≤、
/// `\alpha` → α。它**不是**完整 LaTeX 引擎，也刻意不做完整——判据是"宁可原样保留，
/// 也不要转出错的东西"：转换不了的命令**原样留着**，用户至少能看出这里有个公式。
///
/// ⚠️ 与 <see cref="OcrTextNormalizer"/> 的分工：那边处理的是**送进模型之前**的 OCR 文本，
/// 这边处理的是**模型输出之后**要印进 PDF 的公式。两者都做"下标 Unicode 化"，但对象不同，
/// 因此各自独立（共用一份映射表反而会把两边的失败面耦合在一起）。
/// </summary>
internal static class LatexToUnicode
{
    /// <summary>Unicode 下标字符（含常用字母，够覆盖 i/j/n/k/1-9 这类变量与数字）。</summary>
    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['i'] = 'ᵢ', ['j'] = 'ⱼ', ['n'] = 'ₙ', ['k'] = 'ₖ', ['m'] = 'ₘ',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['o'] = 'ₒ', ['p'] = 'ₚ', ['r'] = 'ᵣ',
        ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ', ['v'] = 'ᵥ', ['x'] = 'ₓ',
    };

    /// <summary>Unicode 上标字符。</summary>
    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['n'] = 'ⁿ', ['i'] = 'ⁱ',
    };

    /// <summary>命令 → Unicode（不带反斜杠的写法）。只收高频且**含义无歧义**的。</summary>
    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        // 关系与运算
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥",
        ["ne"] = "≠", ["neq"] = "≠", ["approx"] = "≈", ["equiv"] = "≡",
        ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["pm"] = "±", ["mp"] = "∓",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["Rightarrow"] = "⇒",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["subseteq"] = "⊆",
        ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅", ["varnothing"] = "∅",
        ["infty"] = "∞", ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫",
        ["forall"] = "∀", ["exists"] = "∃", ["partial"] = "∂", ["nabla"] = "∇",
        ["sqrt"] = "√", ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥",
        ["therefore"] = "∴", ["because"] = "∵",
        // 函数名（去掉反斜杠即可，写出来是为了避免被当成变量斜体）
        ["min"] = "min", ["max"] = "max", ["log"] = "log", ["ln"] = "ln", ["lg"] = "lg",
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["lim"] = "lim", ["exp"] = "exp",
        ["gcd"] = "gcd", ["lcm"] = "lcm", ["mod"] = "mod",
        // 希腊字母（小写）
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["epsilon"] = "ε", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
        ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ",
        ["tau"] = "τ", ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ",
        ["omega"] = "ω",
        // 希腊字母（大写）
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ",
        ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
    };

    /// <summary>需要**整体丢弃**的命令（它们只影响排版，丢掉后语义不变）。</summary>
    private static readonly HashSet<string> IgnorableCommands = new(StringComparer.Ordinal)
    {
        "left", "right", "displaystyle", "textstyle", "limits", "nolimits",
        "quad", "qquad", "!", ",", ";", ":", "thinspace", "enspace", "medspace",
    };

    /// <summary><c>\begin{...}</c> 这类环境名 → 直接渲染出的文本（其余环境原样保留）。</summary>
    private static readonly Dictionary<string, string> Environments = new(StringComparer.Ordinal)
    {
        ["cases"] = "", ["aligned"] = "", ["array"] = "", ["matrix"] = "", ["pmatrix"] = "(",
        ["bmatrix"] = "[",
    };

    /// <summary>
    /// 命令的形态：<c>\name</c> 且**后面不再接字母**（`\min` 的 `n` 之后不接字母；
    /// 而 <c>\timesb</c> 这种要当整条命令名看待）。
    ///
    /// ⚠️ 刻意**不吃掉尾随空格**：早期写成 <c>\\\s*</c>，于是 `a \times b` 变成
    /// `a ×b`（命令后的空格被吞了）——空格在数学式里是有意义的对齐信息。
    /// </summary>
    private static readonly Regex CommandRegex = new(@"\\([A-Za-z]+)(?![A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex GroupRegex = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex SubSupRegex = new(@"[_^]\s*(\{[^{}]*\}|[A-Za-z0-9])", RegexOptions.Compiled);

    /// <summary>
    /// 把一段公式（不含 <c>$</c> 分隔符）转成 Unicode 近似排版。
    /// 转换不了的命令**原样保留**——宁可让用户看见一个陌生命令，也不要转出错误的内容。
    /// </summary>
    public static string Convert(string? latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) return "";

        string text = latex.Replace("\r", " ").Replace("\n", " ").Trim();

        // 1. 先把上下标处理掉：它们是"结构"，必须在命令替换之前做（`\min_{...}` 的下标里可能还有命令）
        text = ConvertSubSup(text);

        // 2. 分式：\frac{a}{b} → a/b（够用；PDF 排不出真正的分式，斜杠比原样更可读）
        text = ConvertFractions(text);

        // 3. 根式：\sqrt{x} → √(x)
        text = ConvertSqrt(text);

        // 4. 命令 → 符号
        text = CommandRegex.Replace(text, m =>
        {
            string name = m.Groups[1].Value;
            if (IgnorableCommands.Contains(name)) return "";
            if (Commands.TryGetValue(name, out var symbol)) return symbol;
            if (Environments.TryGetValue(name, out var env)) return env;
            return m.Value;   // 认不出来就原样保留（不猜）
        });

        // 5. 只用于分组的括号：`{...}` 里的内容保留，括号本身去掉
        text = StripGroupingBraces(text);

        return CollapseSpaces(text).Trim();
    }

    /// <summary>
    /// 把 Markdown 行里的 <c>$...$</c> / <c>$$...$$</c> 公式替换成 Unicode 近似排版。
    /// </summary>
    /// <param name="line">Markdown 原文行。</param>
    public static string ConvertInlineMath(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains('$')) return line ?? "";

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] != '$')
            {
                sb.Append(line[i]);
                i++;
                continue;
            }

            int fence = i + 1 < line.Length && line[i + 1] == '$' ? 2 : 1;
            string closer = new('$', fence);
            int close = line.IndexOf(closer, i + fence, StringComparison.Ordinal);
            if (close < 0)
            {
                // 没有配对的 $：当普通字符（与渲染器一侧的口径一致，避免吞掉一句普通话）
                sb.Append(line[i]);
                i++;
                continue;
            }

            sb.Append(Convert(line[(i + fence)..close]));
            i = close + fence;
        }

        return sb.ToString();
    }

    // ── 各部分转换 ──────────────────────────────────────

    private static string ConvertSubSup(string text)
    {
        return SubSupRegex.Replace(text, m =>
        {
            char marker = m.Value[0];
            string payload = m.Value[1..].Trim();
            bool braced = payload.StartsWith('{') && payload.EndsWith('}');
            payload = TrimBraces(payload).Replace(@"\", "");

            var map = marker == '_' ? Subscripts : Superscripts;
            var sb = new StringBuilder();
            bool allMapped = true;
            foreach (char c in payload)
            {
                if (map.TryGetValue(c, out char mapped)) sb.Append(mapped);
                else { allMapped = false; break; }
            }

            if (allMapped && sb.Length > 0) return sb.ToString();

            // 有一个字符映射不出来就**整体放弃**（半下标比不转更容易误读）。
            // 还原成可读的原文：带花括号的还原花括号（`d_{i,j}`），裸写的还原裸写（`a_i`），
            // 不能把 `d_{i,j}` 变成 `d_i,j`——那会让用户以为逗号是正文的一部分。
            return braced ? $"{marker}{{{payload}}}" : $"{marker}{payload}";
        });
    }

    private static string ConvertFractions(string text)
    {
        var regex = new Regex(@"\\frac\s*\{([^{}]*)\}\s*\{([^{}]*)\}", RegexOptions.Compiled);
        int guard = 0;
        while (regex.IsMatch(text) && guard++ < 8)
        {
            text = regex.Replace(text, m =>
            {
                string numerator = m.Groups[1].Value.Trim();
                string denominator = m.Groups[2].Value.Trim();
                // 单项分母写成 `a/b` 更接近原式；复杂分母加括号避免歧义
                bool simpleDenominator = denominator.Length <= 2;
                return simpleDenominator ? $"{numerator}/{denominator}" : $"{numerator}/({denominator})";
            });
        }
        return text;
    }

    private static string ConvertSqrt(string text)
    {
        var regex = new Regex(@"\\sqrt\s*\{([^{}]*)\}", RegexOptions.Compiled);
        int guard = 0;
        while (regex.IsMatch(text) && guard++ < 8)
            text = regex.Replace(text, m => "√(" + m.Groups[1].Value.Trim() + ")");
        return text;
    }

    /// <summary>
    /// 去掉只用于分组的 <c>{ }</c>（内容保留），但**保留下标/上标的花括号**。
    ///
    /// 后者很关键：`d_{i,j}` 的下标因为含逗号而无法转 Unicode（整体放弃），
    /// 此时花括号是公式语法的一部分，去掉就变成 `d_i,j`——看起来像逗号是正文。
    /// </summary>
    private static string StripGroupingBraces(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '{' or '}')
            {
                // 前一个有效字符是 _ 或 ^ → 这是下标/上标的分组，保留
                int prev = i - 1;
                while (prev >= 0 && text[prev] == ' ') prev--;
                if (prev >= 0 && (text[prev] == '_' || text[prev] == '^'))
                {
                    sb.Append(c);
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string TrimBraces(string text)
        => text.Length >= 2 && text[0] == '{' && text[^1] == '}' ? text[1..^1] : text;

    private static string CollapseSpaces(string text)
        => Regex.Replace(text, @"\s{2,}", " ").Trim();
}
