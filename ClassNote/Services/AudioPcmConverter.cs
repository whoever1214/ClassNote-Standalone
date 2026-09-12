using NAudio.Wave;

namespace ClassNote.Services;

/// <summary>
/// 采集侧格式转换：把设备原生格式（任意采样率 / 声道数 / PCM 位深，含 32 位浮点）
/// 降混 + 重采样为 STT 管线要求的 16kHz 单声道 PCM16。
///
/// 单独成类而不是藏在采集通道里，是为了让这段数值计算能脱离音频设备做单元测试
/// （采样率、声道数、位深的组合无法在 CI 上真机覆盖，只能靠纯函数验证）。
/// 实例按"流式"使用：反复调用 <see cref="Process"/> 追加输入，未凑够插值点的尾巴自动留到下次。
/// </summary>
public sealed class AudioPcmConverter
{
    private readonly WaveFormat _target;
    private readonly List<float> _pending = new();
    private WaveFormat _source;
    private double _position;   // 待插值游标（相对 _pending 起点）

    public AudioPcmConverter(WaveFormat source, WaveFormat target)
    {
        _source = source;
        _target = target;
    }

    /// <summary>源格式（设备实际格式）。</summary>
    public WaveFormat SourceFormat => _source;

    /// <summary>设备实际格式与预期不符时切换（丢弃残留样本，避免用错采样率插值）。</summary>
    public void Rebind(WaveFormat source)
    {
        _source = source;
        _pending.Clear();
        _position = 0;
    }

    /// <summary>
    /// 处理一段原始字节（应当是整帧长度；不足一帧的尾巴不会被消费）。
    /// 返回本次产出的目标格式（16bit 单声道）样本。
    /// </summary>
    public byte[] Process(byte[] buffer, int count)
    {
        int frameSize = Math.Max(1, _source.BlockAlign);
        int usable = count - count % frameSize;
        if (usable <= 0)
            return Array.Empty<byte>();

        DecodeToMonoFloat(buffer, usable);
        return Resample();
    }

    /// <summary>
    /// 一次性转换整段数据（测试与离线场景用）。比流式多 flush 一步：
    /// 把尾部最后 1 个样本也插值出来，避免整段转换永远丢掉最后一帧。
    /// </summary>
    public static byte[] ConvertOnce(byte[] buffer, int count, WaveFormat source, WaveFormat target)
    {
        var converter = new AudioPcmConverter(source, target);
        var first = converter.Process(buffer, count);
        var tail = converter.Flush();
        if (tail.Length == 0)
            return first;

        var combined = new byte[first.Length + tail.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(tail, 0, combined, first.Length, tail.Length);
        return combined;
    }

    /// <summary>把剩余样本全部插值出来（整段转换结束时调用）。</summary>
    public byte[] Flush() => Resample(final: true);

    // ── 降混 ─────────────────────────────────────────────────

    /// <summary>原始字节 → 单声道 float 采样（多声道取算术平均）。</summary>
    private void DecodeToMonoFloat(byte[] buffer, int count)
    {
        int channels = Math.Max(1, _source.Channels);
        int bits = _source.BitsPerSample;
        int bytesPerSample = Math.Max(1, bits / 8);
        int frameSize = Math.Max(1, _source.BlockAlign);
        int frames = count / frameSize;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * frameSize;
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += ReadSample(buffer, baseIdx + c * bytesPerSample, bits);
            _pending.Add(sum / channels);
        }
    }

    private static float ReadSample(byte[] b, int idx, int bits) => bits switch
    {
        32 => BitConverter.ToSingle(b, idx),                                     // IEEE float（WASAPI 混合格式常见）
        24 => (b[idx] | (b[idx + 1] << 8) | ((sbyte)b[idx + 2] << 16)) / 8388608f,
        16 => BitConverter.ToInt16(b, idx) / 32768f,
        8 => (b[idx] - 128) / 128f,
        _ => 0f,
    };

    // ── 重采样（线性插值） ───────────────────────────────────

    private byte[] Resample(bool final = false)
    {
        double ratio = (double)_source.SampleRate / _target.SampleRate;
        if (ratio <= 0 || _pending.Count == 0)
            return Array.Empty<byte>();

        var outBytes = new List<byte>(_pending.Count * 2);
        while (_position + 1 < _pending.Count)
        {
            int i = (int)_position;
            double frac = _position - i;
            float value = (float)(_pending[i] * (1 - frac) + _pending[i + 1] * frac);
            AppendPcm16(outBytes, value);
            _position += ratio;
        }

        // final：连最后一个样本也输出（没有后继样本时按零阶保持），否则整段转换会丢尾
        if (final && _position < _pending.Count)
        {
            AppendPcm16(outBytes, _pending[(int)_position]);
            _position = _pending.Count;
        }

        // 丢弃已插值完的样本，保留 1 个用于跨块插值
        int consumed = (int)_position;
        if (consumed > 0 && consumed <= _pending.Count)
        {
            _pending.RemoveRange(0, consumed);
            _position -= consumed;
        }
        return outBytes.ToArray();
    }

    /// <summary>float → PCM16 小端（带限幅，混音超幅时不会绕回成噪声）。</summary>
    private static void AppendPcm16(List<byte> sink, float value)
    {
        int s = (int)Math.Round(value * 32767f);
        s = Math.Clamp(s, short.MinValue, short.MaxValue);
        sink.Add((byte)(s & 0xFF));
        sink.Add((byte)((s >> 8) & 0xFF));
    }
}
