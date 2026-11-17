using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ClassNote.Services;
using Moq;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 转写完成后的原料清理：**转写成功 → 删音频**（只留转写与截图）；
/// **转写失败 → 保留音频**（那是唯一的补救素材）。
/// </summary>
[Collection("localrepository")]
public class PostTranscriptionCleanupTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LocalRepository _repo;

    public PostTranscriptionCleanupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "classnote_cleanup_" + Guid.NewGuid().ToString("N") + ".db");
        LocalRepository.RedirectTo(_dbPath);
        _repo = LocalRepository.Instance;
    }

    public void Dispose()
    {
        LocalRepository.RedirectTo(LocalRepository.DefaultDbPath());
        try { File.Delete(_dbPath); } catch { }
    }

    private static AppSettingsData OcrSettings() => new()
    {
        ScreenshotInput = nameof(ScreenshotInputMode.Ocr),
        ContentRetentionDays = 0,
    };

    /// <summary>最小合法 WAV：16kHz 单声道 PCM16，带若干采样。</summary>
    private static string WriteWav(int dataBytes = 32000)
    {
        var path = Path.Combine(Path.GetTempPath(), "classnote_cleanup_" + Guid.NewGuid().ToString("N") + ".wav");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(18);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write((short)0);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return path;
    }

    private (Guid Id, string AudioPath) SeedWithAudio()
    {
        var id = _repo.CreateSession("英语", null);
        _repo.EndSession(id, 2400);
        var audio = WriteWav();
        _repo.SetSessionAudioTracks(id, audio, null);
        return (id, audio);
    }

    private static NoteProcessor BuildProcessor(ILlmService llm, ISttService stt)
        => new(stt, Mock.Of<IOcrService>(), llm, assembler: null, ocrQueue: null, settingsProvider: OcrSettings);

    [Fact]
    public async Task TranscriptionSucceeded_AudioIsDeleted()
    {
        var (id, audio) = SeedWithAudio();

        var stt = new Mock<ISttService>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<string>(), It.IsAny<IProgress<string>>()))
           .ReturnsAsync("这是一段真实转写文本，长度足够作为素材");

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GenerateNoteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<string>>(), It.IsAny<TranscriptTimeline?>(), It.IsAny<IReadOnlyList<NoteImage>>()))
           .ReturnsAsync(new NoteGenerationResult("# 英语 课堂笔记\n- 内容", 1, 0, 0));

        var processor = BuildProcessor(llm.Object, stt.Object);
        await processor.ProcessAsync(id, new RecordingAudio(new[]
        {
            new RecordingAudioFile(RecordingAudioSource.Microphone, audio),
        }));

        Assert.False(File.Exists(audio));                              // 原料已丢
        var (micPath, _) = _repo.GetSessionAudioTracks(id);
        Assert.True(string.IsNullOrEmpty(micPath));                    // 路径也置空
        Assert.NotNull(_repo.GetNote(id));                              // 笔记在
    }

    [Fact]
    public async Task TranscriptionFailed_AudioIsKept()
    {
        var (id, audio) = SeedWithAudio();

        var stt = new Mock<ISttService>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<string>(), It.IsAny<IProgress<string>>()))
           .ThrowsAsync(new InvalidOperationException("模拟语音识别模型缺失"));

        var llm = new Mock<ILlmService>(MockBehavior.Strict);

        var processor = BuildProcessor(llm.Object, stt.Object);
        await processor.ProcessAsync(id, new RecordingAudio(new[]
        {
            new RecordingAudioFile(RecordingAudioSource.Microphone, audio),
        }));

        // 转写失败：音频必须留着，否则这节就再也没法补转写了
        Assert.True(File.Exists(audio));
    }
}
