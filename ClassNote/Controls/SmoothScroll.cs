using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassNote.Controls;

/// <summary>
/// 全局滚轮手感优化：
/// - 用带缓动的动画平滑滚动，替代 WPF 默认"一次跳 3 行"的生硬跳变；
/// - 每次滚动的距离按当前视口大小比例计算（约 1/2 视口），小窗口不会滚太快，长列表也不会滚太慢；
/// - 连续滚动时动画实时重定向，跟手不卡顿；滚轮落在不可滚动区域时自动交给外层容器；
/// - Shift + 滚轮支持横向滚动（当存在横向滚动能力时）。
/// 只需在启动时调用一次 <see cref="Enable"/>。
///
/// 实现说明：动画用 DispatcherTimer 逐帧（约 60fps）直接调用
/// ScrollViewer.ScrollToVerticalOffset/ScrollToHorizontalOffset 驱动滚动，动画基准
/// 始终读取 ScrollViewer 的实际偏移。不采用"动画化代理依赖属性"的方案——那种方案在
/// 动画结束（FillBehavior.Stop）时属性会回落到基值，导致滚轮释放后列表自动弹回顶部。
/// </summary>
public static class SmoothScroll
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    /// <summary>每次滚轮滚动约视口的 1/2（灵敏度：一格滚半屏）。</summary>
    private const double StepFactor = 1.0 / 2.0;

    /// <summary>单次滚动动画时长；连续滚轮事件会不断重定向，不打断整体节奏。</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(380);

    /// <summary>动画帧间隔（约 60fps）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);

    static SmoothScroll()
    {
        // 在隧道阶段拦截，早于 ScrollViewer 默认的按行滚动处理（PreviewMouseWheel + Handled=true
        // 是 WPF 官方推荐的自定义滚轮方式，可完全接管默认行为）
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
    }

    /// <summary>启用全局滚轮优化（幂等，应用启动时调用一次）。</summary>
    public static void Enable()
    {
        // 静态构造函数在首次访问类型时完成类处理器注册
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 找到鼠标所在位置应滚动的容器：优先最内层；若它不可滚动则向上找第一个可滚动的，
        // 避免"内部不滚动、外层却纹丝不动"的问题
        ScrollViewer? sv = FindAncestorScrollViewer(e.OriginalSource as DependencyObject);
        while (sv != null && sv.ScrollableHeight <= 0 && sv.ScrollableWidth <= 0)
            sv = FindAncestorScrollViewer(GetParent(sv));

        // 类处理器会对路由上的每个 ScrollViewer 触发，只让真正该滚动的那一个执行
        if (sv == null || !ReferenceEquals(sv, sender))
            return;

        var state = States.GetValue(sv, _ => new ScrollState(sv));
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // 方向约定：WPF 中滚轮向前（Delta>0）为向上/向左，向后（Delta<0）为向下/向右，
        // 因此目标偏移 = 当前偏移 - Delta 折算的步长。
        if (shift && sv.ScrollableWidth > 0)
        {
            var step = Math.Max(sv.ViewportWidth * StepFactor, 1.0);
            var target = Math.Clamp(sv.HorizontalOffset - e.Delta / 120.0 * step, 0, sv.ScrollableWidth);
            state.ScrollToHorizontal(target);
        }
        else if (sv.ScrollableHeight > 0)
        {
            var step = Math.Max(sv.ViewportHeight * StepFactor, 1.0);
            var target = Math.Clamp(sv.VerticalOffset - e.Delta / 120.0 * step, 0, sv.ScrollableHeight);
            state.ScrollToVertical(target);
        }
        else
        {
            return; // 无可滚动方向，交给默认行为
        }

        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv)
                return sv;
            d = GetParent(d);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject d)
    {
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(d);
        if (d is ContentElement ce)
            return ContentOperations.GetParent(ce);
        if (d is FrameworkContentElement fce)
            return fce.Parent;
        return null;
    }

    /// <summary>
    /// 单个 ScrollViewer 的滚动状态：持有逐帧动画定时器，直接驱动 ScrollViewer 偏移。
    /// 每次滚轮事件从 ScrollViewer 的实际偏移重新起算并重定向动画；若检测到外部滚动
    /// （拖滚动条、键盘翻页等），立即中断动画，以用户的实际位置为准。
    /// </summary>
    private sealed class ScrollState
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _timer;

        private double _from;
        private double _to;
        private bool _vertical;
        private DateTime _start;

        /// <summary>当前位置与动画预期位置偏离超过该值时，视为外部滚动（拖滚动条/键盘），中断动画。</summary>
        private const double ExternalJumpThreshold = 24.0;

        public ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _timer = new DispatcherTimer { Interval = TickInterval };
            _timer.Tick += OnTick;
        }

        public void ScrollToVertical(double target)
        {
            var from = _viewer.VerticalOffset;
            if (Math.Abs(target - from) < 0.5)
                return; // 已在目标位置（如滚动到边界），无需动画
            BeginAnimation(from, target, vertical: true);
        }

        public void ScrollToHorizontal(double target)
        {
            var from = _viewer.HorizontalOffset;
            if (Math.Abs(target - from) < 0.5)
                return;
            BeginAnimation(from, target, vertical: false);
        }

        private void BeginAnimation(double from, double to, bool vertical)
        {
            _from = from;
            _to = to;
            _vertical = vertical;
            _start = DateTime.UtcNow;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var t = (DateTime.UtcNow - _start).TotalMilliseconds / Duration.TotalMilliseconds;
            double value;
            if (t >= 1.0)
            {
                _timer.Stop();
                value = _to;
            }
            else
            {
                // 与旧实现一致的 QuadraticEase EaseOut 缓动：先快后慢
                var eased = 1 - Math.Pow(1 - t, 2);
                value = _from + (_to - _from) * eased;
            }

            // 逐帧以 ScrollViewer 实际偏移为基准校验：ScrollToVerticalOffset 的生效和
            // ScrollChanged 事件是延迟到下一次布局才发生的，无法用它区分"本组件的滚动"
            // 与"用户拖滚动条"，因此改为对比实际位置与动画预期位置。偏离过大说明存在
            // 外部滚动（拖滚动条、键盘翻页、内容重排等），立即中断动画，以用户位置为准。
            var actual = _vertical ? _viewer.VerticalOffset : _viewer.HorizontalOffset;
            if (Math.Abs(actual - value) > ExternalJumpThreshold)
            {
                _timer.Stop();
                return;
            }

            if (_vertical)
                _viewer.ScrollToVerticalOffset(value);
            else
                _viewer.ScrollToHorizontalOffset(value);
        }
    }
}
