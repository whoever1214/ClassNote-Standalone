using System.Windows;
using System.Windows.Media;
using ClassNote.Services;

namespace ClassNote.Views;

/// <summary>
/// LLM API 配置对话框：客户端配置 API Key、基础地址、模型与请求超时，
/// 持久化到本地设置文件（AppSettings）；支持一键测试 v1 接口连通性。
/// </summary>
public partial class SettingsWindow : Window
{
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

        AppSettings.Instance.Update(s =>
        {
            s.LlmApiKey = ApiKeyBox.Text.Trim();
            s.LlmBaseUrl = BaseUrlBox.Text.Trim();
            s.LlmModel = ModelBox.Text.Trim();
            s.LlmTimeoutSeconds = timeout;
            s.LlmFallbackApiKey = FallbackApiKeyBox.Text.Trim();
            s.LlmFallbackBaseUrl = FallbackBaseUrlBox.Text.Trim();
        });
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
