using System.Windows;
using System.Windows.Controls;
using ClassNote.Services;
using ClassNote.ViewModels;

namespace ClassNote.Views;

public partial class NoteViewPage : Page
{
    private readonly NoteViewModel _viewModel;
    private readonly Guid? _sessionId;

    /// <summary>Raised when the user clicks the "← 返回" button.</summary>
    public event EventHandler? BackRequested;

    public NoteViewPage()
    {
        InitializeComponent();

        _viewModel = new NoteViewModel(new ApiService());
        DataContext = _viewModel;
    }

    public NoteViewPage(Guid sessionId) : this()
    {
        _sessionId = sessionId;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_sessionId is not Guid sid)
            return;

        try
        {
            await MarkdownBrowser.EnsureCoreWebView2Async();
        }
        catch
        {
            // WebView2 runtime unavailable — browser stays empty
        }

        try
        {
            await _viewModel.LoadNoteAsync(sid);
        }
        catch (Exception ex)
        {
            // LoadNoteAsync 内部已捕获，这里兜底防止任何未预期异常令 async void 崩溃
            System.Diagnostics.Debug.WriteLine($"[NoteViewPage] 加载笔记异常: {ex}");
        }

        if (MarkdownBrowser.CoreWebView2 != null && !string.IsNullOrEmpty(_viewModel.MarkdownHtml))
            MarkdownBrowser.NavigateToString(_viewModel.MarkdownHtml);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void ExportPdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionId is not Guid sid)
            return;

        ExportPdfButton.IsEnabled = false;
        ExportPdfButton.Content = "导出中...";
        try
        {
            var path = await _viewModel.ExportPdfToDesktopAsync(sid);
            MessageBox.Show(
                $"笔记 PDF 已保存到：{path}",
                "导出成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"导出 PDF 失败：{ex.Message}",
                "导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ExportPdfButton.Content = "导出 PDF";
            ExportPdfButton.IsEnabled = true;
        }
    }
}
