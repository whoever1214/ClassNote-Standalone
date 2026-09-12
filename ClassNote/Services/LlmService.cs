using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ClassNote.Services;

/// <summary>
/// 本地 LLM 服务：直接调用 OpenAI 兼容 API（默认 DeepSeek），
/// API Key 由用户在客户端配置（AppSettings）。
/// </summary>
public interface ILlmService
{
    /// <summary>生成课堂笔记（Markdown）。素材过长时自动分段整理后合并。</summary>
    Task<NoteGenerationResult> GenerateNoteAsync(string course, string transcript, string ocrText, IProgress<string>? progress = null);

    /// <summary>检查 OpenAI 兼容 API（v1 接口）健康状态（GET /v1/models）。</summary>
    Task<LlmHealthResult> CheckHealthAsync(string? apiKey = null, string? baseUrl = null);

    /// <summary>拉取该 API 供应商的可用模型列表（GET /v1/models），供设置界面下拉选择。</summary>
    Task<LlmModelsResult> ListModelsAsync(string? apiKey = null, string? baseUrl = null);
}

/// <summary>LLM 服务健康检查结果。</summary>
/// <param name="IsOk">服务是否可达（HTTP 有响应即视为可达；未配置 Key 时为 false）。</param>
/// <param name="Message">面向用户的说明文字。</param>
/// <param name="IsConfigured">是否已配置 API Key（未配置时网络检查会跳过）。</param>
public sealed record LlmHealthResult(bool IsOk, string Message, bool IsConfigured = true);

/// <summary>模型列表拉取结果。</summary>
/// <param name="Models">可用模型 ID（已去重排序）；失败时为空。</param>
/// <param name="Message">面向用户的说明文字（成功时为"共 N 个模型"）。</param>
/// <param name="IsOk">是否成功取得模型列表。</param>
public sealed record LlmModelsResult(IReadOnlyList<string> Models, string Message, bool IsOk);

/// <summary>笔记生成结果。</summary>
/// <param name="Markdown">笔记正文（Markdown）。</param>
/// <param name="SegmentCount">实际使用的分段数（1 表示一次性生成）。</param>
/// <param name="FailedSegments">整理失败的分段数（这些段的内容不会出现在笔记里）。</param>
/// <param name="OversizedChars">
/// 因段数上限而被并入最后一段、未再切分的转写字符数（0 = 最后一段也没超单段上限）。
/// 这些内容**没有被丢弃**，但最后一段的输入量超限，可能被模型截断——需要如实告知用户。
/// </param>
public sealed record NoteGenerationResult(string Markdown, int SegmentCount, int FailedSegments, int OversizedChars);

public sealed class LlmService : ILlmService
{
    /// <summary>默认请求超时（秒）：本地模型推理慢，默认 30 分钟。</summary>
    public const int DefaultTimeoutSeconds = 1800;

    /// <summary>健康检查超时（秒）。</summary>
    private const int HealthCheckTimeoutSeconds = 10;

    /// <summary>模型列表拉取超时（秒）：本地推理服务的 /v1/models 偶尔较慢，给得比健康检查宽裕。</summary>
    private const int ModelListTimeoutSeconds = 15;

    /// <summary>模型下拉列表的条数上限（供应商有时返回数百个，超出部分不铺进下拉框）。</summary>
    public const int MaxModelCount = 300;

    /// <summary>未配置基础地址时的默认值。</summary>
    private const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>分段数量超过该值时，合并阶段先分组合并再逐级合并（避免合并输入本身又超限）。</summary>
    private const int MergeCascadeThreshold = 6;

    /// <summary>校对摘录的上限：合并提示词里附带的原素材片段，只用于查漏补缺。</summary>
    private const int MergeExcerptChars = 1500;

    /// <summary>
    /// 进程级共享的 HTTP 客户端（v0.7 起）。
    ///
    /// 旧实现每次调用都 <c>new HttpClient</c>：那不只是多一个对象——它意味着**每次新建连接池**，
    /// 于是每次调用都要重做一遍 TCP + TLS 握手（国内直连 API 时 100–300ms/次），
    /// 而一节课的分段整理要调用 5–10 次，光握手就白等一两秒；被丢弃的客户端还会留下一串 TIME_WAIT 的 socket。
    /// 主页每 30 秒一次的健康检查同样受益。
    ///
    /// 注意 <see cref="HttpClient.Timeout"/> 是客户端级的，共享之后不能再用它表达"每次请求的超时"
    /// （用户可在设置里把超时调到 30 分钟），因此改为每个请求各自的 <see cref="CancellationTokenSource"/>。
    /// </summary>
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 连接复用 5 分钟后重建：既让 DNS 变化最终能生效，又不会每次请求都握手
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // 真正的超时见各调用点的 CTS
        };
    }

    /// <summary>
    /// 生成课堂笔记（Markdown）。素材超过单次调用上限时自动分段：逐段整理 → 合并，
    /// 避免早期 Truncate() 静默丢弃后半节课的内容。
    /// </summary>
    public async Task<NoteGenerationResult> GenerateNoteAsync(
        string course, string transcript, string ocrText, IProgress<string>? progress = null)
    {
        var plan = NoteSegmenter.Build(course, transcript, ocrText);
        // 分轨录制（麦克风 + 系统声音）的转写带有来源分区标注：提示词据此加入"分辨谁在说话"的要求，
        // 合并阶段也要据此事先声明"不许把来源标记洗掉"。
        bool multiSource = TranscriptSections.IsMultiSource(transcript);

        if (plan.Segments.Count == 1 && !plan.IsSegmented)
        {
            // 常见情形：素材在上限内，一次调用出完整笔记
            progress?.Report("正在生成笔记…");
            var single = plan.Segments[0];
            var markdown = await ChatWithFallbackAsync(
                NotePromptBuilder.NoteSystemPrompt,
                NotePromptBuilder.BuildSingleNotePrompt(course, single.Transcript, single.OcrText, multiSource));
            return new NoteGenerationResult(markdown, 1, 0, plan.OversizedChars);
        }

        // 分段：每段只做"整理"，不做合并；段内失败不影响其它段
        int total = plan.Segments.Count;
        int failures = 0;
        var segmentJsons = new List<string>(total);
        var segmentParsed = new List<(string Core, string Pitfalls)>(total);
        // 结构化条目：分段结果先汇成条目列表，再走"确定性去重 → 排序润色 → 回校"
        var allItems = new List<NotePromptBuilder.NoteItem>();
        int usedItemsSchema = 0;

        // 明确告知规模：段数 × 最多 2 次调用对本地慢模型可能是几十分钟，用户有权提前知道
        progress?.Report($"素材较长，将分 {total} 段整理（最多 {total * 2} 次模型调用）…");

        // 单段尝试：把异常收成返回值，好让调用方按错误性质决定"重试 / 记为该段失败 / 整体失败"
        async Task<(string? Raw, Exception? Error)> TrySegmentAsync(NoteSegment segment, bool isRetry)
        {
            try
            {
                var text = await ChatWithFallbackAsync(
                    NotePromptBuilder.SegmentItemsSystemPrompt,
                    NotePromptBuilder.BuildSegmentPrompt(course, segment, isRetry, multiSource, itemsSchema: true),
                    jsonMode: true);
                return (text, null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        for (int i = 0; i < total; i++)
        {
            var segment = plan.Segments[i];
            progress?.Report(FormatSegmentProgress("正在整理", i + 1, total, segment.TimestampHint));

            var (raw, error) = await TrySegmentAsync(segment, isRetry: false);

            if (error != null)
            {
                // 配置类错误（未填 API Key 等）：重试无意义，而且每一段都会失败。
                // 直接向上抛，让 NoteProcessor 给出"去设置里配置"+ 原文兜底，
                // 而不是产出一篇堆满失败占位符、状态却是"完成"的笔记。
                // （throw error 会重置堆栈，但这里只关心我们自己写好的 Message。）
                if (IsConfigurationError(error))
                    throw error;

                if (IsTransientError(error))
                {
                    // 瞬时故障（超时/网络/429/5xx/空响应）才值得重试一次：
                    // 去掉分段说明、只留素材与规则，降低再次失败的几率
                    progress?.Report($"第 {i + 1}/{total} 段整理失败，正在重试…" +
                                     (string.IsNullOrEmpty(segment.TimestampHint) ? "" : $"（{segment.TimestampHint}）"));
                    (raw, error) = await TrySegmentAsync(segment, isRetry: true);
                }
            }

            if (error != null || string.IsNullOrWhiteSpace(raw))
            {
                failures++;
                // 失败段在合并输入里**留空**：绝不能把"（第 N 段整理失败：…）"这类错误文本塞进
                // 合并输入——合并提示词要求"所有分段的知识点都必须进入最终笔记"，
                // 模型很可能真把它当成一条知识点抄进用户的笔记。
                segmentJsons.Add(EmptySegmentJson);
                segmentParsed.Add(("", ""));
                if (error != null)
                    progress?.Report($"第 {i + 1}/{total} 段整理失败：{ShortError(error.Message)}");
                continue;
            }

            segmentJsons.Add(raw.Trim());
            segmentParsed.Add(NotePromptBuilder.ParseSegmentJson(raw));

            // 条目化：解析得出条目就入列；解析不出（旧 schema / 纯文本 / 数组被截断）
            // 则退回 Markdown 文本，保证这一段的内容不丢
            var items = NotePromptBuilder.ParseSegmentItems(raw, i + 1);
            if (items.Count > 0)
            {
                usedItemsSchema++;
                allItems.AddRange(items);
            }
        }

        // 全部段都失败：视为整体失败，交给 NoteProcessor 的兜底分支（原文笔记 + 明确原因），
        // 而不是产出一篇只含"（本轮未整理出核心知识点）"的"完成"笔记
        if (failures == total)
            throw new InvalidOperationException($"全部 {total} 段整理失败，未能生成笔记。");

        // 结构化路径：条目化可用时走"确定性去重 → 排序润色 → 回校"
        if (usedItemsSchema > 0)
        {
            var result = await ComposeFromItemsAsync(course, allItems, progress, failures, total, plan.OversizedChars);
            return result;
        }

        // 兜底路径：条目一个都没解析出来（本地模型可能整体拒绝结构化输出），
        // 退回 Markdown 合并——旧行为，慢且依赖模型，但不会因为 schema 不被支持就生成失败
        progress?.Report("模型未按条目格式输出，退回文本合并…");
        string merged;
        try
        {
            var excerpts = total > 1 ? (BuildExcerpt(transcript), BuildExcerpt(ocrText)) : (null, null);
            merged = NotePromptBuilder.CleanMarkdown(await MergeCascadeAsync(
                course, segmentJsons, excerpts.Item1, excerpts.Item2, multiSource));
        }
        catch (Exception)
        {
            // 合并失败不能导致内容丢失：退回机械拼接（保真内容已在分段结果里）
            merged = NotePromptBuilder.AssembleNote(course, segmentParsed);
        }

        if (string.IsNullOrWhiteSpace(merged))
            merged = NotePromptBuilder.AssembleNote(course, segmentParsed);

        // 失败段与"末段超限"都在正文里显式留痕：宁可让用户看到"有内容没进来"，
        // 也不要静默缺失（与单段路径的失败兜底保持同一口径）
        if (failures > 0)
        {
            merged = merged.TrimEnd() + "\n\n" + NotePromptBuilder.FailureNotice(failures, total);
            progress?.Report($"警告：{failures}/{total} 段整理失败，该部分内容缺失");
        }

        if (plan.OversizedChars > 0)
            merged = merged.TrimEnd() + "\n\n" + NotePromptBuilder.OversizedNotice(plan.OversizedChars);

        return new NoteGenerationResult(merged, total, failures, plan.OversizedChars);
    }

    /// <summary>
    /// 结构化合并：**后端确定性去重 → 一次全局排序 + 连贯性润色 → 逐条回校 → 拼装**。
    ///
    /// 与"把各段 Markdown 丢给模型来回合并"相比，这条路径的每一步都是可验证的：
    /// 去重只合并完全一致的条目（零静默丢失）；润色稿必须逐条回校，任一条正文对不上就整体退回
    /// 确定性拼装。模型无法在长输入下悄悄删条、揉条或改写正文而不被发现。
    /// </summary>
    private async Task<NoteGenerationResult> ComposeFromItemsAsync(
        string course, IReadOnlyList<NotePromptBuilder.NoteItem> rawItems,
        IProgress<string>? progress, int failures, int total, int oversizedChars)
    {
        var dedup = NotePromptBuilder.Deduplicate(rawItems);
        var items = dedup.Items;
        if (dedup.MergedCount > 0)
            progress?.Report($"已按标题去重：合并 {dedup.MergedCount} 条完全重复内容，剩余 {items.Count} 条");

        string markdown;
        if (items.Count == 0)
        {
            markdown = NotePromptBuilder.AssembleFromItems(course, items);
        }
        else
        {
            progress?.Report($"正在做全局排序与连贯性润色（{items.Count} 条）…");
            bool polished = false;
            markdown = "";
            try
            {
                var rawPlan = await ChatWithFallbackAsync(
                    NotePromptBuilder.FinalPassSystemPrompt,
                    NotePromptBuilder.BuildFinalPassPrompt(course, items),
                    jsonMode: true);

                var plan = NotePromptBuilder.ParseFinalPlan(rawPlan, items.Count);
                if (plan != null)
                {
                    // 逐条回校：模型改写/丢弃任何一条正文都会被这里拦下
                    var report = NoteItemVerifier.Verify(plan.Select(p => p.Body).ToList(), items);
                    if (report.AllVerified)
                    {
                        markdown = NotePromptBuilder.AssembleFromPolished(course, items, plan);
                        polished = true;
                    }
                    else
                    {
                        progress?.Report("润色稿未通过完整性校验，已改用按原条目拼装（内容未受影响）");
                        markdown = NotePromptBuilder.AssembleFromItems(course, items)
                                   + "\n\n" + NoteItemVerifier.FallbackNotice(items.Count, report);
                    }
                }
                else
                {
                    progress?.Report("润色输出格式不完整，已改用按原条目拼装");
                }
            }
            catch (Exception ex)
            {
                // 配置类错误仍然要抛：否则整个功能看起来"完成"了却什么都没生成
                if (IsConfigurationError(ex)) throw;
                progress?.Report($"润色失败（{ShortError(ex.Message)}），已改用按原条目拼装");
            }

            if (!polished && string.IsNullOrWhiteSpace(markdown))
                markdown = NotePromptBuilder.AssembleFromItems(course, items);
        }

        if (failures > 0)
        {
            markdown = markdown.TrimEnd() + "\n\n" + NotePromptBuilder.FailureNotice(failures, total);
            progress?.Report($"警告：{failures}/{total} 段整理失败，该部分内容缺失");
        }
        if (oversizedChars > 0)
            markdown = markdown.TrimEnd() + "\n\n" + NotePromptBuilder.OversizedNotice(oversizedChars);

        return new NoteGenerationResult(markdown, total, failures, oversizedChars);
    }

    /// <summary>失败段的占位 JSON：合法但为空，表示"这一段没有可用内容"。</summary>
    private const string EmptySegmentJson = "{\"coreKnowledge\":\"\",\"pitfalls\":\"\"}";

    /// <summary>
    /// 逐级合并：一组合并成一份，组内结果再合并，直到只剩一份。
    /// 轮次上限被触发时返回 null，由调用方退回机械拼接——绝不能只交回其中一份而丢掉其余分段。
    /// </summary>
    private async Task<string?> MergeCascadeAsync(string course, IReadOnlyList<string> segmentJsons,
        string? transcriptExcerpt, string? ocrExcerpt, bool multiSource = false)
    {
        // 只有一份输入时没有可"合并"的对象：直接装配成笔记。
        // 绝不能把这一份**原始分段 JSON** 当结果返回——那会原样变成笔记正文
        // （调用方的 CleanMarkdown 只剥 Markdown 代码块，不解析 JSON）。
        if (segmentJsons.Count == 1)
        {
            var (core, pitfalls) = NotePromptBuilder.ParseSegmentJson(segmentJsons[0]);
            return NotePromptBuilder.AssembleNote(course, new[] { (core, pitfalls) });
        }

        var current = new List<string>(segmentJsons);
        bool firstRound = true;
        int guard = 0;
        // 段数上限 40、每组 3 段时需 3 轮；留足余量即可，超过就说明出现异常输入
        const int maxRounds = 24;

        while (current.Count > 1)
        {
            if (guard++ >= maxRounds) return null;

            // 只有一轮合并时把原素材摘录带上（用于查漏补缺）；多轮时后续轮次不再重复附带
            var excerpt = firstRound ? transcriptExcerpt : null;
            var excerptOcr = firstRound ? ocrExcerpt : null;
            var next = new List<string>();

            if (current.Count <= NoteSegmenter.DefaultMergeGroupSize)
            {
                next.Add(await ChatWithFallbackAsync(
                    NotePromptBuilder.MergeSystemPromptFor(multiSource),
                    NotePromptBuilder.BuildMergePrompt(course, current, excerpt, excerptOcr, multiSource)));
            }
            else
            {
                for (int i = 0; i < current.Count; i += NoteSegmenter.DefaultMergeGroupSize)
                {
                    var group = current.Skip(i).Take(NoteSegmenter.DefaultMergeGroupSize).ToList();
                    var text = await ChatWithFallbackAsync(
                        NotePromptBuilder.MergeSystemPromptFor(multiSource),
                        NotePromptBuilder.BuildMergePrompt(course, group, excerpt, excerptOcr, multiSource));
                    next.Add(NotePromptBuilder.CleanMarkdown(text));
                }
            }

            firstRound = false;
            current = next;
        }

        return current.Count > 0 ? current[0] : null;
    }

    private static string FormatSegmentProgress(string prefix, int index, int total, string? hint)
    {
        string text = $"{prefix} {index}/{total} 段…";
        if (!string.IsNullOrEmpty(hint)) text += $"（{hint}）";
        return text;
    }

    /// <summary>取素材首尾各一半作为校对摘录：中段内容由分段 JSON 覆盖，首尾最容易被合并阶段漏掉。</summary>
    private static string BuildExcerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length <= MergeExcerptChars) return text;

        int half = MergeExcerptChars / 2;
        return text[..half] + "\n……（中间略）……\n" + text[^half..];
    }

    /// <summary>
    /// 检查 v1 接口健康：GET {base}/v1/models（OpenAI 兼容标准端点，本地 LLM 服务如
    /// llama.cpp / Ollama / LM Studio / vLLM 均支持）。任何 HTTP 响应都视为服务可达
    /// （401 等状态码说明服务在线但鉴权/密钥有问题）；网络错误或超时才视为不可达。
    /// apiKey / baseUrl 为空时使用已保存的设置（设置窗口可传入未保存的界面值先行测试）。
    /// </summary>
    public async Task<LlmHealthResult> CheckHealthAsync(string? apiKey = null, string? baseUrl = null)
    {
        var s = AppSettings.Instance.Snapshot();
        apiKey ??= s.LlmApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new LlmHealthResult(false, "未配置 API Key", IsConfigured: false);

        var endpoint = BuildModelsEndpoint(string.IsNullOrWhiteSpace(baseUrl) ? s.LlmBaseUrl : baseUrl);

        // 健康检查由主页每 30 秒轮询一次：旧实现每次都新建 HttpClient，
        // 等于每 30 秒白做一次 TCP+TLS 握手（还留下 TIME_WAIT 的 socket）。改用共享连接。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            using var response = await SharedClient.SendAsync(request, cts.Token);
            int status = (int)response.StatusCode;
            string detail = status == 200 ? "服务正常" : $"服务可达（HTTP {status}）";
            return new LlmHealthResult(true, detail);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new LlmHealthResult(false, $"连接超时（{HealthCheckTimeoutSeconds}s）");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            return new LlmHealthResult(false, "服务不可达：" + ShortError(ex.Message));
        }
    }

    /// <summary>
    /// 拉取该供应商的可用模型（GET {base}/v1/models）。填好地址与 Key 后由设置界面调用，
    /// 把"模型名称"从手输改为下拉选择。
    /// </summary>
    public async Task<LlmModelsResult> ListModelsAsync(string? apiKey = null, string? baseUrl = null)
    {
        var s = AppSettings.Instance.Snapshot();
        var key = string.IsNullOrWhiteSpace(apiKey) ? s.LlmApiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
            return new LlmModelsResult(Array.Empty<string>(), "请先填写 API Key", IsOk: false);

        var resolvedBase = string.IsNullOrWhiteSpace(baseUrl) ? s.LlmBaseUrl : baseUrl;
        if (string.IsNullOrWhiteSpace(resolvedBase))
            return new LlmModelsResult(Array.Empty<string>(), "请先填写 API 基础地址", IsOk: false);

        var endpoint = BuildModelsEndpoint(resolvedBase);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModelListTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var response = await SharedClient.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string hint = status switch
                {
                    401 or 403 => "API Key 无效或无权访问该地址",
                    404 => "该地址没有 /v1/models 接口",
                    429 => "请求过于频繁，请稍后再试",
                    _ => "供应商返回 HTTP " + status,
                };
                return new LlmModelsResult(Array.Empty<string>(), $"获取模型列表失败：{hint}", IsOk: false);
            }

            var models = ParseModelIds(body);
            if (models.Count == 0)
                return new LlmModelsResult(models, "该地址未返回任何模型，请手动填写模型名称", IsOk: false);

            IReadOnlyList<string> shown = models.Count > MaxModelCount
                ? models.Take(MaxModelCount).ToList()
                : models;
            var suffix = models.Count > MaxModelCount ? $"（仅列出前 {MaxModelCount} 个）" : "";
            return new LlmModelsResult(shown, $"已获取 {models.Count} 个可用模型{suffix}", IsOk: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new LlmModelsResult(Array.Empty<string>(),
                $"获取模型列表超时（{ModelListTimeoutSeconds}s）", IsOk: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            return new LlmModelsResult(Array.Empty<string>(),
                "服务不可达：" + ShortError(ex.Message), IsOk: false);
        }
    }

    /// <summary>
    /// 解析 OpenAI 兼容的 /v1/models 响应，取出模型 ID（去重 + 不区分大小写排序）。
    /// 单独抽出来是为了能脱离网络做单元测试。
    /// </summary>
    public static List<string> ParseModelIds(string? json)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return models;
        try
        {
            var parsed = JsonConvert.DeserializeObject<ModelListResponse>(json);
            if (parsed?.Data == null)
                return models;
            foreach (var item in parsed.Data)
            {
                var id = item?.Id?.Trim();
                if (!string.IsNullOrEmpty(id))
                    models.Add(id);
            }
        }
        catch
        {
            return new List<string>(); // 非 JSON / 结构不符：按"没有可用模型"处理
        }
        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>拼 /v1/models 端点：地址已带 /v1 时不重复追加。</summary>
    private static string BuildModelsEndpoint(string? baseUrl)
    {
        var resolved = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        return resolved.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? resolved + "/models"
            : resolved + "/v1/models";
    }

    private (string Key, string BaseUrl, string Model) GetPrimaryConfig()
    {
        var s = AppSettings.Instance.Snapshot();
        return (s.LlmApiKey, s.LlmBaseUrl, s.LlmModel);
    }

    /// <summary>生成笔记：主配置一次调用（内部已按 429/5xx/网络异常指数退避重试）。</summary>
    private async Task<string> ChatWithFallbackAsync(string system, string user, bool jsonMode = false)
    {
        var (key, baseUrl, model) = GetPrimaryConfig();
        return await ChatAsync(key, baseUrl, model, system, user, jsonMode);
    }

    private async Task<string> ChatAsync(string apiKey, string baseUrl, string model,
        string system, string user, bool jsonMode = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LlmCallException("尚未配置 LLM API Key，请在「设置」中配置。",
                isTransient: false, isConfigurationError: true);

        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
        model = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model;

        var endpoint = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/chat/completions"
            : baseUrl + "/v1/chat/completions";

        // JSON 模式偶发返回空内容（服务端已知问题）：给出可操作的提示，由调用方决定是否重试
        string emptyMessage = jsonMode
            ? "LLM 返回空内容（JSON 模式偶发，可重新生成）"
            : "LLM 返回空内容";

        var payload = new ChatRequest
        {
            Model = model,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = system },
                new() { Role = "user", Content = user },
            },
            Temperature = 0.3,
            // 分段整理用 JSON 输出：模型少写解释文字，直接省掉一轮"清洗输出"的开销。
            // 本地推理服务可能不认 response_format，故 jsonMode 为 false 时整个字段不发送
            // （发送 null 有服务端直接 400 的风险），解析层也仍做容错。
            ResponseFormat = jsonMode ? new ResponseFormat { Type = "json_object" } : null,
            // JSON 模式下必须显式给 max_tokens，否则输出可能被截断成不完整 JSON（DeepSeek 文档要求）
            MaxTokens = jsonMode ? NotePromptBuilder.MaxOutputTokens : null,
        };

        // 超时按用户设置（本地模型推理慢，默认 30 分钟）。
        // 客户端是进程级共享的（见 SharedClient），所以超时不能挂在客户端上，
        // 改成每次请求自己的 CTS：既保留"每次调用独立超时"的语义，又不再为每次调用重建连接池。
        var timeoutSeconds = AppSettings.Instance.Snapshot().LlmTimeoutSeconds;
        if (timeoutSeconds <= 0) timeoutSeconds = DefaultTimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            // 瞬时性故障（429 / 5xx / 网络异常）指数退避重试，避免一次抖动就丢失 AI 整理能力
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    request.Content = new StringContent(
                        JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                    var response = await SharedClient.SendAsync(request, timeoutCts.Token);
                    var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<ChatResponse>(body);
                        var content = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
                        if (string.IsNullOrWhiteSpace(content))
                            throw new LlmCallException(emptyMessage, isTransient: true);
                        return content;
                    }

                    int status = (int)response.StatusCode;
                    bool transient = status == 429 || status >= 500;
                    if (transient && attempt < maxAttempts)
                    {
                        await Task.Delay(BackoffDelay(attempt));
                        continue;
                    }

                    // 429/5xx 到这里说明重试已用尽（瞬时）；其余 4xx 是请求本身的问题，
                    // 再试也不会变——两者都用 IsTransient 如实标注，由上层决定要不要重试
                    throw new LlmCallException(
                        "LLM API 调用失败 (" + status + "): " + Truncate(body, 300), isTransient: transient);
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    await Task.Delay(BackoffDelay(attempt));
                    // 继续下一轮重试
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // 请求级超时触发（.NET 8 抛 TaskCanceledException/OperationCanceledException）：
            // 超时不重试，直接给出明确提示
            throw new TimeoutException(
                $"LLM 响应超时（已等待 {timeout.TotalMinutes:0.#} 分钟）。" +
                "本地模型推理较慢时，请在「设置」中调大“请求超时（秒）”，或检查 v1 接口是否正常（主页右上角有服务状态）。");
        }
    }

    /// <summary>指数退避：500ms → 1000ms → 2000ms（封顶 5s）。</summary>
    private static TimeSpan BackoffDelay(int attempt)
    {
        int ms = Math.Min(500 << (Math.Max(0, attempt - 1)), 5000);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// 截断长文本用于错误提示（<paramref name="max"/> 必填：留个用不上的默认值只会掩盖调用点的意图）。
    /// </summary>
    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    /// <summary>
    /// 瞬时故障判定：只有这些错误"重试一次"才有意义。
    /// 其余（4xx、配置缺失）重试不会改善结果，只是白打一次 API。
    /// </summary>
    internal static bool IsTransientError(Exception ex) => ex switch
    {
        LlmCallException call => call.IsTransient,
        HttpRequestException => true, // 网络抖动
        TimeoutException => true,     // 请求超时（用户可配置）
        _ => false,
    };

    /// <summary>
    /// 配置类错误（例如未填 API Key）：每一段都会失败，应当立即整体失败，
    /// 而不是为每一段都重试一遍再堆出一篇失败占位符。
    /// </summary>
    internal static bool IsConfigurationError(Exception ex)
        => ex is LlmCallException { IsConfigurationError: true };

    private static string ShortError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知错误";
        // 只取第一行，避免把底层堆栈细节糊到 UI 上
        int nl = message.IndexOfAny(new[] { '\r', '\n' });
        var first = nl >= 0 ? message[..nl] : message;
        return first.Length > 120 ? first[..120] : first;
    }

    /// <summary>POST /v1/chat/completions 的请求体。</summary>
    private sealed class ChatRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; } = "";

        [JsonProperty("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonProperty("temperature")]
        public double Temperature { get; set; }

        /// <summary>jsonMode 为 false 时整个字段不序列化（见 ChatAsync 注释）。</summary>
        [JsonProperty("response_format", NullValueHandling = NullValueHandling.Ignore)]
        public ResponseFormat? ResponseFormat { get; set; }

        /// <summary>jsonMode 为 false 时不下发，沿用服务端默认值。</summary>
        [JsonProperty("max_tokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxTokens { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; } = "";

        [JsonProperty("content")]
        public string Content { get; set; } = "";
    }

    private sealed class ResponseFormat
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "json_object";
    }

    private sealed class ChatResponse
    {
        [JsonProperty("choices")]
        public List<Choice>? Choices { get; set; }
    }

    /// <summary>GET /v1/models 的响应体（OpenAI 兼容：{"data":[{"id":"..."}]}）。</summary>
    private sealed class ModelListResponse
    {
        [JsonProperty("data")]
        public List<ModelEntry>? Data { get; set; }
    }

    private sealed class ModelEntry
    {
        [JsonProperty("id")]
        public string? Id { get; set; }
    }

    private sealed class Choice
    {
        [JsonProperty("message")]
        public Message? Message { get; set; }
    }

    private sealed class Message
    {
        [JsonProperty("content")]
        public string? Content { get; set; }
    }
}

/// <summary>
/// LLM 调用失败。<see cref="IsTransient"/> 与 <see cref="IsConfigurationError"/> 把
/// "重试有没有意义"和"整篇是不是都救不回来"分开表达，让调用方能区别处置：
/// · 瞬时故障（超时/网络/429/5xx/空响应）→ 该段重试一次；
/// · 配置缺失（未填 API Key）→ 立即整体失败，让用户看到"去设置里配置"+原文兜底笔记；
/// · 其余 4xx → 不重试，但只算该段失败，不牵连已经整理好的其它段。
/// </summary>
internal sealed class LlmCallException : Exception
{
    public bool IsTransient { get; }

    public bool IsConfigurationError { get; }

    public LlmCallException(string message, bool isTransient, bool isConfigurationError = false, Exception? inner = null)
        : base(message, inner)
    {
        IsTransient = isTransient;
        IsConfigurationError = isConfigurationError;
    }
}
