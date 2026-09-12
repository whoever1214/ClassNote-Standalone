using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClassNote.Services;
using ClassNote.Views;
using WinForms = System.Windows.Forms;

namespace ClassNote;

public partial class MainWindow : Window
{
    /// <summary>调度器评估周期：课表到点后至多延迟一个周期开始录音。</summary>
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _schedulerTimer;
    private readonly ScheduleEngine _scheduleEngine;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        // 单机模式：启动直接进入主页面（无需服务端、无需认证）
        NavigateToMainPage();

        // 每周课表 → 定时录音：进程存活期间持续评估（AppSettings 总开关控制是否生效）
        _scheduleEngine = new ScheduleEngine();
        _schedulerTimer = new DispatcherTimer { Interval = SchedulerInterval };
        _schedulerTimer.Tick += async (_, _) => await SchedulerTickAsync();
        _schedulerTimer.Start();

        Loaded += (_, _) => RefreshTrayResident();
        Closed += (_, _) => DisposeTray();
    }

    /// <summary>允许主窗口真正关闭（托盘"退出"、系统注销/关机时调用）。</summary>
    public void AllowWindowClose() => _allowClose = true;

    /// <summary>
    /// Navigate to the main page (session list and course selection).
    /// </summary>
    public void NavigateToMainPage()
    {
        var mainPage = new MainPage();
        mainPage.StartRecordingRequested += OnMainPageStartRecording;
        mainPage.SessionSelected += OnMainPageSessionSelected;
        mainPage.ScheduleRequested += OnMainPageScheduleRequested;
        MainFrame.Navigate(mainPage);
    }

    /// <summary>
    /// Navigate to the timetable page (read-only weekly view + toggles).
    /// </summary>
    public void NavigateToSchedulePage()
    {
        var schedulePage = new Views.SchedulePage();
        schedulePage.BackRequested += (_, _) => NavigateToMainPage();
        MainFrame.Navigate(schedulePage);
    }

    private void OnMainPageScheduleRequested(object? sender, EventArgs e) => NavigateToSchedulePage();

    /// <summary>
    /// Navigate to the recording page for a new session.
    /// </summary>
    public void NavigateToRecordingPage(Guid sessionId, string course, RecordingConfig? config = null,
        DateTime? autoStopAt = null)
    {
        var recordingPage = new RecordingPage(sessionId, course, config, autoStopAt);
        recordingPage.RecordingEnded += OnRecordingEnded;
        MainFrame.Navigate(recordingPage);
    }

    /// <summary>
    /// Navigate to the note viewing page for a given session.
    /// </summary>
    public void NavigateToNoteViewPage(Guid sessionId)
    {
        var notePage = new NoteViewPage(sessionId);
        notePage.BackRequested += OnNoteViewBackRequested;
        MainFrame.Navigate(notePage);
    }

    /// <summary>
    /// 启动即后台驻留托盘：窗口"构建完成但不显示"——不抢焦点、不弹到前台，用户全程看不到。
    ///
    /// 必须先 Show 一次再 Hide：WPF 窗口只有被显示过才会创建 HWND 与视觉树（<c>IsLoaded</c>），
    /// 之后在隐藏状态下导航到录音页才会触发 <c>Loaded</c>；而定时录音正是靠录音页的 Loaded 启动的，
    /// 若窗口从未被显示过，到点自动录音会因为页面永远不 Loaded 而静默失效。
    /// 由于 Show 与 Hide 在同一段 UI 代码里连续执行（不回到消息循环），窗口不会被绘制，无闪烁。
    /// </summary>
    public void StartHiddenToTray()
    {
        RefreshTrayResident();   // 先备好托盘图标（气泡提示与托盘菜单都依赖它）
        ShowActivated = false;   // 构建期间不抢焦点
        Show();
        Hide();
        ShowActivated = true;    // 还原默认：用户主动唤出时正常激活
    }

    // ── 定时记录调度 ──────────────────────────────────────────

    /// <summary>当前是否正在录音（调度冲突判断 + 关闭窗口驻留判断）。</summary>
    public bool IsRecordingActive => MainFrame.Content is RecordingPage page && page.IsRecording;

    /// <summary>
    /// 课表保存后回调：刷新托盘驻留状态。
    /// </summary>
    public void OnScheduleSettingsSaved() => RefreshTrayResident();

    /// <summary>
    /// 每个调度周期评估一次每周课表：到点且未在录音 → 自动开始录音；
    /// 已在录音（手动或其它定时）→ 跳过本节（一天只跳一次）并托盘提示；
    /// 唤醒/繁忙错过开始时刻 ≤10 分钟会自动补触发。
    /// </summary>
    private async Task SchedulerTickAsync()
    {
        var snapshot = AppSettings.Instance.Snapshot();
        if (!snapshot.ScheduleEnabled)
            return;

        var entries = LocalRepository.Instance.ListScheduleEntries();
        var due = _scheduleEngine.Evaluate(entries);
        if (due.Count == 0)
            return;

        // 有其它可见窗口（课表编辑器/设置窗口等模态操作）时本次不打断用户
        if (HasVisibleSecondaryWindow())
        {
            ShowTrayBalloonIfHidden("定时记录已跳过",
                $"检测到正在操作其它窗口，本次到点自动开始已跳过（{due[0].Entry.Course}）。");
            return;
        }

        // 正常情况同一到点时刻至多一节；逐条触发是防御性处理
        foreach (var occ in due)
            await StartScheduledRecordingAsync(occ);
    }

    private async Task StartScheduledRecordingAsync(ScheduleOccurrence occ)
    {
        var entry = occ.Entry;
        if (IsRecordingActive)
        {
            ShowTrayBalloonIfHidden("定时记录已跳过",
                $"「{entry.Course}」到点自动开始时正在录音，本次跳过。");
            return;
        }

        // 无可用麦克风时不创建空会话，跳过并在托盘提示（仅麦克风来源强依赖采集端点）
        var settings = AppSettings.Instance.Snapshot();
        var config = AppSettings.ToRecordingConfig(settings);
        var audio = new AudioService();
        if (config.NeedsMicrophone)
        {
            var micIds = audio.GetInputDeviceIds();
            if (micIds.Length == 0)
            {
                ShowTrayBalloonIfHidden("定时记录已跳过", $"未检测到麦克风，「{entry.Course}」本次跳过。");
                return;
            }
            // 设置为"混合"但设备已拔出时，把失效的麦克风 ID 去掉，由采集服务回退系统默认设备
            if (config.MicId != null && !micIds.Contains(config.MicId))
                config = config with { MicId = null };
        }

        try
        {
            var api = new ApiService();
            var sessionId = await api.CreateSessionAsync(entry.Course, entry.Title);

            // 后台静默开始：主窗口保持原状态（隐藏/最小化都不唤出），
            // 录音页在隐藏窗口内导航即可 Loaded 并开始录音；用户下次打开主界面就能看到本次录音。
            NavigateToRecordingPage(sessionId, entry.Course, config, autoStopAt: occ.End);
            ShowTrayBalloonIfHidden("定时记录已自动开始",
                $"{entry.WeekdayName} {entry.Course}（{occ.Start:HH:mm}–{occ.End:HH:mm}），" +
                $"来源：{AudioSourceKinds.ToDisplayName(config.Source)}，到点自动结束。");
        }
        catch (Exception ex)
        {
            ShowTrayBalloonIfHidden("定时记录启动失败", $"{entry.Course}：{ex.Message}");
        }
    }

    /// <summary>是否有本应用除主窗口外的可见窗口（模态对话框等）。</summary>
    private bool HasVisibleSecondaryWindow()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (ReferenceEquals(w, this))
                continue;
            if (w.IsVisible)
                return true;
        }
        return false;
    }

    // ── 托盘常驻 ─────────────────────────────────────────────

    /// <summary>关闭主窗口 → 驻留托盘（定时记录开启 / 正在录音 / 自启驻留时）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (WantsTrayResident() && _trayIcon != null)
        {
            e.Cancel = true;
            HideToTray(balloon: true);
            return;
        }
        base.OnClosing(e);
    }

    private bool WantsTrayResident()
        => AppSettings.Instance.Snapshot().ScheduleEnabled || IsRecordingActive;

    /// <summary>隐藏到托盘（确保图标存在，并按需气泡提示）。</summary>
    private void HideToTray(bool balloon)
    {
        RefreshTrayResident();
        if (_trayIcon == null)
            return;
        Hide();
        if (balloon)
            ShowTrayBalloonIfHidden("ClassNote 仍在后台运行",
                "定时记录与进行中的录音不会中断。双击托盘图标可回到主界面。", force: true);
    }

    /// <summary>
    /// 按需创建/回收托盘图标。回收仅在窗口可见时进行：
    /// 窗口已隐藏而条件消失（如定时关闭）时保留图标，等用户恢复窗口后再清理，避免"无窗无托盘"死局。
    /// </summary>
    public void RefreshTrayResident()
    {
        if (WantsTrayResident())
            EnsureTrayIcon();
        else if (IsVisible)
            DisposeTray();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null)
            return;
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (iconStream == null)
                return;

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("打开主界面", null, (_, _) => RestoreFromTray());
            menu.Items.Add("课表与定时设置", null, (_, _) => OpenScheduleEditor());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                _allowClose = true;
                Close();
            });

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(iconStream),
                Text = "ClassNote 课堂笔记助手（定时记录运行中）",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch
        {
            // 托盘创建失败（无桌面会话/资源异常）静默降级，不影响应用主体
            _trayIcon = null;
        }
    }

    private void DisposeTray()
    {
        var icon = _trayIcon;
        _trayIcon = null;
        try
        {
            if (icon != null)
            {
                icon.Visible = false;
                icon.Dispose();
            }
        }
        catch { /* 清理失败无害 */ }
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        RefreshTrayResident(); // 回到可见状态后，若已不再需要常驻则清理托盘
    }

    private void EnsureVisible()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
    }

    /// <summary>打开课表页（主窗口 Frame 内导航；主窗口隐藏时先恢复可见）。</summary>
    private void OpenScheduleEditor()
    {
        EnsureVisible();
        NavigateToSchedulePage();
    }

    /// <summary>托盘气泡提示。主窗口可见时默认不打扰（force=true 强制显示）。</summary>
    public void ShowTrayBalloonIfHidden(string title, string text, bool force = false)
    {
        if (!force && IsVisible && WindowState != WindowState.Minimized)
            return;
        try
        {
            _trayIcon?.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);
        }
        catch { /* 托盘不可用时静默 */ }
    }

    // ── Event handlers ──────────────────────────────────────

    private void OnMainPageStartRecording(object? sender, (Guid SessionId, string Course, RecordingConfig Config) args)
    {
        NavigateToRecordingPage(args.SessionId, args.Course, args.Config);
    }

    private void OnMainPageSessionSelected(object? sender, Guid sessionId)
    {
        NavigateToNoteViewPage(sessionId);
    }

    private void OnRecordingEnded(object? sender, System.EventArgs e)
    {
        NavigateToMainPage();
        RefreshTrayResident();
    }

    private void OnNoteViewBackRequested(object? sender, System.EventArgs e)
    {
        NavigateToMainPage();
    }
}
