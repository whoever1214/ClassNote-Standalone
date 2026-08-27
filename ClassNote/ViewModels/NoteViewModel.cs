using System.IO;
using ClassNote.Models;
using ClassNote.Services;
using Markdig;

namespace ClassNote.ViewModels;

public class NoteViewModel : BaseViewModel
{
    private readonly IApiService _api;
    private Note? _note;
    private string _markdownHtml = "";
    private bool _isLoading;
    private string _errorMessage = "";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        // 关键安全项：禁止 raw HTML 直接透传，避免服务端/用户内容被注入 <script> 等标签执行
        .DisableHtml()
        .Build();

    public NoteViewModel(IApiService api) { _api = api; }

    public Note? Note { get => _note; set { _note = value; OnPropertyChanged(); } }
    public string MarkdownHtml { get => _markdownHtml; set { _markdownHtml = value; OnPropertyChanged(); } }
    public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }
    public string ErrorMessage { get => _errorMessage; set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async Task LoadNoteAsync(Guid sessionId)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            Note = await _api.GetNoteAsync(sessionId);
            MarkdownHtml = "";
            if (Note?.ContentMarkdown != null)
            {
                var body = Markdown.ToHtml(Note.ContentMarkdown, Pipeline);
                MarkdownHtml = BuildDocument(body);
            }
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

    /// <summary>Wraps rendered markdown body into a full HTML document with MathJax + readable styling.</summary>
    private static string BuildDocument(string bodyHtml)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta http-equiv='Content-Security-Policy' content=""default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline' https://cdn.jsdelivr.net; img-src data: https:;"">
<style>
  body {{
    font-family: 'Segoe UI', 'Microsoft YaHei', -apple-system, sans-serif;
    font-size: 15px;
    line-height: 1.7;
    color: #333;
    padding: 20px 26px;
    max-width: 860px;
    margin: 0 auto;
    user-select: text;
    -webkit-user-select: text;
  }}
  h1 {{ font-size: 24px; border-bottom: 2px solid #1976D2; padding-bottom: 8px; }}
  h2 {{ font-size: 20px; margin-top: 24px; color: #1565C0; }}
  h3 {{ font-size: 17px; color: #333; }}
  p, li {{ line-height: 1.8; }}
  ul, ol {{ padding-left: 24px; }}
  code {{
    background: #f5f5f5;
    padding: 2px 6px;
    border-radius: 4px;
    font-family: Consolas, 'Courier New', monospace;
    font-size: 13px;
  }}
  pre {{
    background: #f6f8fa;
    padding: 14px 16px;
    border-radius: 6px;
    overflow-x: auto;
  }}
  pre code {{ background: none; padding: 0; }}
  blockquote {{
    border-left: 4px solid #B0BEC5;
    margin: 12px 0;
    padding: 4px 16px;
    color: #555;
    background: #fafafa;
  }}
  table {{ border-collapse: collapse; margin: 12px 0; }}
  th, td {{ border: 1px solid #ddd; padding: 6px 12px; }}
  th {{ background: #f1f5f9; }}
  .math.inline {{ display: inline; }}
  .math.display {{ display: block; text-align: center; margin: 12px 0; }}
  a {{ color: #1976D2; }}
  hr {{ border: none; border-top: 1px solid #e0e0e0; }}
</style>
<script>
  MathJax = {{
    tex: {{ inlineMath: [['(', ')']], displayMath: [['[', ']']] }},
    svg: {{ fontCache: 'global' }}
  }};
</script>
<script async src='https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-svg.js'></script>
</head>
<body>
{bodyHtml}
</body>
</html>";
    }
}
