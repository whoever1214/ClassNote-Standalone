using System;

namespace ClassNote.Services;

public interface IAudioService : IDisposable
{
    /// <summary>可用的录音设备显示名，与 GetInputDeviceIds() 一一对应。</summary>
    string[] GetInputDevices();

    /// <summary>与 GetInputDevices() 一一对应的稳定设备标识（WASAPI ID 或 mme:{index}）。</summary>
    string[] GetInputDeviceIds();

    /// <summary>按稳定设备标识开始录音；失败返回 false 并经 LastError 说明原因。</summary>
    bool StartRecording(string outputPath, string deviceId);

    void StopRecording();
    byte[] GetFileBytes();

    /// <summary>当前录音输出文件路径；未开始录音或已停止时可能为 null。</summary>
    string? GetOutputPath();

    /// <summary>最近一次 StartRecording 失败的原因；成功时为 null。</summary>
    string? LastError { get; }
}
