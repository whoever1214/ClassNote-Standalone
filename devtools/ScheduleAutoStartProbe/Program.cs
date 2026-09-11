// 开发辅助工具：端到端验证「定时记录 · 后台静默启动」（BUG-3 回归）。
//
// 真实链路：进程内跑真实的 App 资源 + MainWindow + 10s 调度器 + ScheduleEngine，
// 并进入真实的 Application.Run() 消息循环（与生产 App.Main 一致），
// 用一条"下一分钟到点"的临时课表条目触发自动录音，全程观测：
//   1. 主窗口是否弹出（MainWindow.IsVisible + EnumWindows/IsWindowVisible 枚举进程内可见顶层窗口）；
//   2. 隐藏在后台的录音页是否真的 Loaded 并开始录音（MainWindow.IsRecordingActive）。
//
// 用法：ScheduleAutoStartProbe [--legacy-startup] [--timeout=120] [--cleanup]
//   --legacy-startup  复现修复前行为（App 先 Hide、窗口从未创建 + 到点 EnsureVisible 弹窗）
//   --cleanup         只清理历史遗留的测试数据后退出
//
// ⚠️ 会临时改写 settings.json（毫秒级，立即还原）与课表条目（结束后还原），
//    并创建 1 个测试会话（结束后连同采集文件一并删除）。请在开启定时记录前运行。
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using ClassNote.Models;
using ClassNote.Services;

internal static class Program
{
    /// <summary>测试课程名（刻意加前缀避免与用户数据冲突，用于结束后清理）。</summary>
    private const string TestCourse = "__静默启动验证__";

    /// <summary>探针宿主 App。</summary>
    private static Application? _app;

    /// <summary>
    /// 纯 WPF 宿主：不继承 <c>ClassNote.App</c>，避免 <c>App.OnStartup</c> 再建一个主窗口、
    /// 以及与应用单实例互斥体冲突（用户可能正开着 ClassNote）。
    /// App.xaml 的资源在下面按 ResourceDictionary 从源码加载，效果等同 BAML。
    /// </summary>
    private sealed class ProbeApp : Application { }

    [STAThread]
    private static int Main(string[] args)
    {
        var legacyStartup = args.Contains("--legacy-startup");
        var idleMode = args.Contains("--idle");   // 不写临时课表条目：只观察静默驻留期间是否会自己冒出窗口
        var timeoutSec = 120;
        foreach (var arg in args)
            if (arg.StartsWith("--timeout=", StringComparison.Ordinal) &&
                int.TryParse(arg["--timeout=".Length..], out var parsed))
                timeoutSec = parsed;

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassNote");
        var settingsPath = Path.Combine(dataDir, "settings.json");
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

        var repo = LocalRepository.Instance;
        var scheduleBackup = repo.ListScheduleEntries();
        Console.WriteLine($"[probe] 运行前课表 {scheduleBackup.Count} 条：" +
                          (scheduleBackup.Count == 0
                              ? "（空）"
                              : string.Join("；", scheduleBackup.Select(e => e.ToString()))));

        if (args.Contains("--cleanup"))
        {
            CleanupTestData(repo, scheduleBackup);
            try { if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup); } catch { }
            Console.WriteLine("[cleanup] 完成（已移除测试课表条目与测试会话）");
            return 0;
        }

        var verdict = "INCONCLUSIVE";
        var triggered = false;
        var visibleEver = false;
        var maxVisibleWindows = 0;
        var startupVisibleWindows = 0;
        var samples = new List<string>();

        try
        {
            // ── 1) 让本进程内存里的 ScheduleEnabled=true：改文件 → 读入单例 → 立刻还原文件 ──
            if (settingsBackup == null)
            {
                Console.WriteLine("[probe] 未找到 settings.json，无法临时开启定时记录");
                return 2;
            }
            File.WriteAllText(settingsPath,
                Regex.Replace(settingsBackup, "\"ScheduleEnabled\"\\s*:\\s*(true|false)", "\"ScheduleEnabled\": true"));
            var scheduleEnabled = AppSettings.Instance.Snapshot().ScheduleEnabled;
            File.WriteAllText(settingsPath, settingsBackup);
            Console.WriteLine($"[probe] in-memory ScheduleEnabled={scheduleEnabled}（settings.json 已立即还原）");

            // ── 2) 写入"下一分钟到点"的临时课表条目（--idle 时跳过）──
            var now = DateTime.Now;
            var trigger = now.AddMinutes(1);
            var startMin = trigger.Hour * 60 + trigger.Minute;
            var entry = new ScheduleEntry
            {
                WeekdayIndex = ScheduleEntry.WeekdayIndexOf(now.DayOfWeek),
                StartMin = startMin,
                EndMin = startMin + 10,
                Course = TestCourse,
                Title = "后台静默启动验证",
                Enabled = true,
            };
            if (idleMode)
            {
                Console.WriteLine("[probe] --idle：不写入临时课表条目，只观察静默驻留");
            }
            else
            {
                repo.ReplaceScheduleEntries(scheduleBackup.Append(entry));
                Console.WriteLine($"[probe] 临时课表条目：{entry}（现在 {now:HH:mm:ss}）");
            }

            // ── 3) 真实启动流程（与 App.OnStartup 的两种分支一致）──
            _app = new ProbeApp();
            _app.Resources = LoadAppResources();              // 等价于 App.InitializeComponent()
            _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var mainWindow = new ClassNote.MainWindow();
            Application.Current.MainWindow = mainWindow;

            if (legacyStartup)
            {
                mainWindow.RefreshTrayResident();             // 修复前：窗口从未被真正创建（无 HWND/视觉树）
                mainWindow.Hide();
            }
            else
            {
                mainWindow.StartHiddenToTray();                // 修复后：构建完成但不显示（后台静默驻留）
            }

            startupVisibleWindows = DescribeVisibleWindows().Count;
            Console.WriteLine($"[probe] 启动方式={(legacyStartup ? "legacy(Hide-only)" : "StartHiddenToTray")} " +
                              $"IsVisible={mainWindow.IsVisible} 可见顶层窗口={startupVisibleWindows}");

            // ── 4) 进入真实消息循环，按 250ms 采样直到录音开始 / 超时 ──
            var watch = Stopwatch.StartNew();
            var visibilityChanges = new List<string>();
            mainWindow.IsVisibleChanged += (_, _) =>
            {
                var line = $"t+{watch.Elapsed.TotalSeconds:F1}s IsVisible={mainWindow.IsVisible} " +
                           $"WindowState={mainWindow.WindowState}\n{new StackTrace(fNeedFileInfo: true)}";
                visibilityChanges.Add(line);
                Console.WriteLine($"[probe] 主窗口可见性变化：{line}");
            };
            var mainFrame = (Frame)mainWindow.Content;
            mainFrame.Navigated += (_, _) =>
                Console.WriteLine($"[probe] t+{watch.Elapsed.TotalSeconds:F1}s Frame.Navigated -> " +
                                  $"{mainFrame.Content?.GetType().Name} IsVisible={mainWindow.IsVisible}");
            mainWindow.Activated += (_, _) =>
                Console.WriteLine($"[probe] t+{watch.Elapsed.TotalSeconds:F1}s Window.Activated IsVisible={mainWindow.IsVisible}");
            mainWindow.Closed += (_, _) =>
                Console.WriteLine($"[probe] t+{watch.Elapsed.TotalSeconds:F1}s Window.Closed");
            var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            sampler.Tick += (_, _) =>
            {
                var windows = DescribeVisibleWindows();
                maxVisibleWindows = Math.Max(maxVisibleWindows, windows.Count);
                if (mainWindow.IsVisible || windows.Count > 0)
                    visibleEver = true;

                if (mainWindow.IsRecordingActive)
                {
                    triggered = true;
                    samples.Add($"t+{watch.Elapsed.TotalSeconds:F1}s 录音中 IsVisible={mainWindow.IsVisible} 可见窗口={windows.Count}");
                    sampler.Stop();
                    _app.Shutdown();
                    return;
                }

                if (samples.Count == 0 || watch.Elapsed.TotalSeconds % 5 < 0.3)
                    samples.Add($"t+{watch.Elapsed.TotalSeconds:F1}s 等待 IsVisible={mainWindow.IsVisible} " +
                                $"可见窗口={windows.Count} 页面类型={(mainWindow.Content as Frame)?.Content?.GetType().Name ?? "?"}");
                if (watch.Elapsed.TotalSeconds >= timeoutSec)
                {
                    samples.Add($"t+{watch.Elapsed.TotalSeconds:F1}s 超时");
                    sampler.Stop();
                    _app.Shutdown();
                }
            };
            sampler.Start();
            _app.Run();

            var afterTrigger = DescribeVisibleWindows();
            Console.WriteLine($"[probe] 采样：{string.Join(" | ", samples.TakeLast(8))}");
            Console.WriteLine($"[result] 到点自动录音={triggered} 主窗口曾可见={visibleEver} " +
                              $"IsVisible={mainWindow.IsVisible} 可见顶层窗口={afterTrigger.Count}" +
                              (afterTrigger.Count > 0 ? $" [{string.Join(", ", afterTrigger)}]" : "") +
                              $" 启动后最大可见窗口数={maxVisibleWindows}");

            verdict = triggered && !visibleEver
                ? "PASS: 到点后台静默开始录音，主界面全程未弹出"
                : triggered && visibleEver
                    ? "FAIL: 录音开始了，但主界面/窗口被弹出"
                    : "FAIL: 到点未开始录音（调度器没触发或录音页未 Loaded）";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[probe] 异常：{ex}");
            verdict = "FAIL: 探针异常 " + ex.GetType().Name;
        }
        finally
        {
            // ── 5) 收尾：还原课表 + 清理测试会话与临时 WAV ──
            CleanupTestData(repo, scheduleBackup);
            try { if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup); } catch { }
        }

        Console.WriteLine($"[verdict] {verdict}");
        GC.KeepAlive(_app);
        return verdict.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>
    /// 加载 <c>ClassNote/App.xaml</c> 的 <c>Application.Resources</c> 内容为 ResourceDictionary
    /// （等价于编译期 BAML 的资源加载，供本探针宿主使用；App.xaml 只用到标准 WPF 命名空间）。
    /// </summary>
    private static ResourceDictionary LoadAppResources()
    {
        var appXaml = FindRepoFile(Path.Combine("ClassNote", "App.xaml"));
        var text = File.ReadAllText(appXaml);
        const string open = "<Application.Resources>";
        const string close = "</Application.Resources>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = text.LastIndexOf(close, StringComparison.Ordinal);
        if (start < 0 || end < start)
            throw new InvalidOperationException($"未在 {appXaml} 中找到 Application.Resources");
        var inner = text[(start + open.Length)..end];
        var wrapped =
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + inner + "</ResourceDictionary>";
        return (ResourceDictionary)XamlReader.Parse(wrapped);
    }

    /// <summary>从探针输出目录向上找到仓库内的相对路径文件。</summary>
    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 {relativePath}（请在仓库内运行本探针）");
    }

    /// <summary>移除测试课表条目、测试会话与临时 WAV；<paramref name="scheduleBackup"/> 为原始课表。</summary>
    private static void CleanupTestData(LocalRepository repo, IReadOnlyList<ScheduleEntry> scheduleBackup)
    {
        try { repo.ReplaceScheduleEntries(scheduleBackup.Where(e => e.Course != TestCourse)); }
        catch (Exception ex) { Console.WriteLine("[cleanup] 课表还原失败: " + ex.Message); }

        try
        {
            foreach (var session in repo.ListSessions(50).Where(s => s.Course == TestCourse))
            {
                var wav = Path.Combine(Path.GetTempPath(), $"classnote_{session.Id}.wav");
                repo.DeleteSession(session.Id);
                try { if (File.Exists(wav)) File.Delete(wav); } catch { }
                Console.WriteLine($"[cleanup] 已删除测试会话 {session.Id} 与临时录音");
            }
        }
        catch (Exception ex) { Console.WriteLine("[cleanup] 测试会话清理失败: " + ex.Message); }
    }

    /// <summary>枚举当前进程内"可见"的顶层窗口（IsWindowVisible），用于判定是否弹窗。</summary>
    private static List<string> DescribeVisibleWindows()
    {
        var result = new List<string>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != self || !IsWindowVisible(hWnd))
                return true;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            result.Add($"{hWnd.ToInt64():X}:{sb}");
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
