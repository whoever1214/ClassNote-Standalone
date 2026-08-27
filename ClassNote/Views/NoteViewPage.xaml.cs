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

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionId is not Guid sid)
            return;

        var confirm = MessageBox.Show(
            "确认删除这条课堂记录？删除后其录音、截图与笔记将一并永久移除，且无法恢复。",
            "删除会话",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        DeleteButton.IsEnabled = false;
        try
        {
            await _viewModel.DeleteSessionAsync(sid);
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"删除失败：{ex.Message}",
                "删除失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            DeleteButton.IsEnabled = true;
        }
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
