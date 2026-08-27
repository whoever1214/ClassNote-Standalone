using NAudio.Wave;

namespace ClassNote.Services;

public interface IAudioService : IDisposable
{
    string[] GetInputDevices();
    bool StartRecording(string outputPath, int deviceIndex = 0);
    void StopRecording();
    byte[] GetFileBytes();

    /// <summary>当前录音输出文件路径；未开始录音或已停止时可能为 null。</summary>
    string? GetOutputPath();
}
