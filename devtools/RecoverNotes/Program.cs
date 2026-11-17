using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassNote.Models;
using ClassNote.Services;

namespace RecoverNotes;

/// <summary>
/// 离线恢复工具：把**已经存在**的录音 WAV 重新转写，并按当前设置重新生成笔记。
///
/// 存在的理由：v0.9 之前，语音识别一旦失败（模型缺失 / ONNX 加载失败），
/// <c>TranscriptAssembler</c> 会把每一块的异常吞掉，于是整节课的转写静默变成空串，
/// 用户最后只拿到一篇"只有课件截图"的笔记。而音频其实一直躺在
/// <c>%LOCALAPPDATA%\ClassNote\audio</c> 里——素材没丢，只是没被用上。
///
/// 用法：
///   RecoverNotes --db "D:\ClassNote\classnote.db" --audio-dir "D:\ClassNote\audio" --stt-only
///   RecoverNotes --db ... --audio-dir ... --mode vision --max-images 30
///   RecoverNotes --db ... --audio-dir ... --session 9ea701c5 --session 083d3480
///
/// 退出码：0 成功；2 参数/模型问题；3 有会话处理失败（其余照常处理）。
/// </summary>
internal static class Program
{
    private sealed class Options
    {
        public string? DbPath;
        public string? AudioDir;
        public readonly List<string> SessionPrefixes = new();
        public bool SttOnly;
        public bool SelectImagesOnly;
        public string? ScreenshotMode;
        public int? MaxImages;
        public int? MaxEdgePx;
        public string? LlmApiKey;
        public string? LlmBaseUrl;
        public string? LlmModel;
        public string? VisionModel;
        public bool Help;
    }

    private static int Main(string[] args)
    {
        var options = Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.DbPath))
        {
            Console.Error.WriteLine("缺少 --db <classnote.db 路径>");
            PrintUsage();
            return 2;
        }
        if (!File.Exists(options.DbPath))
        {
            Console.Error.WriteLine($"找不到数据库：{options.DbPath}");
            return 2;
        }

        // 把进程级库实例指向目标库文件（管线里到处是 LocalRepository.Instance）
        LocalRepository.RedirectTo(options.DbPath!);
        var repo = LocalRepository.Instance;

        ApplySettingsOverrides(options);

        var stt = SenseVoiceSttService.Shared;
        if (!stt.IsModelReady)
        {
            Console.Error.WriteLine("语音识别模型不可用：model_quant.onnx / tokens.json / am.mvn 三件套缺失。");
            Console.Error.WriteLine("模型应位于 安装目录\\models\\sensevoice 或 %LOCALAPPDATA%\\ClassNote\\models\\sensevoice。");
            return 2;
        }

        var audioDir = ResolveAudioDir(options.AudioDir);

        var sessions = repo.ListSessions(1000)
            .Where(s => options.SessionPrefixes.Count == 0 ||
                        options.SessionPrefixes.Any(p => s.Id.ToString().StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.StartTime)
            .ToList();

        if (sessions.Count == 0)
        {
            Console.Error.WriteLine("没有匹配的会话。");
            return 2;
        }

        Console.WriteLine($"库：{options.DbPath}");
        Console.WriteLine($"音频目录：{audioDir ?? "(未指定)"}");
        Console.WriteLine($"模式：{(options.SttOnly ? "仅重新转写（不调用 LLM）" : "重新转写 + 重新生成笔记")}");
        Console.WriteLine($"会话数：{sessions.Count}");
        Console.WriteLine();

        return RunAsync(sessions, audioDir, options).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(List<Session> sessions, string? audioDir, Options options)
    {
        var repo = LocalRepository.Instance;
        var settings = AppSettings.Instance.Snapshot();
        var stt = SenseVoiceSttService.Shared;

        var ocr = OcrServiceFactory.Create(settings);
        var llm = new LlmService();
        var assembler = new TranscriptAssembler(repo, stt);
        var processor = new NoteProcessor(stt, ocr, llm, assembler);

        int ok = 0, skipped = 0, failed = 0;

        foreach (var session in sessions)
        {
            var audioPath = ResolveAudioFile(repo, session.Id, audioDir);
            if (audioPath == null)
            {
                Console.WriteLine($"[跳过] {session.Id} {session.StartTime:yyyy-MM-dd HH:mm} {session.Course}：找不到音频文件");
                skipped++;
                continue;
            }

            long audioBytes = new FileInfo(audioPath).Length;
            Console.WriteLine($"[处理] {session.Id.ToString()[..8]} {session.StartTime:yyyy-MM-dd HH:mm} {session.Course} " +
                              $"({session.Duration ?? 0}s, 音频 {audioBytes / 1024 / 1024} MB)");

            try
            {
                var progress = new Progress<string>(m => Console.WriteLine("        · " + m));

                if (options.SelectImagesOnly)
                {
                    // 只跑"选图 + 缩放"这一段（不解码模型、不调用 LLM），用于核对留存算法在真实数据上的表现
                    var settingsNow = AppSettings.Instance.Snapshot();
                    var selectionOptions = ScreenshotSelectionOptions.FromSettings(settingsNow);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = NoteImageBuilder.Build(session.Id, selectionOptions, repo, progress);
                    sw.Stop();
                    long imageBytes = result.Images.Sum(i => (long)i.Jpeg.Length);
                    Console.WriteLine($"        {result.Stats.Describe()}；" +
                                      $"实际发送 {imageBytes / 1024} KB，耗时 {sw.ElapsedMilliseconds} ms" +
                                      (result.Images.Count > 0
                                          ? $"，单张平均 {imageBytes / result.Images.Count / 1024} KB / {result.Images[0].Width}×{result.Images[0].Height}"
                                          : ""));
                    ok++;
                    Console.WriteLine();
                    continue;
                }

                if (options.SttOnly)
                {
                    // 只补转写：AssembleAsync 会把每个算好的块写回 transcript_chunks
                    var text = await assembler.AssembleAsync(session.Id, audioPath,
                        RecordingAudioSource.Microphone, tappedSamples: 0, progress: progress);
                    Console.WriteLine($"        转写 {text.Length} 字{(text.Length == 0 ? "（⚠ 仍为空，检查模型）" : "")}");
                }
                else
                {
                    var audio = new RecordingAudio(new[]
                    {
                        new RecordingAudioFile(RecordingAudioSource.Microphone, audioPath),
                    });
                    await processor.ProcessAsync(session.Id, audio, progress);
                    var note = repo.GetNote(session.Id);
                    Console.WriteLine($"        笔记 {(note?.ContentMarkdown?.Length ?? 0)} 字，摘要：{note?.Summary}");
                }
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"        ✗ 失败：{ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"完成：成功 {ok}，跳过 {skipped}，失败 {failed}");
        return failed > 0 ? 3 : 0;
    }

    /// <summary>优先用库里登记的路径；不行再按会话 ID 在音频目录里找（_mic / _audio / _system）。</summary>
    private static string? ResolveAudioFile(LocalRepository repo, Guid sessionId, string? audioDir)
    {
        var (mic, system) = repo.GetSessionAudioTracks(sessionId);
        foreach (var candidate in new[] { mic, system })
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;

        if (string.IsNullOrWhiteSpace(audioDir) || !Directory.Exists(audioDir))
            return null;

        foreach (var suffix in new[] { "_mic.wav", "_audio.wav", "_system.wav", ".wav" })
        {
            var path = Path.Combine(audioDir, sessionId + suffix);
            if (File.Exists(path)) return path;
        }

        // 兜底：目录里任何以会话 ID 开头的 wav
        return Directory.EnumerateFiles(audioDir, sessionId.ToString() + "*.wav").FirstOrDefault();
    }

    private static string? ResolveAudioDir(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir) && Directory.Exists(explicitDir))
            return explicitDir;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote", "audio");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static void ApplySettingsOverrides(Options options)
    {
        bool any = options.ScreenshotMode != null || options.MaxImages != null || options.MaxEdgePx != null ||
                   options.LlmApiKey != null || options.LlmBaseUrl != null || options.LlmModel != null ||
                   options.VisionModel != null;
        if (!any) return;

        AppSettings.Instance.Update(s =>
        {
            if (options.ScreenshotMode != null) s.ScreenshotInput = options.ScreenshotMode;
            if (options.MaxImages is int max) s.VisionMaxImagesPerNote = max;
            if (options.MaxEdgePx is int edge) s.VisionMaxEdgePx = edge;
            if (options.LlmApiKey != null) s.LlmApiKey = options.LlmApiKey;
            if (options.LlmBaseUrl != null) s.LlmBaseUrl = options.LlmBaseUrl;
            if (options.LlmModel != null) s.LlmModel = options.LlmModel;
            if (options.VisionModel != null) s.LlmVisionModel = options.VisionModel;
        });
        Console.WriteLine("已把命令行覆盖项写入设置（settings.json）。");
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a.ToLowerInvariant())
            {
                case "--db": o.DbPath = Next(); break;
                case "--audio-dir": o.AudioDir = Next(); break;
                case "--session": if (Next() is string p) o.SessionPrefixes.Add(p); break;
                case "--stt-only": o.SttOnly = true; break;
                case "--select-images-only": o.SelectImagesOnly = true; break;
                case "--mode": o.ScreenshotMode = Next(); break;
                case "--max-images": if (int.TryParse(Next(), out var m)) o.MaxImages = m; break;
                case "--max-edge": if (int.TryParse(Next(), out var e)) o.MaxEdgePx = e; break;
                case "--llm-key": o.LlmApiKey = Next(); break;
                case "--llm-base-url": o.LlmBaseUrl = Next(); break;
                case "--llm-model": o.LlmModel = Next(); break;
                case "--vision-model": o.VisionModel = Next(); break;
                case "-h":
                case "--help": o.Help = true; break;
                default:
                    Console.Error.WriteLine($"忽略未知参数：{a}");
                    break;
            }
        }
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
RecoverNotes —— 用已归档的录音重新转写并重建笔记

  --db <path>            classnote.db 路径（必填，工具会把进程内库实例指向它）
  --audio-dir <path>     录音目录（默认 %LOCALAPPDATA%\ClassNote\audio）
  --session <前缀>       只处理指定会话（可重复，支持 ID 前缀）
  --stt-only             只重新转写并写入 transcript_chunks，不调用 LLM（省钱、可先验证模型）
  --select-images-only   只跑截图选图与缩放，打印「送去几张/多大/耗时」，不解码模型、不调用 LLM
  --mode <ocr|vision|both>  截图输入方式（写入设置）
  --max-images <n>       每节课最多送几张图（写入设置）
  --max-edge <px>        图片长边上限（写入设置）
  --llm-key/--llm-base-url/--llm-model/--vision-model   覆盖 LLM 配置（写入设置）

例：
  RecoverNotes --db ..\ClassNote\classnote.db --stt-only
  RecoverNotes --db ..\ClassNote\classnote.db --mode vision --max-images 30
");
    }
}
