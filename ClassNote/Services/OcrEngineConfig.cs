namespace ClassNote.Services;

/// <summary>
/// 截图 OCR 用哪个引擎。存枚举名而不是界面文案（与录音来源、课堂占用档位同一套做法），
/// 这样改文案不会让用户已保存的选择失效。
/// </summary>
public enum OcrEngineKind
{
    /// <summary>Windows 10/11 内置 OCR（Windows.Media.Ocr）：离线、零配置，默认。</summary>
    Windows = 0,

    /// <summary>
    /// 内网自建的 PaddleOCR HTTP 服务。识别精度（尤其中英混排、公式、代码）明显好于内置引擎，
    /// 代价是必须有一台能连上的内网服务器，且截图会离开本机（发往该内网地址）。
    /// </summary>
    PaddleHttp = 1,
}

/// <summary>
/// 远程 OCR 服务的请求编码方式。两代 PaddleOCR 的 serving 接受的请求体不同，
/// 必须由用户按实际部署选，否则"能连上但一个字都识别不出"。
/// </summary>
public enum OcrRequestFormat
{
    /// <summary>
    /// PaddleOCR 2.x hubserving：POST JSON <c>{"images":["&lt;base64&gt;"]}</c> 到 <c>/predict/ocr_system</c>。
    /// </summary>
    JsonBase64 = 0,

    /// <summary>
    /// PaddleOCR 3.x / PaddleX serving：POST JSON <c>{"file":"&lt;base64&gt;","fileType":1}</c> 到 <c>/ocr</c>。
    /// （官方示例就是 JSON + base64，不是表单上传。）
    /// </summary>
    PaddleXServing = 1,
}

/// <summary>
/// OCR 引擎的展示名、存储名与默认值（与 <see cref="ClassroomTranscriptionModes"/> 同构）。
/// </summary>
public static class OcrEngines
{
    /// <summary>引擎下拉框文案；顺序与 <see cref="OcrEngineKind"/> 一一对应。</summary>
    public static readonly string[] DisplayNames =
    {
        "内置 Windows OCR（离线，无需配置）",
        "内网 PaddleOCR 服务（精度更高）",
    };

    /// <summary>请求方式下拉框文案；顺序与 <see cref="OcrRequestFormat"/> 一一对应。</summary>
    public static readonly string[] RequestFormatDisplayNames =
    {
        "JSON + images 数组（PaddleOCR 2.x hubserving：/predict/ocr_system）",
        "JSON + file 字段（PaddleOCR 3.x / PaddleX serving：/ocr）",
    };

    /// <summary>默认的 PaddleOCR 服务地址（hubserving 的 ocr_system 常用 8868 端口）。</summary>
    public const string DefaultPaddleUrl = "http://127.0.0.1:8868/predict/ocr_system";

    /// <summary>远程 OCR 的默认请求超时（秒）：内网单张截图通常 &lt;1 秒，给 20 秒足够容错。</summary>
    public const int DefaultPaddleTimeoutSeconds = 20;

    /// <summary>超时的合法区间（设置界面与服务端都要按这个口径校验）。</summary>
    public const int MinTimeoutSeconds = 3;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>把任意值收敛成已定义的引擎；未知值按内置引擎处理（与旧版行为一致）。</summary>
    public static OcrEngineKind Normalize(OcrEngineKind kind)
        => Enum.IsDefined(kind) ? kind : OcrEngineKind.Windows;

    public static OcrRequestFormat NormalizeFormat(OcrRequestFormat format)
        => Enum.IsDefined(format) ? format : OcrRequestFormat.JsonBase64;

    public static string ToDisplayName(OcrEngineKind kind) => DisplayNames[(int)Normalize(kind)];

    public static string ToDisplayName(OcrRequestFormat format) => RequestFormatDisplayNames[(int)NormalizeFormat(format)];

    /// <summary>从设置文件读引擎名；缺失或损坏时回退内置引擎（旧 settings.json 没有这个字段）。</summary>
    public static OcrEngineKind FromStorage(string? value)
        => Enum.TryParse<OcrEngineKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : OcrEngineKind.Windows;

    public static OcrRequestFormat RequestFormatFromStorage(string? value)
        => Enum.TryParse<OcrRequestFormat>(value, ignoreCase: true, out var format) && Enum.IsDefined(format)
            ? format
            : OcrRequestFormat.JsonBase64;

    public static string ToStorage(OcrEngineKind kind) => Normalize(kind).ToString();

    public static string ToStorage(OcrRequestFormat format) => NormalizeFormat(format).ToString();

    /// <summary>超时值的合法化：0/负数/越界都回落到默认值。</summary>
    public static int NormalizeTimeout(int seconds)
        => seconds < MinTimeoutSeconds || seconds > MaxTimeoutSeconds
            ? DefaultPaddleTimeoutSeconds
            : seconds;

    /// <summary>
    /// 服务地址是否可用（远程引擎必须填一个 http/https 绝对地址）。
    /// 只做形状校验，不联网——能不能连上由设置界面的"测试连接"回答。
    /// </summary>
    public static bool IsUsableUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
