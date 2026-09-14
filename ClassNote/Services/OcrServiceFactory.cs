using System.Diagnostics;

namespace ClassNote.Services;

/// <summary>
/// 带降级的主备 OCR：优先用远程服务，失败时回退内置 Windows OCR。
///
/// 关键设计是**熔断冷却**：内网服务挂掉时，如果每张截图都老老实实等一次超时，
/// 一节课 100+ 张就是"100 × 20 秒"的纯等待。因此首次失败后进入冷却期（默认 2 分钟），
/// 期间直接走内置 OCR，冷却结束再试一次远程——服务恢复了能自动用回来，
/// 服务挂着的代价最多是每 2 分钟一次失败重试。
/// </summary>
public sealed class FallbackOcrService : IOcrService
{
    /// <summary>远程服务失败后的冷却时长（同一实例内）。</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(2);

    private readonly IOcrService _primary;
    private readonly IOcrService _fallback;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();

    private DateTime _primaryBlockedUntil = DateTime.MinValue;

    public FallbackOcrService(IOcrService primary, IOcrService fallback,
        TimeSpan? cooldown = null, Func<DateTime>? now = null)
    {
        _primary = primary;
        _fallback = fallback;
        _cooldown = cooldown ?? DefaultCooldown;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>远程服务连续失败的次数（用于诊断与向用户解释）。</summary>
    public long PrimaryFailures { get; private set; }

    /// <summary>回退到内置 OCR 的次数。</summary>
    public long FallbackCount { get; private set; }

    /// <summary>最近一次远程失败的原因（诊断用）。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前是否处于远程服务冷却期。</summary>
    public bool IsCoolingDown
    {
        get { lock (_lock) return _now() < _primaryBlockedUntil; }
    }

    public async Task<string> RecognizeAsync(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0) return "";

        if (!IsCoolingDown)
        {
            try
            {
                var text = await _primary.RecognizeAsync(imageData);
                lock (_lock) _primaryBlockedUntil = DateTime.MinValue;   // 成功即解除冷却
                return text;
            }
            catch (Exception ex)
            {
                // 单张图失败不该让整节课没有文字：记因、进冷却、回退内置
                lock (_lock)
                {
                    PrimaryFailures++;
                    LastError = ex.Message;
                    _primaryBlockedUntil = _now() + _cooldown;
                }
                Debug.WriteLine($"[FallbackOcrService] 远程 OCR 失败，回退内置引擎：{ex.Message}");
            }
        }

        lock (_lock) FallbackCount++;
        return await _fallback.RecognizeAsync(imageData);
    }
}

/// <summary>
/// 按设置装配 OCR 服务（录音侧与课后处理侧必须用**同一个实例**，
/// 否则两处各自持有引擎状态，"冷却""计数"都失去意义）。
/// </summary>
public static class OcrServiceFactory
{
    /// <summary>
    /// 依设置创建 OCR 服务。
    /// 远程引擎但地址不合法时**回退内置引擎**而不是抛异常：设置写错不该让用户录不了课
    /// （设置界面已对地址做校验，这里只是兜底）。
    /// </summary>
    public static IOcrService Create(AppSettingsData settings)
    {
        var kind = OcrEngines.FromStorage(settings.OcrEngine);
        if (kind != OcrEngineKind.PaddleHttp || !OcrEngines.IsUsableUrl(settings.OcrServiceUrl))
            return new WindowsOcrService();

        var remote = new PaddleOcrService(
            settings.OcrServiceUrl,
            OcrEngines.RequestFormatFromStorage(settings.OcrRequestFormat),
            settings.OcrServiceApiKey,
            settings.OcrTimeoutSeconds);

        return settings.OcrFallbackToWindows
            ? new FallbackOcrService(remote, new WindowsOcrService())
            : remote;
    }

    /// <summary>当前设置下的引擎（设置界面与诊断展示用）。</summary>
    public static OcrEngineKind ResolveEngine(AppSettingsData settings)
    {
        var kind = OcrEngines.FromStorage(settings.OcrEngine);
        return kind == OcrEngineKind.PaddleHttp && !OcrEngines.IsUsableUrl(settings.OcrServiceUrl)
            ? OcrEngineKind.Windows
            : kind;
    }
}
