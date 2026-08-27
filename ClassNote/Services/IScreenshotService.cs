namespace ClassNote.Services;

public interface IScreenshotService : IDisposable
{
    void Start(int initialIntervalMs = 10000);
    void Stop();
    event EventHandler<ScreenshotResult> ScreenshotCaptured;
}
