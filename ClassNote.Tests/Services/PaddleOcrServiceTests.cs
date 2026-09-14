using System.Net;
using System.Net.Http;
using System.Text;
using ClassNote.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 远程 PaddleOCR 服务的回归测试：请求编码、多形态响应解析、以及"什么情况下必须抛错"。
///
/// 抛错口径是这里的关键：**只有"合法但没有文字"才允许返回空串**，
/// 其余（HTTP 错误、服务报错、结构不认识、连不上）都必须抛异常，
/// 这样上层才能回退内置引擎——静默返回空串等于让整节课的课件文字凭空消失。
/// </summary>
public class PaddleOcrServiceTests
{
    private const string HubservingBody = """
    {"status":"000","msg":"","results":[[
      {"text":"第二行","confidence":0.90,"text_region":[[10,60],[110,60],[110,80],[10,80]]},
      {"text":"第一行","confidence":0.91,"text_region":[[10,10],[110,10],[110,30],[10,30]]},
      {"text":"第一行右","confidence":0.92,"text_region":[[130,12],[230,12],[230,32],[130,32]]}
    ]]}
    """;

    // ── 请求编码 ─────────────────────────────────────────────

    [Fact]
    public async Task RecognizeAsync_JsonBase64_SendsImagesArrayAndReturnsText()
    {
        var handler = new StubHandler(_ => Json(HubservingBody));
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            OcrRequestFormat.JsonBase64, client: new HttpClient(handler));

        var text = await service.RecognizeAsync(new byte[] { 1, 2, 3 });

        // 阅读顺序由坐标还原：同一行按 X，下一行在后
        Assert.Equal("第一行\n第一行右\n第二行", text);

        var payload = JObject.Parse(handler.Bodies[0]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), (string?)payload["images"]![0]);
        Assert.Equal("application/json", handler.Requests[0].Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task RecognizeAsync_PaddleXServing_SendsFileFieldAsBase64()
    {
        // PaddleOCR 3.x / PaddleX serving 的官方示例：JSON + base64 的 file 字段（fileType 1 = 图片）
        var handler = new StubHandler(_ => Json("""{"result":{"txts":["甲","乙"]}}"""));
        var service = new PaddleOcrService("http://10.0.0.5:8080/ocr",
            OcrRequestFormat.PaddleXServing, client: new HttpClient(handler));

        var text = await service.RecognizeAsync(new byte[] { 9, 8, 7 });

        Assert.Equal("甲\n乙", text);
        var payload = JObject.Parse(handler.Bodies[0]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 9, 8, 7 }), (string?)payload["file"]);
        Assert.Equal(1, (int?)payload["fileType"]);
        // 必须显式关掉可视化：否则每张截图都会回传一张标注图（base64），白占带宽
        Assert.False((bool?)payload["visualize"]);
        Assert.Equal("application/json", handler.Requests[0].Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void TryReadError_PaddleXSuccessEnvelope_IsNotTreatedAsError()
    {
        // PaddleX 成功时 errorCode=0 且 errorMsg="Success"：不能因为 errorMsg 非空就报错
        const string ok = """
        {"logId":"x","errorCode":0,"errorMsg":"Success","result":{"ocrResults":[]}}
        """;

        Assert.Null(PaddleOcrResponseParser.TryReadError(ok));
    }

    [Fact]
    public async Task RecognizeAsync_WithApiKey_SendsBearerToken()
    {
        var handler = new StubHandler(_ => Json(HubservingBody));
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            apiKey: "secret-token", client: new HttpClient(handler));

        await service.RecognizeAsync(new byte[] { 1 });

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    // ── 解析兼容性 ───────────────────────────────────────────

    [Fact]
    public void Parse_PaddleXPrunedResult_ReadsRecTexts()
    {
        const string body = """
        {"logId":"abc","result":{"ocrResults":[{"prunedResult":{
          "rec_texts":["动 态 规 划","DP"],
          "rec_scores":[0.99,0.98],
          "rec_polys":[[[0,0],[50,0],[50,20],[0,20]],[[60,0],[100,0],[100,20],[60,20]]]
        }}]}}
        """;

        var text = PaddleOcrResponseParser.Reconstruct(PaddleOcrResponseParser.Parse(body));

        // 解析层只负责取文本与排序，不做规范化（规范化是 OcrTextNormalizer 的职责）
        Assert.Equal("动 态 规 划\nDP", text);
    }

    [Fact]
    public void Parse_GenericObjectsWithTextField_ArePickedUp()
    {
        const string body = """{"foo":[{"text":"X"},{"text":"Y"}]}""";

        Assert.Equal("X\nY", PaddleOcrResponseParser.Reconstruct(PaddleOcrResponseParser.Parse(body)));
    }

    [Fact]
    public void Parse_WithoutBoxes_KeepsServiceOrder()
    {
        const string body = """{"result":{"txts":["先","后"]}}""";

        Assert.Equal("先\n后", PaddleOcrResponseParser.Reconstruct(PaddleOcrResponseParser.Parse(body)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Parse_UnusableInput_ReturnsNothing(string? body)
    {
        Assert.Empty(PaddleOcrResponseParser.Parse(body));
    }

    [Theory]
    [InlineData("""{"status":"100","msg":"image decode failed"}""", "image decode failed")]
    [InlineData("""{"errorCode":336,"errorMsg":"bad token"}""", "bad token")]
    public void TryReadError_ExtractsServerSideErrors(string body, string expectedFragment)
    {
        Assert.Contains(expectedFragment, PaddleOcrResponseParser.TryReadError(body));
    }

    [Fact]
    public void TryReadError_SuccessStatus_IsNotAnError()
    {
        Assert.Null(PaddleOcrResponseParser.TryReadError("""{"status":"000","msg":"","results":[[]]}"""));
    }

    // ── 抛错口径 ─────────────────────────────────────────────

    [Fact]
    public async Task RecognizeAsync_HttpError_ThrowsSoCallerCanFallBack()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            client: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(new byte[] { 1 }));
        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task RecognizeAsync_ServerReportedError_Throws()
    {
        var handler = new StubHandler(_ => Json("""{"status":"100","msg":"image decode failed"}"""));
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            client: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(new byte[] { 1 }));
        Assert.Contains("image decode failed", ex.Message);
    }

    [Fact]
    public async Task RecognizeAsync_UnparseableBody_ThrowsInsteadOfLookingBlank()
    {
        // 最危险的情形：请求方式和部署方式对不上，返回一坨 HTML。
        // 必须抛错（→ 回退内置引擎），绝不能当成"这张图没有文字"。
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>404 Not Found</body></html>", Encoding.UTF8, "text/html"),
        });
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            client: new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task RecognizeAsync_LegitimatelyEmptyResult_ReturnsEmptyWithoutThrowing()
    {
        // 空白幻灯片是合法输入：不能用"抛错+回退"把它变成一次多余的本地识别
        var handler = new StubHandler(_ => Json("""{"status":"000","msg":"","results":[[]]}"""));
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            client: new HttpClient(handler));

        Assert.Equal("", await service.RecognizeAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task RecognizeAsync_UnusableUrl_ThrowsWithoutSendingRequest()
    {
        var handler = new StubHandler(_ => Json(HubservingBody));
        var service = new PaddleOcrService("", client: new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(new byte[] { 1 }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RecognizeAsync_EmptyImage_ReturnsEmptyWithoutSendingRequest()
    {
        var handler = new StubHandler(_ => Json(HubservingBody));
        var service = new PaddleOcrService("http://10.0.0.5:8868/predict/ocr_system",
            client: new HttpClient(handler));

        Assert.Equal("", await service.RecognizeAsync(Array.Empty<byte>()));
        Assert.Empty(handler.Requests);
    }

    // ── 测试替身 ─────────────────────────────────────────────

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content != null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return _responder(request);
        }
    }
}
