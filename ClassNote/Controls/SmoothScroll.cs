using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClassNote.Controls;

/// <summary>
/// 全局滚轮手感优化：
/// - 用带缓动的动画平滑滚动，替代 WPF 默认"一次跳 3 行"的生硬跳变；
/// - 每次滚动的距离按当前视口大小比例计算（约 1/3 视口），小窗口不会滚太快，长列表也不会滚太慢；
/// - 连续滚动时动画实时重定向，跟手不卡顿；滚轮落在不可滚动区域时自动交给外层容器；
/// - Shift + 滚轮支持横向滚动（当存在横向滚动能力时）。
/// 只需在启动时调用一次 <see cref="Enable"/>。
/// </summary>
public static class SmoothScroll
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    /// <summary>每次滚轮滚动约视口的 1/3。</summary>
    private const double StepFactor = 1.0 / 3.0;

    /// <summary>单次滚动动画时长；连续滚轮事件会不断重定向，不打断整体节奏。</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(380);

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

        var state = States.GetValue(sv, _ => new ScrollState(_));
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (shift && sv.ScrollableWidth > 0)
        {
            var step = Math.Max(sv.ViewportWidth * StepFactor, 1.0);
            var target = Math.Clamp(state.HorizontalOffset + e.Delta / 120.0 * step, 0, sv.ScrollableWidth);
            state.ScrollToHorizontal(target);
        }
        else if (sv.ScrollableHeight > 0)
        {
            var step = Math.Max(sv.ViewportHeight * StepFactor, 1.0);
            var target = Math.Clamp(state.VerticalOffset + e.Delta / 120.0 * step, 0, sv.ScrollableHeight);
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

    /// <summary>单个 ScrollViewer 的滚动状态：持有偏移量代理并负责启动/重定向动画。</summary>
    private sealed class ScrollState
    {
        private readonly ScrollViewerOffsetMediator _mediator = new();

        public ScrollState(ScrollViewer viewer)
        {
            _mediator.ScrollViewer = viewer;
        }

        public double VerticalOffset => _mediator.VerticalOffset;
        public double HorizontalOffset => _mediator.HorizontalOffset;

        public void ScrollToVertical(double target)
        {
            var from = _mediator.VerticalOffset;
            if (Math.Abs(target - from) < 0.5)
                return;
            _mediator.BeginAnimation(ScrollViewerOffsetMediator.VerticalOffsetProperty, NewAnimation(from, target));
        }

        public void ScrollToHorizontal(double target)
        {
            var from = _mediator.HorizontalOffset;
            if (Math.Abs(target - from) < 0.5)
                return;
            _mediator.BeginAnimation(ScrollViewerOffsetMediator.HorizontalOffsetProperty, NewAnimation(from, target));
        }

        private static DoubleAnimation NewAnimation(double from, double to) => new(from, to, Duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            // 动画结束后立即释放，避免把滚动位置"钉"住，拖滚动条/键盘滚动不受影响
            FillBehavior = FillBehavior.Stop,
        };
    }

    /// <summary>
    /// 可动画化的偏移量代理：ScrollViewer 的 VerticalOffset/HorizontalOffset 是只读依赖属性，
    /// 无法直接动画化，这里用代理属性 + BeginAnimation 驱动 ScrollToVerticalOffset/ScrollToHorizontalOffset。
    /// 同时监听 ScrollChanged，把外部滚动（拖滚动条、键盘、内容变化）同步回来，保证动画基准一致。
    /// </summary>
    private sealed class ScrollViewerOffsetMediator : Animatable
    {
        public static readonly DependencyProperty ScrollViewerProperty =
            DependencyProperty.Register(nameof(ScrollViewer), typeof(ScrollViewer), typeof(ScrollViewerOffsetMediator),
                new PropertyMetadata(null, OnScrollViewerChanged));

        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.Register(nameof(VerticalOffset), typeof(double), typeof(ScrollViewerOffsetMediator),
                new PropertyMetadata(0.0, OnVerticalOffsetChanged, OnCoerceVerticalOffset));

        public static readonly DependencyProperty HorizontalOffsetProperty =
            DependencyProperty.Register(nameof(HorizontalOffset), typeof(double), typeof(ScrollViewerOffsetMediator),
                new PropertyMetadata(0.0, OnHorizontalOffsetChanged, OnCoerceHorizontalOffset));

        private bool _updating;

        protected override Freezable CreateInstanceCore() => new ScrollViewerOffsetMediator();

        public ScrollViewer? ScrollViewer
        {
            get => (ScrollViewer?)GetValue(ScrollViewerProperty);
            set => SetValue(ScrollViewerProperty, value);
        }

        public double VerticalOffset
        {
            get => (double)GetValue(VerticalOffsetProperty);
            set => SetValue(VerticalOffsetProperty, value);
        }

        public double HorizontalOffset
        {
            get => (double)GetValue(HorizontalOffsetProperty);
            set => SetValue(HorizontalOffsetProperty, value);
        }

        private static void OnScrollViewerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var mediator = (ScrollViewerOffsetMediator)d;
            if (e.OldValue is ScrollViewer old)
                old.ScrollChanged -= mediator.OnScrollChanged;
            if (e.NewValue is ScrollViewer sv)
                sv.ScrollChanged += mediator.OnScrollChanged;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_updating)
                return;
            _updating = true;
            try
            {
                SetCurrentValue(VerticalOffsetProperty, e.VerticalOffset);
                SetCurrentValue(HorizontalOffsetProperty, e.HorizontalOffset);
            }
            finally
            {
                _updating = false;
            }
        }

        private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var mediator = (ScrollViewerOffsetMediator)d;
            if (mediator._updating || mediator.ScrollViewer is not { } sv)
                return;
            sv.ScrollToVerticalOffset((double)e.NewValue);
        }

        private static object OnCoerceVerticalOffset(DependencyObject d, object baseValue)
        {
            var mediator = (ScrollViewerOffsetMediator)d;
            if (mediator.ScrollViewer is not { } sv || sv.ScrollableHeight <= 0)
                return baseValue;
            return Math.Clamp((double)baseValue, 0, sv.ScrollableHeight);
        }

        private static void OnHorizontalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var mediator = (ScrollViewerOffsetMediator)d;
            if (mediator._updating || mediator.ScrollViewer is not { } sv)
                return;
            sv.ScrollToHorizontalOffset((double)e.NewValue);
        }

        private static object OnCoerceHorizontalOffset(DependencyObject d, object baseValue)
        {
            var mediator = (ScrollViewerOffsetMediator)d;
            if (mediator.ScrollViewer is not { } sv || sv.ScrollableWidth <= 0)
                return baseValue;
            return Math.Clamp((double)baseValue, 0, sv.ScrollableWidth);
        }
    }
}