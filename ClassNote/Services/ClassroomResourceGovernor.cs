using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassNote.Services;

/// <summary>
/// 边录边转写的开关档位。存枚举名而不是界面文案（与 <see cref="AudioSourceKinds"/> 同一约定）。
/// </summary>
public enum ClassroomTranscriptionMode
{
    /// <summary>关闭：不在课堂上跑推理，全部留到课后批处理（旧行为）。</summary>
    Off = 0,

    /// <summary>
    /// 默认：课上空闲时按块增量转写，但**永远只占一半逻辑核，且按占空比让路**，
    /// 检测到全屏放映 / 视频播放 / 电池供电时自动降速。
    /// </summary>
    Auto = 1,

    /// <summary>极速：课堂期间不打折（仍只占一半逻辑核）。用户明确表示"不影响放映"时选用。</summary>
    Aggressive = 2,
}

/// <summary>档位的解析 / 显示文案（枚举与持久化字符串之间的唯一转换点）。</summary>
public static class ClassroomTranscriptionModes
{
    /// <summary>设置界面下拉框的文案（顺序必须与枚举取值一一对应）。</summary>
    public static readonly string[] DisplayNames = { "关闭（全部课后处理）", "自动（推荐）", "极速（课堂期间也全力转写）" };

    public static ClassroomTranscriptionMode Normalize(ClassroomTranscriptionMode mode)
        => Enum.IsDefined(mode) ? mode : ClassroomTranscriptionMode.Auto;

    public static string ToDisplayName(ClassroomTranscriptionMode mode) => DisplayNames[(int)Normalize(mode)];

    public static ClassroomTranscriptionMode FromStorage(string? value)
        => Enum.TryParse<ClassroomTranscriptionMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : ClassroomTranscriptionMode.Auto;
}

/// <summary>课堂资源预算：让路档位决定"打几折"，与具体探测实现解耦，便于单测。</summary>
/// <param name="Duty">推理占空比（0..1]。1 = 一个块接一个块不停；0.2 = 跑一份停四份。</param>
/// <param name="MaxSelfUtilization">自身进程占**整机**CPU 的上限（0..1]，超过就自动折算占空比。</param>
/// <param name="Reason">给用户看的原因（"检测到全屏放映"等）。</param>
/// <param name="PauseRequested">是否完全暂停（用户手动暂停）。</param>
public sealed record ClassroomLoadPolicy(double Duty, double MaxSelfUtilization, string Reason, bool PauseRequested = false)
{
    /// <summary>自动档默认策略表：让路强度按"前台是否在放东西 / 是否电池"分档。</summary>
    public static ClassroomLoadPolicy For(ClassroomTranscriptionMode mode, ClassroomLoad load)
    {
        mode = ClassroomTranscriptionModes.Normalize(mode);
        if (mode == ClassroomTranscriptionMode.Off)
            return new ClassroomLoadPolicy(0, 0, "已关闭边录边转写", PauseRequested: true);

        double full = mode == ClassroomTranscriptionMode.Aggressive ? 1.0 : 0.6;

        return load switch
        {
            // 前台全屏放映 / 视频播放：只借一点点空闲，保证放映不卡
            ClassroomLoad.Presenting => new ClassroomLoadPolicy(full * 0.25, 0.15, "检测到全屏放映 / 视频播放"),
            // 电池供电：不为了提前出笔记而烧电
            ClassroomLoad.OnBattery => new ClassroomLoadPolicy(full * 0.5, 0.25, "电池供电，已降速"),
            ClassroomLoad.PresentingOnBattery => new ClassroomLoadPolicy(full * 0.2, 0.12, "电池供电且正在放映，已大幅降速"),
            _ => new ClassroomLoadPolicy(full, 0.5, "空闲"),
        };
    }
}

/// <summary>课堂当前的外部负载状态（由探测得到）。</summary>
public enum ClassroomLoad
{
    /// <summary>没人放东西，插着电。</summary>
    Idle = 0,

    /// <summary>前台有全屏放映 / 屏幕在放视频。</summary>
    Presenting = 1,

    /// <summary>电池供电。</summary>
    OnBattery = 2,

    /// <summary>电池供电 + 正在放映。</summary>
    PresentingOnBattery = 3,
}

/// <summary>
/// 课堂资源探测：把"现在能不能借 CPU"这件事抽成可替身的接口，
/// 让策略与占空比逻辑可以脱离真实系统状态做单元测试。
/// </summary>
public interface IClassroomResourceProbe
{
    /// <summary>前台是否有非本进程的全屏窗口（PPT 放映 / 全屏视频 / 全屏游戏）。</summary>
    bool IsFullscreenAppForeground();

    /// <summary>屏幕内容是否正在播视频（由截图服务的变化检测给出）。</summary>
    bool IsScreenVideoPlaying();

    /// <summary>是否电池供电（未知状态按"不是电池"处理，避免在台式机上误降速）。</summary>
    bool IsOnBattery();

    /// <summary>自身进程占**整机**CPU 的比例（0..1，按 2 秒窗口测量）。</summary>
    double OwnCpuUtilization();
}

/// <summary>真实系统状态探测（Win32 + 进程 CPU 计时）。</summary>
public sealed class SystemResourceProbe : IClassroomResourceProbe
{
    private readonly Func<bool>? _videoPlaying;
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly int _logicalCores = Math.Max(1, Environment.ProcessorCount);
    private TimeSpan _lastCpu;
    private long _lastTicks;

    /// <param name="screenVideoPlaying">屏幕视频判定（一般来自截图服务的视频模式检测）。</param>
    public SystemResourceProbe(Func<bool>? screenVideoPlaying = null)
    {
        _videoPlaying = screenVideoPlaying;
    }

    public bool IsFullscreenAppForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            // 我们自己的窗口（录音页/主窗口最大化）不算"别人在用机器"
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
                return false;

            if (!GetWindowRect(hwnd, out var rect))
                return false;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return false;

            long monitorArea = (long)(info.rcMonitor.Right - info.rcMonitor.Left) * (info.rcMonitor.Bottom - info.rcMonitor.Top);
            long windowArea = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (monitorArea <= 0)
                return false;

            // 覆盖显示器 ≥95% 视为全屏（最大化窗口也算：前台正被别的应用占满时同样该让路）
            return windowArea * 100 >= monitorArea * 95;
        }
        catch
        {
            return false; // 探测失败按"没有放映"处理：宁可多借点 CPU，也不要无端停转写
        }
    }

    public bool IsScreenVideoPlaying()
    {
        try { return _videoPlaying?.Invoke() == true; }
        catch { return false; }
    }

    public bool IsOnBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return false;
            return status.ACLineStatus == 0; // 0 = 离线（电池），1 = 在线，255 = 未知
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自身 CPU 占整机比例：进程 CPU 时间增量 /（墙钟增量 × 逻辑核数）。
    /// 首次调用没有基准，返回 0（不误判为超预算）。
    /// </summary>
    public double OwnCpuUtilization()
    {
        try
        {
            _self.Refresh();
            var cpu = _self.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();

            if (_lastTicks == 0)
            {
                _lastCpu = cpu;
                _lastTicks = now;
                return 0;
            }

            double wallSeconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
            double cpuSeconds = (cpu - _lastCpu).TotalSeconds;
            _lastCpu = cpu;
            _lastTicks = now;

            if (wallSeconds <= 0)
                return 0;

            double utilization = cpuSeconds / (wallSeconds * _logicalCores);
            return double.IsFinite(utilization) ? Math.Clamp(utilization, 0, 1) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>
/// 课堂资源调度器：录音期间每 2 秒探测一次外部负载，算出"这一轮能借多少 CPU"。
///
/// 职责边界刻意划得很窄——它**只**决定占空比，不碰线程数与推理本身：
/// · 逻辑核占用上限由 ONNX 会话创建时的 <see cref="ClassroomPolicy"/> 决定（永远是逻辑核的一半），
///   ORT 的线程池不支持运行期改大小，所以"留一半核"这条硬保证在会话创建时就钉死；
/// · 运行期的让路靠"跑一块、歇一会儿"的占空比实现，可随时收放，也不需要重建会话。
///
/// 两者叠加后的上界：峰值 ≤ 50% 逻辑核，常态均值按占空比再打折（默认档位见 <see cref="ClassroomLoadPolicy.For"/>）。
/// </summary>
public sealed class ClassroomResourceGovernor : IDisposable
{
    /// <summary>探测周期（毫秒）。太长会让"开始放映"后还在抢 CPU，太短则自身开销可观。</summary>
    public const int PollIntervalMs = 2000;

    private readonly IClassroomResourceProbe _probe;
    private readonly ClassroomTranscriptionMode _mode;
    private readonly object _lock = new();
    private Timer? _timer;
    private ClassroomBudget _current;
    private volatile bool _userPaused;

    public ClassroomResourceGovernor(IClassroomResourceProbe probe, ClassroomTranscriptionMode mode)
    {
        _probe = probe;
        _mode = ClassroomTranscriptionModes.Normalize(mode);
        _current = Evaluate(); // 先给一个保守初值，Start 之前的读取也有意义
    }

    /// <summary>当前预算（线程安全，可随时读取）。</summary>
    public ClassroomBudget Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>预算发生变化时触发（用于给界面提示"已让路"）。</summary>
    public event EventHandler<ClassroomBudget>? BudgetChanged;

    public void Start()
    {
        lock (_lock)
        {
            _timer ??= new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
        }
    }

    /// <summary>用户手动暂停 / 恢复课堂转写（录音页开关）。</summary>
    public void SetUserPaused(bool paused)
    {
        _userPaused = paused;
        Poll();
    }

    private void Poll()
    {
        ClassroomBudget next;
        try { next = Evaluate(); }
        catch { return; }

        bool changed;
        lock (_lock)
        {
            changed = !next.Equals(_current);
            _current = next;
        }
        if (changed)
            BudgetChanged?.Invoke(this, next);
    }

    private ClassroomBudget Evaluate()
    {
        if (_userPaused)
            return new ClassroomBudget(0, ClassroomLoad.Idle, "已手动暂停课堂转写");

        bool presenting = _probe.IsScreenVideoPlaying() || _probe.IsFullscreenAppForeground();
        bool battery = _probe.IsOnBattery();

        var load = (presenting, battery) switch
        {
            (true, true) => ClassroomLoad.PresentingOnBattery,
            (true, false) => ClassroomLoad.Presenting,
            (false, true) => ClassroomLoad.OnBattery,
            _ => ClassroomLoad.Idle,
        };

        var policy = ClassroomLoadPolicy.For(_mode, load);
        if (policy.PauseRequested)
            return new ClassroomBudget(0, load, policy.Reason);

        // 自身超预算时按比例折减：不引入状态机，实测值一回落就自动恢复，不会越退越慢
        double measured = _probe.OwnCpuUtilization();
        double scale = 1.0;
        if (policy.MaxSelfUtilization > 0 && measured > policy.MaxSelfUtilization)
            scale = Math.Max(0.1, policy.MaxSelfUtilization / measured);

        double duty = Math.Clamp(policy.Duty * scale, 0, 1);
        return new ClassroomBudget(duty, load, policy.Reason);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>一次评估得到的预算：占空比 + 负载归类 + 给用户的原因。</summary>
/// <param name="Duty">推理占空比（0..1]；0 表示这一轮完全不跑。</param>
/// <param name="Load">负载归类。</param>
/// <param name="Reason">面向用户的说明（录音页展示）。</param>
public sealed record ClassroomBudget(double Duty, ClassroomLoad Load, string Reason)
{
    /// <summary>是否被要求完全停下（关闭档 / 手动暂停）。</summary>
    public bool IsPaused => Duty <= 0;

    /// <summary>是否处于"为放映让路"的状态（用于界面提示）。</summary>
    public bool IsYielding => Load is ClassroomLoad.Presenting or ClassroomLoad.PresentingOnBattery;
}

/// <summary>
/// 与具体系统探测无关的静态策略：线程预算与档位常量集中在此，
/// 让"永远只占一半逻辑核"这条硬保证只有一个出处。
/// </summary>
public static class ClassroomPolicy
{
    /// <summary>
    /// ONNX 会话的 intra-op 线程数上限：**永远不超过逻辑核的一半**。
    /// 这就是"留足资源给 PPT / 视频"的硬边界——即使占空比是 1.0，机器上也始终有
    /// 一半逻辑核从没被我们占过。ORT 的线程池大小不能在运行期改，所以这条必须在
    /// 会话创建时一次定死。
    /// </summary>
    public static int IntraOpThreads(int logicalProcessors)
        => Math.Max(1, Math.Max(1, logicalProcessors) / 2);

    /// <summary>课堂期单个块跑完后按占空比计算的等待时间（毫秒）。</summary>
    public static int CooldownMs(double blockWallMs, double duty)
    {
        if (duty >= 1) return 0;
        if (duty <= 0) return 0; // 完全暂停由调用方走"等待预算恢复"的路径，这里不表达暂停
        double idle = blockWallMs * (1.0 / duty - 1.0);
        return (int)Math.Clamp(idle, 0, 60_000);
    }
}
