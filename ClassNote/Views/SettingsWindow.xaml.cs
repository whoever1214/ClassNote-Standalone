using System.Windows;
using System.Windows.Media;
using ClassNote.Services;

namespace ClassNote.Views;

/// <summary>
/// 应用设置对话框：LLM API 配置 + 定时记录默认麦克风，
/// 持久化到本地设置文件（AppSettings）；支持一键测试 v1 接口连通性。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly string[] _micIds;
    private readonly string[] _micNames;

    public SettingsWindow()
    {
        InitializeComponent();

        var s = AppSettings.Instance.Snapshot();
        ApiKeyBox.Text = s.LlmApiKey;
        BaseUrlBox.Text = s.LlmBaseUrl;
        ModelBox.Text = s.LlmModel;
        TimeoutBox.Text = (s.LlmTimeoutSeconds > 0 ? s.LlmTimeoutSeconds : LlmService.DefaultTimeoutSeconds).ToString();
        FallbackApiKeyBox.Text = s.LlmFallbackApiKey;
        FallbackBaseUrlBox.Text = s.LlmFallbackBaseUrl;

        // 定时记录默认麦克风：枚举真实输入设备（含"系统默认"占位项）
        var audio = new AudioService();
        var names = audio.GetInputDevices().ToList();
        var ids = audio.GetInputDeviceIds().ToList();
        var items = new List<string> { "（系统默认）" };
        items.AddRange(names);
        _micNames = items.ToArray();
        _micIds = new List<string> { "" }.Concat(ids).ToArray();

        ScheduleMicCombo.ItemsSource = _micNames;
        // 当前保存的麦克风：优先按稳定 ID 反查，其次名称；查不到则默认项
        var savedIdx = string.IsNullOrWhiteSpace(s.ScheduleMicId)
            ? 0
            : Math.Max(0, ids.IndexOf(s.ScheduleMicId) + 1);
        if (savedIdx <= 0 && !string.IsNullOrWhiteSpace(s.ScheduleMicName))
            savedIdx = Math.Max(0, names.IndexOf(s.ScheduleMicName) + 1);
        ScheduleMicCombo.SelectedIndex = savedIdx;

        if (names.Count == 0)
            ScheduleMicHint.Text = "未检测到麦克风：定时触发时将因无设备而跳过本节。";
    }

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

        var micIdx = ScheduleMicCombo.SelectedIndex;
        string micId = "", micName = "";
        if (micIdx > 0 && micIdx < _micIds.Length)
        {
            micId = _micIds[micIdx];
            micName = _micNames[micIdx];
        }

        AppSettings.Instance.Update(s =>
        {
            s.LlmApiKey = ApiKeyBox.Text.Trim();
            s.LlmBaseUrl = BaseUrlBox.Text.Trim();
            s.LlmModel = ModelBox.Text.Trim();
            s.LlmTimeoutSeconds = timeout;
            s.LlmFallbackApiKey = FallbackApiKeyBox.Text.Trim();
            s.LlmFallbackBaseUrl = FallbackBaseUrlBox.Text.Trim();
            s.ScheduleMicId = micId;
            s.ScheduleMicName = micName;
        });
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
