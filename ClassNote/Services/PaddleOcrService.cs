using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClassNote.Services;

/// <summary>
/// OCR 返回的一行文字（附带可选的置信度与位置，用于还原阅读顺序）。
/// </summary>
/// <param name="Text">该行文字。</param>
/// <param name="Confidence">置信度（0–1）；协议里没有则为 null。</param>
/// <param name="CenterX">文字框中心 X（像素）；协议里没有则为 null。</param>
/// <param name="CenterY">文字框中心 Y（像素）；协议里没有则为 null。</param>
internal sealed record OcrTextLine(string Text, double? Confidence, double? CenterX, double? CenterY);

/// <summary>
/// 远程 OCR 服务的响应解析：**不联网的纯函数**，这样多形态响应才有回归测试。
///
/// 要兼容三种现实存在的返回体（用户在内网部署哪一种都不该让我们"连得上却识别不出"）：
/// 1. PaddleOCR hubserving：<c>{"results":[[{"text":…,"confidence":…,"text_region":[[x,y],…]}]]}</c>
/// 2. PaddleX / PP-OCRv5 serving：<c>{"result":{"ocrResults":[{"prunedResult":{"rec_texts":[…]}}]}}</c>
/// 3. 其它封装（RapidOCR 服务等）：<c>{"result":{"txts":[…]}}</c>，或任意带 <c>text</c> 字段的对象数组
///
/// 顺序问题：OCR 服务返回的往往是**按检测框面积/置信度**排的，而不是阅读顺序。
/// 因此只要协议给了坐标，就按"行（Y）→ 列（X）"重排；没有坐标才退回原顺序。
/// </summary>
internal static class PaddleOcrResponseParser
{
    /// <summary>同一行内 Y 的容差（像素）：小于它的两个框算同一行。</summary>
    private const double RowTolerance = 12;

    internal static List<OcrTextLine> Parse(string? json)
    {
        var result = new List<OcrTextLine>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        JToken root;
        try
        {
            root = JToken.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (!TryReadHubserving(root, result) &&
            !TryReadPaddleX(root, result) &&
            !TryReadTxtArrays(root, result))
        {
            TryReadGenericObjects(root, result);
        }

        return result;
    }

    /// <summary>把解析出的行按阅读顺序拼成文本；没有坐标时保持服务返回的顺序。</summary>
    internal static string Reconstruct(IEnumerable<OcrTextLine> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (list.Count == 0) return "";

        bool hasBoxes = list.Any(l => l.CenterY.HasValue);
        if (hasBoxes)
        {
            // 先按 Y 分行（容差内视为同一行），行内再按 X 从左到右
            list = list
                .OrderBy(l => Math.Round((l.CenterY ?? 0) / RowTolerance))
                .ThenBy(l => l.CenterX ?? 0)
                .ToList();
        }

        return string.Join("\n", list.Select(l => l.Text.Trim()));
    }

    /// <summary>协议里显式报了错（status != 000 / errorCode != 0）时取出错误信息，用于"抛错以便回退"。</summary>
    internal static string? TryReadError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var root = JToken.Parse(json);

            // PaddleOCR 2.x hubserving：status "000" 才是成功
            string? status = (string?)root["status"];
            if (!string.IsNullOrEmpty(status) && status != "000")
                return $"服务返回 status={status}：{(string?)root["msg"] ?? (string?)root["message"] ?? "无说明"}";

            string? errorMessage = (string?)root["errorMsg"] ?? (string?)root["error_message"] ?? (string?)root["msg"];

            // PaddleOCR 3.x / PaddleX serving：errorCode 0 = 成功、errorMsg = "Success"；
            // 因此**先看 errorCode**，不能因为 errorMsg 非空就当成错误
            var errorCode = root["errorCode"] ?? root["error_code"];
            if (errorCode != null && errorCode.Type != JTokenType.Null)
            {
                string code = errorCode.ToString();
                if (code != "0") return $"服务返回 errorCode={code}：{errorMessage ?? "无说明"}";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage) && root["result"] == null)
                return "服务返回错误：" + errorMessage;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── 协议 1：hubserving ───────────────────────────────────
    private static bool TryReadHubserving(JToken root, List<OcrTextLine> result)
    {
        if (root["results"] is not JArray images) return false;

        foreach (var image in images)
        {
            if (image is not JArray items) continue;
            foreach (var item in items)
            {
                if (item is not JObject obj) continue;
                string? text = (string?)obj["text"] ?? (string?)obj["rec_text"];
                if (string.IsNullOrWhiteSpace(text)) continue;

                var region = obj["text_region"] ?? obj["box"] ?? obj["points"];
                var (cx, cy) = BoxCenter(region);
                result.Add(new OcrTextLine(text.Trim(), ToDouble(obj["confidence"] ?? obj["score"]), cx, cy));
            }
        }
        return result.Count > 0;
    }

    // ── 协议 2：PaddleX / PP-OCRv5 ───────────────────────────
    private static bool TryReadPaddleX(JToken root, List<OcrTextLine> result)
    {
        foreach (var pruned in FindAll(root, "prunedResult"))
        {
            if (pruned["rec_texts"] is not JArray texts) continue;
            var scores = pruned["rec_scores"] as JArray;
            var polys = (pruned["rec_polys"] ?? pruned["dt_polys"]) as JArray;

            for (int i = 0; i < texts.Count; i++)
            {
                string? text = (string?)texts[i];
                if (string.IsNullOrWhiteSpace(text)) continue;

                var (cx, cy) = BoxCenter(At(polys, i));
                result.Add(new OcrTextLine(text.Trim(), At(scores, i)?.Value<double>(), cx, cy));
            }
        }
        return result.Count > 0;
    }

    // ── 协议 3：txts / rec_texts 数组 ────────────────────────
    private static bool TryReadTxtArrays(JToken root, List<OcrTextLine> result)
    {
        foreach (var name in new[] { "txts", "rec_texts", "texts" })
        {
            foreach (var token in FindAll(root, name))
            {
                if (token is not JArray arr) continue;
                foreach (var item in arr)
                {
                    string? text = item.Type == JTokenType.String ? (string?)item : (string?)item["text"];
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(new OcrTextLine(text.Trim(), null, null, null));
                }
                if (result.Count > 0) return true;
            }
        }
        return false;
    }

    // ── 协议 4：通用递归（任何带 text/rec_text 字段的对象） ──
    private static void TryReadGenericObjects(JToken root, List<OcrTextLine> result)
    {
        foreach (var obj in EnumerateObjects(root))
        {
            foreach (var field in new[] { "text", "rec_text", "txt", "content" })
            {
                string? text = (string?)obj[field];
                if (string.IsNullOrWhiteSpace(text)) continue;

                var (cx, cy) = BoxCenter(obj["text_region"] ?? obj["box"] ?? obj["points"] ?? obj["poly"]);
                result.Add(new OcrTextLine(text.Trim(), ToDouble(obj["confidence"] ?? obj["score"]), cx, cy));
                break;
            }
        }
    }

    /// <summary>递归找出所有名为 <paramref name="name"/> 的子节点（先序）。</summary>
    private static IEnumerable<JToken> FindAll(JToken root, string name)
    {
        foreach (var token in SelfAndDescendants(root))
        {
            if (token is JObject obj && obj.TryGetValue(name, out var value))
                yield return value;
        }
    }

    private static IEnumerable<JObject> EnumerateObjects(JToken root)
        => SelfAndDescendants(root).OfType<JObject>();

    /// <summary>
    /// 自己走一遍树：Newtonsoft 的 <c>DescendantsAndSelf</c> 只挂在 <c>JContainer</c> 上，
    /// 对 <c>JToken</c> 静态类型不可用（而响应根的静态类型正是 JToken）。
    /// </summary>
    private static IEnumerable<JToken> SelfAndDescendants(JToken root)
    {
        yield return root;

        switch (root)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                    foreach (var token in SelfAndDescendants(property.Value))
                        yield return token;
                break;
            case JArray array:
                foreach (var item in array)
                    foreach (var token in SelfAndDescendants(item))
                        yield return token;
                break;
        }
    }

    private static JToken? At(JArray? array, int index)
        => array != null && index >= 0 && index < array.Count ? array[index] : null;

    private static double? ToDouble(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        return double.TryParse(token.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// 从坐标结构中取文字框中心：支持 [[x,y],…]（四点多边形）、[x1,y1,x2,y2]、{"x":…,"y":…}。
    /// </summary>
    private static (double? X, double? Y) BoxCenter(JToken? region)
    {
        if (region == null || region.Type == JTokenType.Null) return (null, null);

        if (region is JArray array)
        {
            if (array.Count == 4 && array.All(t => t.Type == JTokenType.Float || t.Type == JTokenType.Integer))
            {
                double x1 = array[0].Value<double>(), y1 = array[1].Value<double>();
                double x2 = array[2].Value<double>(), y2 = array[3].Value<double>();
                return ((x1 + x2) / 2, (y1 + y2) / 2);
            }

            var points = array.OfType<JArray>()
                .Where(p => p.Count >= 2)
                .Select(p => (X: p[0].Value<double>(), Y: p[1].Value<double>()))
                .ToList();
            if (points.Count > 0)
                return (points.Average(p => p.X), points.Average(p => p.Y));

            return (null, null);
        }

        if (region is JObject obj && obj["x"] != null && obj["y"] != null)
        {
            double w = ToDouble(obj["w"] ?? obj["width"]) ?? 0;
            double h = ToDouble(obj["h"] ?? obj["height"]) ?? 0;
            return (obj["x"]!.Value<double>() + w / 2, obj["y"]!.Value<double>() + h / 2);
        }

        return (null, null);
    }
}

/// <summary>
/// 远程 OCR 服务（内网自建 PaddleOCR）。走 OpenAI 之外的第二条外部依赖，因此刻意做窄：
/// · 只认两种请求编码（hubserving JSON+base64 / PaddleX 表单上传），由设置选择；
/// · 返回体解析交给 <see cref="PaddleOcrResponseParser"/>，兼容多种部署；
/// · **失败就抛异常**，由 <see cref="FallbackOcrService"/> 决定要不要回退内置 OCR——
///   绝不能把"服务连不上"伪装成"这张图没有文字"，那会静默丢掉整节课的课件文字。
/// </summary>
public sealed class PaddleOcrService : IOcrService
{
    /// <summary>
    /// 进程级共享 HttpClient（与 LlmService 同一考虑）：一节课上百张截图，
    /// 每次新建客户端等于每次重做 TCP 握手，还会留下一串 TIME_WAIT。
    /// 超时因此挂在每个请求自己的 CTS 上。
    /// </summary>
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
        };
        return new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    private readonly string _url;
    private readonly OcrRequestFormat _requestFormat;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _client;

    /// <param name="url">服务地址，如 http://10.0.0.5:8868/predict/ocr_system。</param>
    /// <param name="requestFormat">请求编码方式，必须与服务器部署方式一致。</param>
    /// <param name="apiKey">可选访问密钥（非空时发 Authorization: Bearer）。</param>
    /// <param name="timeoutSeconds">单张截图的请求超时（秒）。</param>
    /// <param name="client">测试用注入；生产走进程级共享实例。</param>
    public PaddleOcrService(string url, OcrRequestFormat requestFormat = OcrRequestFormat.JsonBase64,
        string? apiKey = null, int timeoutSeconds = OcrEngines.DefaultPaddleTimeoutSeconds,
        HttpClient? client = null)
    {
        _url = (url ?? "").Trim();
        _requestFormat = OcrEngines.NormalizeFormat(requestFormat);
        _apiKey = (apiKey ?? "").Trim();
        _timeout = TimeSpan.FromSeconds(OcrEngines.NormalizeTimeout(timeoutSeconds));
        _client = client ?? SharedClient;
    }

    /// <summary>当前请求格式（设置界面与日志用）。</summary>
    public OcrRequestFormat RequestFormat => _requestFormat;

    /// <summary>服务地址（设置界面与日志用）。</summary>
    public string Url => _url;

    public async Task<string> RecognizeAsync(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0) return "";
        if (!OcrEngines.IsUsableUrl(_url))
            throw new InvalidOperationException("远程 OCR 服务地址未配置或不是合法的 http/https 地址。");

        using var cts = new CancellationTokenSource(_timeout);
        HttpResponseMessage response;
        string body;
        try
        {
            using var request = BuildRequest(imageData);
            response = await _client.SendAsync(request, cts.Token);
            body = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"远程 OCR 请求超时（已等待 {_timeout.TotalSeconds:0} 秒）：{_url}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"远程 OCR 返回 HTTP {(int)response.StatusCode}：{Truncate(body, 200)}");
        }

        string? error = PaddleOcrResponseParser.TryReadError(body);
        if (error != null)
            throw new InvalidOperationException("远程 OCR 服务报错 —— " + error);

        var text = PaddleOcrResponseParser.Reconstruct(PaddleOcrResponseParser.Parse(body));

        // 解析不出任何行，且响应里既没有错误字段也不像空结果：多半是请求格式/协议对不上，
        // 抛错让上层回退（静默返回空串会让用户以为"课件本来就没字"）
        if (text.Length == 0 && !LooksLikeEmptyResult(body))
            throw new InvalidOperationException(
                "远程 OCR 返回了无法解析的内容（请确认设置里的「请求方式」与服务部署方式一致）：" +
                Truncate(body, 200));

        return text;
    }

    private HttpRequestMessage BuildRequest(byte[] imageData)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _url);
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

        if (_requestFormat == OcrRequestFormat.PaddleXServing)
        {
            // PaddleOCR 3.x / PaddleX serving：官方示例就是 JSON + base64（不是表单上传）
            //   payload = {"file": base64, "fileType": 1}   # 1 = 图片
            // visualize=false 很关键：不关的话每张图都会回传一张"画了检测框的 JPEG"（base64），
            // 一节课上百张截图，白占的带宽与解码时间远超识别本身。
            var payload = JsonConvert.SerializeObject(new
            {
                file = Convert.ToBase64String(imageData),
                fileType = 1,
                visualize = false,
            });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }
        else
        {
            // PaddleOCR 2.x hubserving：{"images": ["<base64>"]}（不带 data URI 前缀）
            var payload = JsonConvert.SerializeObject(new { images = new[] { Convert.ToBase64String(imageData) } });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>响应是不是"合法但没有文字"（而不是"结构不认识"）。</summary>
    private static bool LooksLikeEmptyResult(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            var root = JToken.Parse(body);
            // hubserving：results 存在且是空数组 / 内含空数组
            if (root["results"] is JArray results)
                return results.Count == 0 || results.All(r => r is JArray a && a.Count == 0);
            // PaddleX：prunedResult 里 rec_texts 是空数组
            if (root.SelectTokens("$..rec_texts").Any(t => t is JArray a && a.Count == 0))
                return true;
            // 通用：{"result": {"txts": []}}
            if (root.SelectTokens("$..txts").Any(t => t is JArray a && a.Count == 0))
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
