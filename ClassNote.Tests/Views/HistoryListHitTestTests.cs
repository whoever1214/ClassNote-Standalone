using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using ClassNote.Controls;
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using ClassNote.Views;
using Xunit;

namespace ClassNote.Tests.Views;

/// <summary>
/// 「点击历史记录弹出 System.Windows.Documents.Run 不是 Visual 或 Visual3D」的回归测试。
///
/// 现场症状（用户报告）：一体机上点一下「最近记录」里的记录，就弹出
///   「程序遇到未处理的错误：System.Windows.Documents.Run 不是 Visual 或 Visual3D」。
///
/// 机理：「最近记录」的时间戳列在 v0.4.x 由
///   <c>&lt;TextBlock Text="{Binding StartTime, StringFormat=...}" /&gt;</c>
/// 改成
///   <c>&lt;TextBlock&gt;&lt;Run/&gt;&lt;Run/&gt;&lt;/TextBlock&gt;</c>（要多带一个"周几"）。
/// WPF 命中测试会把**行内元素本身**（<c>Run</c>，一个 <see cref="FrameworkContentElement"/>）
/// 当作 <c>e.OriginalSource</c> 交给事件处理器；而
/// <c>MainPage.SessionList_MouseDoubleClick</c> 里那句
/// <c>FindVisualParent&lt;CheckBox&gt;(e.OriginalSource)</c> 一路调用
/// <c>VisualTreeHelper.GetParent</c> —— 对 <c>Run</c> 调用它会直接抛
/// <see cref="InvalidOperationException"/>，消息与用户看到的一字不差。
///
/// 这组用例不走 Mock，而是把真实的 <see cref="MainPage"/> 装在离屏窗口里渲染出来做命中测试，
/// 拿到 WPF 真正会传出来的那个对象 —— 只有这样才能证明"用户点的就是 Run"。
/// </summary>
[Collection(nameof(WpfUiCollection))]
public class HistoryListHitTestTests
{
    [Fact]
    public void TimestampColumn_HitTestReturnsInlineRun_NotVisual()
    {
        WpfUi.Run(() =>
        {
            var (host, _, list) = BuildMainPageWithOneRow();
            try
            {
                var run = FirstTimestampRun(list);
                Assert.NotNull(run);

                var hit = HitTimestamp(list, run!);
                Assert.NotNull(hit);

                // 这就是现场那个对象：行内元素，不是 Visual
                Assert.IsType<Run>(hit);
                Assert.IsAssignableFrom<FrameworkContentElement>(hit);
                Assert.False(hit is System.Windows.Media.Visual);
            }
            finally { host.Close(); }
        });
    }

    /// <summary>
    /// **机理证明**：对命中到的 Run 直接用 <c>VisualTreeHelper.GetParent</c> 向上走，
    /// 得到的正是用户看到的那句报错。这条用例描述 WPF 行为，不代表修复后的代码。
    /// </summary>
    [Fact]
    public void VisualTreeHelperGetParent_OnRun_ThrowsTheReportedError()
    {
        WpfUi.Run(() =>
        {
            var (host, _, list) = BuildMainPageWithOneRow();
            try
            {
                var run = FirstTimestampRun(list);
                var hit = HitTimestamp(list, run!);
                Assert.IsType<Run>(hit);

                var ex = Assert.Throws<InvalidOperationException>(
                    () => System.Windows.Media.VisualTreeHelper.GetParent(hit!));
                Assert.Contains("Visual", ex.Message);
            }
            finally { host.Close(); }
        });
    }

    /// <summary>
    /// 修复后的行为：<see cref="VisualTreeWalk"/> 能从 Run 一路走到宿主控件，
    /// 双击时间戳不再抛异常，也不会被误判成"点在了勾选框上"。
    /// </summary>
    [Fact]
    public void VisualTreeWalk_FromRun_FindsHostControls_AndDoesNotThrow()
    {
        WpfUi.Run(() =>
        {
            var (host, _, list) = BuildMainPageWithOneRow();
            try
            {
                var run = FirstTimestampRun(list);
                var hit = HitTimestamp(list, run!);
                Assert.IsType<Run>(hit);

                // 一路向上：必须能走到 ListViewItem、ListView，且全程不抛
                Assert.NotNull(VisualTreeWalk.FindAncestor<ListViewItem>(hit));
                Assert.NotNull(VisualTreeWalk.FindAncestor<ListView>(hit));

                // 生产代码就是靠这一句把"双击勾选框"排除掉的：时间戳不是勾选框
                Assert.Null(VisualTreeWalk.FindAncestor<CheckBox>(hit));
                Assert.Null(VisualTreeWalk.FindAncestor<CheckBox>(run));
            }
            finally { host.Close(); }
        });
    }

    [Fact]
    public void VisualTreeWalk_ReturnsNullForDetachedInline_NeverThrows()
    {
        WpfUi.Run(() =>
        {
            var orphan = new Run("孤立的行内元素");
            Assert.Null(VisualTreeWalk.GetParent(orphan));
            Assert.Null(VisualTreeWalk.FindAncestor<CheckBox>(orphan));
            Assert.Null(VisualTreeWalk.GetParent(null));
        });
    }

    /// <summary>
    /// 触摸支持必须真的接到界面上，而不是只写了个类：
    /// 检查 App.xaml 里的 <c>ListViewItem</c> 样式确实挂上了长按行为，且 <c>ListView</c> 样式
    /// 打开了单指拖动滚动（<c>PanningMode</c>）。WPF 默认是 <c>None</c>，只靠提升为鼠标事件，
    /// 手指拖列表根本不滚 —— 这正是"触控支持非常差"的主要成因。
    /// </summary>
    [Fact]
    public void AppStyles_WireUpTouchSupport_LongPressAndPanning()
    {
        WpfUi.Run(() =>
        {
            // 样式来自 App.xaml（隐式样式），必须能在应用资源里找到
            var itemStyle = (Style)Application.Current.FindResource(typeof(ListViewItem));
            Assert.NotNull(itemStyle);
            Assert.Equal(true, itemStyle.Setters
                .OfType<Setter>()
                .Where(s => s.Property == TouchLongPress.IsEnabledProperty)
                .Select(s => s.Value)
                .SingleOrDefault());

            var listStyle = (Style)Application.Current.FindResource(typeof(ListView));
            Assert.NotNull(listStyle);
            var panning = listStyle.Setters
                .OfType<Setter>()
                .Where(s => s.Property == ScrollViewer.PanningModeProperty)
                .Select(s => s.Value)
                .SingleOrDefault();
            Assert.Equal(PanningMode.VerticalOnly, panning);
        });
    }

    // ── 现场搭建 ────────────────────────────────────────────────

    private static (Window Host, MainPage Page, ListView List) BuildMainPageWithOneRow()
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            Course = "物理",
            Title = "牛顿第二定律",
            StartTime = new DateTime(2026, 9, 14, 9, 0, 0),
            Status = "completed",
        };
        var vm = new MainViewModel(new StubApi(new List<Session> { session }));
        vm.LoadSessionsAsync().GetAwaiter().GetResult();

        var page = new MainPage { DataContext = vm };
        var host = new Window
        {
            Content = page,
            Width = 1040,
            Height = 680,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -4000,
            Top = -4000,
        };
        host.Show();
        page.UpdateLayout();
        host.UpdateLayout();

        var list = (ListView)page.FindName("SessionList")!;
        list.UpdateLayout();
        Assert.NotEmpty(list.Items);
        return (host, page, list);
    }

    /// <summary>取第 0 行时间戳列（TextBlock + 两个显式 Run）的第一个 Run。</summary>
    private static Run? FirstTimestampRun(ListView list)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(0) is not DependencyObject container)
            return null;

        var runs = new List<Run>();
        Collect(container, runs);
        return runs.FirstOrDefault();

        static void Collect(DependencyObject node, List<Run> found)
        {
            if (node is TextBlock tb)
                foreach (var inline in tb.Inlines)
                    CollectInline(inline, found);

            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
                Collect(System.Windows.Media.VisualTreeHelper.GetChild(node, i), found);
        }

        static void CollectInline(Inline inline, List<Run> found)
        {
            if (inline is Run r) found.Add(r);
            else if (inline is Span span)
                foreach (var child in span.Inlines)
                    CollectInline(child, found);
        }
    }

    /// <summary>命中测试时间戳文字，返回 WPF 会当作 e.OriginalSource 的对象。</summary>
    private static DependencyObject? HitTimestamp(ListView list, Run run)
    {
        if (run.Parent is not TextBlock tb)
            return null;

        var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
            rect = new Rect(0, 0, Math.Max(1, tb.ActualWidth), Math.Max(1, tb.ActualHeight));

        var inTextBlock = new Point(rect.Left + Math.Min(2, rect.Width / 2), rect.Top + rect.Height / 2);
        return list.InputHitTest(tb.TranslatePoint(inTextBlock, list)) as DependencyObject;
    }

    private sealed class StubApi : IApiService
    {
        private readonly List<Session> _sessions;
        public StubApi(List<Session> sessions) => _sessions = sessions;

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

/// <summary>
/// 一个进程里只能有一个 <see cref="Application"/>，而且它绑在创建它的那个 STA 线程上。
/// 因此所有需要"应用资源字典"（App.xaml 里的样式）的界面测试共用这一条常驻 STA 线程：
/// 在这里创建 Application、也在这里做全部 UI 操作（xUnit 的测试线程各不相同，不能各自建）。
/// </summary>
public sealed class WpfUi
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    public static void Run(Action action)
    {
        lock (Gate)
        {
            _dispatcher ??= StartStaThreadWithApp();
            Exception? error = null;
            _dispatcher.Invoke(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            if (error != null)
                throw error;
        }
    }

    private static Dispatcher StartStaThreadWithApp()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var app = new ClassNote.App();
            var init = typeof(ClassNote.App).GetMethod("InitializeComponent",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
            init?.Invoke(app, null);
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ClassNote.Tests.UI",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)) || dispatcher == null)
            throw new InvalidOperationException("UI 测试线程未能在 30 秒内就绪");
        return dispatcher;
    }
}

[CollectionDefinition(nameof(WpfUiCollection), DisableParallelization = true)]
public sealed class WpfUiCollection { }
