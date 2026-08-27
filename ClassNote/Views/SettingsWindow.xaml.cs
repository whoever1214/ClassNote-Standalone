using System.Windows;
using ClassNote.Services;

namespace ClassNote.Views;

/// <summary>
/// LLM API 配置对话框：客户端配置 API Key、基础地址与模型，
/// 持久化到本地设置文件（AppSettings）。
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
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.Update(s =>
        {
            s.LlmApiKey = ApiKeyBox.Text.Trim();
            s.LlmBaseUrl = BaseUrlBox.Text.Trim();
            s.LlmModel = ModelBox.Text.Trim();
        });
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
