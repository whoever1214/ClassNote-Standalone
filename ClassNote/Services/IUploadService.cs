namespace ClassNote.Services;

/// <summary>
/// 本地文件/数据落地服务（单机模式）。原为服务端上传服务（分片上传 + 离线队列），
/// 现改为：截图直接写入本地存储、音频复制到本地数据目录并关联会话。
/// 接口签名保持不变以兼容现有调用方。
/// </summary>
public interface IUploadService : IDisposable
{
    Task<bool> UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp,
        string type, byte[] imageData, string? url);
    Task FlushQueueAsync();

    /// <summary>将录音文件落地到本地数据目录并关联会话，成功返回 true。</summary>
    Task<bool> UploadAudioAsync(Guid sessionId, string filePath, string filename);

    /// <summary>登记录音文件到持久目录（供处理后使用）。</summary>
    Task EnqueueAudioAsync(Guid sessionId, string filePath, string filename);

    /// <summary>补发/整理离线音频队列（单机模式下基本为 no-op）。</summary>
    Task FlushAudioQueueAsync();
}
