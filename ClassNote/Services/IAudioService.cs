using System;

namespace ClassNote.Services;

public interface IAudioService : IDisposable
{
    /// <summary>可用的录音设备显示名，与 GetInputDeviceIds() 一一对应。</summary>
    string[] GetInputDevices();

    /// <summary>与 GetInputDevices() 一一对应的稳定设备标识（WASAPI ID 或 mme:{index}）。</summary>
    string[] GetInputDeviceIds();

    /// <summary>可用播放设备显示名（系统声音回环采集的来源），与 GetOutputDeviceIds() 一一对应。</summary>
    string[] GetOutputDevices();

    /// <summary>与 GetOutputDevices() 一一对应的稳定设备标识（WASAPI 渲染端点 ID）。</summary>
    string[] GetOutputDeviceIds();

    /// <summary>设备枚举是否降级到 MME 回退路径（此路径下无系统声音回环能力）。</summary>
    bool IsUsingMmeFallback { get; }

    /// <summary>
    /// 按录音配置开始采集（麦克风 / 系统声音 / 两者混合），输出统一为
    /// 16kHz 单声道 PCM16 WAV；失败返回 false 并经 LastError 说明原因。
    /// </summary>
    bool StartRecording(string outputPath, RecordingConfig config);

    /// <summary>按稳定麦克风设备标识开始录音（单路麦克风，等价于 Microphone 来源）。</summary>
    bool StartRecording(string outputPath, string deviceId);

    void StopRecording();
    byte[] GetFileBytes();

    /// <summary>当前录音输出文件路径；未开始录音或已停止时可能为 null。</summary>
    string? GetOutputPath();

    /// <summary>最近一次 StartRecording 失败的原因；成功时为 null。</summary>
    string? LastError { get; }
}
