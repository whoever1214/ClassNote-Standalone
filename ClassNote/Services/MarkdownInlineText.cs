using System;
using System.Collections.Generic;
using System.Text;

namespace ClassNote.Services;

/// <summary>
/// 一行 Markdown 文本 → 可排版的富文本片段（纯函数，便于单测）。
///
/// 为什么需要它：PDF 导出走 QuestPDF，它**不渲染 HTML**，所以导出时必须自己把行内标记处理掉。
/// 旧实现直接把 markdown 原文逐行打印，于是 PDF 里会出现：
/// · 字面的 <c>**加粗**</c> 星号；· 字面的 <c>$公式$</c> 美元符；· 字面的反引号；
/// · 编号列表完全识别不到（识别用的正则 <c>^d+[.)]s+</c> 转义丢失，永远匹配不上，形同虚设）。
///
/// 处理原则：**只去掉标记，绝不改动文字内容**——这与全局的"技术内容逐字保真"口径一致。
/// 公式（<c>$...$</c> / <c>$$...$$</c>）在这里只脱掉分隔符：QuestPDF 无法排版 LaTeX，
/// 保留 <c>d_{i,j}</c> 这样的原文反而比印出美元符更有用。
/// </summary>
public static class MarkdownInlineText
{
    /// <summary>一段文本及其样式。</summary>
    /// <param name="Text">文本内容（不含标记）。</param>
    /// <param name="Bold">是否加粗（来自 <c>**...**</c> 或 <c>__...__</c>）。</param>
    /// <param name="Code">是否等宽（来自反引号）。</param>
    public readonly record struct Run(string Text, bool Bold, bool Code);

    /// <summary>把一行 Markdown 拆成文本片段。空行返回空列表。</summary>
    public static List<Run> ParseLine(string? line)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(line))
            return runs;

        var plain = new StringBuilder();
        int i = 0;
        bool bold = false;
        bool code = false;

        void Flush()
        {
            if (plain.Length > 0)
            {
                runs.Add(new Run(plain.ToString(), bold, code));
                plain.Clear();
            }
        }

        while (i < line.Length)
        {
            char c = line[i];

            // $$ 行间公式与 $ 行内公式：脱掉分隔符，公式内容原样保留
            if (c == '$')
            {
                int fence = i + 1 < line.Length && line[i + 1] == '$' ? 2 : 1;
                int close = line.IndexOf(new string('$', fence), i + fence, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    runs.Add(new Run(line[(i + fence)..close], bold, false));
                    i = close + fence;
                    continue;
                }
                // 没有配对的 $：当普通字符（避免把一句普通话吞掉）
                plain.Append(c);
                i++;
                continue;
            }

            // 反引号
            if (c == '`')
            {
                int close = line.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    code = true;
                    runs.Add(new Run(line[(i + 1)..close], false, true));
                    code = false;
                    i = close + 1;
                    continue;
                }
                plain.Append(c);
                i++;
                continue;
            }

            // **加粗** / __加粗__
            if ((c == '*' || c == '_') && i + 1 < line.Length && line[i + 1] == c)
            {
                string marker = new(c, 2);
                int close = line.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    bold = !bold;
                    plain.Append(line[(i + 2)..close]);
                    Flush();
                    bold = !bold;
                    i = close + 2;
                    continue;
                }
                plain.Append(c);
                i++;
                continue;
            }

            // 链接 [文本](地址) → 只留文本
            if (c == '[')
            {
                int closeBracket = line.IndexOf(']', i + 1);
                if (closeBracket > i && closeBracket + 1 < line.Length && line[closeBracket + 1] == '(')
                {
                    int closeParen = line.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket)
                    {
                        plain.Append(line[(i + 1)..closeBracket]);
                        i = closeParen + 1;
                        continue;
                    }
                }
                // 注意：这里刻意**不**处理 [i][j] 这类"引用式链接"——
                // 它在笔记里几乎总是数学下标写法（d[i][j]），必须原样保留。
                plain.Append(c);
                i++;
                continue;
            }

            // 转义字符 \* \_ \$ 等：还原为字符本身
            if (c == '\\' && i + 1 < line.Length && IsEscapable(line[i + 1]))
            {
                plain.Append(line[i + 1]);
                i += 2;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
        return runs;
    }

    /// <summary>该行是否为 Markdown 有序列表项（<c>1. </c> / <c>2) </c>）。</summary>
    public static bool IsOrderedListItem(string line) => TryStripOrderedMarker(line, out _);

    /// <summary>剥掉有序列表序号，返回正文。<paramref name="content"/> 为正文。</summary>
    public static bool TryStripOrderedMarker(string line, out string content)
    {
        content = line;
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        if (i == 0 || i >= line.Length)
            return false;

        char sep = line[i];
        if (sep != '.' && sep != ')' && sep != '、')
            return false;

        int j = i + 1;
        while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
            j++;
        if (j == i + 1 && sep != '、')
            return false;   // "1.文本"（无空格）也不算，避免误伤小数与版本号

        content = line[j..].TrimStart();
        return true;
    }

    private static bool IsEscapable(char c)
        => c is '*' or '_' or '`' or '$' or '[' or ']' or '\\' or '#' or '>' or '|' or '~';
}
