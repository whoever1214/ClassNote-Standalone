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

            // 本地化 MathJax：将虚拟主机 appassets.local 映射到输出目录 assets 文件夹，
            // 使笔记 HTML 无需联网即可加载数学公式渲染脚本（离线渲染）。
            var assetsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "assets");
            if (MarkdownBrowser.CoreWebView2 != null && System.IO.Directory.Exists(assetsDir))
            {
                MarkdownBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "appassets.local", assetsDir,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
        }
        catch
        {
            // WebView2 runtime unavailable — browser stays empty
        }

        try
        {
            await _viewModel.LoadNoteAsync(sid);
            // 收藏图标跟着实际状态走（实心星 / 空心星）
            FavoriteIcon.Text = _viewModel.IsFavorite ? "\uE735" : "\uE734";
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

    /// <summary>
    /// 收藏 / 取消收藏。收藏后这节的录音、转写与截图永久保留（过期清理会跳过）。
    /// </summary>
    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionId is not Guid sid)
            return;

        FavoriteButton.IsEnabled = false;
        try
        {
            bool nowFavorite = await _viewModel.ToggleFavoriteAsync(sid);
            FavoriteIcon.Text = nowFavorite ? "\uE735" : "\uE734";   // 实心星 / 空心星
            if (nowFavorite)
            {
                MessageBox.Show(
                    "已收藏。这节课的录音、转写与截图将永久保留，不会被自动清理。",
                    "已收藏", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败：{ex.Message}", "收藏失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FavoriteButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 用已保存的转写 + 截图重新生成笔记（不重新转写、不重新录音）。
    /// 会花掉一次 LLM 调用，所以先问一句。
    /// </summary>
    private async void RegenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionId is not Guid sid)
            return;

        var confirm = MessageBox.Show(
            "用已保存的课堂转写与截图重新生成笔记？\n\n" +
            "· 不会重新录音，也不会重新转写；\n" +
            "· 会消耗一次 LLM 调用（按当前模型计费）；\n" +
            "· 原来的笔记会被新生成的笔记覆盖。",
            "重新生成笔记",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        RegenerateButton.IsEnabled = false;
        ExportPdfButton.IsEnabled = false;
        try
        {
            var error = await _viewModel.RegenerateAsync(sid);
            if (error != null)
            {
                MessageBox.Show($"重新生成失败：{error}", "重新生成失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MarkdownBrowser.CoreWebView2 != null && !string.IsNullOrEmpty(_viewModel.MarkdownHtml))
                MarkdownBrowser.NavigateToString(_viewModel.MarkdownHtml);
        }
        finally
        {
            RegenerateButton.IsEnabled = true;
            ExportPdfButton.IsEnabled = true;
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
