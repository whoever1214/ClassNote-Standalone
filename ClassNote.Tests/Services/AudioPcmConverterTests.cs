using ClassNote.Services;
using NAudio.Wave;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 采集侧格式转换的纯函数测试：录音设备原生格式（48kHz 立体声浮点、44.1kHz 立体声
/// PCM16、16kHz 单声道……）无法在 CI 上真机覆盖，只能靠这里验证数值行为。
/// </summary>
public class AudioPcmConverterTests
{
    private static readonly WaveFormat Target = new(16000, 16, 1);

    /// <summary>生成正弦波 PCM16 单声道字节。</summary>
    private static byte[] Pcm16Mono(int samples, int sampleRate, double frequency = 440)
    {
        var bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 20000);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>生成 IEEE float 立体声（WASAPI 混合格式）字节，左右声道同相。</summary>
    private static byte[] Float32Stereo(int frames, double amplitude = 0.5)
    {
        var bytes = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * amplitude);
            for (int c = 0; c < 2; c++)
                BitConverter.GetBytes(v).CopyTo(bytes, i * 8 + c * 4);
        }
        return bytes;
    }

    private static short[] SamplesOf(byte[] pcm)
    {
        var result = new short[pcm.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = BitConverter.ToInt16(pcm, i * 2);
        return result;
    }

    [Fact]
    public void ConvertOnce_48kStereoFloat_DownsamplesTo16kMono()
    {
        // 48000 帧 = 1 秒；左右声道相同，降混取平均后仍是原波形
        var input = Float32Stereo(48000);
        var output = AudioPcmConverter.ConvertOnce(input, input.Length, new WaveFormat(48000, 32, 2), Target);

        // 允许首尾各差几个采样（线性插值边界）
        int samples = output.Length / 2;
        Assert.InRange(samples, 15995, 16005);

        var decoded = SamplesOf(output);
        Assert.Contains(decoded, s => Math.Abs(s) > 10000); // 波形真的被搬过来了，不是静音
    }

    [Fact]
    public void ConvertOnce_16kMonoPcm16_IsValuePreserving()
    {
        var input = Pcm16Mono(1600, 16000);
        var output = AudioPcmConverter.ConvertOnce(input, input.Length, new WaveFormat(16000, 16, 1), Target);

        Assert.Equal(input.Length, output.Length);
        // 1:1 采样率下只经过 float 往返，允许 ±2 的量化误差
        var before = SamplesOf(input);
        var after = SamplesOf(output);
        for (int i = 0; i < before.Length; i++)
            Assert.InRange(after[i], before[i] - 2, before[i] + 2);
    }

    [Fact]
    public void ConvertOnce_StereoPcm16_AveragesBothChannels()
    {
        // 左声道满幅、右声道静音 → 降混后应为半幅
        var input = new byte[200];
        for (int i = 0; i < 50; i++)
        {
            input[i * 4] = 0xFF; input[i * 4 + 1] = 0x7F;      // 左 = +32767
            input[i * 4 + 2] = 0x00; input[i * 4 + 3] = 0x00;  // 右 = 0
        }
        var output = AudioPcmConverter.ConvertOnce(input, input.Length, new WaveFormat(16000, 16, 2), Target);

        var decoded = SamplesOf(output);
        Assert.NotEmpty(decoded);
        Assert.All(decoded, s => Assert.InRange(s, 16300, 16400));
    }

    [Fact]
    public void ConvertOnce_44k1Stereo_ProducesAboutOneSecondOf16kAudio()
    {
        // 44100 帧立体声 PCM16 → 约 16000 个单声道采样
        var input = new byte[44100 * 4];
        for (int i = 0; i < input.Length; i += 4)
        {
            input[i] = 0x00; input[i + 1] = 0x10;      // 左
            input[i + 2] = 0x00; input[i + 3] = 0x10;  // 右
        }
        var output = AudioPcmConverter.ConvertOnce(input, input.Length, new WaveFormat(44100, 16, 2), Target);

        Assert.InRange(output.Length / 2, 15990, 16010);
    }

    [Fact]
    public void Process_PartialFrameIsLeftForNextCall()
    {
        // 立体声 PCM16：一帧 4 字节。传 6 字节（1.5 帧）时只应消费 1 整帧
        var converter = new AudioPcmConverter(new WaveFormat(16000, 16, 2), Target);
        var bytes = new byte[6];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = 0x00;

        // 不抛异常即为通过（残帧被留在内部缓冲，等下次补齐）
        var output = converter.Process(bytes, 6);
        Assert.NotNull(output);

        // 补齐剩下 2 字节后可以继续处理，且不会因为残帧卡死
        var rest = new byte[2];
        var output2 = converter.Process(rest, 2);
        Assert.NotNull(output2);
    }

    [Fact]
    public void Process_InChunks_MatchesSinglePassLength()
    {
        var input = Float32Stereo(24000); // 0.5 秒
        var source = new WaveFormat(48000, 32, 2);

        var oneShot = AudioPcmConverter.ConvertOnce(input, input.Length, source, Target);

        var converter = new AudioPcmConverter(source, Target);
        int produced = 0;
        const int chunk = 4800 * 8 / 10; // 非整块的 100ms 分片，检验流式累积
        for (int offset = 0; offset + chunk <= input.Length; offset += chunk)
            produced += converter.Process(input.AsSpan(offset, chunk).ToArray(), chunk).Length;

        // 流式与一次性的产出量级一致（差在最后一次未 flush 的尾巴上）
        Assert.InRange(produced, oneShot.Length - 64, oneShot.Length + 64);
    }

    [Fact]
    public void Rebind_UsesNewSampleRate()
    {
        var converter = new AudioPcmConverter(new WaveFormat(48000, 16, 1), Target);
        converter.Rebind(new WaveFormat(16000, 16, 1));
        Assert.Equal(16000, converter.SourceFormat.SampleRate);

        var input = Pcm16Mono(160, 16000);
        var output = converter.Process(input, input.Length);
        Assert.InRange(output.Length / 2, 155, 165);
    }

    [Fact]
    public void Process_EmptyOrTooShortInput_ReturnsNothing()
    {
        var converter = new AudioPcmConverter(new WaveFormat(48000, 32, 2), Target);
        Assert.Empty(converter.Process(Array.Empty<byte>(), 0));
        Assert.Empty(converter.Process(new byte[3], 3)); // 不足一帧（8 字节）
    }
}
