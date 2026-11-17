using System.Diagnostics;
using System.IO;
using System.Text;
using ClassNote.Services;

namespace OcrNormalizeEval;

/// <summary>
/// OCR 规范化的**离线评测工具**（P0-α 的量化依据）。
///
/// 它回答一个问题：转写词典 + 化学式规则到底把课件 OCR 修干净了多少？
/// 不猜、不估，直接跑生产代码里的同一个 <see cref="OcrTextNormalizer"/>，把"修复前/修复后"的
/// 可量化指标并排打出来。
///
/// 用法：
///   dotnet run --project devtools/OcrNormalizeEval -- --ocr 原始OCR.txt [--transcript 转写.txt] [--out 报告.md]
///
/// 输入格式：纯文本即可。OCR 文件里可以带 <c>[new_slide @ 00:12:30]</c> 这类来源标记行——
/// 它们会被原样保留（与管线里的拼装口径一致）。
///
/// ⚠️ 本工具**只读**，不写数据库、不调模型、不改动任何生产数据。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = ParseArgs(args);
        if (options == null)
        {
            PrintUsage();
            return 2;
        }

        string ocrPath = options.Value.Ocr;
        if (!File.Exists(ocrPath))
        {
            Console.Error.WriteLine($"找不到 OCR 文件：{ocrPath}");
            return 2;
        }

        string rawOcr = File.ReadAllText(ocrPath);
        string transcript = options.Value.Transcript != null && File.Exists(options.Value.Transcript)
            ? File.ReadAllText(options.Value.Transcript)
            : "";

        var sw = Stopwatch.StartNew();

        // 修复前：只做 v0.7 的字符级规范化（无词典）
        string before = OcrTextNormalizer.Normalize(rawOcr, null);

        // 修复后：加上转写词典
        var lexicon = OcrLexicon.FromTranscript(transcript);
        string after = OcrTextNormalizer.Normalize(rawOcr, lexicon);

        sw.Stop();

        var report = BuildReport(ocrPath, options.Value.Transcript, rawOcr, transcript, lexicon, before, after, sw.Elapsed);

        Console.WriteLine(report);

        if (options.Value.Out != null)
        {
            File.WriteAllText(options.Value.Out, report, new UTF8Encoding(false));
            Console.WriteLine($"\n（报告已写入 {options.Value.Out}）");
        }

        return 0;
    }

    private static string BuildReport(
        string ocrPath, string? transcriptPath,
        string rawOcr, string transcript,
        OcrLexicon lexicon, string before, string after, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# OCR 规范化效果评测");
        sb.AppendLine();
        sb.AppendLine($"- OCR 素材：`{ocrPath}`（{rawOcr.Length} 字符）");
        sb.AppendLine(transcriptPath != null
            ? $"- 转写素材：`{transcriptPath}`（{transcript.Length} 字符）"
            : "- 转写素材：**未提供**（词典校正整体关闭，报告只反映字符级规则）");
        sb.AppendLine($"- 词典条目：{lexicon.Count}");
        sb.AppendLine($"- 耗时：{elapsed.TotalMilliseconds:0} ms");
        sb.AppendLine();

        sb.AppendLine("## 1. 疑点与坏字（越少越好）");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 修复前 | 修复后 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| 生僻偏旁行数 | {CountLinesWithRareRadical(before)} | {CountLinesWithRareRadical(after)} |");
        sb.AppendLine($"| 【OCR不清】标注行数 | {CountMarker(before)} | {CountMarker(after)} |");
        sb.AppendLine($"| ASCII 字母/数字形态可疑段（0↔O 混用） | {CountDigitLetterConfusion(before)} | {CountDigitLetterConfusion(after)} |");
        sb.AppendLine($"| 化学方程式可读行数（含 → 且无 === ） | {CountReadableEquations(before)} | {CountReadableEquations(after)} |");
        sb.AppendLine();

        sb.AppendLine("## 2. 化学式规范化样本（前后对照）");
        sb.AppendLine();
        AppendFormulaDiff(sb, before, after);

        sb.AppendLine("## 3. 词典校正明细（逐行）");
        sb.AppendLine();
        AppendLineDiff(sb, before, after);

        sb.AppendLine("## 4. 仍然无法确定性修复的内容（必须由【OCR不清】暴露）");
        sb.AppendLine();
        AppendUnresolved(sb, after);

        return sb.ToString();
    }

    private static void AppendFormulaDiff(StringBuilder sb, string before, string after)
    {
        var b = before.Split('\n');
        var a = after.Split('\n');
        int shown = 0;

        sb.AppendLine("只列出**内容确实发生变化**的行（按行对照，避免把不相关的 token 排在一起比较）：");
        sb.AppendLine();
        sb.AppendLine("| 修复前 | 修复后 |");
        sb.AppendLine("|---|---|");

        for (int i = 0; i < Math.Min(b.Length, a.Length); i++)
        {
            if (b[i] == a[i]) continue;
            if (!b[i].Any(char.IsAsciiLetter) && !a[i].Any(char.IsAsciiLetter)) continue;
            sb.AppendLine($"| `{Truncate(b[i], 100)}` | `{Truncate(a[i], 100)}` |");
            if (++shown >= 30) break;
        }

        if (shown == 0)
            sb.AppendLine("| （无可对照的化学式变化） | |");
        sb.AppendLine();
    }

    private static void AppendLineDiff(StringBuilder sb, string before, string after)
    {
        var b = before.Split('\n');
        var a = after.Split('\n');
        int count = 0;
        for (int i = 0; i < Math.Min(b.Length, a.Length); i++)
        {
            if (b[i] == a[i]) continue;
            sb.AppendLine($"- 第 {i + 1} 行");
            sb.AppendLine($"  - 前：`{Truncate(b[i], 120)}`");
            sb.AppendLine($"  - 后：`{Truncate(a[i], 120)}`");
            if (++count >= 40)
            {
                sb.AppendLine("- …（更多差异已省略）");
                break;
            }
        }
        if (count == 0)
            sb.AppendLine("（没有任何行被改动——说明这份素材里没有可确证的修复点，或者词典没覆盖到）");
        sb.AppendLine();
    }

    private static void AppendUnresolved(StringBuilder sb, string after)
    {
        var lines = after.Split('\n')
            .Select((line, index) => (line, index))
            .Where(x => OcrTextNormalizer.IsAnnotatedUncertain(x.line))
            .Take(40)
            .ToList();

        if (lines.Count == 0)
        {
            sb.AppendLine("（没有遗留疑点）");
            return;
        }

        foreach (var (line, index) in lines)
            sb.AppendLine($"- 第 {index + 1} 行：`{Truncate(line, 120)}`");
    }

    // ── 指标计算 ──────────────────────────────────────

    private static int CountLinesWithRareRadical(string text)
        => text.Split('\n').Count(OcrTextNormalizer.ContainsRareRadical);

    private static int CountMarker(string text)
        => text.Split('\n').Count(OcrTextNormalizer.IsAnnotatedUncertain);

    /// <summary>含字母数字混排且出现 `0` 夹在字母数字之间的段落数（0↔O 互认的候选）。</summary>
    private static int CountDigitLetterConfusion(string text)
    {
        int count = 0;
        foreach (var line in text.Split('\n'))
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(line, @"[A-Za-z0-9]*[A-Za-z][A-Za-z0-9]*"))
            {
                if (m.Value.Contains('0')) { count++; break; }
            }
        }
        return count;
    }

    /// <summary>含反应箭头、且不再出现 `===`/`==` 的行数（方程式记号已理顺）。</summary>
    private static int CountReadableEquations(string text)
        => text.Split('\n').Count(line =>
            line.Contains('→') && !line.Contains("==") && !line.Contains("＝"));

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static (string Ocr, string? Transcript, string? Out)? ParseArgs(string[] args)
    {
        string? ocr = null, transcript = null, output = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ocr" when i + 1 < args.Length: ocr = args[++i]; break;
                case "--transcript" when i + 1 < args.Length: transcript = args[++i]; break;
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                default: return null;
            }
        }
        return ocr == null ? null : (ocr, transcript, output);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("OCR 规范化离线评测");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project devtools/OcrNormalizeEval -- --ocr <原始OCR.txt> [--transcript <转写.txt>] [--out <报告.md>]");
        Console.WriteLine();
        Console.WriteLine("  --ocr         必填：截图 OCR 的原始文本（可含 [type @ 时间] 标记行）");
        Console.WriteLine("  --transcript  选填：同一节课的转写文本（提供则启用转写词典校正）");
        Console.WriteLine("  --out         选填：把 Markdown 报告写入该路径");
    }
}
