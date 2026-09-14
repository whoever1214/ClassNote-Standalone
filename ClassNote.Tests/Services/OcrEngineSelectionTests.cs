using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// OCR 引擎选择与主备降级的测试。
///
/// 这里锁住两条产品口径：
/// 1. 旧设置文件（没有 OcrEngine 字段）必须仍然用内置引擎——升级不能把用户的识别方式改掉；
/// 2. 远程服务挂掉时**不能每张截图都等一次超时**（一节课 100+ 张 × 20 秒 = 半小时纯等待），
///    因此首次失败后进入冷却期，冷却期内直接走内置引擎。
/// </summary>
public class OcrEngineSelectionTests
{
    // ── 设置读写与默认值 ─────────────────────────────────────

    [Theory]
    [InlineData(null, OcrEngineKind.Windows)]
    [InlineData("", OcrEngineKind.Windows)]
    [InlineData("garbage", OcrEngineKind.Windows)]
    [InlineData("Windows", OcrEngineKind.Windows)]
    [InlineData("PaddleHttp", OcrEngineKind.PaddleHttp)]
    [InlineData("paddlehttp", OcrEngineKind.PaddleHttp)]
    public void FromStorage_UnknownOrLegacyValuesFallBackToBuiltInEngine(string? stored, OcrEngineKind expected)
    {
        Assert.Equal(expected, OcrEngines.FromStorage(stored));
    }

    [Fact]
    public void FromStorage_DefaultsMatchTheEnumNameRoundTrip()
    {
        var defaults = new AppSettingsData();

        Assert.Equal(OcrEngineKind.Windows, OcrEngines.FromStorage(defaults.OcrEngine));
        Assert.Equal(OcrRequestFormat.JsonBase64, OcrEngines.RequestFormatFromStorage(defaults.OcrRequestFormat));
        Assert.Equal(OcrEngines.DefaultPaddleTimeoutSeconds, defaults.OcrTimeoutSeconds);
        Assert.True(defaults.OcrFallbackToWindows);
        Assert.Equal("", defaults.OcrServiceUrl);
        Assert.Equal("", defaults.OcrServiceApiKey);
    }

    [Theory]
    [InlineData(0, OcrEngines.DefaultPaddleTimeoutSeconds)]
    [InlineData(-5, OcrEngines.DefaultPaddleTimeoutSeconds)]
    [InlineData(1, OcrEngines.DefaultPaddleTimeoutSeconds)]
    [InlineData(2, OcrEngines.DefaultPaddleTimeoutSeconds)]
    [InlineData(30, 30)]
    [InlineData(9999, OcrEngines.DefaultPaddleTimeoutSeconds)]
    public void NormalizeTimeout_ClampsToSaneRange(int input, int expected)
    {
        Assert.Equal(expected, OcrEngines.NormalizeTimeout(input));
    }

    [Theory]
    [InlineData("http://10.0.0.5:8868/predict/ocr_system", true)]
    [InlineData("https://ocr.campus.local/ocr", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("10.0.0.5:8868", false)]      // 少了协议头，Uri 会当成相对路径
    [InlineData("ftp://10.0.0.5/ocr", false)]
    public void IsUsableUrl_AcceptsOnlyAbsoluteHttpUrls(string url, bool expected)
    {
        Assert.Equal(expected, OcrEngines.IsUsableUrl(url));
    }

    [Fact]
    public void ResolveEngine_FallsBackToBuiltInWhenRemoteUrlIsMissing()
    {
        var remoteButNoUrl = new AppSettingsData { OcrEngine = nameof(OcrEngineKind.PaddleHttp) };
        var remote = new AppSettingsData
        {
            OcrEngine = nameof(OcrEngineKind.PaddleHttp),
            OcrServiceUrl = "http://10.0.0.5:8868/predict/ocr_system",
        };

        Assert.Equal(OcrEngineKind.Windows, OcrServiceFactory.ResolveEngine(remoteButNoUrl));
        Assert.Equal(OcrEngineKind.PaddleHttp, OcrServiceFactory.ResolveEngine(remote));
        Assert.Equal(OcrEngineKind.Windows, OcrServiceFactory.ResolveEngine(new AppSettingsData()));
    }

    // ── 主备降级 ─────────────────────────────────────────────

    [Fact]
    public async Task Fallback_WhenPrimaryFails_UsesBuiltInAndEntersCooldown()
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var primary = new FakeOcr(() => throw new TimeoutException("内网服务无响应"));
        var fallback = new FakeOcr(() => "本地识别结果");
        var service = new FallbackOcrService(primary, fallback, TimeSpan.FromMinutes(2), () => clock);

        Assert.Equal("本地识别结果", await service.RecognizeAsync(new byte[] { 1 }));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, service.FallbackCount);
        Assert.Contains("内网服务无响应", service.LastError);
        Assert.True(service.IsCoolingDown);

        // 冷却期内不再逐张去等超时——这是"一节课 100 张截图"场景下的关键行为
        Assert.Equal("本地识别结果", await service.RecognizeAsync(new byte[] { 1 }));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(2, service.FallbackCount);

        // 冷却结束：再试一次远程
        clock = clock.AddMinutes(3);
        Assert.False(service.IsCoolingDown);
        Assert.Equal("本地识别结果", await service.RecognizeAsync(new byte[] { 1 }));
        Assert.Equal(2, primary.Calls);
    }

    [Fact]
    public async Task Fallback_WhenPrimaryRecovers_UsesItAgainAndClearsCooldown()
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bool down = true;
        var primary = new FakeOcr(() => down ? throw new InvalidOperationException("down") : "远程识别结果");
        var fallback = new FakeOcr(() => "本地识别结果");
        var service = new FallbackOcrService(primary, fallback, TimeSpan.FromMinutes(2), () => clock);

        Assert.Equal("本地识别结果", await service.RecognizeAsync(new byte[] { 1 }));

        down = false;
        clock = clock.AddMinutes(3);
        Assert.Equal("远程识别结果", await service.RecognizeAsync(new byte[] { 1 }));
        Assert.False(service.IsCoolingDown);

        Assert.Equal("远程识别结果", await service.RecognizeAsync(new byte[] { 1 }));
        Assert.Equal(1, service.FallbackCount);   // 恢复后不再回退
    }

    [Fact]
    public async Task Fallback_WhenPrimaryWorks_NeverTouchesBuiltIn()
    {
        var primary = new FakeOcr(() => "远程识别结果");
        var fallback = new FakeOcr(() => "本地识别结果");
        var service = new FallbackOcrService(primary, fallback);

        Assert.Equal("远程识别结果", await service.RecognizeAsync(new byte[] { 1 }));

        Assert.Equal(0, fallback.Calls);
        Assert.Equal(0, service.FallbackCount);
        Assert.False(service.IsCoolingDown);
    }

    [Fact]
    public async Task Fallback_EmptyImage_DoesNotCallAnyone()
    {
        var primary = new FakeOcr(() => "远程");
        var fallback = new FakeOcr(() => "本地");
        var service = new FallbackOcrService(primary, fallback);

        Assert.Equal("", await service.RecognizeAsync(Array.Empty<byte>()));
        Assert.Equal(0, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    private sealed class FakeOcr : IOcrService
    {
        private readonly Func<string> _result;

        public FakeOcr(Func<string> result) => _result = result;

        public int Calls { get; private set; }

        public Task<string> RecognizeAsync(byte[] imageData)
        {
            Calls++;
            return Task.FromResult(_result());
        }
    }
}
