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

    /// <summary>生成思维导图（JSON 层级数据）。</summary>
    Task<object?> GenerateMindmapAsync(string course, string transcript, IProgress<string>? progress = null);
}

public sealed class LlmService : ILlmService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const string NoteSystemPrompt =
        "你是一名资深助教，负责将课堂录音转写和课件截图 OCR 内容整理成一份结构清晰的课堂笔记。用 Markdown 输出。";
    private const string MindmapSystemPrompt =
        "你是一名知识整理助手，请根据课堂内容提炼知识点，输出 JSON 层级结构供思维导图使用。只输出 JSON，不要任何额外文字。";

    public async Task<string> GenerateNoteAsync(string course, string transcript, string ocrText, IProgress<string>? progress = null)
    {
        progress?.Report("正在生成笔记…");
        var user = BuildNotePrompt(course, transcript, ocrText);
        var (key, baseUrl, model) = GetPrimaryConfig();
        return await ChatAsync(key, baseUrl, model, NoteSystemPrompt, user);
    }

    public async Task<object?> GenerateMindmapAsync(string course, string transcript, IProgress<string>? progress = null)
    {
        progress?.Report("正在生成思维导图…");
        var sb = new StringBuilder();
        sb.Append("课程：").Append(course).AppendLine();
        sb.AppendLine("课堂内容（转写）：");
        sb.AppendLine(Truncate(transcript));
        sb.AppendLine();
        sb.AppendLine("请提炼知识点，输出 JSON 层级结构，格式示例：");
        sb.AppendLine("{ \"name\": \"主题\", \"children\": [ { \"name\": \"子主题\", \"children\": [] } ] }");
        var user = sb.ToString();

        var (key, baseUrl, model) = GetPrimaryConfig();
        var json = await ChatAsync(key, baseUrl, model, MindmapSystemPrompt, user);

        json = ExtractJson(json);
        try
        {
            return JsonConvert.DeserializeObject(json);
        }
        catch
        {
            return null;
        }
    }

    private (string Key, string BaseUrl, string Model) GetPrimaryConfig()
    {
        var s = AppSettings.Instance.Snapshot();
        return (s.LlmApiKey, s.LlmBaseUrl, s.LlmModel);
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

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Content = new StringContent(
            JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("LLM API 调用失败 (" + (int)response.StatusCode + "): " + Truncate(body, 300));

        var result = JsonConvert.DeserializeObject<ChatResponse>(body);
        var content = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM 返回空内容");
        return content;
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

    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "{}";
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text.Substring(start, end - start + 1);
        return text;
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
