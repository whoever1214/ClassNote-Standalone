// BugRepro：把用户报告的现场问题逐条做成可复现的最小实验。
//
//   1  history-run        —— 双击/右键「历史记录」时间戳 → Run 不是 Visual 或 Visual3D
//   2  context-menu       —— 右键（触摸长按等价物）落在时间戳上时，菜单能否拿到该行数据
//   3  autostop-freeze    —— UI 线程被占用时，定时自动下课（DispatcherTimer 看门狗）会被推迟多久
//   4  schedule-handoff   —— 物理课录音未按时结束 → 语文课被跳过 → 内容落到物理名下
//
// 用法：BugRepro <实验名>
// 退出码：0 = 未复现；2 = 复现
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using ClassNote.Views;

namespace BugRepro;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "history-run";
        Console.WriteLine($"===== 实验: {name} =====");

        return name switch
        {
            "history-run" => HistoryRun(),
            "context-menu" => ContextMenuOnTimestamp(),
            "autostop-freeze" => AutoStopFreeze(),
            "schedule-handoff" => ScheduleHandoff(),
            "ui-block" => UiBlockProbe.Run(args),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("可用实验: history-run | context-menu | autostop-freeze | schedule-handoff | ui-block");
        return 3;
    }

    // ─────────────────────────────────────────────────────────────
    // 公共：在离屏窗口里把真实的 MainPage 渲染出来
    // ─────────────────────────────────────────────────────────────

    private static (Window Host, MainPage Page, ListView List, MainViewModel Vm) BuildMainPage(
        params (string Course, string Title, DateTime Start)[] rows)
    {
        var app = new ClassNote.App();
        var init = typeof(ClassNote.App).GetMethod("InitializeComponent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        init?.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var sessions = rows.Select(r => new Session
        {
            Id = Guid.NewGuid(), Course = r.Course, Title = r.Title,
            StartTime = r.Start, Status = "completed",
        }).ToList();

        var vm = new MainViewModel(new FakeApi(sessions));
        vm.LoadSessionsAsync().GetAwaiter().GetResult();

        var page = new MainPage { DataContext = vm };
        var host = new Window
        {
            Content = page, Width = 1040, Height = 680,
            ShowActivated = false, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Left = -4000, Top = -4000,
        };
        host.Show();
        page.UpdateLayout();
        host.UpdateLayout();

        var list = page.FindName("SessionList") as ListView
                   ?? throw new InvalidOperationException("找不到 SessionList（MainPage.xaml 改过名字？）");
        list.UpdateLayout();
        return (host, page, list, vm);
    }

    /// <summary>取第 row 行时间戳列的起始 Run。</summary>
    private static Run? TimestampRun(ListView list, int row = 0)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(row) is not DependencyObject container)
            return null;
        var runs = new List<Run>();
        Walk(container, runs);
        return runs.FirstOrDefault();
    }

    private static void Walk(DependencyObject node, List<Run> runs)
    {
        if (node is TextBlock tb)
            foreach (var inline in tb.Inlines)
                CollectRuns(inline, runs);

        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            Walk(VisualTreeHelper.GetChild(node, i), runs);
    }

    private static void CollectRuns(Inline inline, List<Run> runs)
    {
        if (inline is Run r) runs.Add(r);
        else if (inline is Span sp)
            foreach (var child in sp.Inlines)
                CollectRuns(child, runs);
    }

    /// <summary>命中测试时间戳文字，返回 WPF 真正会交给 e.OriginalSource 的那个对象。</summary>
    private static DependencyObject? HitTimestamp(ListView list, Run run)
    {
        if (run.Parent is not TextBlock tb) return null;
        var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
            rect = new Rect(0, 0, Math.Max(1, tb.ActualWidth), Math.Max(1, tb.ActualHeight));
        var pt = new Point(rect.Left + Math.Min(2, rect.Width / 2), rect.Top + rect.Height / 2);
        return list.InputHitTest(tb.TranslatePoint(pt, list)) as DependencyObject;
    }

    // 生产代码 MainPage.xaml.cs 里的同一个实现（逐字复制用于对照）
    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────
    // 实验 1：双击时间戳 → FindVisualParent 崩溃
    // ─────────────────────────────────────────────────────────────

    private static int HistoryRun()
    {
        var (host, _, list, _) = BuildMainPage(
            ("物理", "牛顿第二定律", new DateTime(2026, 9, 14, 9, 0, 0)));

        var run = TimestampRun(list);
        if (run == null) { Console.WriteLine("FAIL: 没找到时间戳 Run"); return 3; }

        var hit = HitTimestamp(list, run);
        Console.WriteLine($"时间戳文字的命中结果类型: {hit?.GetType().FullName}");
        if (hit is not Run)
        {
            Console.WriteLine("结论: 本机命中结果不是 Run，未复现");
            host.Close();
            return 0;
        }

        try
        {
            var parent = FindVisualParent<CheckBox>(hit);
            Console.WriteLine($"FindVisualParent 返回 {parent?.GetType().Name ?? "<null>"}（未抛异常）");
            host.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("===== 复现成功 =====");
            Console.WriteLine($"异常: {ex.GetType().FullName}");
            Console.WriteLine($"消息: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            host.Close();
            return 2;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 实验 2：右键落在时间戳上 → 行数据能否挂到 ContextMenu
    //          （触摸长按最终走的也是这条 ContextMenuOpening 路径）
    // ─────────────────────────────────────────────────────────────

    private static int ContextMenuOnTimestamp()
    {
        var (host, page, list, _) = BuildMainPage(
            ("物理", "牛顿第二定律", new DateTime(2026, 9, 14, 9, 0, 0)));

        var run = TimestampRun(list);
        if (run == null) { Console.WriteLine("FAIL: 没找到时间戳 Run"); return 3; }

        var hit = HitTimestamp(list, run);
        Console.WriteLine($"右键/长按命中的对象: {hit?.GetType().FullName}（生产代码会把它当 e.OriginalSource）");

        // 生产代码路径：MainPage.SessionList_ContextMenuOpening
        //   AttachSessionToContextMenu(listView, e.OriginalSource as DependencyObject)
        Session? attached = null;
        try
        {
            attached = MainPage.AttachSessionToContextMenu(list, hit);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AttachSessionToContextMenu 抛出: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine($"挂到菜单上的记录: {(attached == null ? "<null>" : attached.Course + " / " + attached.Title)}");

        // 菜单项最终拿到的 DataContext（SessionOf(sender)）
        Session? menuDataContext = null;
        if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem item && item.ContextMenu != null)
            menuDataContext = item.ContextMenu.DataContext as Session;
        else
            Console.WriteLine("注意：命中行没有 ContextMenu（右键菜单根本不会弹出）");

        Console.WriteLine($"ContextMenu.DataContext: {(menuDataContext == null ? "<null>" : menuDataContext.Course)}");

        bool broken = attached == null;
        Console.WriteLine();
        Console.WriteLine(broken
            ? "===== 复现成功：长按/右键落在时间戳上时，菜单拿不到该行记录 ====="
            : "结论: 菜单能拿到该行记录（未复现）");

        host.Close();
        return broken ? 2 : 0;
    }

    // ─────────────────────────────────────────────────────────────
    // 实验 3：UI 线程被占用时，"到点自动下课"的看门狗被推迟多久
    //         生产代码 RecordingPage.StartAutoStopWatcher 用的是 DispatcherTimer
    // ─────────────────────────────────────────────────────────────

    private static int AutoStopFreeze()
    {
        var app = new ClassNote.App();
        var init = typeof(ClassNote.App).GetMethod("InitializeComponent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        init?.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var firedAt = new List<TimeSpan>();
        var autoStopAt = DateTime.Now.AddSeconds(1.5);   // 1.5 秒后该下课
        var start = Stopwatch.StartNew();

        // 与生产代码同构：DispatcherTimer，间隔 1s，到点即 Stop
        var watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watcher.Tick += (_, _) =>
        {
            if (DateTime.Now >= autoStopAt)
            {
                watcher.Stop();
                firedAt.Add(start.Elapsed);
            }
        };
        watcher.Start();

        // 模拟"UI 线程正忙于别的事"：占住 UI 线程 8 秒
        var blocker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var blockedUntil = start.Elapsed.Add(TimeSpan.FromSeconds(8));
        blocker.Tick += (_, _) =>
        {
            if (start.Elapsed >= blockedUntil)
            {
                blocker.Stop();
                return;
            }
            // 长任务：UI 线程在此期间无法处理其它 Dispatcher 工作
            var spin = Stopwatch.StartNew();
            while (spin.ElapsedMilliseconds < 900) { Thread.SpinWait(200); }
        };
        blocker.Start();

        // 跑消息循环直到下课被触发（或 20 秒超时）
        var deadline = Stopwatch.StartNew();
        while (firedAt.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(20))
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        if (firedAt.Count == 0)
        {
            Console.WriteLine("FAIL: 20 秒内没有触发自动结束（看门狗始终被饿死）");
            return 2;
        }

        var delay = firedAt[0] - TimeSpan.FromSeconds(1.5);
        Console.WriteLine($"应下课时刻: 1.50s（相对本次观测起点）");
        Console.WriteLine($"实际触发时刻: {firedAt[0].TotalSeconds:F2}s");
        Console.WriteLine($"推迟: {delay.TotalMilliseconds:F0} ms");
        Console.WriteLine();
        Console.WriteLine(delay.TotalSeconds >= 3
            ? "===== 复现成功：UI 线程繁忙时『到点自动下课』被推迟数秒（上课期间一直占用则永不触发）====="
            : "结论: 推迟在可接受范围");
        return delay.TotalSeconds >= 3 ? 2 : 0;
    }

    // ─────────────────────────────────────────────────────────────
    // 实验 4：物理课录音没有按时结束 → 语文课被跳过 → 内容落到物理名下
    //         组合生产代码：ScheduleEngine（纯逻辑）+ MainWindow 的占用判断
    // ─────────────────────────────────────────────────────────────

    private static int ScheduleHandoff()
    {
        // 课表：物理 09:00–09:45，语文 10:00–10:45（相差 1 小时）
        var physics = new ScheduleEntry
        {
            Id = Guid.NewGuid(), WeekdayIndex = 0, StartMin = 9 * 60, EndMin = 9 * 60 + 45, Course = "物理",
        };
        var chinese = new ScheduleEntry
        {
            Id = Guid.NewGuid(), WeekdayIndex = 0, StartMin = 10 * 60, EndMin = 10 * 60 + 45, Course = "语文",
        };

        var now = new DateTime(2026, 9, 14, 8, 0, 0);   // 周一 08:00 进程已启动
        var engine = new ScheduleEngine(() => now);
        var entries = new List<ScheduleEntry> { physics, chinese };

        // 09:00 物理到点 → 开始录音（占用中）
        now = new DateTime(2026, 9, 14, 9, 0, 0);
        var at9 = engine.Evaluate(entries);
        Console.WriteLine($"09:00 触发: [{string.Join(", ", at9.Select(o => o.Entry.Course))}]");
        bool recording = at9.Any(o => o.Entry.Course == "物理");   // MainWindow.IsRecordingActive

        // 09:45 本应自动下课（RecordingPage 的 DispatcherTimer 看门狗）。若 UI 卡死 → 没停。
        // 情景 A：看门狗正常 → 09:45 停止录音
        // 情景 B：UI 卡死/录音未停 → 09:45 之后仍在录音（用户报告的"卡死"正是这种）
        bool stoppedOnTime = false;   // 用户现场：卡死，没停

        // 10:00 语文到点
        now = new DateTime(2026, 9, 14, 10, 0, 0);
        var at10 = engine.Evaluate(entries);
        Console.WriteLine($"10:00 触发: [{string.Join(", ", at10.Select(o => o.Entry.Course))}]（空 = 该节被跳过）");

        // MainWindow.SchedulerTickAsync 的行为：
        //   循环里逐条 → StartScheduledRecordingAsync → IsRecordingActive 为真 → 跳过并托盘提示
        var started = new List<string>();
        foreach (var occ in at10)
        {
            if (recording && !stoppedOnTime)
            {
                Console.WriteLine($"  跳过「{occ.Entry.Course}」：到点自动开始时正在录音（物理那节还没停）");
                continue;
            }
            started.Add(occ.Entry.Course);
        }

        recording = recording && !stoppedOnTime;
        Console.WriteLine($"此时仍在录音的会话课程 = {(recording ? "物理" : "<无>")}");
        Console.WriteLine($"新开始的会话 = [{string.Join(", ", started)}]");

        // 用户在课间手动点了「结束录音」→ 这一整段（含语文课内容）落到"物理"名下
        bool misfiled = recording && at10.All(o => !started.Contains(o.Entry.Course));

        Console.WriteLine();
        if (misfiled)
        {
            Console.WriteLine("===== 复现成功：一节录音跨了两节课 =====");
            Console.WriteLine("  物理那节没按时自动结束 → 语文课到点被判定『正在录音，跳过』");
            Console.WriteLine("  → 语文课内容录进『物理』那条记录里 → 课表上『语文』这一节永远没有记录");
            Console.WriteLine("  → 现场表现：『语文课记录在上节物理课上，且物理课记录丢失』");
            return 2;
        }

        Console.WriteLine("结论: 未复现该交接错位");
        return 0;
    }

    private sealed class FakeApi : IApiService
    {
        private readonly List<Session> _sessions;
        public FakeApi(List<Session> sessions) => _sessions = sessions;

        public Task<Guid> CreateSessionAsync(string course, string? title) => Task.FromResult(Guid.NewGuid());
        public Task<List<Session>> ListSessionsAsync() => Task.FromResult(_sessions);
        public Task<Session> GetSessionAsync(Guid id) => Task.FromResult(_sessions[0]);
        public Task EndSessionAsync(Guid id, int durationSeconds) => Task.CompletedTask;
        public Task DeleteSessionAsync(Guid id) => Task.CompletedTask;
        public Task UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp, string type, byte[] imageData, string? url) => Task.CompletedTask;
        public Task<AudioUploadInitResult> InitAudioUploadAsync(Guid sessionId, int fileSize, string filename)
            => Task.FromResult(new AudioUploadInitResult { UploadId = "x", PartSize = 1 });
        public Task UploadAudioPartAsync(string uploadId, int partNumber, byte[] data) => Task.CompletedTask;
        public Task CompleteAudioUploadAsync(Guid sessionId, string uploadId) => Task.CompletedTask;
        public Task<Note?> GetNoteAsync(Guid sessionId) => Task.FromResult<Note?>(null);
        public Task<byte[]?> ExportNotePdfAsync(Guid sessionId) => Task.FromResult<byte[]?>(null);
    }
}
