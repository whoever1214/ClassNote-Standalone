using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassNote.Models;

namespace ClassNote.Services;

/// <summary>
/// 课堂笔记处理管线编排器：在录音结束后依次执行
/// STT（转写）→ OCR（截图识别）→ LLM（生成笔记），
/// 全部在客户端本地完成，无需服务端。
///
/// v0.7 提速要点（结论见 docs/笔记生成提速方案-v0.7.md）：
/// · **增量转写交接**：录音期间已按块转写完的部分直接复用，只补算缺的块；
/// · **转写与 OCR 并行**：两者没有数据依赖，旧实现却严格串行；
/// · **OCR 提前做**：录音期间截图一落库就 OCR，管线阶段只读现成结果；
/// · **多路并行**：麦克风 / 系统声音两路同时补算（课后已无放映要保护）。
/// </summary>
public interface INoteProcessor
{
    /// <summary>
    /// 处理一个已结束的会话，生成笔记。
    /// </summary>
    /// <param name="audio">
    /// 本次录音的音频文件。分轨录音（麦克风和系统声音）会带多路，
    /// 逐路转写后按来源分别标注再交给 LLM。
    /// </param>
    /// <param name="progress">进度回调（面向用户的文案）。</param>
    Task ProcessAsync(Guid sessionId, RecordingAudio? audio, IProgress<string>? progress = null);
}

/// <summary>
/// 单路转写结果：来自哪个来源、转出了什么。
/// </summary>
/// <param name="Source">这一路是哪来的（麦克风 / 系统声音）。</param>
/// <param name="Text">转写文本；该路没采到声音时为空串。</param>
public sealed record TranscriptTrack(RecordingAudioSource Source, string Text);

public sealed class NoteProcessor : INoteProcessor
{
    /// <summary>录音结束后等待"课堂增量转写"补完尾部的最长时间；超时则由本管线补算。</summary>
    public const int IncrementalTailWaitSeconds = 120;

    /// <summary>等待后台 OCR 队列排空的最长时间；超时则本管线对缺 OCR 的截图自行识别。</summary>
    public const int OcrDrainWaitSeconds = 60;

    private readonly ISttService _stt;
    private readonly IOcrService _ocr;
    private readonly ILlmService _llm;
    private readonly TranscriptAssembler? _assembler;
    private readonly BackgroundOcrQueue? _ocrQueue;

    /// <param name="stt">STT 实现（增量装配不可用时用它跑整文件）。</param>
    /// <param name="ocr">OCR 实现。</param>
    /// <param name="llm">LLM 实现。</param>
    /// <param name="assembler">增量转写装配器；为 null 时退化为 v0.6.0 的整文件转写。</param>
    /// <param name="ocrQueue">录音期间的后台 OCR 队列；为 null 时由本管线现场识别全部截图。</param>
    public NoteProcessor(ISttService stt, IOcrService ocr, ILlmService llm,
        TranscriptAssembler? assembler = null, BackgroundOcrQueue? ocrQueue = null)
    {
        _stt = stt;
        _ocr = ocr;
        _llm = llm;
        _assembler = assembler;
        _ocrQueue = ocrQueue;
    }

    public async Task ProcessAsync(Guid sessionId, RecordingAudio? audio, IProgress<string>? progress = null)
    {
        var repo = LocalRepository.Instance;
        var session = repo.GetSession(sessionId);
        if (session == null)
        {
            // 会话不存在也要清掉注册表条目：否则一个失败的会话会在进程里留下悬挂的转写会话与工作线程
            IncrementalTranscriptionHub.Unregister(sessionId);
            return;
        }

        repo.UpdateSessionStatus(sessionId, "processing");

        var incremental = IncrementalTranscriptionHub.Get(sessionId);
        try
        {
            await RunPipelineAsync(sessionId, session.Course, session.Title, audio, incremental, progress);
        }
        catch (Exception ex)
        {
            // 未预期异常也必须把会话推进到终态：否则会话会永远停在 processing，
            // 主页持续轮询（bench BT-3 的"永久处理中"就是这条路径漏掉了）。
            Debug.WriteLine($"[NoteProcessor] 管线异常: {ex}");
            progress?.Report($"处理失败：{ex.Message}");
            repo.UpdateSessionStatus(sessionId, "failed");
        }
        finally
        {
            if (incremental != null)
                IncrementalTranscriptionHub.Unregister(sessionId);
        }
    }

    private async Task RunPipelineAsync(Guid sessionId, string course, string? title,
        RecordingAudio? audio, IncrementalTranscriptionSession? incremental, IProgress<string>? progress)
    {
        var repo = LocalRepository.Instance;

        // 0. 交接边录边转写：告知输入完结，等尾部补算（正常情况下只剩最后不到一块）
        if (incremental != null)
        {
            incremental.SignalInputComplete();
            progress?.Report("正在收尾课堂转写…");
            if (!incremental.WaitForCompletion(TimeSpan.FromSeconds(IncrementalTailWaitSeconds)))
                progress?.Report("课堂转写尚未全部完成，剩余部分在此补算…");
        }

        // 1 + 2. 转写与 OCR 并行：两者之间没有任何数据依赖，旧实现把它们串起来纯属排队
        var transcriptTask = TranscribeTracksAsync(sessionId, audio, incremental, progress);
        var ocrTask = BuildOcrMaterialAsync(sessionId, progress);
        await Task.WhenAll(transcriptTask, ocrTask);

        var tracks = await transcriptTask;
        string transcript = tracks.Count > 1
            ? TranscriptSections.Render(tracks)
            : (tracks.FirstOrDefault()?.Text ?? "");
        string ocrText = await ocrTask;

        // 2.5 素材检查：既没有转写、也没有截图文字时，**不调用 LLM**。
        //     否则会白跑一次模型调用（长课程还可能是几十段调用），换来的只是一篇空笔记。
        //     判定刻意是"素材完全为空"而不是"没录到声音"：只放课件的课（没人讲解）虽然没有声音，
        //     但截图 OCR 仍是有效素材，照"没声音就跳过"处理会把整节课的笔记整篇丢掉。
        if (string.IsNullOrWhiteSpace(transcript) && string.IsNullOrWhiteSpace(ocrText))
        {
            progress?.Report("本次没有可整理的素材（未采集到声音，也没有截图文字），已跳过笔记生成。");
            repo.UpdateSessionStatus(sessionId, "completed");
            return;
        }

        progress?.Report("正在用 LLM 生成笔记…");

        // 3. LLM 生成笔记
        Note note = new()
        {
            SessionId = sessionId,
            Title = title ?? course,
        };

        try
        {
            var result = await _llm.GenerateNoteAsync(course, transcript, ocrText, progress);
            var md = result.Markdown;
            if (!IsPlausibleNote(md, course))
                throw new InvalidOperationException("LLM 返回内容不符合笔记格式（过短且无结构）");
            note.ContentMarkdown = md;

            // 分段生成的说明写进 Summary：PDF 导出与笔记页都会展示，用户能看出这是分几段整理的。
            // 措辞必须与事实对齐：OversizedChars 表示"末尾素材被并入最后一段、未再切分"，
            // **不是**"已被丢掉"——旧文案写"未纳入"会让用户以为内容丢了。
            if (result.SegmentCount > 1)
            {
                note.Summary = $"共 {result.SegmentCount} 段整理"
                    + (result.FailedSegments > 0 ? $"，其中 {result.FailedSegments} 段失败" : "")
                    + (result.OversizedChars > 0 ? $"，末尾约 {result.OversizedChars} 字未再切分" : "")
                    + "。";
            }
            else if (result.FailedSegments > 0)
            {
                note.Summary = "生成不完整：有分段未能整理，部分素材未进入笔记。";
            }
            else if (result.OversizedChars > 0)
            {
                note.Summary = $"生成不完整：末尾约 {result.OversizedChars} 字未再切分，可能被模型截断。";
            }
        }
        catch (Exception ex)
        {
            // LLM 失败时用转写/OCR 原文兜底生成基础笔记（转写同样保留来源标注，
            // 否则分轨录制的两路原文会混在一起，看不出哪句是哪来的）
            note.ContentMarkdown = BuildFallbackNote(course, transcript, ocrText);
            note.Summary = $"笔记生成失败：{ex.Message}";
        }

        repo.SaveNote(note);
        repo.UpdateSessionStatus(sessionId, "completed");
        progress?.Report("完成");
    }

    /// <summary>
    /// 逐路转写（**多路并行**）：每一路各自取用课堂期间已算好的块，只补算缺的部分。
    ///
    /// 并行是安全的：课后已无"课堂放映"需要保护，两路各占一半逻辑核正好吃满整机
    /// （课堂上那条"最多占一半核 + 按占空比让路"的约束由 ClassroomResourceGovernor 管，见录音侧）。
    /// </summary>
    private async Task<List<TranscriptTrack>> TranscribeTracksAsync(
        Guid sessionId, RecordingAudio? audio, IncrementalTranscriptionSession? incremental,
        IProgress<string>? progress)
    {
        var tracks = new List<TranscriptTrack>();
        if (audio == null || audio.IsEmpty)
            return tracks;

        var pending = new List<Task<TranscriptTrack?>>();
        foreach (var file in audio.Files)
        {
            if (string.IsNullOrEmpty(file.Path) || !System.IO.File.Exists(file.Path))
                continue;

            if (!HasAudioSamples(file.Path))
            {
                // 音频里一个采样都没有（真机验证发现的场景）："仅系统声音"时播放设备全程没出声，
                // WASAPI 回环对空闲端点**一个包都不发**，单路直写于是只写下一个 WAV 头（46 字节、data 块长度 0）。
                // 这种素材转写必然是空的，直接跳过 STT——连模型加载（峰值约 600MB）都省掉。
                Debug.WriteLine($"[NoteProcessor] {file.Source} 音频无采样数据，跳过 STT: {file.Path}");
                continue;
            }

            pending.Add(TranscribeTrackAsync(sessionId, file, incremental, progress));
        }

        // Task.WhenAll 保持输入顺序，转写来源的顺序因此与 audio.Files 一致（多来源标注依赖这个顺序）
        var results = await Task.WhenAll(pending);
        foreach (var result in results)
        {
            if (result != null)
                tracks.Add(result);
        }
        return tracks;
    }

    private async Task<TranscriptTrack?> TranscribeTrackAsync(
        Guid sessionId, RecordingAudioFile file, IncrementalTranscriptionSession? incremental,
        IProgress<string>? progress)
    {
        try
        {
            long tapped = incremental?.TotalSamplesFor(file.Source) ?? 0;
            var text = _assembler != null
                ? await _assembler.AssembleAsync(sessionId, file.Path, file.Source, tapped, progress)
                : await _stt.TranscribeAsync(file.Path, progress);
            return new TranscriptTrack(file.Source, text ?? "");
        }
        catch (Exception ex)
        {
            // 单路 STT 失败不阻塞整体（OCR 文字仍可兜底，另一路也继续）
            progress?.Report($"{AudioSourceLabel(file.Source)}转写失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 组装截图 OCR 素材：**优先用录音期间已经识别好的结果**，只有确实没有的才现场识别。
    /// 素材的拼装格式与 v0.6.0 完全一致（<c>[类型 @ 时间]</c> 行 + 正文），保证提示词输入不变。
    /// </summary>
    private async Task<string> BuildOcrMaterialAsync(Guid sessionId, IProgress<string>? progress)
    {
        var repo = LocalRepository.Instance;

        // 等课堂期间的后台 OCR 收尾（它一直在跑，通常这里已经排空）
        _ocrQueue?.WaitIdle(TimeSpan.FromSeconds(OcrDrainWaitSeconds));

        var ocrSb = new StringBuilder();
        bool reported = false;
        foreach (var shot in repo.ListScreenshotsDetailed(sessionId))
        {
            string? text = shot.OcrText;
            if (text == null)
            {
                if (string.IsNullOrEmpty(shot.ImagePath) || !System.IO.File.Exists(shot.ImagePath))
                    continue;
                if (!reported)
                {
                    progress?.Report("正在识别截图文字…");
                    reported = true;
                }
                try
                {
                    var imageData = await System.IO.File.ReadAllBytesAsync(shot.ImagePath);
                    text = await _ocr.RecognizeAsync(imageData) ?? "";
                    // 空结果也要写回：它表示"这张图确实没文字"，下次不再重复识别
                    repo.SetScreenshotOcr(sessionId, shot.SeqNo, text);
                }
                catch (Exception ex)
                {
                    // 单张 OCR 失败跳过，不阻塞整体管线；但必须留痕，避免静默丢失截图文字
                    Debug.WriteLine($"[NoteProcessor] 截图 {shot.SeqNo} OCR 失败: {ex.Message}");
                    progress?.Report($"截图 {shot.SeqNo} OCR 失败：{ex.Message}");
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                ocrSb.AppendLine($"[{shot.Type} @ {FormatTime(shot.Timestamp)}]");
                ocrSb.AppendLine(text);
            }
        }
        return ocrSb.ToString();
    }

    /// <summary>面向用户的来源叫法（与提示词里的分区标注用词保持一致）。</summary>
    private static string AudioSourceLabel(RecordingAudioSource source)
        => source == RecordingAudioSource.System ? "系统声音" : "麦克风";

    /// <summary>
    /// 判断 WAV 里是否存在实际采样数据（data 块长度 &gt; 0）。
    ///
    /// 背景（真机冒烟验证发现）：WASAPI 回环在播放设备空闲时**一个数据包都不发**，
    /// 而 v0.6.0 的单路直写路径是"来多少写多少"，于是"仅系统声音"全程无声时
    /// 只会写下一个 46 字节的 WAV 头（fmt 18 + data 0）。这种素材跑 STT 必然是空转写，
    /// 属于纯粹的浪费，因此提前判定并跳过。
    ///
    /// 判定原则：**只在能明确读到 data 块长度为 0 时才返回 false**；
    /// 读不了、不是 RIFF/WAVE、找不到 data 块一律返回 true，
    /// 交给 STT 自己去失败或成功——不要把一个损坏文件误判成"没录到声音"而静默跳过。
    /// </summary>
    internal static bool HasAudioSamples(string audioPath)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(audioPath);
            using var br = new System.IO.BinaryReader(fs);
            if (fs.Length < 12)
                return false;
            if (new string(br.ReadChars(4)) != "RIFF")
                return true;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE")
                return true;

            while (fs.Position + 8 <= fs.Length)
            {
                var chunkId = new string(br.ReadChars(4));
                int size = br.ReadInt32();
                if (chunkId == "data")
                    return size > 0;
                if (size < 0)
                    return true;
                fs.Position += size + (size % 2); // 块长按偶数字节对齐
            }
        }
        catch
        {
            return true; // 读不了就别自作主张
        }
        return true; // 没找到 data 块：交给 STT
    }

    /// <summary>
    /// 朴素但保守的"像不像一篇笔记"校验：内容非空（已在 LlmService 保证）之外，
    /// 若既不含标题（#），又不含课程名，且长度 < 100，判定为无效输出。
    /// 为避免误杀，阈值取保守值——正常课堂笔记几乎必然含课程名或 Markdown 标题。
    /// </summary>
    private static bool IsPlausibleNote(string content, string course)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (content.Length >= 100) return true;
        if (!string.IsNullOrEmpty(course) && content.Contains(course, StringComparison.Ordinal)) return true;
        if (content.Contains('#')) return true;
        return false;
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
