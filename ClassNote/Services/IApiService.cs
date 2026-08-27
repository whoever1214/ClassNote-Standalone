using ClassNote.Models;

namespace ClassNote.Services;

/// <summary>
/// 会话/笔记数据访问接口（单机模式）。原为服务端 REST API 客户端，
/// 现改为本地 SQLite 存储实现，函数签名保持不变以确保 ViewModel/测试可用。
/// </summary>
public interface IApiService
{
    Task<Guid> CreateSessionAsync(string course, string? title);
    Task<List<Session>> ListSessionsAsync();
    Task<Session> GetSessionAsync(Guid id);
    Task EndSessionAsync(Guid id, int durationSeconds);

    /// <summary>删除一个会话及其全部关联数据（本地）。</summary>
    Task DeleteSessionAsync(Guid id);
    Task UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp, string type, byte[] imageData, string? url);
    Task<AudioUploadInitResult> InitAudioUploadAsync(Guid sessionId, int fileSize, string filename);
    Task UploadAudioPartAsync(string uploadId, int partNumber, byte[] data);
    Task CompleteAudioUploadAsync(Guid sessionId, string uploadId);
    Task<Note?> GetNoteAsync(Guid sessionId);

    /// <summary>导出课堂笔记为 Markdown 文本（单机模式：本地渲染，不再调用服务端 PDF）。</summary>
    Task<byte[]?> ExportNotePdfAsync(Guid sessionId);
}
