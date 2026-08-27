// 开发辅助工具：对 UI 截图做 OCR 文本抽查 + 关键区域像素采样，用于无头验证界面。
// 仅用于本机验证，不入库。
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

var path = args.Length > 0 ? args[0] : "";
if (!File.Exists(path)) { Console.Error.WriteLine("file not found: " + path); return 1; }

if (args.Length > 2 && args[2] == "scan")
{
    using var bmp2 = new Bitmap(args[0]);
    int rLo = 0x3A, rHi = 0x5F, gLo = 0x57, gHi = 0x8F, bLo = 0xE8, bHi = 0xFF; // 品牌蓝渐变范围
    int minX = bmp2.Width, minY = bmp2.Height, maxX = -1, maxY = -1, count = 0;
    var rows = new Dictionary<int, int>();
    for (int y = 0; y < bmp2.Height; y += 2)
        for (int x = 0; x < bmp2.Width; x += 2)
        {
            var c = bmp2.GetPixel(x, y);
            if (c.R >= rLo && c.R <= rHi && c.G >= gLo && c.G <= gHi && c.B >= bLo && c.B <= bHi)
            {
                count++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                rows[y] = rows.TryGetValue(y, out var v) ? v + 1 : 1;
            }
        }
    Console.WriteLine($"[scan] brand-blue px={count} bbox=({minX},{minY})-({maxX},{maxY})");
    foreach (var kv in rows.OrderByDescending(kv => kv.Value).Take(6))
        Console.WriteLine($"[scan] y={kv.Key} count={kv.Value}");
    return 0;
}

await OcrCheck(path);

PixelCheck(path);

return 0;

async Task OcrCheck(string file)
{
    try
    {
        using var stream = File.OpenRead(file);
        using var ras = new InMemoryRandomAccessStream();
        await stream.CopyToAsync(ras.AsStreamForWrite());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) { Console.WriteLine("[OCR] no engine"); return; }

        var result = await engine.RecognizeAsync(software);
        Console.WriteLine("---- OCR ----");
        foreach (var line in result.Lines)
            Console.WriteLine(line.Text);
        Console.WriteLine("---- end OCR ----");
    }
    catch (Exception ex) { Console.WriteLine("[OCR] failed: " + ex.Message); }
}

void PixelCheck(string file)
{
    try
    {
        using var bmp = new Bitmap(file);
        Console.WriteLine($"---- PIXELS ({bmp.Width}x{bmp.Height}) ----");
        var pts = new (int x, int y, string label)[] {
            (4, 4, "top-left bg"),
            (bmp.Width / 2, 4, "top-center bg"),
            (40, 40, "header area"),
            (30, bmp.Height / 2, "left margin bg"),
            (bmp.Width - 30, bmp.Height / 2, "right margin bg"),
            (bmp.Width / 2, bmp.Height / 2, "center"),
        };
        foreach (var (x, y, label) in pts)
        {
            if (x >= 0 && y >= 0 && x < bmp.Width && y < bmp.Height)
            {
                var c = bmp.GetPixel(x, y);
                Console.WriteLine($"{label} [{x},{y}] = #{c.R:X2}{c.G:X2}{c.B:X2}");
            }
        }
    }
    catch (Exception ex) { Console.WriteLine("[PIXEL] failed: " + ex.Message); }
}
