using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace ClassNote.Services;

public enum ScreenshotType { Annotation, NewSlide, Video }

public class ScreenshotResult
{
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public ScreenshotType Type { get; set; }
    public string? UrlFound { get; set; }

    /// <summary>API wire format: the server accepts only annotation / new_slide / video.</summary>
    public string ApiType => Type switch
    {
        ScreenshotType.Annotation => "annotation",
        ScreenshotType.NewSlide => "new_slide",
        ScreenshotType.Video => "video",
        _ => "annotation",
    };
}

/// <summary>
/// 周期截屏 + 变化检测。全屏抓取、pHash 计算与 JPEG 编码都在线程池后台线程执行，
/// 结果通过捕获的 SynchronizationContext 封送回 UI 线程后再触发 ScreenshotCaptured，
/// 避免阻塞 UI 线程或跨线程修改绑定属性。
/// </summary>
public class ScreenshotService : IScreenshotService
{
    private byte[]? _previousHash;
    private int _highChangeCount;
    private bool _videoMode;
    private int _stableCount;
    private int? _pendingInterval;

    private const int BASE_INTERVAL_MS = 10_000;
    private const int VIDEO_INTERVAL_MS = 60_000;
    private const double CHANGE_THRESHOLD_MINOR = 0.01;
    private const double CHANGE_THRESHOLD_MAJOR = 0.08;
    private const double CHANGE_THRESHOLD_VIDEO = 0.40;
    private const int VIDEO_CONFIRM_FRAMES = 3;
    private const int VIDEO_EXIT_FRAMES = 2;

    private Timer? _timer;
    private SynchronizationContext? _syncContext;

    public event EventHandler<ScreenshotResult>? ScreenshotCaptured;

    public void Start(int initialIntervalMs = BASE_INTERVAL_MS)
    {
        _syncContext = SynchronizationContext.Current; // 通常为 UI 线程的 DispatcherSynchronizationContext
        _timer = new Timer(_ => OnTick(), null, initialIntervalMs, initialIntervalMs);
    }

    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        if (timer != null)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            timer.Dispose();
        }
    }

    private void OnTick()
    {
        try
        {
            var result = CaptureAndAnalyze();

            // CaptureAndAnalyze 可能请求切换采样间隔（进入/退出视频模式）
            if (_pendingInterval is int interval)
            {
                _pendingInterval = null;
                _timer?.Change(interval, interval);
            }

            if (result != null)
                Publish(result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenshotService] 截图捕获失败: {ex}");
        }
    }

    private void Publish(ScreenshotResult result)
    {
        if (_syncContext != null)
            _syncContext.Post(_ => ScreenshotCaptured?.Invoke(this, result), null);
        else
            ScreenshotCaptured?.Invoke(this, result);
    }

    private ScreenshotResult? CaptureAndAnalyze()
    {
        using var bitmap = CaptureScreen();
        var hash = ComputeHash(bitmap);
        double change = _previousHash != null ? CompareHash(_previousHash, hash) : 1.0;
        _previousHash = hash;

        if (!_videoMode)
        {
            if (change < CHANGE_THRESHOLD_MINOR)
                return null;  // 丢弃无变化帧

            if (change >= CHANGE_THRESHOLD_VIDEO)
            {
                _highChangeCount++;
                if (_highChangeCount >= VIDEO_CONFIRM_FRAMES)
                {
                    _videoMode = true;
                    _pendingInterval = VIDEO_INTERVAL_MS;
                    _stableCount = 0;
                    return CaptureWithType(bitmap, ScreenshotType.Video);
                }
                return null;
            }

            _highChangeCount = 0;

            if (change >= CHANGE_THRESHOLD_MAJOR)
                return CaptureWithType(bitmap, ScreenshotType.NewSlide);

            return CaptureWithType(bitmap, ScreenshotType.Annotation);
        }
        else
        {
            if (change < CHANGE_THRESHOLD_VIDEO)
            {
                _stableCount++;
                if (_stableCount >= VIDEO_EXIT_FRAMES)
                {
                    _videoMode = false;
                    _pendingInterval = BASE_INTERVAL_MS;
                }
            }
            else
            {
                _stableCount = 0;
            }
            return CaptureWithType(bitmap, ScreenshotType.Video);
        }
    }

    private ScreenshotResult CaptureWithType(Bitmap bitmap, ScreenshotType type)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Jpeg);
        return new ScreenshotResult { ImageData = ms.ToArray(), Type = type };
    }

    private static Bitmap CaptureScreen()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var bmp = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
        return bmp;
    }

    private static byte[] ComputeHash(Bitmap bmp)
    {
        // 简化 pHash: 缩放 8x8 → 灰度 → 计算均值 → 生成 64-bit hash
        using var small = new Bitmap(bmp, new Size(8, 8));
        byte[] hash = new byte[8];
        float total = 0;
        float[] pixels = new float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var px = small.GetPixel(x, y);
                float v = (px.R + px.G + px.B) / 3f;
                pixels[y * 8 + x] = v;
                total += v;
            }
        float avg = total / 64;
        for (int i = 0; i < 64; i++)
            if (pixels[i] >= avg)
                hash[i / 8] |= (byte)(1 << (i % 8));
        return hash;
    }

    private static double CompareHash(byte[] a, byte[] b)
    {
        int diff = 0;
        for (int i = 0; i < 8; i++)
        {
            var x = (byte)(a[i] ^ b[i]);
            for (int j = 0; j < 8; j++)
                if ((x & (1 << j)) != 0) diff++;
        }
        return diff / 64.0;
    }

    public void Dispose() => Stop();
}
