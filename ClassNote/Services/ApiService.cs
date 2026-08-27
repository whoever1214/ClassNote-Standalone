using ClassNote.Models;

namespace ClassNote.Services;

/// <summary>
/// 单机模式的会话/笔记数据访问实现：直接读写本地 SQLite（LocalRepository），
/// 不再依赖任何 HTTP 服务端。保留原 IApiService 接口签名以兼容现有调用方。
/// </summary>
public class ApiService : IApiService
{
    private readonly LocalRepository _repo;
    private readonly IPdfExportService _pdf;

    public ApiService()
    {
        _repo = LocalRepository.Instance;
        _pdf = new PdfExportService();
    }

    public ApiService(string baseUrl) : this()
    {
        // baseUrl 参数已无意义（单机模式），保留构造函数以兼容旧调用方
    }

    public Task<Guid> CreateSessionAsync(string course, string? title)
        => Task.FromResult(_repo.CreateSession(course, title));

    public Task<List<Session>> ListSessionsAsync()
        => Task.FromResult(_repo.ListSessions(20));

    public Task<Session> GetSessionAsync(Guid id)
    {
        var s = _repo.GetSession(id)
            ?? throw new InvalidOperationException("会话不存在");
        return Task.FromResult(s);
    }

    public Task EndSessionAsync(Guid id, int durationSeconds)
    {
        // 结束录音：写入结束时间与时长，并把状态推进到 processing，交由管线完成
        _repo.EndSession(id, durationSeconds);
        return Task.CompletedTask;
    }

    public Task UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp,
        string type, byte[] imageData, string? url)
    {
        _repo.SaveScreenshot(sessionId, seqNo, timestamp, type, imageData, url);
        return Task.CompletedTask;
    }

    public Task<AudioUploadInitResult> InitAudioUploadAsync(Guid sessionId, int fileSize, string filename)
    {
        // 单机模式：无需分片上传，返回占位结果
        return Task.FromResult(new AudioUploadInitResult
        {
            UploadId = Guid.NewGuid().ToString(),
            PartSize = 5 * 1024 * 1024,
        });
    }

    public Task UploadAudioPartAsync(string uploadId, int partNumber, byte[] data)
        => Task.CompletedTask;

    public Task CompleteAudioUploadAsync(Guid sessionId, string uploadId)
        => Task.CompletedTask;

    public Task<Note?> GetNoteAsync(Guid sessionId)
        => Task.FromResult(_repo.GetNote(sessionId));

    public Task<byte[]?> ExportNotePdfAsync(Guid sessionId)
    {
        // 单机模式：本地用 QuestPDF 将笔记渲染为 PDF（含摘要 + 正文）。
        var note = _repo.GetNote(sessionId);
        return Task.FromResult(_pdf.Export(note));
    }
}
