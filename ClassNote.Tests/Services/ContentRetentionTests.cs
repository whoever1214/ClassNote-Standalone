using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 原料过期清理：非收藏会话超过留存天数后，音频 / 截图 / 转写被删，**笔记与会话保留**；
/// 收藏的会话一律不动。这些语义直接决定"用户会不会丢课"，必须有回归测试。
/// </summary>
public class ContentRetentionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LocalRepository _repo;

    public ContentRetentionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "classnote_retention_" + Guid.NewGuid().ToString("N") + ".db");
        _repo = new LocalRepository(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 临时库删不掉不影响结论 */ }
    }

    /// <summary>把会话的开始时间改到 N 天前（CreateSession 只会写"现在"）。</summary>
    private void BackdateSession(Guid id, int days)
    {
        using var db = new System.Data.SQLite.SQLiteConnection($"Data Source={_dbPath}");
        db.Open();
        using var cmd = new System.Data.SQLite.SQLiteCommand(
            "UPDATE sessions SET start_time = @t WHERE id = @id", db);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.AddDays(-days).ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    private string CreateAudioFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "classnote_audio_" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    /// <summary>造一个"这一节有原料"的会话：音频 + 转写块 + 截图（文件与行）。</summary>
    private (Guid Id, string AudioPath, string ShotsDir) SeedSession(int daysOld, bool favorite)
    {
        var id = _repo.CreateSession("数学", "第 3 讲");
        _repo.EndSession(id, 2400);
        BackdateSession(id, daysOld);
        if (favorite) _repo.SetSessionFavorite(id, true);

        var audio = CreateAudioFile();
        _repo.SetSessionAudioTracks(id, audio, null);

        _repo.SaveTranscriptChunk(id, RecordingAudioSource.Microphone, 0, 0, "第一块转写");
        _repo.SaveTranscriptChunk(id, RecordingAudioSource.Microphone, 1, 30, "第二块转写");

        // 截图：走生产同一条写入路径（落文件 + 落行），目录就是 %LOCALAPPDATA%\ClassNote\screenshots\{id}
        _repo.SaveScreenshot(id, seqNo: 1, timestamp: 10, type: "new_slide", imageData: new byte[1024], url: null);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote", "screenshots", id.ToString());

        _repo.SaveNote(new ClassNote.Models.Note
        {
            SessionId = id,
            Title = "第 3 讲",
            ContentMarkdown = "# 数学 课堂笔记\n- 内容",
        });

        return (id, audio, dir);
    }

    [Fact]
    public void ExpiredNonFavorite_LosesRawMaterialButKeepsNote()
    {
        var (id, audio, shotsDir) = SeedSession(daysOld: 10, favorite: false);

        var result = _repo.PurgeExpiredContent(7);

        Assert.True(result.SessionsPurged >= 1);
        Assert.False(File.Exists(audio));
        Assert.False(Directory.Exists(shotsDir));
        Assert.Empty(_repo.ListStoredTranscripts(id));
        Assert.Empty(_repo.ListScreenshotsDetailed(id));

        // 笔记与会话本身必须还在：用户仍然能看/导出这节课
        Assert.NotNull(_repo.GetNote(id));
        Assert.NotNull(_repo.GetSession(id));
    }

    [Fact]
    public void ExpiredFavorite_KeepsEverything()
    {
        var (id, audio, shotsDir) = SeedSession(daysOld: 10, favorite: true);

        var result = _repo.PurgeExpiredContent(7);

        Assert.Equal(0, result.SessionsPurged);
        Assert.True(File.Exists(audio));
        Assert.True(Directory.Exists(shotsDir));
        Assert.NotEmpty(_repo.ListStoredTranscripts(id));
        Assert.NotEmpty(_repo.ListScreenshotsDetailed(id));

        // 清理后自己收拾测试产物
        try { File.Delete(audio); } catch { }
        try { Directory.Delete(shotsDir, recursive: true); } catch { }
    }

    [Fact]
    public void ExpiredButNeverTranscribed_KeepsAudio()
    {
        // 现场回归：STT 失败、一块转写都没有的课，音频是唯一能补救的素材，
        // 不能因为"超过 7 天"就被清掉（真实数据里 86 节课全是这种状态）。
        var id = _repo.CreateSession("语文", null);
        _repo.EndSession(id, 2400);
        BackdateSession(id, 30);
        var audio = CreateAudioFile();
        _repo.SetSessionAudioTracks(id, audio, null);

        var result = _repo.PurgeExpiredContent(7);

        Assert.Equal(0, result.SessionsPurged);
        Assert.Equal(1, result.SkippedNoTranscript);
        Assert.True(File.Exists(audio));

        try { File.Delete(audio); } catch { }
    }

    [Fact]
    public void FreshSession_IsKept()
    {
        var (id, audio, shotsDir) = SeedSession(daysOld: 1, favorite: false);

        var result = _repo.PurgeExpiredContent(7);

        Assert.Equal(0, result.SessionsPurged);
        Assert.True(File.Exists(audio));
        Assert.True(Directory.Exists(shotsDir));

        try { File.Delete(audio); } catch { }
        try { Directory.Delete(shotsDir, recursive: true); } catch { }
    }

    [Fact]
    public void ZeroDays_DisablesPurge()
    {
        var (id, audio, shotsDir) = SeedSession(daysOld: 30, favorite: false);

        var result = _repo.PurgeExpiredContent(0);

        Assert.Equal(0, result.SessionsPurged);
        Assert.Equal(0, result.SessionsScanned);
        Assert.True(File.Exists(audio));

        try { File.Delete(audio); } catch { }
        try { Directory.Delete(shotsDir, recursive: true); } catch { }
    }

    [Fact]
    public void RetentionService_ReportsPurgedCount()
    {
        SeedSession(daysOld: 9, favorite: false);
        var settings = new AppSettingsData { ContentRetentionDays = 7 };

        int purged = ContentRetention.RunOnce(settings, _repo);

        Assert.True(purged >= 1);
    }

    [Fact]
    public void DeleteSession_RemovesBothAudioTracks()
    {
        var id = _repo.CreateSession("物理", null);
        var mic = CreateAudioFile();
        var sys = CreateAudioFile();
        _repo.SetSessionAudioTracks(id, mic, sys);

        _repo.DeleteSession(id);

        // 回归：旧实现只删 audio_path（麦克风那一路），系统声音那一路永远残留
        Assert.False(File.Exists(mic));
        Assert.False(File.Exists(sys));
    }
}
