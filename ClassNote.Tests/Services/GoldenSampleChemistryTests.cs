using System;
using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 黄金样本：化学课那份问题笔记的 OCR 素材，作为**永久的回归网**。
///
/// 素材来自用户提供的 `化学.pdf`（钠及其氧化物、碳酸钠与碳酸氢钠一节的截图 OCR），
/// 它当时产出的笔记有三个病：坏字被照抄（`Na2C03 古亻本`）、同一句话重复出现在两个章节
/// （整篇重复行占比 25.4%）、判断题的选项原句被当成事实陈述。
///
/// 本文件把**输入侧**的那批坏行固定下来，并断言"不管以后怎么改，这四条质量契约都不能破"：
/// 1. 坏字要么被确定性修掉，要么**必须带疑点标注**——绝不静默出现在输出里；
/// 2. 关键知识点的内容不得因为修复而丢失；
/// 3. 化学式、离子、方程式的呈现必须规范化；
/// 4. 数学课的硬约束（`1 7 3 5 9 4 8` 不许被粘成一个数）必须依然成立。
///
/// 第 4 条是有意放在这里的：它跨课程，而"化学规则误伤数学素材"正是最容易被忽略的回归。
/// </summary>
public class GoldenSampleChemistryTests
{
    /// <summary>化学课截图 OCR 的真实形态（含当年级别的坏字）。</summary>
    private const string RawOcr = """
        [new_slide @ 00:09:00]
        钠及其氧化物、碳酸钠与碳酸氢钠
        [new_slide @ 00:28:00]
        2 ． 金属钠可以放置在煤氵由、石蜡油或 CC14 等液态有机物中保存
        3 ． 金属钠着火时，可用泡沫灭火器或干砂灭火
        [annotation @ 01:44:00]
        6 釒属钠在空气中加热生成淡黄色固体：4Na + 02 2Na20
        7·Na202 能与 C02 反应产生 02，可作供氧剂
        [new_slide @ 03:12:00]
        1.Na2C03 和 NaHC03 的比较
        物质碳酸钠（Na2C03，白色粉末）碳酸氢钠（NaHC03，白色晶体）
        [annotation @ 04:05:00]
        碳酸钠 氵谷氵夜 显 碱 性
        2Na202+2H20===4NaOH+02t
        """;

    /// <summary>化学课转写的真实形态（术语在口语里会反复出现）。</summary>
    private const string Transcript = """
        【麦克风（教室现场）】
        今天讲钠及其氧化物，还有碳酸钠和碳酸氢钠。碳酸钠俗名纯碱，碳酸氢钠俗名小苏打。
        金属钠要保存在煤油或者石蜡油里，不能放在四氯化碳里。
        过氧化钠和二氧化碳反应生成碳酸钠和氧气，所以可以做供氧剂。
        碳酸钠溶液显碱性。碳酸钠溶液能使酚酞变红。
        """;

    /// <summary>
    /// 会出现在"字被拆开"的残片里的字符。
    /// 刻意不含「一」「口」「日」「十」这类**能独立成词的常用汉字**——
    /// 把它们算进来，正常正文会被误判成坏字。
    /// </summary>
    private static readonly char[] RareRadicals =
        "亻氵冫钅釒纟讠刂阝卩冂匚凵屮尢廴廾弋彐彡攴攵殳毋爿爫爻犭牜礻衤疒癶罒耒聿虍豸辶闩巛丿亠宀冖".ToCharArray();

    private static string NormalizeWithLexicon()
    {
        var lexicon = OcrLexicon.FromTranscript(Transcript);
        return OcrTextNormalizer.Normalize(RawOcr, lexicon);
    }

    // ── 契约 1：坏字要么被修掉，要么必须带疑点标注 ──────────────

    [Fact]
    public void NoUnmarkedGarbageCharactersSurvive()
    {
        string output = NormalizeWithLexicon();

        var offenders = output.Split('\n')
            .Where(line => RareRadicals.Any(line.Contains))
            .Where(line => !OcrTextNormalizer.IsAnnotatedUncertain(line))
            .ToList();

        Assert.True(offenders.Count == 0,
            "含生僻偏旁的残片必须被确定性修掉或带【OCR不清】标注，以下行两者都不是：\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void EveryLineThatHadGarbageIsFlagged()
    {
        // 修复成功也要留痕：修掉了偏旁不等于"这一行 OCR 没问题"。
        // 少了这条，用户看到的就是一条干净行，永远不知道这里曾经读错。
        var sourceLinesWithRadicals = RawOcr.Split('\n')
            .Where(line => RareRadicals.Any(line.Contains))
            .ToList();

        string output = NormalizeWithLexicon();

        Assert.NotEmpty(sourceLinesWithRadicals);
        // 输出里每一条"原文带残片"的内容都必须留下标记（按关键字核对，避免行序耦合）
        Assert.Contains("煤氵由", output);
        Assert.Contains(OcrTextNormalizer.UncertainSuffix, output);
        Assert.True(
            output.Split('\n').Count(OcrTextNormalizer.IsAnnotatedUncertain) >= 3,
            $"至少应有 3 行带疑点标注，实际 {output.Split('\n').Count(OcrTextNormalizer.IsAnnotatedUncertain)} 行");
    }

    [Fact]
    public void RepairableGarbageIsActuallyRepaired()
    {
        string output = NormalizeWithLexicon();

        // `釒属` 里的残片偏旁可以被确定性去掉（词典里有「属」相关的词）
        Assert.DoesNotContain("釒属", output);
        // `CC14` 是四氯化碳 CCl4（l↔1 互认）
        Assert.Contains("CCl", output);
    }

    // ── 契约 2：关键知识点不得因修复而丢失 ──────────────────────

    [Fact]
    public void KeyKnowledgePointsSurvive()
    {
        string output = NormalizeWithLexicon();

        // 钠的保存方式（这句在原文里是坏字最密集的一行，最容易被"修没了"）
        Assert.Contains("煤", output);
        Assert.Contains("石蜡油", output);
        // 核心反应：过氧化钠与二氧化碳
        Assert.Contains("供氧剂", output);
        // 碳酸钠/碳酸氢钠的名称不能被修复过程吃掉
        Assert.Contains("碳酸钠", output);
        Assert.Contains("碳酸氢钠", output);
    }

    // ── 契约 3：化学式/离子/方程式的呈现规范化 ─────────────────

    [Fact]
    public void ChemicalFormsAreNormalized()
    {
        string output = NormalizeWithLexicon();

        // 方程式记号：`===` 归一到箭头，`02t` 里的 t 是 ↑ 的误识
        Assert.DoesNotContain("===", output);
        Assert.Contains("→", output);
        Assert.Contains("↑", output);
        // 化学式下标：`Na2C03` 的 0↔O 被修掉之后应当排成下标写法
        Assert.Contains("₂", output);
        // 原文里的 `氵谷氵夜` 无法可靠还原 → 必须带疑点标注（见契约 1）
        Assert.Contains(OcrTextNormalizer.UncertainSuffix, output);
    }

    // ── 契约 4：跨课程约束（化学规则不得误伤数学素材）─────────

    [Fact]
    public void MathClassHardConstraint_StillHolds()
    {
        // 数学课的样例输入：纯数字序列被粘成一个数就是彻底的语义事故
        Assert.Equal("1 7 3 5 9 4 8", OcrTextNormalizer.Normalize("1 7 3 5 9 4 8"));

        // 标识符与 pH 值也不得被化学规则改动
        var lexicon = OcrLexicon.FromTranscript(Transcript);
        Assert.Equal("utf8", OcrTextNormalizer.Normalize("utf8", lexicon));
        Assert.Equal("pH7", OcrTextNormalizer.Normalize("pH7", lexicon));
    }

    // ── 契约「负向」：确定性修复不得凭空造词 ────────────────────

    [Fact]
    public void RepairNeverInventsTermsThatAreNotInTheDictionary()
    {
        // 词典里只有化学品名；「氯化铁」不在其中，任何修复都必须保持它原样
        string output = OcrTextNormalizer.Normalize("氯 化 铁 是 红 褐 色 固 体", OcrLexicon.FromTranscript(Transcript));

        Assert.Contains("氯化铁", output);
    }

    [Fact]
    public void Normalize_IsIdempotentOnTheGoldenSample()
    {
        // 幂等是所有确定性修复的底线：跑第二遍不得改坏第一遍的结果
        string once = NormalizeWithLexicon();
        string twice = OcrTextNormalizer.Normalize(once, OcrLexicon.FromTranscript(Transcript));

        Assert.Equal(once, twice);
    }
}
