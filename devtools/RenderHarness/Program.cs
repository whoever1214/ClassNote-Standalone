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

        // 7. SchedulePage（每周课表展示页，临时样例数据渲染后还原）
        var repo = LocalRepository.Instance;
        var snapshot = repo.ListScheduleEntries();
        try
        {
            repo.ReplaceScheduleEntries(SampleSchedule());
            var expectedBlocks = SampleSchedule().Count;
            var schedulePage = new SchedulePage();
            RenderPageDirect(schedulePage, Path.Combine(outDir, "m6-schedule-week.png"), 1120, 720);
            var blockCount = CountEntryBorders(schedulePage);
            Console.WriteLine($"[check] week-entry-blocks={blockCount} (expect {expectedBlocks})");
            // 课块重叠检查（BUG-2 回归）：同一列内任意两个课块的矩形不得相交
            var overlaps = CountOverlappingEntryBlocks(schedulePage);
            Console.WriteLine($"[check] week-entry-overlaps={overlaps} (expect 0)");

            // 7.1 课程安排编辑窗（列表式）
            RenderWindowContentRoot(new ScheduleEditorWindow(repo.ListScheduleEntries()),
                Path.Combine(outDir, "m7-schedule-editor.png"), 860, 640);

            // 7.2 条目编辑对话框（新增模式 + 编辑自定义课程名模式：验证课程名可回显）
            var addDialog = new ScheduleEntryDialog(null, 0, 8 * 60,
                repo.ListScheduleEntries(), ClassNote.ViewModels.MainViewModel.DefaultCourses);
            RenderWindowContentRoot(addDialog, Path.Combine(outDir, "m8-schedule-entry.png"), 440, 500);
            Console.WriteLine($"[check] add-dialog-course-text='{FindComboBoxSelectedText(addDialog)}' " +
                              $"(expect non-empty)");
            Console.WriteLine($"[check] add-dialog-course-isEditable={ReadCourseComboEditable(addDialog)} " +
                              $"(expect False：课程名只能选择)");
            Console.WriteLine($"[check] add-dialog-course-displayed='{ReadCourseComboDisplay(addDialog)}' " +
                              $"(expect non-empty, same as text)");
            Console.WriteLine($"[check] add-dialog-combo-arrows-visible=" +
                              $"{ComboArrowsVisible(addDialog, "CourseBox", "WeekdayBox")} (expect True)");

            var customCourse = new ClassNote.Models.ScheduleEntry
            {
                WeekdayIndex = 2, StartMin = 10 * 60, EndMin = 11 * 60,
                Course = "自习（自定义名）", Enabled = true,
            };
            var editDialog = new ScheduleEntryDialog(customCourse, 2, 10 * 60,
                repo.ListScheduleEntries(), ClassNote.ViewModels.MainViewModel.DefaultCourses);
            RenderWindowContentRoot(editDialog, Path.Combine(outDir, "m9-schedule-entry-edit.png"), 440, 500);
            var courseText = FindComboBoxSelectedText(editDialog);
            Console.WriteLine($"[check] edit-dialog-course='{courseText}' (expect '自习（自定义名）')");
            Console.WriteLine($"[check] edit-dialog-course-isEditable={ReadCourseComboEditable(editDialog)} " +
                              $"(expect False：课程名只能选择)");
            Console.WriteLine($"[check] edit-dialog-course-displayed='{ReadCourseComboDisplay(editDialog)}' " +
                              $"(expect '自习（自定义名）')");
        }
        finally { repo.ReplaceScheduleEntries(snapshot); }

        // 8. 内容判定：对全部渲染产物评估有效内容占比，供人工筛选（有效截图才归档到 docs/）
        foreach (var name in Directory.GetFiles(outDir, "m*.png"))
            ProbeContent(name);
        Console.WriteLine("RENDER_DONE");
    }

    /// <summary>取对话框「课程名称」下拉框（按 x:Name 定位，避免依赖视觉树的遍历顺序）。</summary>
    static ComboBox? CourseComboBox(Window window) => window.FindName("CourseBox") as ComboBox;

    /// <summary>课程名下拉框当前文本（验证编辑对话框课程名回显）。</summary>
    static string? FindComboBoxSelectedText(Window window) => CourseComboBox(window)?.Text;

    /// <summary>
    /// 读取课程下拉框"屏幕上真正显示"的文字 + 是否可手输：
    ///   - 可编辑（IsEditable=True）：显示位是模板里名为 PART_EditableTextBox 的 TextBox；
    ///     模板缺少该命名部件时 ComboBox.Text 有值但界面渲染为空白（BUG-1 回归点）。
    ///   - 不可编辑（当前课程框：只能选择）：显示位是 ContentSite（选中项展示位）。
    /// </summary>
    static string ReadCourseComboDisplay(Window window)
    {
        var combo = CourseComboBox(window);
        if (combo == null)
            return "<no-CourseBox>";
        if (combo.IsEditable)
        {
            var box = FindDescendant<TextBox>(combo, t => t.Name == "PART_EditableTextBox");
            if (box == null)
                return "<no-PART_EditableTextBox>";
            return box.Visibility == Visibility.Visible ? box.Text : $"<hidden:{box.Text}>";
        }
        var site = FindDescendant<ContentPresenter>(combo, cp => cp.Name == "ContentSite");
        if (site == null)
            return "<no-ContentSite>";
        var shown = site.Content?.ToString() ?? "";
        return site.Visibility == Visibility.Visible ? shown : $"<hidden:{shown}>";
    }

    /// <summary>课程下拉框是否允许手输（当前需求：不允许，只能从列表选择）。</summary>
    static string ReadCourseComboEditable(Window window)
    {
        var combo = CourseComboBox(window);
        return combo == null ? "<no-CourseBox>" : combo.IsEditable.ToString();
    }

    /// <summary>
    /// 检查下拉框右侧的箭头（模板里 Path x:Name="Arrow"）是否真的可见、且完整落在下拉框内。
    /// 箭头被挤出可视区时用户的"可点开选择"提示就消失了（历史坑：渲染时给内容根多算了 Margin）。
    /// </summary>
    static bool ComboArrowsVisible(Window window, params string[] comboNames)
    {
        foreach (var name in comboNames)
        {
            if (window.FindName(name) is not ComboBox combo)
                return false;
            combo.ApplyTemplate();
            if (combo.Template.FindName("Arrow", combo) is not FrameworkElement arrow)
                return false;
            if (arrow.Visibility != Visibility.Visible)
                return false;
            var origin = arrow.TransformToAncestor(combo).Transform(new Point(0, 0));
            if (origin.X + arrow.ActualWidth > combo.ActualWidth)
                return false;
        }
        return true;
    }

    static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T hit && predicate(hit))
            return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant(VisualTreeHelper.GetChild(root, i), predicate);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// 统计同一列（同一天画布）内相互重叠的课块对数：矩形相交即视为重叠
    /// （BUG-2 回归点：相邻 10 分钟的短课不得互相覆盖）。
    /// </summary>
    static int CountOverlappingEntryBlocks(Page page)
    {
        if (page.Content is not DependencyObject root)
            return -1;

        var byCanvas = new Dictionary<Canvas, List<(double Top, double Height, string Course)>>();
        void Walk(DependencyObject node)
        {
            if (node is Border { Tag: ClassNote.Models.ScheduleEntry entry } border
                && VisualTreeHelper.GetParent(border) is Canvas parent)
            {
                if (!byCanvas.TryGetValue(parent, out var list))
                    byCanvas[parent] = list = new List<(double, double, string)>();
                list.Add((Canvas.GetTop(border), border.ActualHeight, entry.Course));
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);

        var overlaps = 0;
        foreach (var list in byCanvas.Values)
        {
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    if (a.Top < b.Top + b.Height && b.Top < a.Top + a.Height)
                    {
                        overlaps++;
                        Console.WriteLine($"[overlap] '{a.Course}'[{a.Top:F1}+{a.Height:F1}] " +
                                          $"与 '{b.Course}'[{b.Top:F1}+{b.Height:F1}] 相交");
                    }
                }
        }
        return overlaps;
    }

    /// <summary>统计页面/窗口根元素子树中挂有课表条目 Tag 的课程块（验证课表已按数据渲染）。</summary>
    static int CountEntryBorders(Page page)
    {
        if (page.Content is not DependencyObject root)
            return -1;
        var count = 0;
        void Walk(DependencyObject node)
        {
            if (node is Border { Tag: ClassNote.Models.ScheduleEntry })
                count++;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return count;
    }

    /// <summary>
    /// 无桌面合成会话下 Window.Show() 渲染为全透明：采用与 RecordingPage 相同的
    /// Measure/Arrange 直接渲染窗口根元素（元素级渲染，不依赖窗口合成）。
    /// </summary>
    /// <summary>
    /// 无桌面合成会话下 Window.Show() 渲染为全透明：对窗口的"内容根"做
    /// Measure/Arrange 后元素级渲染。
    ///
    /// ⚠️ 必须扣掉内容根的 Margin（如 `26,22,26,20`）后再布局：以前直接把根元素设成窗口尺寸
    /// （440×500），相当于给内容多出 52×42 的宽度/高度，右侧控件（下拉箭头）会被挤出渲染范围，
    /// 造成"箭头不见了/整页被横向拉伸"的假象（真实窗口里箭头在 x=378..387，是正常的）。
    /// 这里按窗口尺寸 - Margin 布局，并用 VisualBrush 垫回 Margin 与窗口背景，产出的 PNG
    /// 与真实窗口一致。
    /// </summary>
    static void RenderWindowContentRoot(Window window, string path, double w, double h)
    {
        var root = window.Content as FrameworkElement;
        if (root == null)
        {
            RenderHostWindow(window, path, w, h);
            return;
        }

        var margin = root.Margin;
        var innerW = Math.Max(1, w - margin.Left - margin.Right);
        var innerH = Math.Max(1, h - margin.Top - margin.Bottom);

        var savedMargin = root.Margin;
        root.Margin = new Thickness(0);          // 只用于本次离屏布局，渲染完还原
        try
        {
            root.Width = innerW;
            root.Height = innerH;
            root.Measure(new Size(innerW, innerH));
            root.Arrange(new Rect(0, 0, innerW, innerH));
            root.UpdateLayout();

            // 把内容按 Margin 摆回窗口坐标系，并铺上窗口背景，得到与真实窗口等尺寸的截图
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
                var brush = new VisualBrush(root)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                };
                dc.DrawRectangle(brush, null,
                    new Rect(margin.Left, margin.Top, root.RenderSize.Width, root.RenderSize.Height));
            }
            RenderToPng(visual, path, w, h);
        }
        finally
        {
            root.Margin = savedMargin;
        }
    }

    /// <summary>输出 PNG 的颜色直方图前若干位（用于确认内容真的被渲染而非空白/全灰）。</summary>
    static void DumpColorHistogram(string png)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(png);
            var counts = new Dictionary<int, int>();
            for (int y = 0; y < bmp.Height; y += 2)
            {
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    int key = (c.R / 16) << 16 | (c.G / 16) << 8 | (c.B / 16);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            var top = counts.OrderByDescending(k => k.Value).Take(10)
                .Select(k => $"#{k.Key:X6}~{k.Value}");
            Console.WriteLine($"[histo] {Path.GetFileName(png)}: " + string.Join(" ", top));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[histo] {png} error: {ex.Message}");
        }
    }

    /// <summary>判定 PNG 是否包含真实渲染内容（透明与纯背景不计，统计有效像素占比）。</summary>
    static void ProbeContent(string png)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(png);
            int total = 0, opaque = 0, ink = 0;
            for (int y = 0; y < bmp.Height; y += 2)
            {
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    total++;
                    var c = bmp.GetPixel(x, y);
                    if (c.A < 32) continue;
                    opaque++;
                    // 排除页面白底 / 浅底 (#F2F3F7 / #FFF / 课程块浅色) 之外的真实内容（文字、控件、色块描边等）
                    bool nearWhite = c.R > 235 && c.G > 235 && c.B > 238;
                    bool nearBg = Math.Abs(c.R - 0xF2) < 10 && Math.Abs(c.G - 0xF3) < 10 && Math.Abs(c.B - 0xF7) < 10;
                    if (!nearWhite && !nearBg) ink++;
                }
            }
            var inkPct = 100.0 * ink / total;
            Console.WriteLine($"[content] {Path.GetFileName(png)}: opaque={100.0 * opaque / total:F1}% ink={inkPct:F2}% {(inkPct >= 0.5 ? "USEFUL" : "blank")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[content] {png} error: {ex.Message}");
        }
    }

    /// <summary>
    /// 样例课表：覆盖双视图渲染（含停用条目不启用定时）。
    /// 周六/周日专门放"紧凑课表"回归用例：相邻 10 分钟短课、以及 45 分钟课 + 10 分钟课间，
    /// 用于验证课块不再互相覆盖（BUG-2）。
    /// </summary>
    static IReadOnlyList<ClassNote.Models.ScheduleEntry> SampleSchedule()
    {
        var list = new List<ClassNote.Models.ScheduleEntry>();
        void Add(int day, int s, int e, string course, bool enabled = true)
            => list.Add(new ClassNote.Models.ScheduleEntry
            {
                WeekdayIndex = day, StartMin = s, EndMin = e, Course = course, Enabled = enabled,
            });
        Add(0, 8 * 60, 8 * 60 + 45, "数学");
        Add(0, 9 * 60, 9 * 60 + 45, "英语", enabled: false);
        Add(0, 10 * 60 + 15, 11 * 60, "物理");
        Add(1, 8 * 60, 9 * 60 + 30, "语文");
        Add(1, 13 * 60 + 30, 14 * 60 + 15, "化学");
        Add(2, 8 * 60, 8 * 60 + 45, "数学");
        Add(2, 9 * 60, 10 * 60, "生物");
        Add(3, 14 * 60, 15 * 60 + 30, "历史");
        Add(4, 8 * 60, 9 * 60 + 30, "英语");
        Add(4, 15 * 60 + 30, 17 * 60, "自习", enabled: false);
        // 周六：三节相邻的 10 分钟短课（时间轴上仅 5px/节，最易重叠）
        Add(5, 10 * 60, 10 * 60 + 10, "短课A");
        Add(5, 10 * 60 + 10, 10 * 60 + 20, "短课B");
        Add(5, 10 * 60 + 20, 10 * 60 + 30, "短课C");
        // 周日：45 分钟课 + 10 分钟课间（用户报告的"相邻 10 分钟两课重叠"场景）
        Add(6, 8 * 60, 8 * 60 + 45, "数学");
        Add(6, 8 * 60 + 55, 9 * 60 + 40, "英语");
        return list;
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
