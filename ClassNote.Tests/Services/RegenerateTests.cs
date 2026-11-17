using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassNote.Services;
using Moq;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 「重新生成笔记」：**只用库里已保存的转写 + 截图**重跑 LLM，不重新转写、不重新录音。
/// 这里守三条：① 转写确实从 transcript_chunks 里读出来并进了 LLM 入参；
/// ② 没有素材时不会白跑 LLM；③ 旧的笔记被新笔记覆盖（不会堆出两份）。
/// </summary>
[Collection("localrepository")]
public class RegenerateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LocalRepository _repo;

    public RegenerateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "classnote_regen_" + Guid.NewGuid().ToString("N") + ".db");
        // 管线内部用的是 LocalRepository.Instance（进程级），这里把它指向临时库
        LocalRepository.RedirectTo(_dbPath);
        _repo = LocalRepository.Instance;
    }

    public void Dispose()
    {
        // 还原到生产默认库，避免影响其它测试
        LocalRepository.RedirectTo(LocalRepository.DefaultDbPath());
        try { File.Delete(_dbPath); } catch { }
    }

    private static AppSettingsData OcrSettings() => new()
    {
        ScreenshotInput = nameof(ScreenshotInputMode.Ocr),   // 本测试只关心转写路径，不涉及图片
        ContentRetentionDays = 0,
    };

    private static NoteProcessor BuildProcessor(ILlmService llm, IOcrService? ocr = null, ISttService? stt = null)
        => new(stt ?? Mock.Of<ISttService>(), ocr ?? Mock.Of<IOcrService>(), llm,
            assembler: null, ocrQueue: null, settingsProvider: OcrSettings);

    private Guid SeedSessionWithTranscript(params string[] chunks)
    {
        var id = _repo.CreateSession("数学", "极限");
        _repo.EndSession(id, 2400);
        for (int i = 0; i < chunks.Length; i++)
            _repo.SaveTranscriptChunk(id, RecordingAudioSource.Microphone, i, i * 30, chunks[i]);
        return id;
    }

    [Fact]
    public async Task Regenerate_UsesStoredTranscript_AndNoAudio()
    {
        Guid id = SeedSessionWithTranscript("第一段：极限的定义", "第二段：左右极限");

        string? capturedTranscript = null;
        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GenerateNoteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>>(), It.IsAny<TranscriptTimeline?>(), It.IsAny<IReadOnlyList<NoteImage>>()))
           .Callback<string, string, string, IProgress<string>?, TranscriptTimeline?, IReadOnlyList<NoteImage>?>(
               (course, transcript, ocr, progress, timeline, images) => capturedTranscript = transcript)
           .ReturnsAsync(new NoteGenerationResult("# 数学 课堂笔记\n- 极限的定义\n- 左右极限", 1, 0, 0));

        var processor = BuildProcessor(llm.Object);

        await processor.RegenerateAsync(id, new Progress<string>());

        Assert.NotNull(capturedTranscript);
        Assert.Contains("极限的定义", capturedTranscript);
        Assert.Contains("左右极限", capturedTranscript);

        var note = _repo.GetNote(id);
        Assert.NotNull(note);
        Assert.Contains("极限的定义", note!.ContentMarkdown);
        Assert.Equal("completed", _repo.GetSession(id)!.Status);
    }

    [Fact]
    public async Task Regenerate_ReplacesPreviousNote()
    {
        Guid id = SeedSessionWithTranscript("旧笔记的素材");
        _repo.SaveNote(new ClassNote.Models.Note { SessionId = id, Title = "旧", ContentMarkdown = "# 旧笔记" });

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GenerateNoteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>>(), It.IsAny<TranscriptTimeline?>(), It.IsAny<IReadOnlyList<NoteImage>>()))
           .ReturnsAsync(new NoteGenerationResult("# 新笔记\n- 更新后的内容", 1, 0, 0));

        await BuildProcessor(llm.Object).RegenerateAsync(id);

        var note = _repo.GetNote(id);
        Assert.NotNull(note);
        Assert.Contains("新笔记", note!.ContentMarkdown);
        Assert.DoesNotContain("旧笔记", note.ContentMarkdown);
    }

    [Fact]
    public async Task Regenerate_WithoutMaterial_SkipsLlmAndSaysWhy()
    {
        var id = _repo.CreateSession("化学", null);
        _repo.EndSession(id, 2400);

        var llm = new Mock<ILlmService>(MockBehavior.Strict);   // 不应被调用
        var reports = new List<string>();
        var progress = new Progress<string>(m => { lock (reports) reports.Add(m); });

        await BuildProcessor(llm.Object).RegenerateAsync(id, progress);

        llm.Verify(l => l.GenerateNoteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<string>>(), It.IsAny<TranscriptTimeline?>(), It.IsAny<IReadOnlyList<NoteImage>>()),
            Times.Never);
        Assert.Null(_repo.GetNote(id));
    }

    [Fact]
    public async Task Regenerate_WhenLlmFails_FallsBackToTranscriptInsteadOfLosingEverything()
    {
        Guid id = SeedSessionWithTranscript("老师讲了单调性与极限");

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GenerateNoteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>>(), It.IsAny<TranscriptTimeline?>(), It.IsAny<IReadOnlyList<NoteImage>>()))
           .ThrowsAsync(new InvalidOperationException("模拟 LLM 故障"));

        await BuildProcessor(llm.Object).RegenerateAsync(id);

        var note = _repo.GetNote(id);
        Assert.NotNull(note);
        // 兜底笔记必须带上转写原文——旧版本这里只有 OCR 或干脆没有内容
        Assert.Contains("课堂录音转写", note!.ContentMarkdown);
        Assert.Contains("单调性", note.ContentMarkdown);
        Assert.Contains("笔记生成失败", note.Summary);
    }
}
