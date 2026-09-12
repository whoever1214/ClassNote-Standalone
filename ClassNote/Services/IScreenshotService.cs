namespace ClassNote.Services;

public interface IScreenshotService : IDisposable
{
    void Start(int initialIntervalMs = 10000);
    void Stop();
    event EventHandler<ScreenshotResult> ScreenshotCaptured;

    /// <summary>
    /// 屏幕内容当前是否处于"视频播放"状态（连续多帧大幅变化）。
    /// 这是"课堂正在放视频"的**内容级**判据：无论播放器是全屏、窗口化，还是网页里的视频，
    /// 画面都会持续大幅变化，因此比"看前台窗口是不是全屏"更不容易漏判。
    /// 课堂资源让路据此决定要不要降速（见 <see cref="ClassroomResourceGovernor"/>）。
    /// </summary>
    bool IsVideoMode { get; }
}
