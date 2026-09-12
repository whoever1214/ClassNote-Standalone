namespace ClassNote.Services;

/// <summary>
/// 模型闲置释放的状态机：决定"现在能不能把模型从内存里放掉"，并保证**推理进行中绝不释放**。
///
/// 为什么单独成类：这段逻辑的正确性只能在"有模型加载着、并且恰好有推理在跑"时暴露，
/// 而那种测试要加载 241MB 模型、耗时数秒——于是它最容易被写成"看起来对"的样子。
/// 抽出来之后，时间与"是否在忙"都可以注入，全部判定都能确定性单测（<c>ModelIdleReleaserTests</c>）。
///
/// 使用约定（调用方必须遵守）：
/// · 每次真正开始推理前 <see cref="EnterActivity"/>，结束时 <see cref="ExitActivity"/>（放在 finally 里）；
/// · 任何一次活动都要 <see cref="Touch"/>，否则会被判成"闲置"而释放；
/// · 定时器周期性地调 <see cref="TryRelease"/>，它自己检查阈值与忙闲。
/// </summary>
public sealed class ModelIdleReleaser
{
    private readonly Action _release;
    private readonly Func<DateTime> _clock;
    private readonly object _stateLock = new();

    private DateTime _lastActivityUtc;
    private int _inFlight;
    private long _releaseCount;

    /// <param name="release">真正执行释放的回调（幂等，重复调用要安全）。</param>
    /// <param name="clock">时钟（测试注入假时钟；默认 UTC 墙钟）。</param>
    /// <param name="idleAfter">闲置多久后允许释放；<see cref="TimeSpan.Zero"/> 或负数表示不自动释放。</param>
    /// <param name="enabled">总开关。</param>
    public ModelIdleReleaser(Action release, Func<DateTime>? clock = null,
        TimeSpan? idleAfter = null, bool enabled = true)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _clock = clock ?? (() => DateTime.UtcNow);
        IdleAfter = idleAfter ?? TimeSpan.FromMinutes(10);
        Enabled = enabled;
        _lastActivityUtc = _clock();
    }

    /// <summary>闲置阈值；0 或负数 = 不自动释放。</summary>
    public TimeSpan IdleAfter { get; set; }

    /// <summary>是否启用自动释放。</summary>
    public bool Enabled { get; set; }

    /// <summary>当前是否有推理在进行。</summary>
    public bool IsBusy => Volatile.Read(ref _inFlight) > 0;

    /// <summary>最近一次活动时间。</summary>
    public DateTime LastActivityUtc { get { lock (_stateLock) return _lastActivityUtc; } }

    /// <summary>已经发生过多少次释放（诊断/测试）。</summary>
    public long ReleaseCount => Interlocked.Read(ref _releaseCount);

    /// <summary>记录一次活动（推理开始/结束、预热、外部显式调用）。</summary>
    public void Touch()
    {
        lock (_stateLock)
            _lastActivityUtc = _clock();
    }

    /// <summary>进入一次推理活动（必须与 <see cref="ExitActivity"/> 配对，放在 finally 里）。</summary>
    public void EnterActivity()
    {
        Touch();
        Interlocked.Increment(ref _inFlight);
    }

    /// <summary>退出一次推理活动。</summary>
    public void ExitActivity()
    {
        Interlocked.Decrement(ref _inFlight);
        Touch();
    }

    /// <summary>
    /// 如果"已到闲置阈值且当前没有推理在进行"，就执行释放。
    /// </summary>
    /// <returns>本次是否真的释放了。</returns>
    public bool TryRelease()
    {
        if (!Enabled)
            return false;

        // 先取闲置时长再判定，最后才释放：判定逻辑只有 ShouldRelease 一处，
        // 边界条件（忙 / 阈值 / 关闭）都由它表达，避免这里再来一遍"看起来一样"的 if。
        TimeSpan idle;
        lock (_stateLock)
            idle = _clock() - _lastActivityUtc;

        if (!ShouldRelease(IsBusy, idle, IdleAfter))
            return false;

        try
        {
            _release();
        }
        catch (Exception ex)
        {
            // 释放失败不当成致命：下一轮还会再试（定时器回调里抛出会直接杀掉进程）
            System.Diagnostics.Debug.WriteLine($"[ModelIdleReleaser] 释放失败，将在下一轮重试: {ex.Message}");
            return false;
        }

        Interlocked.Increment(ref _releaseCount);
        // 释放后重置活动时间：否则每个周期都会判成"该释放"而反复调用 release
        Touch();
        return true;
    }

    /// <summary>纯判定（便于直接对边界条件写断言）。</summary>
    public static bool ShouldRelease(bool busy, TimeSpan idle, TimeSpan idleAfter)
        => !busy && idleAfter > TimeSpan.Zero && idle >= idleAfter;
}
