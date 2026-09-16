using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClassNote.Controls;

/// <summary>
/// 长按 = 右键的附加行为（触摸屏专用）。
///
/// 背景（用户现场：海康威视一体机，Windows 10 桌面模式）：WPF 在 Windows 10 桌面上**不会**
/// 把触摸"按住不放"翻译成鼠标右键，因此 <see cref="UIElement.ContextMenuOpening"/> 永远不触发，
/// 程序里的右键菜单（查看笔记 / 导出 PDF / 删除）在一体机上**根本没有入口**——
/// 手指按住只有选中高亮，于是用户报告"长按无法替代鼠标右键"。
/// （Windows 自带的"按住以右键单击"只在平板模式的触摸外壳里生效，桌面模式下得由应用自己实现。）
///
/// 本行为补上这件事：按下 → 在 <see cref="HoldMilliseconds"/> 内没有抬起、也没有明显移动，
/// 就认为用户在"右键"这一行，把该元素的 <see cref="FrameworkElement.ContextMenu"/> 就地打开。
///
/// 刻意的取舍：
/// · **只认触摸/触笔**：鼠标右键本就由 WPF 处理，重复接管会让真右键菜单闪两次；
/// · **移动即取消**：按住后滑动是要滚动列表，不能弹菜单（阈值 <see cref="MoveTolerance"/>）；
/// · **不吞事件**：不设置 <c>Handled</c>，正常点击、拖动、滚动行为完全不受影响；
/// · **触摸与触笔去重**：WPF 对同一次触摸可能同时抛 Touch 与 Stylus 事件，只认先到的那条；
/// · **异常全部吞掉**：输入处理里抛异常会冒到全局异常兜底，反而更糟。
/// </summary>
public static class TouchLongPress
{
    /// <summary>按住多久算长按（毫秒）。比系统"按住以右键单击"的默认值略短，手指不用等太久。</summary>
    public const int HoldMilliseconds = 550;

    /// <summary>手指抖动容差（设备无关像素）：超过就当作"在滑动"，取消长按。</summary>
    public const double MoveTolerance = 14.0;

    /// <summary>
    /// 是否启用长按。直接读设置（<c>设置 → 录音设置 → 触摸屏优化</c>），因此在设置里一改就生效，
    /// 不需要额外的装配步骤，也不会出现"代码里默认开、设置里默认关"这种两处真相。
    /// 普通鼠标电脑上关掉它没有任何影响（长按只认触摸/触笔）。
    /// </summary>
    public static bool Enabled
    {
        get
        {
            try { return Services.AppSettings.Instance.Snapshot().TouchOptimizations; }
            catch { return true; }   // 设置读不出来时保持可用：一体机上关掉等于功能消失
        }
    }

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TouchLongPress),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>在样式里声明 <c>controls:TouchLongPress.IsEnabled="True"</c> 即可挂上（幂等）。</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        if (e.NewValue is true)
            Attach(element);
        else
            Detach(element);
    }

    // ── 每个元素一份状态 ────────────────────────────────────────

    private sealed class State
    {
        public DispatcherTimer? Timer;

        /// <summary>手势按下点（元素坐标系），用于"移动即取消"判定。</summary>
        public Point Origin;

        /// <summary>触笔设备；为 null 表示这条手势来自触摸事件。</summary>
        public StylusDevice? Stylus;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(TouchLongPress), new PropertyMetadata(null));

    private static State? GetState(DependencyObject element) => (State?)element.GetValue(StateProperty);

    private static void Attach(UIElement element)
    {
        element.PreviewTouchDown += OnTouchDown;
        element.PreviewTouchUp += OnTouchUp;
        element.TouchMove += OnTouchMove;
        element.TouchLeave += OnTouchLeave;
        // 触笔（含部分驱动把触摸报成笔）：同一手势的第二条路径，靠 State.Stylus 去重
        element.PreviewStylusDown += OnStylusDown;
        element.PreviewStylusUp += OnStylusUp;
    }

    private static void Detach(UIElement element)
    {
        element.PreviewTouchDown -= OnTouchDown;
        element.PreviewTouchUp -= OnTouchUp;
        element.TouchMove -= OnTouchMove;
        element.TouchLeave -= OnTouchLeave;
        element.PreviewStylusDown -= OnStylusDown;
        element.PreviewStylusUp -= OnStylusUp;
        Cancel(element);
    }

    // ── 触摸路径 ────────────────────────────────────────────────

    private static void OnTouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        var state = new State();                     // Stylus = null → 这是触摸手势
        element.SetValue(StateProperty, state);
        BeginHold(element, e.GetTouchPoint(element).Position, state);
    }

    private static void OnTouchUp(object? sender, TouchEventArgs e) => Cancel(sender);

    private static void OnTouchLeave(object? sender, TouchEventArgs e) => Cancel(sender);

    private static void OnTouchMove(object? sender, TouchEventArgs e)
    {
        if (sender is not UIElement element || GetState(element) is not { } state)
            return;
        if (state.Stylus != null)
            return;                                  // 这条触摸属于笔的手势，已由笔路径处理

        var current = e.GetTouchPoint(element).Position;
        if (Math.Abs(current.X - state.Origin.X) > MoveTolerance ||
            Math.Abs(current.Y - state.Origin.Y) > MoveTolerance)
        {
            Cancel(element);                         // 用户在滑动列表，不是长按
        }
    }

    // ── 触笔路径 ────────────────────────────────────────────────

    private static void OnStylusDown(object? sender, StylusDownEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        var stylus = e.StylusDevice;
        var existing = GetState(element);

        if (existing != null)
        {
            // 触摸事件先到：把这条记为"同一手势的笔事件"，直接忽略，避免菜单开两次
            existing.Stylus = stylus;
            return;
        }

        var state = new State { Stylus = stylus };
        element.SetValue(StateProperty, state);
        BeginHold(element, e.GetPosition(element), state);
    }

    private static void OnStylusUp(object? sender, StylusEventArgs e) => Cancel(sender);

    // ── 共用 ────────────────────────────────────────────────────

    /// <summary>启动长按计时；到时仍未抬起、且没有移动，就打开右键菜单。</summary>
    private static void BeginHold(UIElement element, Point origin, State state)
    {
        state.Origin = origin;

        if (!Enabled)
        {
            Cancel(element);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(HoldMilliseconds),
        };
        timer.Tick += (_, _) =>
        {
            Cancel(element);                         // 先停表，避免二次触发
            TryOpenContextMenu(element);
        };
        state.Timer = timer;
        timer.Start();
    }

    private static void Cancel(object? sender)
    {
        if (sender is not DependencyObject element)
            return;

        var state = GetState(element);
        if (state?.Timer is { } timer)
        {
            timer.Stop();
            state.Timer = null;
        }
        element.SetValue(StateProperty, null);
    }

    private static void TryOpenContextMenu(UIElement element)
    {
        try
        {
            if (element is not FrameworkElement framework)
                return;

            var menu = framework.ContextMenu;
            if (menu == null || menu.Items.Count == 0)
                return;                              // 没有菜单就别弹一个空框

            menu.PlacementTarget = framework;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        catch
        {
            // 输入处理里抛异常会冒到全局兜底，反而更糟：一律静默
        }
    }
}
