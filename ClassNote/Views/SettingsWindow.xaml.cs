using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassNote.Services;

namespace ClassNote.Views;

/// <summary>
/// 应用设置对话框，分为三个页签：
///   · API 配置 —— LLM 供应商地址 / API Key / 模型（自动拉取下拉选择）/ 超时 / 连通性测试；
///   · 录音设置 —— 声音来源（麦克风 / 系统声音 / 混合）与具体设备；
///   · OCR 识别 —— 截图文字用内置 Windows OCR 还是内网 PaddleOCR 服务，以及地址/请求方式/超时/回退。
/// 持久化到本地设置文件（AppSettings）。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>线程内共享的模型列表缓存：同一供应商不必每次打开设置都重新拉一遍。</summary>
    private static string _cachedBaseUrl = "";
    private static string _cachedApiKey = "";
    private static IReadOnlyList<string> _cachedModels = Array.Empty<string>();

    private readonly string[] _micIds;
    private readonly string[] _micNames;
    private readonly string[] _outputIds;
    private readonly string[] _outputNames;

    private readonly DispatcherTimer _autoFetchTimer;
    private bool _modelsLoading;
    private bool _suppressSourceChanged;

    public SettingsWindow()
    {
        InitializeComponent();

        var s = AppSettings.Instance.Snapshot();
        BaseUrlBox.Text = s.LlmBaseUrl;
        ApiKeyBox.Text = s.LlmApiKey;
        ModelCombo.Text = s.LlmModel;
        TimeoutBox.Text = (s.LlmTimeoutSeconds > 0 ? s.LlmTimeoutSeconds : LlmService.DefaultTimeoutSeconds).ToString();

        // 设备枚举：麦克风（采集端点）与播放设备（回环来源）各自带"系统默认"占位项
        var audio = new AudioService();
        var micNames = audio.GetInputDevices().ToList();
        _micNames = new[] { AudioDeviceSelection.SystemDefaultLabel }.Concat(micNames).ToArray();
        _micIds = new[] { "" }.Concat(audio.GetInputDeviceIds()).ToArray();
        MicCombo.ItemsSource = _micNames;
        MicCombo.SelectedIndex = AudioDeviceSelection.ResolveIndex(
            _micIds, _micNames, s.RecordingMicId, s.RecordingMicName);

        var outputNames = audio.GetOutputDevices().ToList();
        _outputNames = new[] { AudioDeviceSelection.SystemDefaultLabel }.Concat(outputNames).ToArray();
        _outputIds = new[] { "" }.Concat(audio.GetOutputDeviceIds()).ToArray();
        OutputCombo.ItemsSource = _outputNames;
        OutputCombo.SelectedIndex = AudioDeviceSelection.ResolveIndex(
            _outputIds, _outputNames, s.RecordingOutputDeviceId, s.RecordingOutputDeviceName);

        if (micNames.Count == 0)
            MicHint.Text = "未检测到麦克风：使用麦克风来源时将无法开始录音。";

        // MME 回退路径没有回环能力：提前说明，别让用户选完才发现录不到系统声音
        if (audio.IsUsingMmeFallback)
        {
            OutputHint.Text = "当前音频子系统仅支持 MME 采集，无法录制系统声音；" +
                              "使用「系统声音」来源将启动失败，请改用麦克风来源。";
            SourceCombo.IsEnabled = false;
        }

        // 声音来源：只在需要时才让用户看到对应的设备选择（减少无效配置）
        SourceCombo.ItemsSource = AudioSourceKinds.DisplayNames;
        _suppressSourceChanged = true;
        SourceCombo.SelectedIndex = (int)AudioSourceKinds.FromStorage(s.RecordingSource);
        _suppressSourceChanged = false;
        ApplySourceVisibility();

        // 课堂占用档位（v0.7 边录边转写）：顺序与枚举一一对应，直接按 SelectedIndex 映射
        ClassroomTranscriptionCombo.ItemsSource = ClassroomTranscriptionModes.DisplayNames;
        ClassroomTranscriptionCombo.SelectedIndex =
            (int)ClassroomTranscriptionModes.FromStorage(s.ClassroomTranscription);

        // 触摸屏优化（一体机 / 触摸屏）：默认开。关掉后长按不再等同于右键，
        // 因此文案里必须说清楚代价（没有鼠标就再也调不出右键菜单）。
        TouchOptimizationsCheck.IsChecked = s.TouchOptimizations;

        // OCR 引擎（v1.1）：同样是"顺序与枚举一一对应"的下拉框
        OcrEngineCombo.ItemsSource = OcrEngines.DisplayNames;
        OcrFormatCombo.ItemsSource = OcrEngines.RequestFormatDisplayNames;
        OcrUrlBox.Text = s.OcrServiceUrl;
        OcrKeyBox.Text = s.OcrServiceApiKey;
        OcrTimeoutBox.Text = OcrEngines.NormalizeTimeout(s.OcrTimeoutSeconds).ToString();
        OcrFallbackCheck.IsChecked = s.OcrFallbackToWindows;
        OcrFormatCombo.SelectedIndex = (int)OcrEngines.RequestFormatFromStorage(s.OcrRequestFormat);
        OcrEngineCombo.SelectedIndex = (int)OcrEngines.FromStorage(s.OcrEngine);
        ApplyOcrVisibility();

        // 地址/Key 填好后自动拉取模型列表（防抖，避免边输入边打请求）
        _autoFetchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _autoFetchTimer.Tick += async (_, _) =>
        {
            _autoFetchTimer.Stop();
            await TryAutoFetchModelsAsync();
        };

        SeedModelCombo();
        Loaded += (_, _) => ScheduleAutoFetch();
    }

    /// <summary>当前选中的声音来源。</summary>
    private AudioSourceKind SelectedSource
        => (AudioSourceKind)Math.Max(0, SourceCombo.SelectedIndex);

    // ── 模型列表 ─────────────────────────────────────────────

    /// <summary>把缓存/已保存的模型填进下拉框（保证当前配置的模型一定在候选里）。</summary>
    private void SeedModelCombo()
    {
        var models = new List<string>();
        var saved = ModelCombo.Text?.Trim() ?? "";
        if (IsCacheValid())
            models.AddRange(_cachedModels);
        if (!string.IsNullOrEmpty(saved) && !models.Contains(saved, StringComparer.OrdinalIgnoreCase))
            models.Insert(0, saved);

        ApplyModels(models.ToArray(), hint: null);
    }

    /// <summary>缓存是否对应当前的地址 + Key（Key 变了说明可能换了供应商）。</summary>
    private bool IsCacheValid()
        => _cachedModels.Count > 0
           && string.Equals(_cachedBaseUrl, BaseUrlBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(_cachedApiKey, ApiKeyBox.Text.Trim(), StringComparison.Ordinal);

    private void ApplyModels(IReadOnlyList<string> models, string? hint)
    {
        var current = ModelCombo.Text?.Trim() ?? "";
        ModelCombo.ItemsSource = models;

        // 已保存的模型不在新列表里时保留用户的选择，不做静默改写
        if (!string.IsNullOrEmpty(current))
            ModelCombo.Text = current;
        else if (models.Count > 0)
            ModelCombo.Text = models[0];

        if (hint != null)
        {
            ModelHint.Text = hint;
            ModelHint.Foreground = (Brush)FindResource("TextHintBrush");
        }
        else if (models.Count > 0)
        {
            ModelHint.Text = $"已载入 {models.Count} 个模型候选；换供应商或换 Key 后可用右上角按钮重新获取。";
            ModelHint.Foreground = (Brush)FindResource("TextHintBrush");
        }
    }

    private void ScheduleAutoFetch()
    {
        if (IsCacheValid() || _modelsLoading)
            return;
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text) || string.IsNullOrWhiteSpace(ApiKeyBox.Text))
            return;
        _autoFetchTimer.Stop();
        _autoFetchTimer.Start();
    }

    /// <summary>
    /// 地址 + Key 都有值时自动拉取可用模型；只自动成功一次，之后靠"刷新模型列表"按钮，
    /// 避免用户每次点开设置都打一次网络请求。
    /// </summary>
    private async Task TryAutoFetchModelsAsync()
    {
        if (_modelsLoading || IsCacheValid())
            return;
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text) || string.IsNullOrWhiteSpace(ApiKeyBox.Text))
            return;
        await FetchModelsAsync(announce: true);
    }

    private async Task FetchModelsAsync(bool announce)
    {
        if (_modelsLoading)
            return;

        _modelsLoading = true;
        RefreshModelsButton.IsEnabled = false;
        var previousHint = ModelHint.Text;
        if (announce)
        {
            ModelHint.Text = "正在获取模型列表…";
            ModelHint.Foreground = (Brush)FindResource("TextHintBrush");
        }

        try
        {
            var result = await new LlmService().ListModelsAsync(
                apiKey: ApiKeyBox.Text.Trim(),
                baseUrl: BaseUrlBox.Text.Trim());

            if (result.IsOk)
            {
                var models = new List<string>(result.Models);
                var current = ModelCombo.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(current) && !models.Contains(current, StringComparer.OrdinalIgnoreCase))
                    models.Insert(0, current);

                _cachedBaseUrl = BaseUrlBox.Text.Trim();
                _cachedApiKey = ApiKeyBox.Text.Trim();
                _cachedModels = result.Models;

                ApplyModels(models, result.Message);
            }
            else
            {
                // 拉取失败不打断用户：保留原候选（含已保存的模型名），只是提示原因
                ModelHint.Text = result.Message + "；可继续手动输入模型名称。";
                ModelHint.Foreground = (Brush)FindResource("TextHintBrush");
                if (!announce)
                    ModelHint.Text = previousHint;
            }
        }
        catch (Exception ex)
        {
            ModelHint.Text = "获取模型列表失败：" + ex.Message + "；可继续手动输入模型名称。";
            ModelHint.Foreground = (Brush)FindResource("TextHintBrush");
        }
        finally
        {
            _modelsLoading = false;
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text) || string.IsNullOrWhiteSpace(ApiKeyBox.Text))
        {
            ModelHint.Text = "请先填写 API 基础地址与 API Key。";
            ModelHint.Foreground = (Brush)FindResource("TextHintBrush");
            return;
        }
        await FetchModelsAsync(announce: true);
    }

    private void BaseUrlBox_LostFocus(object sender, RoutedEventArgs e) => ScheduleAutoFetch();

    private void ApiKeyBox_LostFocus(object sender, RoutedEventArgs e) => ScheduleAutoFetch();

    // ── 录音设置 ─────────────────────────────────────────────

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceChanged)
            return;
        ApplySourceVisibility();
    }

    /// <summary>按选中的来源显示/隐藏对应设备选择，并说明定时记录会用到的来源。</summary>
    private void ApplySourceVisibility()
    {
        var source = SelectedSource;
        bool needsMic = source is AudioSourceKind.Microphone or AudioSourceKind.Both;
        bool needsSystem = source is AudioSourceKind.System or AudioSourceKind.Both;

        MicSection.Visibility = needsMic ? Visibility.Visible : Visibility.Collapsed;
        SystemSection.Visibility = needsSystem ? Visibility.Visible : Visibility.Collapsed;

        string sourceText = AudioSourceKinds.ToDisplayName(source);
        ScheduleSourceHint.Text =
            $"定时记录将使用当前声音来源（{sourceText}）与上面的设备选择。定时触发时没有弹窗选择机会，" +
            "请在此固定设备；设备不可用时麦克风回退系统默认设备。";

        RecordingTipText.Text = source switch
        {
            AudioSourceKind.System =>
                "仅采集系统声音：麦克风不会被录音，适合在线课程 / 视频。注意区分耳机与扬声器，选错设备会录成静音。",
            AudioSourceKind.Both =>
                "麦克风与系统声音各录一份（分轨保存）、分别转写，笔记里会标明哪些内容来自「现场」、哪些来自「课件」，" +
                "适合既要现场人声又要设备声音的课堂；代价是两路各跑一次语音识别，处理时间约为单路的两倍。",
            _ =>
                "仅采集麦克风：教室现场人声。需要同时录下设备外放的声音时，请改选「仅系统声音」或「麦克风和系统声音」。",
        };
        RecordingTipText.Text += " 录音全程在本地完成，不会上传到任何服务器。";
    }

    // ── OCR 识别 ─────────────────────────────────────────────

    /// <summary>当前选中的 OCR 引擎。</summary>
    private OcrEngineKind SelectedOcrEngine
        => OcrEngines.FromStorage(OcrEngines.ToStorage(
            (OcrEngineKind)Math.Clamp(OcrEngineCombo.SelectedIndex, 0, OcrEngines.DisplayNames.Length - 1)));

    private OcrRequestFormat SelectedOcrFormat
        => OcrEngines.RequestFormatFromStorage(OcrEngines.ToStorage(
            (OcrRequestFormat)Math.Clamp(OcrFormatCombo.SelectedIndex, 0, OcrEngines.RequestFormatDisplayNames.Length - 1)));

    private void OcrEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyOcrVisibility();

    /// <summary>只有选了远程服务才显示地址/密钥/超时等配置；同时把界面上的取舍说清楚。</summary>
    private void ApplyOcrVisibility()
    {
        if (OcrRemoteSection == null) return;   // 构造过程中控件尚未就绪

        bool remote = SelectedOcrEngine == OcrEngineKind.PaddleHttp;
        OcrRemoteSection.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;

        if (remote && string.IsNullOrWhiteSpace(OcrUrlBox.Text))
            OcrUrlBox.Text = OcrEngines.DefaultPaddleUrl;

        OcrTipText.Text = remote
            ? "注意：选中内网服务后，截图会发送到上面这个地址做识别；服务地址与请求方式必须和内网部署一致，" +
              "否则会一直识别不出文字（开启回退后会改用本机引擎）。识别结果写入本机数据库，笔记生成时只读现成结果。"
            : "内置引擎完全离线：截图不出本机。识别结果写入本机数据库，笔记生成时只读现成结果。";
    }

    private int ParsedOcrTimeout()
    {
        if (int.TryParse(OcrTimeoutBox.Text?.Trim(), out int seconds)
            && seconds >= OcrEngines.MinTimeoutSeconds && seconds <= OcrEngines.MaxTimeoutSeconds)
            return seconds;
        return OcrEngines.DefaultPaddleTimeoutSeconds;
    }

    /// <summary>
    /// 测试识别：拿**真实的一张截图**（最近一次会话的最后一张）去打一遍，没有截图就现场渲染一张测试卡。
    /// 用真图能一次性验证"地址 + 请求方式 + 鉴权 + 解析"整条链路，比只 ping 一下有用得多。
    /// </summary>
    private async void TestOcrButton_Click(object sender, RoutedEventArgs e)
    {
        TestOcrButton.IsEnabled = false;
        TestOcrResultText.Text = "识别中…";
        TestOcrResultText.Foreground = (Brush)FindResource("TextSecondaryBrush");

        try
        {
            var kind = SelectedOcrEngine;
            if (kind == OcrEngineKind.PaddleHttp && !OcrEngines.IsUsableUrl(OcrUrlBox.Text))
            {
                TestOcrResultText.Text = "请先填写合法的服务地址（http/https）。";
                TestOcrResultText.Foreground = (Brush)FindResource("DangerBrush");
                return;
            }

            var sample = TryLoadNewestScreenshot() ?? RenderTestCard();
            string source = _lastSampleWasRealScreenshot ? "最近一张课堂截图" : "内置测试图";

            IOcrService service = kind == OcrEngineKind.PaddleHttp
                ? new PaddleOcrService(OcrUrlBox.Text.Trim(), SelectedOcrFormat,
                    OcrKeyBox.Text.Trim(), ParsedOcrTimeout())
                : new WindowsOcrService();

            var sw = Stopwatch.StartNew();
            var text = await service.RecognizeAsync(sample);
            sw.Stop();

            if (string.IsNullOrWhiteSpace(text))
            {
                TestOcrResultText.Text = $"{source}：服务可达，但没有识别出文字（用时 {sw.ElapsedMilliseconds} ms）。" +
                                         "请确认请求方式与部署方式一致。";
                TestOcrResultText.Foreground = (Brush)FindResource("DangerBrush");
                return;
            }

            var preview = text.Replace('\n', ' ').Trim();
            if (preview.Length > 40) preview = preview[..40] + "…";
            TestOcrResultText.Text = $"{source}识别成功：{text.Length} 字，用时 {sw.ElapsedMilliseconds} ms。开头：{preview}";
            TestOcrResultText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            TestOcrResultText.Text = "识别失败：" + ex.Message;
            TestOcrResultText.Foreground = (Brush)FindResource("DangerBrush");
        }
        finally
        {
            TestOcrButton.IsEnabled = true;
        }
    }

    private bool _lastSampleWasRealScreenshot;

    /// <summary>取最近一张截图（按修改时间）。没有截图目录/截图时返回 null。</summary>
    private byte[]? TryLoadNewestScreenshot()
    {
        _lastSampleWasRealScreenshot = false;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassNote", "screenshots");
            if (!Directory.Exists(dir)) return null;

            var newest = new DirectoryInfo(dir)
                .EnumerateFiles("*.jpg", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;

            var bytes = File.ReadAllBytes(newest.FullName);
            _lastSampleWasRealScreenshot = bytes.Length > 0;
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;   // 读不到截图不算错误：退回测试卡
        }
    }

    /// <summary>没有截图可测时现场画一张测试卡（含中文、英文与数字序列）。</summary>
    private static byte[] RenderTestCard()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 460, 120));
            var line1 = new FormattedText("ClassNote OCR 测试", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 30, Brushes.Black, 96);
            var line2 = new FormattedText("1 7 3 5 9 4 8    f(n)=min(f(n-1)+1)", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 22, Brushes.Black, 96);
            dc.DrawText(line1, new Point(18, 14));
            dc.DrawText(line2, new Point(18, 66));
        }

        var bitmap = new RenderTargetBitmap(460, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ── 测试连接 ─────────────────────────────────────────────

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResultText.Text = "检测中…";
        TestResultText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        try
        {
            // 使用界面上当前填写的地址/Key 测试（未保存也能测）
            var result = await new LlmService().CheckHealthAsync(
                apiKey: ApiKeyBox.Text.Trim(),
                baseUrl: BaseUrlBox.Text.Trim());
            TestResultText.Text = result.Message;
            TestResultText.Foreground = (Brush)FindResource(
                result.IsOk ? "SuccessBrush" : result.IsConfigured ? "DangerBrush" : "TextHintBrush");
        }
        catch (Exception ex)
        {
            TestResultText.Text = "检测失败：" + ex.Message;
            TestResultText.Foreground = (Brush)FindResource("DangerBrush");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    // ── 保存 ─────────────────────────────────────────────────

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        int timeout = LlmService.DefaultTimeoutSeconds;
        if (!string.IsNullOrWhiteSpace(TimeoutBox.Text))
        {
            if (!int.TryParse(TimeoutBox.Text.Trim(), out timeout)
                || timeout < 60 || timeout > 7200)
            {
                MessageBox.Show("请求超时请输入 60–7200 之间的整数（秒）。",
                    "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // OCR：选了远程服务就必须有一个合法地址，否则下一次录音会静默退回内置引擎，
        // 用户以为在用内网服务、实际识别精度没变——这种"看起来生效了"最该在保存时就拦住。
        var ocrEngine = SelectedOcrEngine;
        if (ocrEngine == OcrEngineKind.PaddleHttp && !OcrEngines.IsUsableUrl(OcrUrlBox.Text))
        {
            MessageBox.Show("选择了内网 PaddleOCR 服务，请填写合法的服务地址（http:// 或 https:// 开头）。",
                "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(OcrTimeoutBox.Text?.Trim(), out int ocrTimeout)
            || ocrTimeout < OcrEngines.MinTimeoutSeconds || ocrTimeout > OcrEngines.MaxTimeoutSeconds)
        {
            MessageBox.Show($"OCR 请求超时请输入 {OcrEngines.MinTimeoutSeconds}–{OcrEngines.MaxTimeoutSeconds} 之间的整数（秒）。",
                "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 路径类输入只接受下拉框里存在的设备，绝不把界面上的任意文本当设备 ID 用
        string micId = "", micName = "";
        if (MicCombo.SelectedIndex > 0 && MicCombo.SelectedIndex < _micIds.Length)
        {
            micId = _micIds[MicCombo.SelectedIndex];
            micName = _micNames[MicCombo.SelectedIndex];
        }

        string outputId = "", outputName = "";
        if (OutputCombo.SelectedIndex > 0 && OutputCombo.SelectedIndex < _outputIds.Length)
        {
            outputId = _outputIds[OutputCombo.SelectedIndex];
            outputName = _outputNames[OutputCombo.SelectedIndex];
        }

        AppSettings.Instance.Update(s =>
        {
            s.LlmApiKey = ApiKeyBox.Text.Trim();
            s.LlmBaseUrl = BaseUrlBox.Text.Trim();
            s.LlmModel = (ModelCombo.Text ?? "").Trim();
            s.LlmTimeoutSeconds = timeout;
            s.RecordingSource = AudioSourceKinds.ToStorage(SelectedSource);
            s.RecordingMicId = micId;
            s.RecordingMicName = micName;
            s.RecordingOutputDeviceId = outputId;
            s.RecordingOutputDeviceName = outputName;
            s.ClassroomTranscription = ((ClassroomTranscriptionMode)Math.Max(0,
                ClassroomTranscriptionCombo.SelectedIndex)).ToString();
            s.OcrEngine = OcrEngines.ToStorage(ocrEngine);
            s.OcrServiceUrl = OcrUrlBox.Text.Trim();
            s.OcrRequestFormat = OcrEngines.ToStorage(SelectedOcrFormat);
            s.OcrServiceApiKey = OcrKeyBox.Text.Trim();
            s.OcrTimeoutSeconds = ocrTimeout;
            s.OcrFallbackToWindows = OcrFallbackCheck.IsChecked == true;
            s.TouchOptimizations = TouchOptimizationsCheck.IsChecked == true;
        });
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
