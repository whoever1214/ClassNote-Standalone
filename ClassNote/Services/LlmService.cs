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
    /// <summary>生成课堂笔记（Markdown）。</summary>
    Task<string> GenerateNoteAsync(string course, string transcript, string ocrText, IProgress<string>? progress = null);

    /// <summary>检查 OpenAI 兼容 API（v1 接口）健康状态（GET /v1/models）。</summary>
    Task<LlmHealthResult> CheckHealthAsync(string? apiKey = null, string? baseUrl = null);
}

/// <summary>LLM 服务健康检查结果。</summary>
/// <param name="IsOk">服务是否可达（HTTP 有响应即视为可达；未配置 Key 时为 false）。</param>
/// <param name="Message">面向用户的说明文字。</param>
/// <param name="IsConfigured">是否已配置 API Key（未配置时网络检查会跳过）。</param>
public sealed record LlmHealthResult(bool IsOk, string Message, bool IsConfigured = true);

public sealed class LlmService : ILlmService
{
    /// <summary>默认请求超时（秒）：本地模型推理慢，默认 30 分钟。</summary>
    public const int DefaultTimeoutSeconds = 1800;

    /// <summary>健康检查超时（秒）。</summary>
    private const int HealthCheckTimeoutSeconds = 10;

    private const string NoteSystemPrompt =
        "你是一名资深助教，负责把课堂录音转写和课件截图 OCR 内容整理成一份结构清晰、重点突出的课堂笔记。请用 Markdown 输出。\n\n" +
        "整理原则：\n" +
        "1. 以知识点为核心：优先提炼并整理本节课的核心知识点、概念定义、公式定理、关键结论与易错点，按知识逻辑分层组织。\n" +
        "2. 忽略非教学内容：课堂开头的寒暄问候、点名、课堂管理、设备调试等与知识点无关的白话一律不写入笔记。\n" +
        "3. 保留有教学价值的内容：典型例题与解析、解题步骤、师生问答（问什么、答什么）、老师强调的重点与考试提示要完整保留。\n" +
        "4. 控制篇幅、避免流水账：宁精勿滥，同一话题合并归纳；重要程度越高越靠前。";

    public async Task<string> GenerateNoteAsync(string course, string transcript, string ocrText, IProgress<string>? progress = null)
    {
        progress?.Report("正在生成笔记…");
        var user = BuildNotePrompt(course, transcript, ocrText);
        return await ChatWithFallbackAsync(NoteSystemPrompt, user);
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
            return new LlmHealthResult(false, "未配置 API Key，无法检测 LLM 服务", IsConfigured: false);

        var resolvedBase = string.IsNullOrWhiteSpace(baseUrl) ? s.LlmBaseUrl : baseUrl;
        resolvedBase = string.IsNullOrWhiteSpace(resolvedBase) ? "https://api.deepseek.com" : resolvedBase.TrimEnd('/');
        var endpoint = resolvedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? resolvedBase + "/models"
            : resolvedBase + "/v1/models";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(HealthCheckTimeoutSeconds) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            using var response = await client.SendAsync(request);
            int status = (int)response.StatusCode;
            string detail = status == 200 ? "服务正常" : $"服务可达（HTTP {status}）";
            return new LlmHealthResult(true, detail);
        }
        catch (TaskCanceledException)
        {
            return new LlmHealthResult(false, $"连接超时（{HealthCheckTimeoutSeconds} 秒无响应）");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            return new LlmHealthResult(false, "服务不可达：" + ShortError(ex.Message));
        }
    }

    private (string Key, string BaseUrl, string Model) GetPrimaryConfig()
    {
        var s = AppSettings.Instance.Snapshot();
        return (s.LlmApiKey, s.LlmBaseUrl, s.LlmModel);
    }

    /// <summary>
    /// 备用配置（可选）：仅当备用 Key 与备用地址都非空时才生效；模型沿用主配置。
    /// </summary>
    private (string? Key, string? BaseUrl, string Model) GetFallbackConfig()
    {
        var s = AppSettings.Instance.Snapshot();
        return (string.IsNullOrWhiteSpace(s.LlmFallbackApiKey) ? null : s.LlmFallbackApiKey,
                string.IsNullOrWhiteSpace(s.LlmFallbackBaseUrl) ? null : s.LlmFallbackBaseUrl,
                s.LlmModel);
    }

    /// <summary>
    /// 先走主配置；主配置失败（ChatAsync 内部已按 429/5xx/网络异常重试后仍失败）时，
    /// 若配置了备用 Key/地址则再尝试一次，仍失败则抛出主配置错误（更贴近根因）。
    /// </summary>
    private async Task<string> ChatWithFallbackAsync(string system, string user)
    {
        var (key, baseUrl, model) = GetPrimaryConfig();
        try
        {
            return await ChatAsync(key, baseUrl, model, system, user);
        }
        catch (Exception primaryEx)
        {
            var (fbKey, fbBaseUrl, fbModel) = GetFallbackConfig();
            if (fbKey == null || fbBaseUrl == null)
                throw; // 无备用配置

            try
            {
                return await ChatAsync(fbKey, fbBaseUrl, fbModel, system, user);
            }
            catch
            {
                throw primaryEx;
            }
        }
    }

    private async Task<string> ChatAsync(string apiKey, string baseUrl, string model,
        string system, string user)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("尚未配置 LLM API Key，请在「设置」中配置。");

        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.deepseek.com" : baseUrl.TrimEnd('/');
        model = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model;

        var endpoint = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/chat/completions"
            : baseUrl + "/v1/chat/completions";

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = 0.3,
        };

        // 超时按用户设置（本地模型推理慢，默认 30 分钟），每次调用独立 HttpClient
        var timeoutSeconds = AppSettings.Instance.Snapshot().LlmTimeoutSeconds;
        if (timeoutSeconds <= 0) timeoutSeconds = DefaultTimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var client = new HttpClient { Timeout = timeout };
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

                    var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<ChatResponse>(body);
                        var content = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
                        if (string.IsNullOrWhiteSpace(content))
                            throw new InvalidOperationException("LLM 返回空内容");
                        return content;
                    }

                    int status = (int)response.StatusCode;
                    bool transient = status == 429 || status >= 500;
                    if (transient && attempt < maxAttempts)
                    {
                        await Task.Delay(BackoffDelay(attempt));
                        continue;
                    }

                    throw new InvalidOperationException("LLM API 调用失败 (" + status + "): " + Truncate(body, 300));
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    await Task.Delay(BackoffDelay(attempt));
                    // 继续下一轮重试
                }
            }
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout 触发（.NET 8 抛 TaskCanceledException）：超时不重试，直接给出明确提示
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

    private static string BuildNotePrompt(string course, string transcript, string ocrText)
    {
        var sb = new StringBuilder();
        sb.Append("课程：").Append(course).AppendLine();
        sb.AppendLine();
        sb.AppendLine("【课堂录音转写】");
        sb.AppendLine(Truncate(transcript, 12000));
        sb.AppendLine();
        sb.AppendLine("【课件/板书截图 OCR】");
        sb.AppendLine(Truncate(ocrText, 8000));
        sb.AppendLine();
        sb.AppendLine("请按以下要求整理成课堂笔记（Markdown）：");
        sb.AppendLine("- 推荐结构：# 课程标题 → ## 核心知识点 → ## 例题与解析 → ## 重点与易错点 → ## 师生问答；");
        sb.AppendLine("- 只记录与知识点相关的内容：课堂开头的寒暄、点名、闲聊、课堂管理等与课程无关的片段一律忽略，不要写入笔记；");
        sb.AppendLine("- 核心知识点、定义、公式、结论优先提炼并靠前排布，重要内容可标注「重点」；");
        sb.AppendLine("- 典型例题与解题步骤、师生问答（问什么、答什么）完整保留；");
        sb.AppendLine("- 无实际教学内容的片段直接省略，不要照搬转写原文，宁精勿滥。");
        return sb.ToString();
    }

    private static string Truncate(string s, int max = 20000)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private static string ShortError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知错误";
        // 只取第一行，避免把底层堆栈细节糊到 UI 上
        int nl = message.IndexOfAny(new[] { '\r', '\n' });
        var first = nl >= 0 ? message[..nl] : message;
        return first.Length > 120 ? first[..120] : first;
    }

    private sealed class ChatResponse
    {
        [JsonProperty("choices")]
        public List<Choice>? Choices { get; set; }
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
