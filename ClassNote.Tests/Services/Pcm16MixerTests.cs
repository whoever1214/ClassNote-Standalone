using System;
using ClassNote.Services;
using NAudio.Wave;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 混音写盘的回归测试。核心断言只有一条：混出来的字节必须是**真正的 PCM16**
/// （各路样本按整数相加，越界夹到 short 范围）。
///
/// 背景：v0.5.0 起的混音路径把 float 缓冲的位模式当成 PCM16 写进 WAV，
/// "麦克风和系统声音"录出来是噪声而不是音频（640 字节里 636 字节与真实 PCM16 不符）。
/// 这个测试不依赖声卡，也不需要真实设备——正是为了让"写下去的到底是不是音频"
/// 这件事可以被回归验证，而不是靠人耳听一遍混音录音。
/// </summary>
public class Pcm16MixerTests
{
    /// <summary>构造一路"已转换的 16kHz 单声道 PCM16"缓冲（与 <c>CaptureChannel.Output</c> 同配置）。</summary>
    private static BufferedWaveProvider CreateChannel(byte[] pcm16)
    {
        var provider = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false, // 数据不足时返回实际字节数，由混音器补零
        };
        if (pcm16.Length > 0)
            provider.AddSamples(pcm16, 0, pcm16.Length);
        return provider;
    }

    private static byte[] Pcm16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return bytes;
    }

    [Fact]
    public void SingleChannel_PassesPcm16ThroughUnchanged()
    {
        // 单路时混音结果必须与输入逐字节相同。旧实现会在这里失败：
        // 它写出去的是这些小数的 IEEE-754 位模式，而不是它们代表的样本值。
        var input = Pcm16(0, 1, -1, 1000, -1000, 32767, -32768);
        var mixer = new Pcm16Mixer(new[] { CreateChannel(input) });

        var output = new byte[input.Length];
        int written = mixer.Read(output, 0, input.Length / 2);

        Assert.Equal(input.Length, written);
        Assert.Equal(input, output);
    }

    [Fact]
    public void TwoChannels_SumSampleBySample()
    {
        var a = Pcm16(100, -100, 1000, 0);
        var b = Pcm16(50, 25, -400, 10);
        var mixer = new Pcm16Mixer(new[] { CreateChannel(a), CreateChannel(b) });

        var output = new byte[8];
        mixer.Read(output, 0, 4);

        Assert.Equal(Pcm16(150, -75, 600, 10), output);
    }

    [Fact]
    public void TwoChannels_SaturateInsteadOfWrappingAround()
    {
        // 两路都接近满量程时相加会超出 int16：必须夹取，回绕会把"很响"变成刺耳的噪声
        var a = Pcm16(30000, -30000);
        var b = Pcm16(30000, -30000);
        var mixer = new Pcm16Mixer(new[] { CreateChannel(a), CreateChannel(b) });

        var output = new byte[4];
        mixer.Read(output, 0, 2);

        Assert.Equal(Pcm16(32767, -32768), output);
    }

    [Fact]
    public void MissingChannelData_IsZeroFilledAndFrameStaysFull()
    {
        // 某一路暂时没数据时只输出另一路，且帧长保持满帧——录音时长必须跟随真实时钟，
        // 不能被某路设备的静音期"拖住"
        var mixer = new Pcm16Mixer(new[] { CreateChannel(Pcm16(5, 6)), CreateChannel(Array.Empty<byte>()) });

        var output = new byte[8];
        int written = mixer.Read(output, 0, 4);

        Assert.Equal(8, written);
        Assert.Equal(Pcm16(5, 6, 0, 0), output);
    }

    [Fact]
    public void NoChannels_ProducesSilenceOfRequestedLength()
    {
        var mixer = new Pcm16Mixer(Array.Empty<BufferedWaveProvider>());

        var output = new byte[4];

        Assert.Equal(4, mixer.Read(output, 0, 2));
        Assert.Equal(new byte[4], output);
    }

    [Fact]
    public void Read_WritesAtOffset_AndReportsFullByteCount()
    {
        var mixer = new Pcm16Mixer(new[] { CreateChannel(Pcm16(7, 8)) });

        var output = new byte[10];
        int written = mixer.Read(output, 4, 2);

        Assert.Equal(4, written);
        Assert.Equal(new byte[4], output[..4]);          // offset 之前不动
        Assert.Equal(Pcm16(7, 8), output[4..8]);
        Assert.Equal(new byte[2], output[8..]);          // offset+count 之后不动
    }
}
