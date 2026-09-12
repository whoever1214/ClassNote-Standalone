using System;
using System.IO;
using System.Text;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 按块读 WAV 的测试：课后补算靠它只读"缺的那一块"，而不是把整段 90 分钟音频读成 float[]。
/// </summary>
public class WavePcmTests
{
    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "classnote_wavepcm_test");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>构造 16kHz 单声道 PCM16 WAV，data 块里放可预测的采样。</summary>
    private static string WriteWav(int samples, Func<int, short> value, bool declareZeroLength = false,
        int sampleRate = 16000, short channels = 1)
    {
        var path = Path.Combine(Dir(), $"wav-{Guid.NewGuid():N}.wav");
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = samples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(18);
        bw.Write((short)1);          // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write((short)0);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(declareZeroLength ? 0 : dataBytes);   // 录音进行中的文件头就是 0
        for (int i = 0; i < samples; i++)
            bw.Write(value(i));
        bw.Flush();
        return path;
    }

    [Fact]
    public void TryReadInfo_ReadsDataChunk()
    {
        var path = WriteWav(1000, i => (short)i);
        Assert.True(WavePcm.TryReadInfo(path, out var info));
        Assert.Equal(1000, info.SampleCount);
        Assert.Equal(2000, info.DataBytes);
        Assert.Equal(46, info.DataOffset); // 18 字节 fmt 块 → 头 46 字节
    }

    [Fact]
    public void TryReadInfo_ZeroDeclaredLength_TrustsActualFileLength()
    {
        // 录音中断/崩溃残留的文件头部长度是 0，但音频确实存在：不能因此判定"没有音频"
        var path = WriteWav(500, i => (short)i, declareZeroLength: true);
        Assert.True(WavePcm.TryReadInfo(path, out var info));
        Assert.Equal(500, info.SampleCount);
    }

    [Fact]
    public void ReadWindow_ReturnsRequestedRange_AndPreviousSample()
    {
        var path = WriteWav(10000, i => (short)(i - 5000));
        Assert.True(WavePcm.TryReadInfo(path, out var info));

        var window = WavePcm.ReadWindow(path, info, startSample: 4000, sampleCount: 100, out var previous);

        Assert.Equal(100, window.Length);
        Assert.Equal((4000 - 5000) / 32768f, window[0]);
        Assert.Equal((4099 - 5000) / 32768f, window[99]);
        Assert.NotNull(previous);
        Assert.Equal((3999 - 5000) / 32768f, previous!.Value);
    }

    [Fact]
    public void ReadWindow_AtStart_HasNoPreviousSample()
    {
        var path = WriteWav(1000, i => (short)i);
        Assert.True(WavePcm.TryReadInfo(path, out var info));

        var window = WavePcm.ReadWindow(path, info, 0, 100, out var previous);

        Assert.Equal(100, window.Length);
        Assert.Null(previous);
    }

    [Fact]
    public void ReadWindow_ClampsAtEndOfAudio()
    {
        var path = WriteWav(120, i => (short)i);
        Assert.True(WavePcm.TryReadInfo(path, out var info));

        var window = WavePcm.ReadWindow(path, info, startSample: 100, sampleCount: 480400, out _);

        Assert.Equal(20, window.Length);
    }

    [Fact]
    public void ReadAll_MatchesReadWindow()
    {
        var path = WriteWav(5000, i => (short)(i % 100));
        Assert.True(WavePcm.TryReadInfo(path, out var info));

        var all = WavePcm.ReadAll(path, info);
        var window = WavePcm.ReadWindow(path, info, 3000, 1000, out _);

        Assert.Equal(5000, all.Length);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(all[3000 + i], window[i]);
    }

    [Fact]
    public void IsSupportedFormat_OnlyAccepts16kMonoPcm16()
    {
        Assert.True(WavePcm.IsSupportedFormat(WriteWav(100, i => 0)));
        Assert.False(WavePcm.IsSupportedFormat(WriteWav(100, i => 0, sampleRate: 48000)));
        Assert.False(WavePcm.IsSupportedFormat(WriteWav(100, i => 0, channels: 2)));
        Assert.False(WavePcm.IsSupportedFormat(Path.Combine(Dir(), "not-a-wav.bin")));
    }

    [Fact]
    public void TryReadInfo_MissingFile_ReturnsFalse()
        => Assert.False(WavePcm.TryReadInfo(Path.Combine(Dir(), $"missing-{Guid.NewGuid():N}.wav"), out _));
}
