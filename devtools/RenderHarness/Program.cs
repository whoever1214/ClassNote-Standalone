// 开发辅助工具：进程内实例化 ClassNote 各视图并离屏渲染为 PNG，用于无头视觉验证。
// 仅用于本机验证，不入库。用法：RenderHarness <outDir>
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassNote.Services;
using ClassNote.ViewModels;
using ClassNote.Views;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : @"C:UserswhoeverDesktopClassNote-Standalone.ui-renders";
        Directory.CreateDirectory(outDir);

        var app = new ClassNote.App();
        var init = typeof(ClassNote.App).GetMethod("InitializeComponent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        init?.Invoke(app, null);

        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("zh-CN");
        var api = new ApiService();

        // 1. MainPage（真实会话数据 + 隐藏窗口渲染）
        var vm = new MainViewModel(api);
        vm.LoadSessionsAsync().GetAwaiter().GetResult();
        vm.SelectAll = true; // 勾选全部，使批量导出/删除按钮处于可用态，便于视觉核对
        var page1 = new MainPage { DataContext = vm };
        RenderHostWindow(page1, Path.Combine(outDir, "m1-main.png"), 1040, 680);

        // 2. RecordingPage（直接 Measure/Arrange，不触发真实录音，模拟“录音中”）
        var guid = Guid.NewGuid();
        var recVm = new RecordingViewModel(guid, new ApiService(), new AudioService(),
            new ScreenshotService(), new UploadService("", new ApiService()), null)
        {
            StatusText = "录音中",
            ElapsedSeconds = 752,
            ScreenshotCount = 12
        };
        var page2 = new RecordingPage(guid, "高等数学", null) { DataContext = recVm };
        RenderPageDirect(page2, Path.Combine(outDir, "m2-recording.png"), 1040, 680);

        // 3. NoteViewPage（隐藏 WebView2 以便渲染工具栏与错误态）
        var noteVm = new NoteViewModel(api);
        var sessions = api.ListSessionsAsync().GetAwaiter().GetResult().ToList();
        var withNote = sessions.FirstOrDefault(s => s.Status == "completed");
        if (withNote != null)
            noteVm.LoadNoteAsync(withNote.Id).GetAwaiter().GetResult();
        var page3 = new NoteViewPage { DataContext = noteVm };
        page3.Loaded += (_, _) => { HideWebView(page3); };
        RenderHostWindow(page3, Path.Combine(outDir, "m3-note.png"), 1040, 680);

        // 4. SettingsWindow
        RenderHostWindow(new SettingsWindow(), Path.Combine(outDir, "m4-settings.png"), 560, 478);

        // 5. RecordingSetupWindow
        RenderHostWindow(new RecordingSetupWindow("数学", new[] { "语文", "数学", "英语" }, new[] { "麦克风阵列", "Realtek(R) Audio" }),
            Path.Combine(outDir, "m5-setup.png"), 440, 424);

        // 6. 探针：GetInputDevices 耗时 + 真实 Show 配置窗口再渲染
        Console.WriteLine("[probe] calling GetInputDevices…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mics = new AudioService().GetInputDevices();
        sw.Stop();
        Console.WriteLine($"[probe] GetInputDevices count={mics.Length} elapsed={sw.ElapsedMilliseconds}ms");
        var setupWin = new RecordingSetupWindow("数学", new[] { "语文", "数学", "英语" }, mics);
        setupWin.Width = 440; setupWin.Height = 424;
        setupWin.Left = -32000; setupWin.Top = -32000;
        setupWin.ShowActivated = false;
        setupWin.Show();
        try
        {
            Thread.Sleep(700);
            setupWin.UpdateLayout();
            RenderToPng(setupWin, Path.Combine(outDir, "m5-setup-shown.png"), 440, 424);
            Thread.Sleep(200);
            var rtb2 = new RenderTargetBitmap(440, 424, 96, 96, PixelFormats.Pbgra32);
            rtb2.Render(setupWin);
            Console.WriteLine("rendered m5-setup-shown (content)");
        }
        finally { setupWin.Close(); }

        Console.WriteLine("RENDER_DONE");
    }

    static void HideWebView(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.Web.WebView2.Wpf.WebView2 wv)
                wv.Visibility = Visibility.Collapsed;
            else
                HideWebView(child);
        }
    }

    static void RenderPageDirect(Page page, string path, double w, double h)
    {
        page.Measure(new Size(w, h));
        page.Arrange(new Rect(0, 0, w, h));
        page.UpdateLayout();
        RenderToPng(page, path, w, h);
    }

    static void RenderHostWindow(object content, string path, double w, double h)
    {
        // 内容本身是 Window 时不能嵌套进宿主窗口，直接离屏显示后渲染
        if (content is Window window)
        {
            window.Width = w;
            window.Height = h;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            try
            {
                Thread.Sleep(700);
                window.UpdateLayout();
                RenderToPng(window, path, w, h);
            }
            finally { window.Close(); }
            return;
        }

        var host = new Window
        {
            Width = w,
            Height = h,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7))
        };
        host.Content = content;
        host.Show();
        try
        {
            Thread.Sleep(700);
            host.UpdateLayout();
            RenderToPng(host, path, w, h);
        }
        finally { host.Close(); }
    }

    static void RenderToPng(Visual v, string path, double w, double h)
    {
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Console.WriteLine("rendered " + path);
    }
}
