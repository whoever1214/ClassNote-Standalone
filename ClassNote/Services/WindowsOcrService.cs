using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ClassNote.Services;

/// <summary>
/// 本地 OCR 服务，基于 Windows 10/11 内置 OCR 引擎（Windows.Media.Ocr），
/// 纯客户端实现，无需服务端或外部二进制。
/// </summary>
public interface IOcrService
{
    /// <summary>识别图片（JPEG 字节）中的文字，返回按行拼接的文本。</summary>
    Task<string> RecognizeAsync(byte[] imageData);
}

public sealed class WindowsOcrService : IOcrService
{
    private static readonly OcrEngine Engine = CreateEngine();

    private static OcrEngine CreateEngine()
    {
        // 优先中文（简体），回退到可用语言
        var langs = OcrEngine.AvailableRecognizerLanguages;
        var zh = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("zh"));
        return zh != null ? OcrEngine.TryCreateFromLanguage(zh) ?? CreateFallback()
                          : CreateFallback();
    }

    private static OcrEngine CreateFallback()
    {
        var langs = OcrEngine.AvailableRecognizerLanguages;
        foreach (var l in langs)
        {
            var engine = OcrEngine.TryCreateFromLanguage(l);
            if (engine != null) return engine;
        }
        return OcrEngine.TryCreateFromUserProfileLanguages()!;
    }

    public async Task<string> RecognizeAsync(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0)
            return "";

        using var ms = new MemoryStream(imageData);
        var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        if (Engine == null)
            return "";

        var result = await Engine.RecognizeAsync(bitmap);
        var lines = result.Lines.Select(l => l.Text);
        return string.Join(Environment.NewLine, lines);
    }
}
