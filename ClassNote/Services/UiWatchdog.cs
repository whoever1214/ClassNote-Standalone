using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace ClassNote.Services;

/// <summary>
/// UI 线程卡顿看门狗：把"界面卡住多久、当时正在做什么"写进 <c>classnote-crash.log</c>。
///
/// 为什么需要它：用户报的是"升级后反复卡死、必须重启进程"，而这类问题最要命的地方是
/// <b>没有证据</b>——现场只留下"点不动"这一个印象，既没有日志也没有复现步骤，
/// 排查只能靠猜（本轮定位到的 4 秒 UI 阻塞就是这么找出来的，靠的还是自建探针）。
///
/// 做法刻意保持极小：在 UI 线程上挂一个 500ms 的 DispatcherTimer，只测"两次 tick 之间实际过了多久"。
/// UI 线程被占住时 tick 不会触发，恢复后第一件事就是发现一个很大的间隔 —— 于是
/// <b>卡顿的时长与它结束的时刻都被如实记下来</b>（卡顿期间无法执行任何代码，这是原理上的限制）。
/// 开销是每 500ms 一次时间比较，可以忽略。
/// </summary>
public static class UiWatchdog
{
    /// <summary>tick 间隔。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>超过这个间隔才值得记一笔：正常的重布局 / 大列表刷新都在几百毫秒内。</summary>
    public static readonly TimeSpan ReportThreshold = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static DispatcherTimer? _timer;
    private static long _lastTicks;

    private static volatile string _phase = "启动";

    /// <summary>当前阶段（"录音中"/"结束录音"/"生成笔记"/"浏览记录"…）。由各处在 UI 线程上更新。</summary>
    public static string Phase => _phase;

    /// <summary>设置当前阶段。允许在任意线程调用，最坏只是晚一点看到新值。</summary>
    public static void SetPhase(string phase) => _phase = phase ?? "";

    /// <summary>诊断日志路径（与应用同一个目录：绿色安装与 Inno 安装都在这里）。</summary>
    public static string LogPath => Path.Combine(AppContext.BaseDirectory, "classnote-crash.log");

    /// <summary>启动看门狗（幂等；必须在 UI 线程上调用）。</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_timer != null)
                return;

            _lastTicks = Stopwatch.GetTimestamp();
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = Interval,
            };
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTicks, now);
        _lastTicks = now;

        if (elapsed < ReportThreshold)
            return;

        Write($"UI 线程卡顿 {elapsed.TotalSeconds:0.0} 秒（阶段：{_phase}）");
    }

    /// <summary>写一行诊断（异常一律吞掉：诊断本身绝不能影响主流程）。</summary>
    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志写不进去就算了 */ }
    }
}
