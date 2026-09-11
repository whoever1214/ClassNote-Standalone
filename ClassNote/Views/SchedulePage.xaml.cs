using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.Views;

/// <summary>
/// 定时记录 · 每周课表（主窗口 Frame 内的独立页面）。
/// 职责收敛为两块：
///   - 展示：00:00–24:00 全天的周一至周日课表网格（只读，点击不触发编辑）；
///   - 配置：总开关（定时记录）与开机自启，改动即时生效；
/// 所有课程增删改统一收敛到「课程安排」按钮 → ScheduleEditorWindow（唯一的编辑入口）。
/// </summary>
public partial class SchedulePage : Page
{
    /// <summary>时间轴起点：05:00（用户要求课表从早上 5 点开始）。</summary>
    private const int RangeStart = 5 * 60;

    /// <summary>时间轴终点：22:00（时间轴锁定在 05:00–22:00 区间）。</summary>
    private const int RangeEnd = 22 * 60;

    /// <summary>时间轴每小时的像素高度（17 小时区间，一眼可扫全）。</summary>
    private const double HourPx = 32;

    /// <summary>时间刻度列宽（左侧时刻标注）。</summary>
    private const double RulerWidth = 46;

    /// <summary>每个星期列的宽度。</summary>
    private const double DayWidth = 136;

    private static readonly string[] CoursePalette =
    {
        "#DCE4FF", "#E4F0FF", "#DFF5E8", "#FFF3D6", "#FDE7F2",
        "#ECE4FF", "#E0F5F5", "#FFEBE0", "#E8F1E0", "#F0E6F0",
    };

    private readonly List<ScheduleEntry> _entries = new();
    private readonly string[] _weekdayNames = ScheduleEntry.Weekdays;

    /// <summary>请求返回主页（由 MainWindow 订阅并导航）。</summary>
    public event EventHandler? BackRequested;

    public SchedulePage()
    {
        InitializeComponent();
        _entries.AddRange(LocalRepository.Instance.ListScheduleEntries());

        var settings = AppSettings.Instance.Snapshot();
        MasterToggle.IsChecked = settings.ScheduleEnabled;
        LaunchToggle.IsChecked = settings.ScheduleLaunchAtStartup;

        UpdateMasterHint();
        UpdateSummary();

        // 立即渲染（页面在宿主窗口显示时会再次触发 Loaded 刷新滚动位置）
        RenderWeekView();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // 时间轴固定在 05:00–22:00：默认不滚动，直接从 05:00 顶部看起
        WeekScroll.ScrollToVerticalOffset(0);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── 配置：总开关 / 开机自启（即时生效）────────────────────

    private void MasterToggle_Changed(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.Update(s => s.ScheduleEnabled = MasterToggle.IsChecked == true);
        // 通知主窗口刷新托盘驻留（开启定时记录后关闭主窗口应驻留托盘）
        (Application.Current.MainWindow as ClassNote.MainWindow)?.RefreshTrayResident();
        UpdateMasterHint();
        UpdateSummary();
    }

    private void UpdateMasterHint()
    {
        MasterHint.Text = MasterToggle.IsChecked == true
            ? "已开启 · 到点自动开始录音"
            : "已关闭 · 仅保存课表，不自动录音";
    }

    private void LaunchToggle_Changed(object sender, RoutedEventArgs e)
    {
        var on = LaunchToggle.IsChecked == true;
        AppSettings.Instance.Update(s => s.ScheduleLaunchAtStartup = on);
        var ok = StartupRegistrar.Apply(on);
        if (!ok)
        {
            // 回滚 UI 勾选态，提示用户：开机自启可能被系统策略/权限阻止
            LaunchToggle.Checked -= LaunchToggle_Changed;
            LaunchToggle.Unchecked -= LaunchToggle_Changed;
            LaunchToggle.IsChecked = StartupRegistrar.IsRegistered();
            LaunchToggle.Checked += LaunchToggle_Changed;
            LaunchToggle.Unchecked += LaunchToggle_Changed;

            MessageBox.Show(
                on
                    ? "无法写入开机自启：系统未允许修改启动项（可能被组策略或权限限制）。\n您仍可手动添加：Win+R 输入 shell:startup，把 ClassNote.exe 的快捷方式放入。"
                    : "无法移除开机自启：系统未允许修改启动项。",
                "开机自启",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ── 课程安排（唯一编辑入口）──────────────────────────────

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ScheduleEditorWindow(_entries)
        {
            Owner = Window.GetWindow(this),
        };
        if (editor.ShowDialog() != true)
            return;
        // 编辑窗口保存成功后：内存列表同步 + 重绘展示
        _entries.Clear();
        _entries.AddRange(LocalRepository.Instance.ListScheduleEntries());
        RenderWeekView();
        UpdateSummary();
    }

    // ── 每周课表可视化展示（只读）────────────────────────────

    /// <summary>重建 05:00–22:00 锁定时段的课表网格：星期表头 + 7 个时间轴列。</summary>
    private void RenderWeekView()
    {
        const int rangeStart = RangeStart, rangeEnd = RangeEnd;
        var totalHeight = (rangeEnd - rangeStart) * HourPx / 60.0 + 24;

        // 表头：时刻列 + 星期列
        WeekHeader.Children.Clear();
        WeekHeader.ColumnDefinitions.Clear();
        WeekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RulerWidth) });
        for (var i = 0; i < 7; i++)
            WeekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DayWidth) });

        var corner = new TextBlock
        {
            Text = "时间",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextHintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(corner, 0);
        WeekHeader.Children.Add(corner);

        var todayIdx = DateTime.Now.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)DateTime.Now.DayOfWeek - 1;
        for (var i = 0; i < 7; i++)
        {
            var cell = new TextBlock
            {
                Text = _weekdayNames[i] + (i == todayIdx ? "（今天）" : ""),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = i == todayIdx
                    ? (Brush)FindResource("PrimaryBrush")
                    : (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cell, i + 1);
            WeekHeader.Children.Add(cell);
        }

        // 时刻刻度列
        var ruler = new Canvas
        {
            Width = RulerWidth,
            Height = totalHeight,
            Background = Brushes.Transparent,
        };
        DrawHourScale(ruler, rangeStart, rangeEnd, isRuler: true);

        // 每一天一列（只读课程块）
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(ruler);
        for (var day = 0; day < 7; day++)
        {
            var dayCanvas = BuildDayCanvas(day, rangeStart, rangeEnd, totalHeight);
            stack.Children.Add(dayCanvas);
        }
        WeekColumns.Children.Clear();
        WeekColumns.Children.Add(stack);
    }

    /// <summary>构建某一天的时间轴列：网格线 + 只读课程块（点击不编辑）。</summary>
    private Canvas BuildDayCanvas(int day, int rangeStart, int rangeEnd, double totalHeight)
    {
        var canvas = new Canvas
        {
            Width = DayWidth,
            Height = totalHeight,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFB, 0xFC, 0xFF)),
            ClipToBounds = true,
            IsHitTestVisible = true,
        };

        DrawHourScale(canvas, rangeStart, rangeEnd, isRuler: false);

        var dayEntries = _entries
            .Where(e => e.WeekdayIndex == day)
            .OrderBy(e => e.StartMin)
            .ToList();

        // 竖向几何统一由 ScheduleGridLayout 计算：高度按真实时长等比，且相邻课块之间保证间隙，
        // 绝不出现"上一节盖住下一节"的视觉重叠（含时间轴外条目的裁剪）。
        var bars = ScheduleGridLayout.ComputeDayBars(
            dayEntries.Select(e => (e.StartMin, e.EndMin)).ToList(), rangeStart, rangeEnd, HourPx);

        for (var i = 0; i < dayEntries.Count; i++)
        {
            var entry = dayEntries[i];
            var bar = bars[i];
            if (!bar.IsVisible)
                continue;   // 与时间轴无交集（定时触发照常）或数据退化

            var color = CourseColor(entry.Course);
            var block = new Border
            {
                Tag = entry,
                Width = DayWidth - 8,
                Height = bar.Height,
                ClipToBounds = true,   // 块矮于文字时裁掉溢出，避免文字越界到下一节
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(Math.Min(7, bar.Height / 2)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0x1F, 0x23, 0x29)),
                Opacity = entry.Enabled ? 1.0 : 0.42,
                Cursor = Cursors.Hand,
                ToolTip = $"{entry.Course}（{entry.WeekdayName}）" +
                          $"\n{ScheduleEntry.FormatClock(entry.StartMin)} – {ScheduleEntry.FormatClock(entry.EndMin)}" +
                          (string.IsNullOrEmpty(entry.Title) ? "" : $"\n{entry.Title}") +
                          "\n（点击课程块编辑此条目）",
            };
            Canvas.SetLeft(block, 4);
            Canvas.SetTop(block, bar.Top);

            // 块内容：文字随块高自适应（两行 → 单行省略号 → 纯色块靠 ToolTip），点击任意位置进入编辑
            var label = BlockLabel(entry, bar.Height);
            if (label.Length > 0)
            {
                block.Child = new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3A)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 1, 4, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            block.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenEntryInView(entry);
            };
            canvas.Children.Add(block);
        }
        return canvas;
    }

    /// <summary>块内文字所需的两行高度（课程名 + 时间段，字号 10 + 上下 Margin）。</summary>
    private const double TwoLineMinHeight = 27;

    /// <summary>块内文字所需的单行高度。</summary>
    private const double OneLineMinHeight = 13;

    /// <summary>
    /// 按块高选择标签：够两行显示"课程名 + 时间段"，只够一行则合并为一行（超出省略号），
    /// 细条（如相邻 10 分钟短课）不写文字，信息由 ToolTip 提供。
    /// </summary>
    private static string BlockLabel(ScheduleEntry entry, double height)
    {
        var range = $"{ScheduleEntry.FormatClock(entry.StartMin)}–{ScheduleEntry.FormatClock(entry.EndMin)}";
        if (height >= TwoLineMinHeight)
            return $"{entry.Course}\n{range}";
        if (height >= OneLineMinHeight)
            return $"{entry.Course} {range}";
        return "";
    }

    /// <summary>
    /// 点击视图中的课程块 → 打开该条目的「编辑课表条目」对话框。
    /// 保存 / 删除后立即写库并重绘视图（同一时间不重叠的校验由对话框完成）。
    /// </summary>
    private void OpenEntryInView(ScheduleEntry entry)
    {
        var dialog = new ScheduleEntryDialog(entry, entry.WeekdayIndex, entry.StartMin,
            _entries, ClassNote.ViewModels.MainViewModel.DefaultCourses)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.Deleted)
        {
            _entries.RemoveAll(x => x.Id == entry.Id);
        }
        else if (dialog.Result is { } result)
        {
            result.Id = entry.Id; // 保持 Id：调度器"当天已触发"记忆不错乱
            _entries.RemoveAll(x => x.Id == entry.Id);
            _entries.Add(result);
        }
        else
        {
            return;
        }

        try
        {
            LocalRepository.Instance.ReplaceScheduleEntries(_entries);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存课表失败：{ex.Message}", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        RenderWeekView();
        UpdateSummary();
    }

    /// <summary>绘制小时刻度：刻度列显示标签，星期列只画浅色刻度线（偶数小时加深便于读数）。</summary>
    private static void DrawHourScale(Canvas canvas, int rangeStart, int rangeEnd, bool isRuler)
    {
        var startHour = rangeStart / 60;
        var endHour = rangeEnd / 60;
        for (var h = startHour; h <= endHour; h++)
        {
            var y = (h * 60 - rangeStart) * HourPx / 60.0;
            if (isRuler)
            {
                var label = new TextBlock
                {
                    Text = h == 24 ? "24:00" : $"{h:00}:00",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)),
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 7);
                canvas.Children.Add(label);
            }
            else
            {
                var width = canvas.Width <= 0 ? DayWidth : canvas.Width;
                var line = new Rectangle
                {
                    Width = width,
                    Height = 1,
                    Fill = h % 2 == 0
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0xE4, 0xE6, 0xEC))
                        : new SolidColorBrush(Color.FromArgb(0x1A, 0xE4, 0xE6, 0xEC)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(line, 0);
                Canvas.SetTop(line, y);
                canvas.Children.Add(line);
            }
        }
    }

    /// <summary>同一课程稳定取同一颜色（按名称哈希取调色板）。</summary>
    private static Color CourseColor(string course)
    {
        var hash = 0;
        foreach (var c in course ?? "")
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        var hex = CoursePalette[hash % CoursePalette.Length];
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private void UpdateSummary()
    {
        var total = _entries.Count;
        var enabled = _entries.Count(e => e.Enabled);
        var weekCount = _entries.GroupBy(e => e.WeekdayIndex).Count();
        // 时段外的课程（如早于 05:00 或晚于 22:00）定时触发照常，但不出现在锁定时间轴上
        var outside = _entries.Count(e => e.EndMin <= RangeStart || e.StartMin >= RangeEnd);
        var outsideNote = outside > 0
            ? $" 另有 {outside} 节在 05:00–22:00 之外（仍会定时触发，此处不显示）。"
            : "";
        SummaryText.Text = total == 0
            ? "暂无课程安排。点击右上角「课程安排」按钮添加每周固定课程，到点将自动开始录音。"
            : $"共 {total} 节课程（已启用 {enabled} 节，覆盖 {weekCount} 天）。编辑请点右上角「课程安排」。{outsideNote}";
    }
}
