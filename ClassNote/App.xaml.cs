using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClassNote.Services;

namespace ClassNote;

public partial class App : Application
{
    /// <summary>单实例互斥体：定时记录依赖托盘常驻，不允许第二个进程同时跑调度器。</summary>
    private static Mutex? _singleInstanceMutex;

    /// <summary>唤醒首实例主窗口的信号（第二实例启动时置位）。</summary>
    private static EventWaitHandle? _showSignal;

    private const string InstanceMutexName = "ClassNote.Standalone.SingleInstance";
    private const string ShowSignalName = "ClassNote.Standalone.ShowMainWindow";

    /// <summary>开机自启参数：登录后直接驻留托盘（不弹主窗口）。</summary>
    public const string AutostartArg = "--autostart";

    public static bool IsAutostartLaunch =>
        Environment.GetCommandLineArgs().Contains(AutostartArg, StringComparer.OrdinalIgnoreCase);

    public App()
    {
        // 全局滚轮手感优化：平滑滚动替代默认的按行跳变
        Controls.SmoothScroll.Enable();

        // 应用级异常兜底：记录并提示，避免无人值守（托盘常驻）时静默崩溃
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "classnote-crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
            }
            catch { /* 日志写入失败不再抛 */ }
            if (Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible))
                MessageBox.Show("程序遇到未处理的错误：\n" + e.Exception.Message,
                    "ClassNote 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI 线程卡顿看门狗：把"卡了多久、当时在做什么"写进 classnote-crash.log。
        // 现场问题（升级后反复卡死）最缺的就是这个证据，没有它排查只能靠猜。
        UiWatchdog.SetPhase("启动");
        UiWatchdog.Start();

        // 单实例：第二实例唤醒首实例后退出（首实例驻留托盘时用户从托盘/信号唤出）
        _singleInstanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // 主实例常驻信号监听：第二实例启动时恢复主窗口到前台
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            var dispatcher = Dispatcher;
            _ = Task.Run(() =>
            {
                while (_showSignal?.WaitOne(TimeSpan.FromSeconds(30)) == true)
                    dispatcher.BeginInvoke(() => ShowMainWindow());
            });
        }
        catch { /* 信号量创建失败仅影响"双击二次唤醒"，主流程不受影响 */ }

        // 开机自启且定时记录开启 → 直接驻留托盘；否则正常显示主窗口
        StartupRegistrar.SelfRepair();   // 设置与注册表自愈：期望自启但注册表丢失时补写
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        // 系统注销/关机：不得因"关闭到托盘"拦截，允许窗口正常关闭退出
        SessionEnding += (_, _) => mainWindow.AllowWindowClose();
        if (IsAutostartLaunch && AppSettings.Instance.Snapshot().ScheduleEnabled)
        {
            // 开机自启 + 定时记录开启 → 后台静默驻留：不弹主窗口，也不让"到点自动录音"把窗口弹出来
            mainWindow.StartHiddenToTray();
        }
        else
        {
            mainWindow.Show();
        }
    }

    /// <summary>第二实例：通知首实例把主窗口带回前台。</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
        }
        catch { /* 首实例未在监听（信号不存在）时静默退出 */ }
    }

    private static void ShowMainWindow()
    {
        if (Current?.MainWindow is MainWindow window)
            window.RestoreFromTray();
    }
}
