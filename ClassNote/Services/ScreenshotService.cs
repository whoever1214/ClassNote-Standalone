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
    private ulong? _previousHash;
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

    /// <summary>
    /// 是否处于"视频播放"状态（连续 VIDEO_CONFIRM_FRAMES 帧大幅变化后置位，稳定 VIDEO_EXIT_FRAMES 帧后复位）。
    /// 只在采样时刻更新，因此"开始放视频"到"转写让路"之间最多延迟一个采样间隔（常态 10 秒）。
    /// </summary>
    public bool IsVideoMode => _videoMode;

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
        var hash = PerceptualHash.FromBitmap(bitmap);

        // 首帧：无前帧可比较，作为基准并直接捕获为首张"新幻灯片"（避免首帧被误丢弃）
        if (_previousHash == null)
        {
            _previousHash = hash;
            return CaptureWithType(bitmap, ScreenshotType.NewSlide);
        }

        // 变化度 = 汉明距离 / 64：与阈值（1%/8%/40%）同一量纲，语义是"这一屏变了多少比例"
        double change = PerceptualHash.Distance(_previousHash.Value, hash) / (double)PerceptualHash.HashBits;
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

    // 感知哈希与"变了多少"的判定统一由 PerceptualHash 提供（截图分类与课后选图必须同一套口径）：
    // 换实现时只需改那一处，不会出现"录音时判定为没变、生成笔记时判定为变了"的分裂。
    public void Dispose() => Stop();
}