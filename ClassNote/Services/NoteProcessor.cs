using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using ClassNote.Models;

namespace ClassNote.Services;

/// <summary>
/// 课堂笔记处理管线编排器：在录音结束后依次执行
/// STT（转写）→ OCR（截图识别）→ LLM（生成笔记 + 思维导图），
/// 全部在客户端本地完成，无需服务端。
/// </summary>
public interface INoteProcessor
{
    /// <summary>处理一个已结束的会话，生成笔记。</summary>
    Task ProcessAsync(Guid sessionId, string? audioPath, IProgress<string>? progress = null);
}

public sealed class NoteProcessor : INoteProcessor
{
    private readonly ISttService _stt;
    private readonly IOcrService _ocr;
    private readonly ILlmService _llm;

    public NoteProcessor(ISttService stt, IOcrService ocr, ILlmService llm)
    {
        _stt = stt;
        _ocr = ocr;
        _llm = llm;
    }

    public async Task ProcessAsync(Guid sessionId, string? audioPath, IProgress<string>? progress = null)
    {
        var repo = LocalRepository.Instance;
        var session = repo.GetSession(sessionId);
        if (session == null)
            return;

        repo.UpdateSessionStatus(sessionId, "processing");

        // 1. STT 转写
        string transcript = "";
        if (!string.IsNullOrEmpty(audioPath) && System.IO.File.Exists(audioPath))
        {
            try
            {
                transcript = await _stt.TranscribeAsync(audioPath, progress);
            }
            catch (Exception ex)
            {
                // STT 失败不影响后续 OCR + LLM（可用截图文字兜底）
                progress?.Report($"语音转写失败：{ex.Message}");
            }
        }

        // 2. OCR 所有截图
        var ocrSb = new StringBuilder();
        var screenshots = repo.ListScreenshots(sessionId);
        foreach (var (seqNo, timestamp, type, imagePath) in screenshots)
        {
            if (!System.IO.File.Exists(imagePath))
                continue;
            try
            {
                var imageData = await System.IO.File.ReadAllBytesAsync(imagePath);
                var text = await _ocr.RecognizeAsync(imageData);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    repo.SetScreenshotOcr(sessionId, seqNo, text);
                    ocrSb.AppendLine($"[{type} @ {FormatTime(timestamp)}]");
                    ocrSb.AppendLine(text);
                }
            }
            catch (Exception ex)
            {
                // 单张 OCR 失败跳过，不阻塞整体管线；但必须留痕，避免静默丢失截图文字
                Debug.WriteLine($"[NoteProcessor] 截图 {seqNo} OCR 失败: {ex.Message}");
                progress?.Report($"截图 {seqNo} OCR 失败：{ex.Message}");
            }
        }

        progress?.Report("正在用 LLM 生成笔记…");

        // 3. LLM 生成笔记 + 思维导图
        Note note = new()
        {
            SessionId = sessionId,
            Title = session.Title ?? session.Course,
        };

        try
        {
            note.ContentMarkdown = await _llm.GenerateNoteAsync(
                session.Course, transcript, ocrSb.ToString(), progress);
        }
        catch (Exception ex)
        {
            // LLM 失败时用转写/OCR 原文兜底生成基础笔记
            note.ContentMarkdown = BuildFallbackNote(session.Course, transcript, ocrSb.ToString());
            note.Summary = $"笔记生成失败：{ex.Message}";
        }

        try
        {
            note.MindmapData = await _llm.GenerateMindmapAsync(session.Course, transcript, progress);
        }
        catch
        {
            note.MindmapData = null;
        }

        repo.SaveNote(note);
        repo.UpdateSessionStatus(sessionId, "completed");
        progress?.Report("完成");
    }

    private static string BuildFallbackNote(string course, string transcript, string ocrText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {course} 课堂笔记");
        sb.AppendLine();
        sb.AppendLine("> 备注：LLM 生成失败，以下为原始转写与截图 OCR 内容。");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            sb.AppendLine("## 课堂录音转写");
            sb.AppendLine();
            sb.AppendLine(transcript);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            sb.AppendLine("## 课件截图 OCR");
            sb.AppendLine();
            sb.AppendLine(ocrText);
        }
        return sb.ToString();
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.ToString(@"hh\:mm\:ss");
    }
}
