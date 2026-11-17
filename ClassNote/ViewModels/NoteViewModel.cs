using System.IO;
using ClassNote.Models;
using ClassNote.Services;

namespace ClassNote.ViewModels;

public class NoteViewModel : BaseViewModel
{
    private readonly IApiService _api;
    private Note? _note;
    private string _markdownHtml = "";
    private bool _isLoading;
    private string _errorMessage = "";
    private bool _isFavorite;
    private string _statusText = "";

    /// <summary>
    /// Markdown 管线与 HTML 文档模板都收在 <see cref="NoteHtmlRenderer"/> 里：
    /// 公式渲染的三段（Markdig 吐出的分隔符 / MathJax 认的分隔符 / CSS 选择器）必须彼此对齐，
    /// 分散到多处曾直接导致"每对括号都被当成公式"的线上事故（详见该类注释）。
    /// </summary>
    public NoteViewModel(IApiService api) { _api = api; }

    public Note? Note { get => _note; set { _note = value; OnPropertyChanged(); } }
    public string MarkdownHtml { get => _markdownHtml; set { _markdownHtml = value; OnPropertyChanged(); } }
    public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }
    public string ErrorMessage { get => _errorMessage; set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>工具栏上的短状态（"正在用已保存的转写重新生成…"）。</summary>
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusText);

    /// <summary>是否已收藏（决定工具栏按钮文案）。</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteButtonText));
        }
    }

    /// <summary>收藏按钮文案。</summary>
    public string FavoriteButtonText => _isFavorite ? "已收藏" : "收藏";

    public async Task LoadNoteAsync(Guid sessionId)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            Note = await _api.GetNoteAsync(sessionId);
            IsFavorite = await _api.IsSessionFavoriteAsync(sessionId);
            MarkdownHtml = "";
            if (Note?.ContentMarkdown != null)
                MarkdownHtml = NoteHtmlRenderer.RenderDocument(Note.ContentMarkdown);
        }
        catch (Exception ex)
        {
            // 不再让异常冒泡到 async void 事件处理器导致进程崩溃，改为展示错误信息
            ErrorMessage = $"加载笔记失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>收藏 / 取消收藏。收藏后这节的音频、转写、截图不再被过期清理回收。</summary>
    public async Task<bool> ToggleFavoriteAsync(Guid sessionId)
    {
        bool next = !IsFavorite;
        await _api.SetSessionFavoriteAsync(sessionId, next);
        IsFavorite = next;
        return next;
    }

    /// <summary>
    /// 用**已保存的转写 + 截图**重新生成笔记（不重新转写）。完成后自动重新加载正文。
    /// </summary>
    /// <returns>失败原因；成功返回 null。</returns>
    public async Task<string?> RegenerateAsync(Guid sessionId)
    {
        IsLoading = true;
        ErrorMessage = "";
        StatusText = "正在用已保存的转写与截图重新生成…";
        try
        {
            var processor = NoteProcessorFactory.CreateDefault();
            await processor.RegenerateAsync(sessionId,
                new Progress<string>(m => StatusText = m));

            await LoadNoteAsync(sessionId);
            if (Note == null)
                return "重新生成后没有读到笔记（可能这节课没有可用的转写或截图素材）";
            return null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"重新生成失败: {ex.Message}";
            return ex.Message;
        }
        finally
        {
            StatusText = "";
            IsLoading = false;
        }
    }

    /// <summary>
    /// 导出课堂笔记为 PDF 并保存到用户桌面（客户端本地渲染，基于 QuestPDF），
    /// 返回保存的完整路径。
    /// </summary>
    public async Task<string> ExportPdfToDesktopAsync(Guid sessionId)
    {
        IsLoading = true;
        try
        {
            var bytes = await _api.ExportNotePdfAsync(sessionId);
            if (bytes == null || bytes.Length == 0)
                throw new InvalidOperationException("笔记尚未生成");

            var title = Note?.Title ?? sessionId.ToString();
            var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, $"{safeTitle}.pdf");

            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除当前会话（含录音、截图、笔记）。</summary>
    public async Task DeleteSessionAsync(Guid sessionId)
    {
        await _api.DeleteSessionAsync(sessionId);
    }
}
