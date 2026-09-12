using System;

namespace ClassNote.Services;

/// <summary>
/// 一批"已转换、且与落盘内容逐字节相同"的 16kHz 单声道 PCM16 数据。
/// 这是边录边转写的输入：它取自写盘回调里的同一份字节，因此增量转写看到的音频
/// 与录音文件里的音频完全一致（不是"另采一路近似音频"）。
/// </summary>
/// <param name="Source">这一批数据来自哪一路（麦克风 / 系统声音）。</param>
/// <param name="Data">PCM16 小端字节；**由调用方立即消费**（订阅方若要留存必须自己拷贝）。</param>
/// <param name="Count">有效字节数。</param>
public sealed class AudioFrameCapturedEventArgs : EventArgs
{
    public AudioFrameCapturedEventArgs(RecordingAudioSource source, byte[] data, int count)
    {
        Source = source;
        Data = data;
        Count = count;
    }

    public RecordingAudioSource Source { get; }
    public byte[] Data { get; }
    public int Count { get; }
}

public interface IAudioService : IDisposable
{
    /// <summary>
    /// 采集到的 PCM16 数据（带来源标注）。
    ///
    /// ⚠️ 与 <see cref="AudioService.AudioDataAvailable"/> 一样，本事件在**采集设备线程**上同步触发，
    /// 且**在数据写入 WAV 之后**触发。订阅方必须立即返回（只做一次内存拷贝 + 入队），
    /// 任何耗时操作都会拖慢采集并造成丢帧；抛出的异常会被采集回调吞掉，不会中断录音。
    ///
    /// 与 <c>AudioDataAvailable</c> 的区别：本事件**每一路都会触发**（分轨录音时两路各自触发），
    /// 并携带来源；<c>AudioDataAvailable</c> 只在单路/合成路径上触发且不带来源，供电平显示等用途。
    /// </summary>
    event EventHandler<AudioFrameCapturedEventArgs>? FrameCaptured;

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
    /// 按录音配置开始采集（仅麦克风 / 仅系统声音 / 麦克风和系统声音），输出统一为
    /// 16kHz 单声道 PCM16 WAV；失败返回 false 并经 LastError 说明原因。
    /// 成功但发生了降级（例如指定播放设备失效、回退到系统默认设备）时，见 LastWarning。
    /// </summary>
    bool StartRecording(string outputPath, RecordingConfig config);

    /// <summary>
    /// 分轨录音：每一路来源写入**各自独立**的 16kHz 单声道 WAV，供 STT 分别转写后带来源标注交给 LLM。
    /// 「麦克风和系统声音」来源必须走这里——混成一路后再拆分是做不到的。
    /// </summary>
    /// <param name="paths">各来源的输出路径（须与 config 实际启用的来源一致，顺序先麦克风后系统声音）。</param>
    /// <returns>成功返回各路文件清单；失败返回 <see cref="RecordingAudio.None"/> 并经 LastError 说明原因。</returns>
    RecordingAudio StartRecordingDual(RecordingConfig config, IReadOnlyList<RecordingAudioFile> paths);

    /// <summary>按稳定麦克风设备标识开始录音（单路麦克风，等价于 Microphone 来源）。</summary>
    bool StartRecording(string outputPath, string deviceId);

    void StopRecording();
    byte[] GetFileBytes();

    /// <summary>当前录音输出文件路径；未开始录音或已停止时可能为 null。</summary>
    string? GetOutputPath();

    /// <summary>
    /// 本次录音实际落盘的各路文件（分轨录音时是多路；单路录音是一路）。
    /// 停止录音后调用，用于把音频落地与转写。
    /// </summary>
    RecordingAudio GetRecordedAudio();

    /// <summary>最近一次 StartRecording 失败的原因；成功时为 null。</summary>
    string? LastError { get; }

    /// <summary>
    /// 最近一次 StartRecording **成功但有降级** 的说明（例如指定的播放设备已失效、
    /// 实际回退到了系统默认设备）；无降级时为 null。
    /// 与 <see cref="LastError"/> 分开：这类情况不该让录音失败，但也绝不能静默——
    /// "配了 A 设备却录到了 B 设备"是回环采集录成静音的典型成因。
    /// </summary>
    string? LastWarning { get; }
}
