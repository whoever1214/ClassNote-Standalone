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
        // 默认 ShutdownMode=OnLastWindowClose：渲染完第一个窗口并 Close() 后整个 Application 会关闭，
        // 后续窗口 Show() 出来是 0x0 且不可见（渲染成空白 PNG）。离屏批量渲染必须显式关停。
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("zh-CN");
        var api = new ApiService();

        // 1. MainPage（真实会话数据 + 隐藏窗口渲染）
        var vm = new MainViewModel(api);
        vm.LoadSessionsAsync().GetAwaiter().GetResult();
        vm.SelectAll = true; // 勾选全部，使批量导出/删除按钮处于可用态，便于视觉核对
        var page1 = new MainPage { DataContext = vm };
        RenderHostWindow(page1, Path.Combine(outDir, "m1-main.png"), 1040, 680, () => CheckMainPage(page1, vm));

        // 1.1 / 1.2 筛选态渲染：先在 ViewModel 上设定筛选条件，再新建页面渲染。
        // 本进程内只有第一个 Window 会被真正合成，因此这两个状态改用"直接布局 + 渲染页面"，
        // 不依赖窗口合成（与 RecordingPage / SchedulePage 的渲染方式一致）。
        vm.CourseFilter = "数学";
        Console.WriteLine($"[probe] filter='数学' visible={vm.VisibleCount} has-any={vm.HasAnySessions}");
        RenderPageDirect(new MainPage { DataContext = vm }, Path.Combine(outDir, "m1b-main-filtered.png"), 1040, 680);

        // 筛选无结果：可见列表为空但库里仍有记录 → 应显示"当前科目下暂无记录"
        vm.CourseFilter = "地理";
        Console.WriteLine($"[probe] filter='地理' visible={vm.VisibleCount}");
        var page1c = new MainPage { DataContext = vm };
        page1c.Measure(new Size(1040, 680));
        page1c.Arrange(new Rect(0, 0, 1040, 680));
        page1c.UpdateLayout();
        CheckEmptyStateText(page1c);
        RenderToPng(page1c, Path.Combine(outDir, "m1c-main-filter-empty.png"), 1040, 680);

        vm.CourseFilter = MainViewModel.AllCourses;

        // 2. RecordingPage（直接 Measure/Arrange，不触发真实录音，模拟“录音中”）
        var guid = Guid.NewGuid();
        var recVm = new RecordingViewModel(guid, new ApiService(), new AudioService(),
            new ScreenshotService(), new UploadService("", new ApiService()),
            new RecordingConfig(AudioSourceKind.Microphone))
        {
            StatusText = "录音中",
            ElapsedSeconds = 752,
            ScreenshotCount = 12
        };
        var page2 = new RecordingPage(guid, "高等数学", new RecordingConfig(AudioSourceKind.Microphone))
            { DataContext = recVm };
        RenderPageDirect(page2, Path.Combine(outDir, "m2-recording.png"), 1040, 680);

        // 2.1 仅系统声音来源的录音页：应显示"正在录制系统声音"而不是麦克风下拉框
        var sysVm = new RecordingViewModel(Guid.NewGuid(), new ApiService(), new AudioService(),
            new ScreenshotService(), new UploadService("", new ApiService()),
            new RecordingConfig(AudioSourceKind.System))
        {
            StatusText = "录音中",
            ElapsedSeconds = 128,
            ScreenshotCount = 3
        };
        var page2b = new RecordingPage(Guid.NewGuid(), "在线公开课", new RecordingConfig(AudioSourceKind.System))
            { DataContext = sysVm };
        page2b.Measure(new Size(1040, 680));
        page2b.Arrange(new Rect(0, 0, 1040, 680));
        page2b.UpdateLayout();
        CheckRecordingSourceLayout(page2b);
        RenderToPng(page2b, Path.Combine(outDir, "m2b-recording-system.png"), 1040, 680);

        // 3. NoteViewPage（隐藏 WebView2 以便渲染工具栏与错误态）
        var noteVm = new NoteViewModel(api);
        var sessions = api.ListSessionsAsync().GetAwaiter().GetResult().ToList();
        var withNote = sessions.FirstOrDefault(s => s.Status == "completed");
        if (withNote != null)
            noteVm.LoadNoteAsync(withNote.Id).GetAwaiter().GetResult();
        var page3 = new NoteViewPage { DataContext = noteVm };
        page3.Loaded += (_, _) => { HideWebView(page3); };
        RenderHostWindow(page3, Path.Combine(outDir, "m3-note.png"), 1040, 680);

        // 4. SettingsWindow：两个页签各渲染一张（API 配置 / 录音设置）。
        //    每次都用新实例：WPF 窗口 Close() 之后无法再次 Show()。
        RenderHostWindow(new SettingsWindow(), Path.Combine(outDir, "m4-settings-api.png"), 600, 620);
        RenderHostWindow(new SettingsWindow(), Path.Combine(outDir, "m4b-settings-recording.png"), 600, 620,
            inspect: null, postShow: SelectTab(1));

        // 5. RecordingSetupWindow
        var setupAudio = new AudioService();
        var setupMics = setupAudio.GetInputDevices();
        var setupMicIds = setupAudio.GetInputDeviceIds();
        var setupOutputs = setupAudio.GetOutputDevices();
        var setupOutputIds = setupAudio.GetOutputDeviceIds();
        RenderHostWindow(
            new RecordingSetupWindow("数学", new[] { "语文", "数学", "英语" },
                setupMics, setupMicIds, setupOutputs, setupOutputIds),
            Path.Combine(outDir, "m5-setup.png"), 440, 568);

        // 6. 探针：GetInputDevices 耗时 + 真实 Show 配置窗口再渲染
        Console.WriteLine("[probe] calling GetInputDevices…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mics = new AudioService().GetInputDevices();
        sw.Stop();
        Console.WriteLine($"[probe] GetInputDevices count={mics.Length} elapsed={sw.ElapsedMilliseconds}ms");
        var setupWin = new RecordingSetupWindow("数学", new[] { "语文", "数学", "英语" }, mics);
        setupWin.Width = 440; setupWin.Height = 568;
        setupWin.Left = -32000; setupWin.Top = -32000;
        setupWin.ShowActivated = false;
        setupWin.Show();
        try
        {
            Thread.Sleep(700);
            setupWin.UpdateLayout();
            var deviceCombo = FindDescendant<ComboBox>(setupWin, c => c.Name == "DeviceCombo");
            Console.WriteLine($"[check] setup-device-combo-items={deviceCombo?.Items.Count ?? -1} " +
                              $"selected={deviceCombo?.SelectedItem ?? "<null>"} " +
                              $"(expect items>0 且 selected 非空)");
            RenderToPng(setupWin, Path.Combine(outDir, "m5-setup-shown.png"), 440, 568);
            Thread.Sleep(200);
            var rtb2 = new RenderTargetBitmap(440, 568, 96, 96, PixelFormats.Pbgra32);
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

    // ── 主页结构检查（需求 1~4 的回归点，无头验证）───────────────

    /// <summary>
    /// 逐项核对本次改版的关键结构：
    ///   1) 顶部操作区是文字按钮（不再是图标字形）；
    ///   2) 左侧「选择课程」模块已移除（页面里不存在课程 ListBox）；
    ///   3) 最近记录有科目筛选下拉框，且默认「全部课程」、内置课程可选；
    ///   4) 每行挂了右键菜单（删除 / 导出 PDF），并能取到该行的记录。
    /// </summary>
    static void CheckMainPage(MainPage page, MainViewModel vm, bool verbose = true)
    {
        var root = page.Content as DependencyObject;
        if (root == null) { Console.WriteLine("[check] main-page: no content root"); return; }

        // 0) 宿主尺寸 + 顶部按钮实际宽度（校验文字按钮没有被挤压/截断）
        if (root is FrameworkElement fe)
            Console.WriteLine($"[check] page-size={fe.ActualWidth:F0}x{fe.ActualHeight:F0} (expect 1040x680)");

        // 0.1 头部布局诊断：DockPanel / 头部 Grid / 各列的实际宽度
        void DumpHeader(DependencyObject node, int depth = 0)
        {
            if (depth > 6) return;
            if (node is FrameworkElement f && (node is DockPanel || node is Grid || node is StackPanel)
                && depth <= 4)
            {
                var d = node as Grid;
                var cols = d != null
                    ? " cols=[" + string.Join(",", d.ColumnDefinitions.Select(c => $"{c.ActualWidth:F0}")) + "]"
                    : "";
                Console.WriteLine($"[diag] {new string(' ', depth * 2)}{node.GetType().Name} " +
                                  $"actual={f.ActualWidth:F0}x{f.ActualHeight:F0} desired={f.DesiredSize.Width:F0}{cols}");
            }
            // 只沿着头部方向看（跳过列表等大子树）
            if (node is ListView or ScrollViewer || node is ContentPresenter) return;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                DumpHeader(VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        DumpHeader(root);

        var buttons = new List<(string Text, double Width, double Desired)>();
        void CollectButtons(DependencyObject node)
        {
            if (node is Button b)
            {
                var text = b.Content as string
                           ?? FindDescendant<TextBlock>(b, _ => true)?.Text
                           ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    buttons.Add((text, b.ActualWidth, b.DesiredSize.Width));
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                CollectButtons(VisualTreeHelper.GetChild(node, i));
        }
        CollectButtons(root);

        if (verbose)
        {
            // 1) 顶部按钮为文字
            foreach (var expected in new[] { "开始记录", "刷新列表", "定时记录", "设置" })
                Console.WriteLine($"[check] toolbar-button-{expected}={(buttons.Any(b => b.Text == expected) ? "OK" : "MISSING")}");

            // 2) 「选择课程」模块已移除：页面上不应再有绑定课程列表的 ListBox。
            //    （ListView 继承自 ListBox，所以用 ItemsSource 内容 + 祖先判定，不能只数类型。）
            var offenders = new List<string>();
            var listControls = new List<string>();
            void FindListBoxes(DependencyObject node)
            {
                if (node is ListBox lb)
                {
                    var source = lb.ItemsSource;
                    var type = lb.GetType().Name;
                    var insideCombo = FindAncestor<ComboBox>(lb) != null;
                    if (insideCombo)
                        listControls.Add($"{type}(combo-dropdown)");
                    else if (source is System.Collections.IEnumerable src)
                    {
                        // 课程选择列表的特征：直接列出内置课程名
                        var names = src.Cast<object>().Take(3).Select(o => o?.ToString()).ToList();
                        var isCourseList = names.Any(n => n != null && MainViewModel.DefaultCourses.Contains(n));
                        listControls.Add($"{type}(name='{lb.Name}',items={names.Count})");
                        if (isCourseList)
                            offenders.Add($"{type} 绑定课程列表 [{string.Join(",", names)}]");
                    }
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    FindListBoxes(VisualTreeHelper.GetChild(node, i));
            }
            FindListBoxes(root);
            Console.WriteLine($"[check] course-selection-listbox-removed={(offenders.Count == 0 ? "OK" : "BAD: " + string.Join("; ", offenders))}");
            Console.WriteLine($"[check] list-controls=[{string.Join(",", listControls)}]");

            // 3) 科目筛选下拉框
            var filter = FindDescendant<ComboBox>(root, c => c.Name == "CourseFilterCombo");
            Console.WriteLine($"[check] filter-combo={(filter != null ? "OK" : "MISSING")} " +
                              $"selected='{filter?.SelectedItem}' items={filter?.Items.Count}");
            Console.WriteLine($"[check] filter-default-is-all-courses=" +
                              $"{Equals(filter?.SelectedItem, MainViewModel.AllCourses)} (expect True)");
            Console.WriteLine($"[check] filter-has-builtin-courses=" +
                              $"{MainViewModel.DefaultCourses.All(c => filter?.Items.Contains(c) == true)} (expect True)");
        }

        // 5) 顶部文字按钮宽度充足（文字不会被截断）
        var headerNeed = 0.0;
        foreach (var (text, width, desired) in buttons.Where(b => b.Text is "开始记录" or "刷新列表" or "定时记录" or "设置"))
        {
            Console.WriteLine($"[check] button-width '{text}' actual={width:F0} desired={desired:F0} " +
                              $"{(width >= desired - 0.5 ? "OK" : "SQUEEZED")}");
            headerNeed += desired;
        }
        // 头部总需求 = 品牌区 + 状态胶囊 + 按钮 + 间距：用于评估默认窗口宽度下是否宽裕
        var brand = FindDescendant<TextBlock>(root, t => t.Text == "课堂笔记助手 · 录音 / 截屏 / AI 笔记");
        var badge = page.FindName("LlmHealthBadge") as FrameworkElement;
        Console.WriteLine($"[check] header-budget brand={brand?.ActualWidth:F0} badge={badge?.ActualWidth:F0} " +
                          $"buttons={headerNeed:F0} (可用约 975 - 品牌 - 胶囊)");

        // 5.1 全局截断检查：任何按钮内的文字实际可用宽度都必须够放下文字本身。
        //     WPF 可能给按钮分配小于 DesiredSize 的宽度，中文文案会被左右截断且不报错，
        //     这里逐个按钮核对（含批量导出/删除等所有文字按钮）。
        var clipped = new List<string>();
        void CheckClipping(DependencyObject node)
        {
            if (node is Button btn)
            {
                var label = FindDescendant<TextBlock>(btn, t => !string.IsNullOrWhiteSpace(t.Text));
                if (label != null)
                {
                    var avail = btn.ActualWidth - btn.Padding.Left - btn.Padding.Right;
                    // 模板 Border 的内边距（14,0 / 18,0）+ 文字宽度 = 按钮需要的宽度
                    var needed = label.DesiredSize.Width + 2 * Math.Max(btn.Padding.Left, 14) + 2;
                    if (btn.ActualWidth + 0.5 < needed || label.ActualWidth + 0.5 < label.DesiredSize.Width)
                        clipped.Add($"'{label.Text}'(button={btn.ActualWidth:F0} need≈{needed:F0} " +
                                    $"text={label.ActualWidth:F0}/{label.DesiredSize.Width:F0})");
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                CheckClipping(VisualTreeHelper.GetChild(node, i));
        }
        CheckClipping(root);
        Console.WriteLine($"[check] clipped-buttons={(clipped.Count == 0 ? "NONE(OK)" : string.Join(", ", clipped))}");

        // 5) 每行右键菜单 + DataContext 绑定到该行记录
        var rows = 0; var withMenu = 0; var menuOk = 0; var boundToRow = 0; var rowText = "";
        void WalkRows(DependencyObject node)
        {
            if (node is ListViewItem item)
            {
                rows++;
                if (item.ContextMenu != null)
                {
                    withMenu++;
                    var headers = item.ContextMenu.Items.OfType<MenuItem>()
                        .Select(m => m.Header?.ToString() ?? "").ToList();
                    if (headers.Contains("删除") && headers.Contains("导出 PDF"))
                        menuOk++;

                    // 真正走一遍右键路径（调用与 ContextMenuOpening 完全相同的生产代码）：
                    // 右键操作必须能定位到"被右键的那一行"，否则菜单项 DataContext 为 null，
                    // 右键删除/导出会静默失效。
                    var listView = FindAncestor<ListView>(item);
                    var hit = FindDescendant<TextBlock>(item, _ => true) ?? (DependencyObject)item;
                    var resolved = listView != null
                        ? ClassNote.Views.MainPage.AttachSessionToContextMenu(listView, hit)
                        : null;
                    if (resolved != null && ReferenceEquals(resolved, item.DataContext))
                        boundToRow++;

                    // 菜单项自身也要能解析到记录（逻辑树继承自 ContextMenu.DataContext）
                    var itemsBound = item.ContextMenu.Items.OfType<MenuItem>()
                        .All(m => ReferenceEquals(m.DataContext, item.DataContext));
                    Console.WriteLine($"[check] menu-items-resolve-row-data={itemsBound} (expect True)");
                    Console.WriteLine($"[check] menu-resolved-session={resolved?.Id.ToString() ?? "null"} " +
                                      $"(expect 行记录 {((ClassNote.Models.Session?)item.DataContext)?.Id})");
                }
                var sb = new System.Text.StringBuilder();
                CollectText(item, sb);
                rowText += sb.ToString();
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                WalkRows(VisualTreeHelper.GetChild(node, i));
        }
        WalkRows(root);
        Console.WriteLine($"[check] rows={rows} withContextMenu={withMenu} menuHasDeleteAndExport={menuOk} " +
                          $"menuDataContextIsRow={boundToRow} (expect 均相等且 >0)");

        // 6) 时间戳带星期几：时间戳必须是日期 + 时间 + 周X
        var weekdayHit = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }.Any(rowText.Contains);
        var hasClock = System.Text.RegularExpressions.Regex.IsMatch(rowText, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}");
        Console.WriteLine($"[check] row-timestamp-date-time={hasClock} has-weekday={weekdayHit} (expect True True)");
        var stamp = System.Text.RegularExpressions.Regex.Match(rowText, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}");
        if (stamp.Success)
        {
            var idx = rowText.IndexOf(stamp.Value, StringComparison.Ordinal);
            Console.WriteLine($"[check] row-timestamp-text='{rowText.Substring(idx, Math.Min(24, rowText.Length - idx))}'");
        }
    }

    /// <summary>把子树里的 TextBlock / Run 文本拼起来（Run 不在视觉树里，需要单独取）。</summary>
    static void CollectText(DependencyObject node, System.Text.StringBuilder sb)
    {
        if (node is TextBlock tb)
        {
            foreach (var inline in tb.Inlines)
                sb.Append(inline is System.Windows.Documents.Run r ? r.Text : inline.ToString());
            sb.Append('|');
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            CollectText(VisualTreeHelper.GetChild(node, i), sb);
    }

    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            node = VisualTreeHelper.GetParent(node);
            if (node is T hit) return hit;
        }
        return null;
    }


    /// <summary>
    /// 核对「仅系统声音」来源的录音页：不得再显示麦克风下拉框（选了系统声音却让用户以为在录麦克风），
    /// 且必须出现"正在录制系统声音"的说明。
    /// </summary>
    static void CheckRecordingSourceLayout(Page page)
    {
        if (page.Content is not DependencyObject root)
            return;

        var micCombos = 0;
        var hasSourceLabel = false;
        var hasSystemNotice = false;
        void Walk(DependencyObject node)
        {
            if (node is ComboBox c && c.Visibility == Visibility.Visible
                && c.ItemsSource is IEnumerable<string> src && src.Contains("Mic 1"))
                micCombos++;
            if (node is TextBlock tb && tb.Visibility == Visibility.Visible)
            {
                if (tb.Name == "SourceLabel" && !string.IsNullOrWhiteSpace(tb.Text))
                    hasSourceLabel = true;
                if (tb.Text.Contains("正在录制系统声音"))
                    hasSystemNotice = true;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);

        Console.WriteLine($"[check] recording-source-label={hasSourceLabel} (expect True)");
        Console.WriteLine($"[check] recording-system-notice={hasSystemNotice} (expect True)");
        Console.WriteLine($"[check] recording-mic-combo-visible-when-system-only={micCombos == 0} (expect True)");
    }

    /// <summary>核对筛选无结果时展示的是"当前科目下暂无记录"而非"暂无课堂记录"。</summary>
    static void CheckEmptyStateText(MainPage page)
    {
        var root = page.Content as DependencyObject;
        if (root == null) return;
        page.UpdateLayout();

        var visible = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock tb && tb.Visibility == Visibility.Visible
                && !string.IsNullOrWhiteSpace(tb.Text) && tb.Text.Contains("暂无"))
                visible.Add(tb.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        Console.WriteLine($"[check] filter-empty-state='{string.Join(" / ", visible)}' " +
                          $"(expect 仅「当前科目下暂无记录…」)");
    }
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

    static void RenderHostWindow(object content, string path, double w, double h, Action? inspect = null)
        => RenderHostWindow(content, path, w, h, inspect, postShow: null);

    /// <summary>
    /// 离屏渲染一个窗口或页面宿主。
    /// <paramref name="postShow"/> 在窗口 Show 之后、UpdateLayout 之前执行：
    /// 用于"必须等窗口显示才能生效"的操作（例如切换 TabControl 页签）。
    /// </summary>
    static void RenderHostWindow(object content, string path, double w, double h,
        Action? inspect, Action<Window>? postShow)
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
                postShow?.Invoke(window);
                window.UpdateLayout();
                inspect?.Invoke();
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
            // 结构检查必须在窗口存活、布局已完成时进行：窗口关闭后视觉树会与表现源脱开
            inspect?.Invoke();
            Console.WriteLine($"[diag] {Path.GetFileName(path)} host={host.ActualWidth:F0}x{host.ActualHeight:F0} " +
                              $"content={(content as FrameworkElement)?.ActualWidth:F0}x{(content as FrameworkElement)?.ActualHeight:F0} " +
                              $"visible={host.IsVisible}");
            RenderToPng(host, path, w, h);
        }
        finally { host.Close(); }
    }

    /// <summary>RenderHostWindow 的 postShow 回调：把设置窗口切到指定页签。</summary>
    static Action<Window> SelectTab(int index) => window =>
    {
        if (FindDescendant<TabControl>(window, _ => true) is { } tabs && index < tabs.Items.Count)
            tabs.SelectedIndex = index;
    };

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
