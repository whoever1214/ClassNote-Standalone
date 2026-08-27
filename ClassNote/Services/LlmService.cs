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
}

public sealed class LlmService : ILlmService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const string NoteSystemPrompt =
        "你是一名资深助教，负责将课堂录音转写和课件截图 OCR 内容整理成一份结构清晰的课堂笔记。用 Markdown 输出。";

    public async Task<string> GenerateNoteAsync(string course, string transcript, string ocrText, IProgress<string>? progress = null)
    {
        progress?.Report("正在生成笔记…");
        var user = BuildNotePrompt(course, transcript, ocrText);
        return await ChatWithFallbackAsync(NoteSystemPrompt, user);
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

                var response = await Http.SendAsync(request);
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
        sb.AppendLine("请整理成课堂笔记，包含：核心知识点、例题、重点标注、师生问答。用 Markdown 输出。");
        return sb.ToString();
    }

    private static string Truncate(string s, int max = 20000)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

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
