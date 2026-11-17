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

    /// <summary>
    /// 用库里**已保存的转写 + 截图**重新生成笔记（不重新转写、不重新 OCR）。
    ///
    /// 这是笔记页「重新生成」按钮的实现：适合"换了模型 / 换了提示词 / 上次 LLM 失败"
    /// 而素材已经转写好的场景。转写要几分钟到十几分钟，重生成不该再付一次。
    /// </summary>
    Task RegenerateAsync(Guid sessionId, IProgress<string>? progress = null);
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
    private readonly Func<AppSettingsData> _settings;

    /// <param name="stt">STT 实现（增量装配不可用时用它跑整文件）。</param>
    /// <param name="ocr">OCR 实现。</param>
    /// <param name="llm">LLM 实现。</param>
    /// <param name="assembler">增量转写装配器；为 null 时退化为 v0.6.0 的整文件转写。</param>
    /// <param name="ocrQueue">录音期间的后台 OCR 队列；为 null 时由本管线现场识别全部截图。</param>
    /// <param name="settingsProvider">
    /// 设置来源；为 null 时读进程级 <see cref="AppSettings.Instance"/>。
    /// 只为测试与批处理留的接缝——否则单测会去读用户真实的 settings.json。
    /// </param>
    public NoteProcessor(ISttService stt, IOcrService ocr, ILlmService llm,
        TranscriptAssembler? assembler = null, BackgroundOcrQueue? ocrQueue = null,
        Func<AppSettingsData>? settingsProvider = null)
    {
        _stt = stt;
        _ocr = ocr;
        _llm = llm;
        _assembler = assembler;
        _ocrQueue = ocrQueue;
        _settings = settingsProvider ?? (() => AppSettings.Instance.Snapshot());
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

    /// <inheritdoc />
    public Task RegenerateAsync(Guid sessionId, IProgress<string>? progress = null)
        => ProcessAsync(sessionId, audio: null, progress);

    private async Task RunPipelineAsync(Guid sessionId, string course, string? title,
        RecordingAudio? audio, IncrementalTranscriptionSession? incremental, IProgress<string>? progress)
    {
        var repo = LocalRepository.Instance;
        var settings = _settings();
        var inputMode = ScreenshotInputModes.FromStorage(settings.ScreenshotInput);

        // 进度留痕：录音页在 quick-stop 后就关闭了，STT 失败之类的原因只报给 progress 等于石沉大海。
        // 这里把消息收一份，转写为空时写进笔记摘要，用户才看得到"为什么只有课件"。
        var relay = new ProgressCollector(progress);
        progress = relay;

        // 0. 交接边录边转写：告知输入完结，等尾部补算（正常情况下只剩最后不到一块）
        if (incremental != null)
        {
            incremental.SignalInputComplete();
            progress?.Report("正在收尾课堂转写…");
            if (!incremental.WaitForCompletion(TimeSpan.FromSeconds(IncrementalTailWaitSeconds)))
                progress?.Report("课堂转写尚未全部完成，剩余部分在此补算…");
        }

        // 1 + 2. 转写与截图素材并行：两者之间没有任何数据依赖，旧实现把它们串起来纯属排队。
        //    截图素材按用户设置的形态取：OCR 文本（本地识别）和/或 图片（直送多模态模型）。
        var transcriptTask = TranscribeTracksAsync(sessionId, audio, incremental, progress);
        var ocrTask = inputMode.UsesOcr()
            ? BuildOcrMaterialAsync(sessionId, progress)
            : Task.FromResult(new List<OcrMaterialLine>());
        await Task.WhenAll(transcriptTask, ocrTask);

        var (tracks, sttErrors) = await transcriptTask;
        string transcript = tracks.Count > 1
            ? TranscriptSections.Render(tracks)
            : (tracks.FirstOrDefault()?.Text ?? "");
        var ocrMaterial = await ocrTask;

        // 2.4 转写词典：同一节课的转写是"高置信词汇表"，用来校正课件截图 OCR 的粉碎性误识
        //     （`Na2C03`、`氵谷氵夜` 这类）。必须等到转写就绪之后再规范化 OCR，
        //     因此步骤 1+2 的并行仍然保留（并行的是**识别**，规范化的只是已经识别出来的文本）。
        var lexicon = OcrLexicon.FromTranscript(transcript);
        string ocrText = inputMode.UsesOcr() ? RenderOcrMaterial(ocrMaterial, lexicon) : "";

        // 2.3 截图图片：按"变化点切分 + 簇尾保留 + 上限"选图并缩放（见 ScreenshotSelector）。
        //     一节课平均 67.6 张、实测 73% 是同一屏的重复帧，原图直送既昂贵又几乎不带来新信息。
        NoteImagesResult? images = null;
        if (inputMode.UsesVision())
        {
            try
            {
                images = NoteImageBuilder.Build(sessionId,
                    ScreenshotSelectionOptions.FromSettings(settings), repo, progress);
                progress?.Report(images.Stats.Describe());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NoteProcessor] 截图准备失败: {ex}");
                progress?.Report($"截图准备失败：{ex.Message}");
            }
        }
        int imageCount = images?.Images.Count ?? 0;

        // 2.45 素材溯源：把"这份笔记是从什么素材得来的"算出来。
        //      用户在成品笔记里看不到这一层，而它恰恰决定了该信到什么程度——
        //      有多少张截图、其中多少张的 OCR 有无法确定性修复的疑点、转写来自几路声音。
        var provenance = BuildProvenance(transcript, tracks.Count, ocrMaterial, lexicon, imageCount, inputMode);

        // 2.46 转写缺失必须说出来：旧实现在 STT 全失败时静默产出"只有 OCR"的笔记，
        //     用户只能从"怎么没有讲课内容"里反推。这里把原因固定写进摘要。
        string? transcriptGap = null;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            transcriptGap = sttErrors.Count > 0
                ? "本次未取到转写：" + sttErrors[0]
                : "本次未取到转写（音频里没有人声，或语音识别模型不可用）";
            progress?.Report(transcriptGap);
        }

        // 2.5 素材检查：既没有转写、也没有截图文字时，**不调用 LLM**。
        //     否则会白跑一次模型调用（长课程还可能是几十段调用），换来的只是一篇空笔记。
        //     判定刻意是"素材完全为空"而不是"没录到声音"：只放课件的课（没人讲解）虽然没有声音，
        //     但截图 OCR 仍是有效素材，照"没声音就跳过"处理会把整节课的笔记整篇丢掉。
        if (string.IsNullOrWhiteSpace(transcript) && string.IsNullOrWhiteSpace(ocrText) && imageCount == 0)
        {
            progress?.Report("本次没有可整理的素材（未采集到声音，也没有可用截图），已跳过笔记生成。");
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
            // 时间轴：让课件截图按**时间戳**归入对应段落，而不是按序号均摊。
            // 这是 P1 交叉核验（用转写校正课件 OCR、区分"老师讲的"与"只在课件上"）的前提——
            // 只有"同一段里的截图与转写讲的是同一时刻"，互证才有意义。
            // 失败即退化：拿不到块信息时 timeline 为 null，分段退回旧行为。
            var timeline = TranscriptTimeline.BuildForSession(sessionId, LocalRepository.Instance);

            var result = await _llm.GenerateNoteAsync(course, transcript, ocrText, progress, timeline,
                images?.Images);
            var md = result.Markdown;
            if (!IsPlausibleNote(md, course))
                throw new InvalidOperationException("LLM 返回内容不符合笔记格式（过短且无结构）");
            note.ContentMarkdown = md;

            // 分段生成的说明写进 Summary：PDF 导出与笔记页都会展示，用户能看出这是分几段整理的。
            // 措辞必须与事实对齐：OversizedChars 表示"末尾素材被并入最后一段、未再切分"，
            // **不是**"已被丢掉"——旧文案写"未纳入"会让用户以为内容丢了。
            var summary = new StringBuilder();
            if (result.SegmentCount > 1)
            {
                summary.Append($"共 {result.SegmentCount} 段整理")
                       .Append(result.FailedSegments > 0 ? $"，其中 {result.FailedSegments} 段失败" : "")
                       .Append(result.OversizedChars > 0 ? $"，末尾约 {result.OversizedChars} 字未再切分" : "")
                       .Append('。');
            }
            else if (result.FailedSegments > 0)
            {
                summary.Append("生成不完整：有分段未能整理，部分素材未进入笔记。");
            }
            else if (result.OversizedChars > 0)
            {
                summary.Append($"生成不完整：末尾约 {result.OversizedChars} 字未再切分，可能被模型截断。");
            }

            // 质量守卫的发现（来源被纠正、正文重复）与分段说明同样重要，一并展示给用户
            if (!string.IsNullOrWhiteSpace(result.QualityNote))
                summary.Append(result.QualityNote);

            // 素材溯源：用户有权知道这份笔记是从什么素材、多可信的素材得来的
            if (provenance.Length > 0)
                summary.Append(provenance);

            if (transcriptGap != null)
                summary.Append(transcriptGap);

            note.Summary = summary.Length > 0 ? summary.ToString() : null;
        }
        catch (Exception ex)
        {
            // LLM 失败时用转写/OCR 原文兜底生成基础笔记（转写同样保留来源标注，
            // 否则分轨录制的两路原文会混在一起，看不出哪句是哪来的）
            note.ContentMarkdown = BuildFallbackNote(course, transcript, ocrText, imageCount);
            note.Summary = $"笔记生成失败：{ex.Message}"
                           + (transcriptGap != null ? "｜" + transcriptGap : "");
        }

        repo.SaveNote(note);
        repo.UpdateSessionStatus(sessionId, "completed");

        // 转写成功后就丢掉原始音频：重新生成笔记只需要"转写 + 截图"，
        // 每节 40 分钟的 73 MB WAV 留着没有用处（用户可在设置里改为保留，见 ContentRetention）。
        // ⚠️ 只在**转写非空**时删：STT 失败时音频是唯一的补救素材。
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            try
            {
                long freed = repo.DeleteSessionAudio(sessionId);
                if (freed > 0)
                    progress?.Report($"原始音频已删除（{freed / 1024.0 / 1024.0:0.#} MB），只保留转写与截图");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NoteProcessor] 删除原始音频失败: {ex.Message}");
            }
        }

        progress?.Report("完成");
    }

    /// <summary>
    /// 逐路转写（**多路并行**）：每一路各自取用课堂期间已算好的块，只补算缺的部分。
    ///
    /// 并行是安全的：课后已无"课堂放映"需要保护，两路各占一半逻辑核正好吃满整机
    /// （课堂上那条"最多占一半核 + 按占空比让路"的约束由 ClassroomResourceGovernor 管，见录音侧）。
    /// </summary>
    private async Task<(List<TranscriptTrack> Tracks, List<string> Errors)> TranscribeTracksAsync(
        Guid sessionId, RecordingAudio? audio, IncrementalTranscriptionSession? incremental,
        IProgress<string>? progress)
    {
        var tracks = new List<TranscriptTrack>();
        var errors = new List<string>();

        if (audio == null || audio.IsEmpty)
        {
            // 没有现场音频有两种情况，处理方式完全一样：读**库里已算好的转写**。
            //  · 「重新生成笔记」：素材早就转写好了，只是要换个模型/提示词重跑 LLM；
            //  · 音频已被过期清理，但转写块还在（留存期内的常见情况）。
            var stored = LocalRepository.Instance.ListStoredTranscripts(sessionId);
            foreach (var (source, text) in stored)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    tracks.Add(new TranscriptTrack(source, text));
            }

            if (tracks.Count > 0)
            {
                progress?.Report($"已载入库里保存的转写（{tracks.Sum(t => t.Text.Length)} 字）…");
            }
            else
            {
                errors.Add(audio == null
                    ? "没有音频，也没有已保存的转写数据"
                    : "没有可用的音频文件，也没有已保存的转写数据");
            }
            return (tracks, errors);
        }

        var pending = new List<Task<(TranscriptTrack? Track, string? Error)>>();
        foreach (var file in audio.Files)
        {
            if (string.IsNullOrEmpty(file.Path) || !System.IO.File.Exists(file.Path))
            {
                errors.Add($"{AudioSourceLabel(file.Source)}音频文件不存在（{System.IO.Path.GetFileName(file.Path)}）");
                continue;
            }

            if (!HasAudioSamples(file.Path))
            {
                // 音频里一个采样都没有（真机验证发现的场景）："仅系统声音"时播放设备全程没出声，
                // WASAPI 回环对空闲端点**一个包都不发**，单路直写于是只写下一个 WAV 头（46 字节、data 块长度 0）。
                // 这种素材转写必然是空的，直接跳过 STT——连模型加载（峰值约 600MB）都省掉。
                Debug.WriteLine($"[NoteProcessor] {file.Source} 音频无采样数据，跳过 STT: {file.Path}");
                errors.Add($"{AudioSourceLabel(file.Source)}音频里没有任何采样（全程静音）");
                continue;
            }

            pending.Add(TranscribeTrackAsync(sessionId, file, incremental, progress));
        }

        // Task.WhenAll 保持输入顺序，转写来源的顺序因此与 audio.Files 一致（多来源标注依赖这个顺序）
        var results = await Task.WhenAll(pending);
        foreach (var (track, error) in results)
        {
            if (track != null)
                tracks.Add(track);
            else if (!string.IsNullOrWhiteSpace(error))
                errors.Add(error!);
        }

        // 转写成功但一个字都没有：也算"没取到转写"，同样要能解释给用户
        if (tracks.Count > 0 && tracks.All(t => string.IsNullOrWhiteSpace(t.Text)))
            errors.Add("音频已转写，但识别结果为空（可能全程无人声，或麦克风静音）");

        return (tracks, errors);
    }

    private async Task<(TranscriptTrack? Track, string? Error)> TranscribeTrackAsync(
        Guid sessionId, RecordingAudioFile file, IncrementalTranscriptionSession? incremental,
        IProgress<string>? progress)
    {
        try
        {
            long tapped = incremental?.TotalSamplesFor(file.Source) ?? 0;
            var text = _assembler != null
                ? await _assembler.AssembleAsync(sessionId, file.Path, file.Source, tapped, progress)
                : await _stt.TranscribeAsync(file.Path, progress);
            return (new TranscriptTrack(file.Source, text ?? ""), null);
        }
        catch (Exception ex)
        {
            // 单路 STT 失败不阻塞整体（截图仍是有效素材，另一路也继续）——
            // 但必须把原因传上去，否则用户只会看到一篇没有讲课内容的笔记。
            progress?.Report($"{AudioSourceLabel(file.Source)}转写失败：{ex.Message}");
            return (null, $"{AudioSourceLabel(file.Source)}转写失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 一条待送进提示词的 OCR 素材：来源标记行 + **原始** OCR 正文。
    ///
    /// 刻意在识别阶段只"取回"文本、不做规范化：词典校正依赖同一节课的转写，
    /// 而转写与 OCR 是并行跑的（见 <see cref="RunPipelineAsync"/>）。
    /// 分离之后并行度保留，规范化推迟到两者都就绪之后的 2.4 步。
    /// </summary>
    /// <param name="Header">形如 <c>[new_slide @ 00:12:30]</c> 的来源标记行（可能为空）。</param>
    /// <param name="Text">原始 OCR 文本（未规范化）。</param>
    internal sealed record OcrMaterialLine(string Header, string Text);

    /// <summary>
    /// 组装截图 OCR 素材：**优先用录音期间已经识别好的结果**，只有确实没有的才现场识别。
    /// 素材的拼装格式与 v0.6.0 完全一致（<c>[类型 @ 时间]</c> 行 + 正文），保证提示词输入结构不变。
    /// </summary>
    private async Task<List<OcrMaterialLine>> BuildOcrMaterialAsync(Guid sessionId, IProgress<string>? progress)
    {
        var repo = LocalRepository.Instance;

        // 等课堂期间的后台 OCR 收尾（它一直在跑，通常这里已经排空）
        _ocrQueue?.WaitIdle(TimeSpan.FromSeconds(OcrDrainWaitSeconds));

        var material = new List<OcrMaterialLine>();
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

            if (string.IsNullOrWhiteSpace(text))
                continue;

            material.Add(new OcrMaterialLine(
                Header: $"[{shot.Type} @ {FormatTime(shot.Timestamp)}]",
                Text: text));
        }
        return material;
    }

    /// <summary>
    /// 规范化并拼装 OCR 素材。
    ///
    /// 字符级 OCR 噪声在这里收敛：逐字拆开的空格、公式行的全角符号、高频误识，
    /// 以及 v0.8 新增的**转写词典校正**与化学式/方程式处理。
    /// 库里存的仍是**原始 OCR**（可追溯、可对拍），规范化只作用于送进提示词的素材。
    /// </summary>
    /// <param name="material">识别阶段取回的原始素材。</param>
    /// <param name="lexicon">转写词典；<see cref="OcrLexicon.Empty"/> 表示不做词典校正。</param>
    internal static string RenderOcrMaterial(IReadOnlyList<OcrMaterialLine> material, OcrLexicon? lexicon)
    {
        var sb = new StringBuilder();
        foreach (var line in material)
        {
            string normalized = OcrTextNormalizer.Normalize(line.Text, lexicon);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;
            if (line.Header.Length > 0)
                sb.AppendLine(line.Header);
            sb.AppendLine(normalized);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 素材溯源：这份笔记用了多少张截图、其中多少张的 OCR 留有无法确定性修复的疑点、
    /// 转写来自几路声音、词典覆盖了多少术语。
    ///
    /// 为什么要展示给用户：**"笔记多可信"取决于素材质量**，而素材质量现在完全不可见。
    /// 一张 OCR 出了 40 处疑点的截图，和一张干净的截图，在成品笔记里长得一样。
    /// 这个字符串让用户能自己判断该信到什么程度——这是产品诚实性的一部分，
    /// 而不是"内部日志"。
    /// </summary>
    /// <param name="transcript">已装配的转写文本。</param>
    /// <param name="trackCount">成功转写的音轨数（0 = 没有转写）。</param>
    /// <param name="ocrMaterial">识别阶段取回的原始截图素材。</param>
    /// <param name="lexicon">本次使用的转写词典。</param>
    internal static string BuildProvenance(string transcript, int trackCount,
        IReadOnlyList<OcrMaterialLine> ocrMaterial, OcrLexicon lexicon,
        int imageCount = 0, ScreenshotInputMode inputMode = ScreenshotInputMode.Ocr)
    {
        var parts = new List<string>();

        // 图片直送时"截图张数"要说的是**真正送进模型的那几张**（已去重/限流），
        // 而不是库里有几张——否则用户以为模型看了 86 张，实际只看了 20 张。
        string shotPart = inputMode.UsesVision()
            ? $"{imageCount} 张截图（图片直送，已去重）"
            : $"{ocrMaterial.Count} 张截图";
        parts.Add(trackCount > 1
            ? $"素材：{shotPart}、{trackCount} 路录音"
            : $"素材：{shotPart}、{(trackCount == 1 ? "1 路录音" : "无录音")}");

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            parts.Add(lexicon.Count > 0
                ? $"词典 {lexicon.Count} 条术语"
                : "无词典");
        }

        int suspicious = CountSuspiciousLines(ocrMaterial);
        if (suspicious > 0)
            parts.Add($"其中 {suspicious} 张截图的 OCR 有无法确定性修复的疑点（已标注【OCR不清】）");

        return $"（{string.Join("，", parts)}）";
    }

    /// <summary>规范化后仍带疑点标注的截图条目数。</summary>
    private static int CountSuspiciousLines(IReadOnlyList<OcrMaterialLine> material)
    {
        int count = 0;
        foreach (var line in material)
        {
            string normalized = OcrTextNormalizer.Normalize(line.Text, null);
            foreach (var normalizedLine in normalized.Split('\n'))
            {
                if (OcrTextNormalizer.IsAnnotatedUncertain(normalizedLine))
                {
                    count++;
                    break;   // 一张截图算一次，不按行数累加
                }
            }
        }
        return count;
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

    private static string BuildFallbackNote(string course, string transcript, string ocrText, int imageCount = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {course} 课堂笔记");
        sb.AppendLine();
        sb.AppendLine(imageCount > 0
            ? $"> 备注：LLM 生成失败，以下为原始转写与截图 OCR 内容（另有 {imageCount} 张截图按「图片直送」模式送入模型，模型失败后无法以文本形式保留）。"
            : "> 备注：LLM 生成失败，以下为原始转写与截图 OCR 内容。");
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

    /// <summary>
    /// 进度转发的旁路记录器：把管线里报过的消息留一份（有上限），
    /// 好让"转写为空"这类**事后才需要解释**的情况写进笔记摘要。
    /// 录音页在 quick-stop 后即关闭，进度消息本身留不住。
    /// </summary>
    private sealed class ProgressCollector : IProgress<string>
    {
        private const int Capacity = 200;
        private readonly IProgress<string>? _inner;
        private readonly List<string> _messages = new();

        public ProgressCollector(IProgress<string>? inner) => _inner = inner;

        public void Report(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lock (_messages)
                {
                    _messages.Add(value);
                    if (_messages.Count > Capacity)
                        _messages.RemoveAt(0);
                }
            }
            _inner?.Report(value);
        }

        /// <summary>报过的、看起来像失败/缺失的消息（按时间顺序）。</summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (_messages)
                    return _messages
                        .Where(m => m.Contains("失败", StringComparison.Ordinal)
                                    || m.Contains("缺失", StringComparison.Ordinal)
                                    || m.Contains("不存在", StringComparison.Ordinal))
                        .ToList();
            }
        }
    }
}
