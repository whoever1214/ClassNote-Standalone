using System;
using System.Collections.Generic;
using System.IO;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 增量转写块在 SQLite 里的往返测试。
///
/// 用临时库文件而不是 <c>LocalRepository.Instance</c>：单测绝不能写进用户真实的
/// <c>%LOCALAPPDATA%/ClassNote/classnote.db</c>（既有测试的约定，见 NoteProcessorAudioTests 注释）。
/// </summary>
public class TranscriptChunkStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LocalRepository _repo;

    public TranscriptChunkStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "classnote_repo_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "test.db");
        _repo = new LocalRepository(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    [Fact]
    public void SaveAndList_RoundTrips_InChunkOrder()
    {
        var sessionId = Guid.NewGuid();

        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 2, 60, "第三块");
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 0, 0, "第一块");
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 1, 30, "第二块");

        var chunks = _repo.ListTranscriptChunks(sessionId, RecordingAudioSource.Microphone);

        Assert.Equal(3, chunks.Count);
        Assert.Equal((0, "第一块"), chunks[0]);
        Assert.Equal((1, "第二块"), chunks[1]);
        Assert.Equal((2, "第三块"), chunks[2]);
    }

    [Fact]
    public void Save_SameChunkTwice_OverwritesInsteadOfDuplicating()
    {
        var sessionId = Guid.NewGuid();
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 0, 0, "旧");
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 0, 0, "新");

        var chunks = _repo.ListTranscriptChunks(sessionId, RecordingAudioSource.Microphone);
        Assert.Single(chunks);
        Assert.Equal("新", chunks[0].Text);
    }

    [Fact]
    public void Tracks_AreIsolatedFromEachOther()
    {
        var sessionId = Guid.NewGuid();
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 0, 0, "现场");
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.System, 0, 0, "课件");

        Assert.Equal("现场", Assert.Single(_repo.ListTranscriptChunks(sessionId, RecordingAudioSource.Microphone)).Text);
        Assert.Equal("课件", Assert.Single(_repo.ListTranscriptChunks(sessionId, RecordingAudioSource.System)).Text);
    }

    [Fact]
    public void Sessions_AreIsolatedFromEachOther()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _repo.SaveTranscriptChunk(a, RecordingAudioSource.Microphone, 0, 0, "A");
        _repo.SaveTranscriptChunk(b, RecordingAudioSource.Microphone, 0, 0, "B");

        Assert.Equal("A", Assert.Single(_repo.ListTranscriptChunks(a, RecordingAudioSource.Microphone)).Text);
        Assert.Equal("B", Assert.Single(_repo.ListTranscriptChunks(b, RecordingAudioSource.Microphone)).Text);
    }

    [Fact]
    public void EmptyText_IsStoredAsEmptyNotNull()
    {
        // 空文本也是有效结果（"这 30 秒没人说话"）：必须能存下来，否则课后会反复重算这一块
        var sessionId = Guid.NewGuid();
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 0, 0, "");

        var chunks = _repo.ListTranscriptChunks(sessionId, RecordingAudioSource.Microphone);
        Assert.Single(chunks);
        Assert.Equal("", chunks[0].Text);
    }

    [Fact]
    public void DeleteSession_AlsoRemovesTranscriptChunks()
    {
        var sessionId = _repo.CreateSession("数学", "第一章");
        _repo.SaveTranscriptChunk(sessionId, RecordingAudioSource.Microphone, 0, 0, "内容");

        _repo.DeleteSession(sessionId);

        Assert.Empty(_repo.ListTranscriptChunks(sessionId, RecordingAudioSource.Microphone));
    }

    [Fact]
    public void ListScreenshotsDetailed_DistinguishesNotYetOcrFromEmptyOcr()
    {
        // null = 还没识别过（管线要自己补做）；"" = 识别过了、图里没字（不要重复识别）
        var sessionId = _repo.CreateSession("数学", null);
        _repo.SaveScreenshot(sessionId, 1, 10, "new_slide", new byte[] { 1, 2, 3 }, null);
        _repo.SaveScreenshot(sessionId, 2, 20, "annotation", new byte[] { 4, 5, 6 }, null);
        _repo.SetScreenshotOcr(sessionId, 1, "");

        var rows = _repo.ListScreenshotsDetailed(sessionId);

        Assert.Equal(2, rows.Count);
        Assert.Equal("", rows[0].OcrText);
        Assert.Null(rows[1].OcrText);
    }
}
