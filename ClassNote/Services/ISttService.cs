using System;
using System.Threading.Tasks;

namespace ClassNote.Services;

/// <summary>
/// 本地语音转文字（STT）服务接口。
/// 由具体实现（SenseVoice ONNX / 其它）提供实际转写能力。
/// </summary>
public interface ISttService
{
    /// <summary>模型文件是否就绪。</summary>
    bool IsModelReady { get; }

    /// <summary>
    /// 将 16kHz 单声道 WAV 转写为文本。
    /// </summary>
    /// <param name="wavPath">WAV 音频文件路径。</param>
    /// <param name="progress">可选进度回调（用户可读的进度描述）。</param>
    Task<string> TranscribeAsync(string wavPath, IProgress<string>? progress = null);
}
