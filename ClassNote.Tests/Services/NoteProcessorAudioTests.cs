using System;
using System.IO;
using System.Text;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// "音频里到底有没有采样数据"的判定测试（真机冒烟验证发现的问题）。
///
/// 现场：来源选「仅系统声音」而播放设备全程没有渲染任何音频时，WASAPI 回环**一个数据包都不发**，
/// v0.6.0 的单路直写路径于是只写下一个 46 字节的 WAV 头（fmt 18 + data 0）。
/// 这种素材跑 STT 必然是空转写，属于纯浪费，应当在管线里提前跳过。
/// 但这个判定必须**只对"确定没有采样"的文件返回 false**——否则一个损坏文件会被误判成
/// "没录到声音"而静默跳过，把真正的问题盖住。
///
/// 为什么不直接测 NoteProcessor.ProcessAsync：它内部使用 LocalRepository.Instance（真实 SQLite 库），
/// 单测会往用户的数据文件里写会话/笔记记录。所以这里只测这个纯判定函数。
/// </summary>
public class NoteProcessorAudioTests
{
    private static string WriteTemp(byte[] bytes, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "classnote_noteproc_test");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>构造最小合法 WAV（16kHz 单声道 PCM16），fmt 块 18 字节（与 NAudio 一致）。</summary>
    private static byte[] WavWithDataBytes(int dataBytes, bool includeDataChunk = true)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(18);
        bw.Write((short)1);   // PCM
        bw.Write((short)1);   // 单声道
        bw.Write(16000);      // 采样率
        bw.Write(32000);      // 字节率
        bw.Write((short)2);   // 块对齐
        bw.Write((short)16);  // 位深
        bw.Write((short)0);   // cbSize
        if (includeDataChunk)
        {
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            bw.Write(new byte[dataBytes]);
        }
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void HasAudioSamples_EmptyDataChunk_ReturnsFalse()
    {
        // 真机产物的确切形状：46 字节，fmt=18、data=0
        var path = WriteTemp(WavWithDataBytes(0), "empty-data.wav");

        Assert.Equal(46L, new FileInfo(path).Length);
        Assert.False(NoteProcessor.HasAudioSamples(path));
    }

    [Fact]
    public void HasAudioSamples_WithSamples_ReturnsTrue()
    {
        var path = WriteTemp(WavWithDataBytes(320 * 2), "with-samples.wav");

        Assert.True(NoteProcessor.HasAudioSamples(path));
    }

    [Fact]
    public void HasAudioSamples_ZeroByteFile_ReturnsFalse()
    {
        var path = WriteTemp(Array.Empty<byte>(), "zero.wav");

        Assert.False(NoteProcessor.HasAudioSamples(path));
    }

    [Fact]
    public void HasAudioSamples_NotWave_ReturnsTrue()
    {
        // 非 WAV / 损坏文件不能被误判成"没录到声音"——交给 STT 去报错
        var path = WriteTemp(Encoding.ASCII.GetBytes("这不是一个 WAV 文件，只是一些随机字节"), "garbage.bin");

        Assert.True(NoteProcessor.HasAudioSamples(path));
    }

    [Fact]
    public void HasAudioSamples_RiffButNoDataChunk_ReturnsTrue()
    {
        var path = WriteTemp(WavWithDataBytes(0, includeDataChunk: false), "no-data-chunk.wav");

        Assert.True(NoteProcessor.HasAudioSamples(path));
    }

    [Fact]
    public void HasAudioSamples_MissingFile_ReturnsTrue()
    {
        // 调用方只在文件存在时才走到这里；万一竞态（文件刚被删）也只该让 STT 去失败
        var path = Path.Combine(Path.GetTempPath(), $"definitely-missing-{Guid.NewGuid():N}.wav");

        Assert.True(NoteProcessor.HasAudioSamples(path));
    }
}
