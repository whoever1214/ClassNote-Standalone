using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;

namespace ClassNote.Views;

public partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _healthTimer;
    private readonly ILlmService _llm = new LlmService();
    private int _healthCheckInFlight;

    /// <summary>
    /// Raised when the user clicks "开始记录" — carries the new session ID, course name and selected microphone.
    /// </summary>
    public event EventHandler<(Guid SessionId, string Course, RecordingConfig Config)>? StartRecordingRequested;

    /// <summary>
    /// Raised when the user double-clicks a session in the list — carries the session ID.
    /// </summary>
    public event EventHandler<Guid>? SessionSelected;

    /// <summary>Raised when the user clicks 顶部「定时记录」— 请求跳转到"定时记录 · 每周课表"页面。</summary>
    public event EventHandler? ScheduleRequested;

    public MainPage()
    {
        InitializeComponent();

        var apiService = new ApiService();
        _viewModel = new MainViewModel(apiService);
        DataContext = _viewModel;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        // LLM 健康检查：每 30 秒检测一次 v1 接口（本地模型服务可能中途挂起/恢复）
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _healthTimer.Tick += async (_, _) => await CheckLlmHealthAsync();

        Loaded += async (_, _) =>
        {
            await _viewModel.LoadSessionsAsync();
            EnsurePolling();
            _healthTimer.Start();
            await CheckLlmHealthAsync();
        };
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            _healthTimer.Stop();
        };
    }

    // ── LLM 状态 ─────────────────────────────────────────

    /// <summary>
    /// 检测 LLM v1 接口健康状态并刷新右上角状态胶囊（灰=未配置、绿=正常、红=不可达）。
    /// 失败静默，等下一轮轮询重试；用互斥位避免上一轮未结束时重入。
    /// </summary>
    private async Task CheckLlmHealthAsync()
    {
        if (Interlocked.Exchange(ref _healthCheckInFlight, 1) != 0)
            return;
        try
        {
            var result = await _llm.CheckHealthAsync();
            await Dispatcher.InvokeAsync(() => ApplyLlmHealth(result));
        }
        catch
        {
            // 健康检查失败静默：下一轮自动重试
        }
        finally
        {
            Interlocked.Exchange(ref _healthCheckInFlight, 0);
        }
    }

    private void ApplyLlmHealth(LlmHealthResult result)
    {
        if (LlmHealthText == null || LlmHealthDot == null)
            return;
        LlmHealthText.Text = result.Message;
        // 胶囊限宽，文案过长会省略显示，完整信息放进 ToolTip
        if (LlmHealthBadge != null)
            LlmHealthBadge.ToolTip = $"{result.Message}\n点击可打开设置";
        var color = result.IsOk
            ? (Brush)FindResource("SuccessBrush")
            : result.IsConfigured
                ? (Brush)FindResource("DangerBrush")
                : (Brush)FindResource("TextHintBrush");
        LlmHealthDot.Fill = color;
    }

    private void LlmHealthBadge_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var settings = new SettingsWindow { Owner = Window.GetWindow(this) };
        settings.ShowDialog();
        // 保存设置后立即刷新一次状态
        _ = CheckLlmHealthAsync();
    }

    // ── 顶部操作区 ────────────────────────────────────────

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = Window.GetWindow(this) };
        settings.ShowDialog();
    }

    /// <summary>请求跳转到「定时记录 · 每周课表」页面（由 MainWindow 处理实际导航）。</summary>
    private void ScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // 1. 弹出配置界面：确认课程、输入标题、选择声音来源与设备
        //    （设备名 + 稳定 ID 一并枚举，避免二次枚举顺序不一致选错设备；
        //     默认值取自「设置 → 录音设置」，用户不必每次重选）
        var audio = new AudioService();
        var saved = AppSettings.ToRecordingConfig(AppSettings.Instance.Snapshot());
        var setup = new RecordingSetupWindow(
            _viewModel.SelectedCourse,
            _viewModel.Courses,
            audio.GetInputDevices(),
            audio.GetInputDeviceIds(),
            audio.GetOutputDevices(),
            audio.GetOutputDeviceIds(),
            saved)
        {
            Owner = Window.GetWindow(this),
        };

        if (setup.ShowDialog() != true || setup.Result == null)
            return; // 用户取消

        // 2. 用配置结果创建会话
        _viewModel.SelectedCourse = setup.Result.Course;
        var sessionId = await _viewModel.StartRecordingAsync(setup.Result.Title);
        if (sessionId.HasValue)
        {
            await _viewModel.LoadSessionsAsync();
            StartRecordingRequested?.Invoke(this,
                (sessionId.Value, setup.Result.Course, setup.Result.AudioConfig));
        }
    }

    // ── 列表刷新 ─────────────────────────────────────────

    /// <summary>
    /// Refresh once; keeps polling automatically while any session is still
    /// being processed (ends only when everything reaches a terminal state).
    /// Silent refresh: no loading animation, so UI stays calm during polling.
    /// </summary>
    private async Task RefreshAsync()
    {
        await _viewModel.LoadSessionsAsync(showLoading: false);
        EnsurePolling();
    }

    private void EnsurePolling()
    {
        if (_viewModel.HasPendingSessions)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    // ── 记录列表交互 ──────────────────────────────────────

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击勾选框用于切换选择状态，不应触发打开笔记
        if (e.OriginalSource is DependencyObject src && FindVisualParent<CheckBox>(src) != null)
        {
            e.Handled = true;
            return;
        }

        if (sender is ListView lv && lv.SelectedItem is Session session)
        {
            SessionSelected?.Invoke(this, session.Id);
        }
    }

    /// <summary>
    /// "全选"复选框点击：全部已选 → 取消全选；未选或部分选 → 全部勾选。
    /// 只覆盖当前筛选出的记录。使用 OneWay 绑定展示三态，实际状态由这里计算后写回 ViewModel。
    /// </summary>
    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox)
            return;

        var total = _viewModel.VisibleCount;
        var selected = _viewModel.SelectedCount;
        _viewModel.SelectAll = total > 0 && selected == total ? false : true;
    }

    // ── 单条记录右键菜单：查看笔记 / 导出 PDF / 删除 ──────────────

    /// <summary>
    /// 右键菜单弹出前，把被右键那一行的记录写进菜单的 DataContext。
    /// ContextMenu 是 Popup，不参与视觉树继承，靠继承拿不到行数据（菜单项会拿到 null，
    /// 右键删除/导出会静默失效）；右键与 Shift+F10 都会先触发本事件，两种入口都覆盖。
    /// </summary>
    private void SessionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListView listView)
            AttachSessionToContextMenu(listView, e.OriginalSource as DependencyObject);
    }

    /// <summary>
    /// 把命中的那一行的记录挂到它自己的右键菜单上。返回命中的记录；
    /// 右键落在空白处（没有行）或该行没有菜单时返回 null。
    /// public 以便无头渲染工具（devtools/RenderHarness）能直接回归这条右键路径。
    /// </summary>
    public static Session? AttachSessionToContextMenu(ListView listView, DependencyObject? hit)
    {
        var item = ItemsControl.ContainerFromElement(listView, hit) as ListViewItem;
        if (item?.ContextMenu is not { } menu)
            return null;

        menu.DataContext = item.DataContext;
        return item.DataContext as Session;
    }

    /// <summary>右键菜单项绑定的记录（由 SessionList_ContextMenuOpening 写入 ContextMenu）。</summary>
    private static Session? SessionOf(object sender)
        => (sender as FrameworkElement)?.DataContext as Session;

    private void ContextViewNote_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is { } session)
            SessionSelected?.Invoke(this, session.Id);
    }

    /// <summary>导出单条记录的笔记为 PDF（文件在用户选择的文件夹内，按标题命名）。</summary>
    private async void ContextExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is not { } session)
            return;

        var folder = PickFolder();
        if (folder == null)
            return; // 用户取消

        try
        {
            var path = await _viewModel.ExportSessionPdfAsync(session, folder);
            if (path == null)
            {
                MessageBox.Show("这条记录还没有生成笔记，无法导出 PDF。\n请先双击打开笔记并等待 AI 生成完成。",
                    "导出 PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBox.Show($"已导出到：\n{path}", "导出 PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>删除被右键的单条记录（含录音、截图与笔记）。</summary>
    private async void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is not { } session)
            return;

        var label = string.IsNullOrWhiteSpace(session.Title) ? session.Course : session.Title;
        var confirm = MessageBox.Show(
            $"确认删除记录「{label}」？\n删除后其录音、截图与笔记将一并永久移除，且无法恢复。",
            "删除记录",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        SetBatchButtonsEnabled(false);
        try
        {
            await _viewModel.DeleteSessionAsync(session);
            await _viewModel.LoadSessionsAsync(showLoading: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBatchButtonsEnabled(true);
        }
    }

    // ── 批量操作 ─────────────────────────────────────────

    /// <summary>批量导出勾选会话的笔记为 PDF 到用户选择的文件夹。</summary>
    private async void ExportPdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSelection)
            return;

        var folder = PickFolder();
        if (folder == null)
            return; // 用户取消

        SetBatchButtonsEnabled(false);
        try
        {
            var (exported, skipped) = await _viewModel.ExportSelectedPdfAsync(folder);
            var message = $"已导出 {exported} 份 PDF 到：\n{folder}";
            if (skipped > 0)
                message += $"\n\n另有 {skipped} 条记录尚未生成笔记，已自动跳过。";
            MessageBox.Show(message, "批量导出 PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"批量导出失败：{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBatchButtonsEnabled(true);
        }
    }

    /// <summary>删除勾选的会话（含录音、截图与笔记）。</summary>
    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSelection)
            return;

        var confirm = MessageBox.Show(
            $"确认删除选中的 {_viewModel.SelectedCount} 条记录？\n删除后其录音、截图与笔记将一并永久移除，且无法恢复。",
            "删除记录",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        SetBatchButtonsEnabled(false);
        try
        {
            await _viewModel.DeleteSelectedAsync();
            await _viewModel.LoadSessionsAsync(showLoading: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBatchButtonsEnabled(true);
        }
    }

    /// <summary>批量操作期间禁用两个批量按钮，避免连点或与自动刷新冲突。</summary>
    private void SetBatchButtonsEnabled(bool enabled)
    {
        ExportPdfButton.IsEnabled = enabled && _viewModel.HasSelection;
        DeleteSelectedButton.IsEnabled = enabled && _viewModel.HasSelection;
    }

    private static string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 PDF 导出文件夹",
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
                return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
