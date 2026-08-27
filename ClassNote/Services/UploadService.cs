using System.IO;
using System.Threading.Tasks;

namespace ClassNote.Services;

/// <summary>
/// 单机模式的数据落地服务：截图直接写入本地 SQLite 存储，
/// 音频文件复制到本地数据目录并关联会话（供转录管线使用）。
/// 无需任何服务端/网络。
/// </summary>
public class UploadService : IUploadService
{
    private readonly IApiService _api;
    private readonly string _audioDir;

    public UploadService(string baseUrl, IApiService api)
    {
        _api = api;
        _audioDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote", "audio");
        Directory.CreateDirectory(_audioDir);
    }

    public Task<bool> UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp,
        string type, byte[] imageData, string? url)
    {
        return Task.Run(() =>
        {
            try
            {
                _api.UploadScreenshotAsync(sessionId, seqNo, timestamp, type, imageData, url).GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public Task FlushQueueAsync() => Task.CompletedTask;

    public Task<bool> UploadAudioAsync(Guid sessionId, string filePath, string filename)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                    return true;

                var dest = Path.Combine(_audioDir, $"{sessionId}_{filename}");
                File.Copy(filePath, dest, overwrite: true);
                LocalRepository.Instance.SetSessionAudioPath(sessionId, dest);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public Task EnqueueAudioAsync(Guid sessionId, string filePath, string filename)
    {
        // 单机模式：直接落地即可（与 UploadAudioAsync 相同），无"离线队列"概念
        return UploadAudioAsync(sessionId, filePath, filename);
    }

    public Task FlushAudioQueueAsync() => Task.CompletedTask;

    public void Dispose()
    {
    }
}
