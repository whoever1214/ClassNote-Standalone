using System;
using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 增量块缓冲的行为测试：证明"采集线程分批喂进来的字节，被攒成与批量路径完全相同的块"。
///
/// 不依赖 ONNX 模型：这里只验证喂给推理器的**输入**（窗口、块前采样、帧数、块号顺序），
/// 而推理实现两条路共用一份（<c>SenseVoiceSttService.TranscribeChunk</c>），
/// 因此输入相同 ⇒ 输出相同。
/// </summary>
public class PcmBlockBufferTests
{
    /// <summary>把 float 采样量化成 PCM16 字节（与写盘口径一致：s * 32767 后取整）。</summary>
    private static byte[] Pcm16(float[] samples, int from, int count)
    {
        var bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            int s = (int)Math.Round(samples[from + i] * 32767f);
            s = Math.Clamp(s, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>量化后的采样值（缓冲内部按 s / 32768f 还原）。</summary>
    private static float Quantized(float value)
    {
        int s = (int)Math.Round(value * 32767f);
        s = Math.Clamp(s, short.MinValue, short.MaxValue);
        return s / 32768f;
    }

    private static float[] MakeSamples(int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = (float)(Math.Sin(i * 0.01) * 0.4 + Math.Sin(i * 0.0031) * 0.2);
        return samples;
    }

    private sealed record Captured(int Index, float[] Window, int Length, float? Previous, int Frames);

    /// <summary>按给定的分片长度喂入全部采样，返回捕获到的块。</summary>
    private static List<Captured> Feed(float[] samples, params int[] sliceSamples)
    {
        var blocks = new List<Captured>();
        var buffer = new PcmBlockBuffer(block =>
        {
            // 缓冲区复用同一份窗口数组，这里必须拷走
            var copy = new float[block.WindowLength];
            Array.Copy(block.Window, copy, block.WindowLength);
            blocks.Add(new Captured(block.Index, copy, block.WindowLength, block.PreviousSample, block.FrameCount));
        });

        int position = 0;
        int slice = 0;
        while (position < samples.Length)
        {
            int take = Math.Min(sliceSamples[slice % sliceSamples.Length], samples.Length - position);
            buffer.Append(Pcm16(samples, position, take), take * 2);
            position += take;
            slice++;
        }
        buffer.Complete();
        return blocks;
    }

    [Fact]
    public void Append_ProducesContiguousChunks_WithExactFrameCounts()
    {
        // 65 秒 → 3 块（2 整块 + 1 个不足一块的尾巴）
        const int seconds = 65;
        long n = (long)seconds * TranscriptionChunking.SampleRate;
        var samples = MakeSamples((int)n);

        var blocks = Feed(samples, 320, 6400, 160);

        Assert.Equal(TranscriptionChunking.ChunkCount(n), blocks.Count);
        for (int k = 0; k < blocks.Count; k++)
        {
            Assert.Equal(k, blocks[k].Index); // 块号连续且从 0 开始
            Assert.Equal(TranscriptionChunking.FramesInChunk(k, n), blocks[k].Frames);
            Assert.Equal(TranscriptionChunking.WindowSampleCount(k, n), blocks[k].Length);
        }
    }

    [Fact]
    public void Append_Chunk0_HasNoPreviousSample_OthersCarryBoundarySample()
    {
        long n = 65L * TranscriptionChunking.SampleRate;
        var samples = MakeSamples((int)n);

        var blocks = Feed(samples, 4096);

        Assert.Null(blocks[0].Previous);
        for (int k = 1; k < blocks.Count; k++)
        {
            int boundary = TranscriptionChunking.ChunkStartSample(k);
            Assert.Equal(Quantized(samples[boundary - 1]), blocks[k].Previous);
        }
    }

    [Fact]
    public void Append_WindowContents_MatchSourceSamples()
    {
        long n = 65L * TranscriptionChunking.SampleRate;
        var samples = MakeSamples((int)n);

        var blocks = Feed(samples, 1000); // 故意用不整齐的分片

        foreach (var block in blocks)
        {
            int start = TranscriptionChunking.ChunkStartSample(block.Index);
            for (int i = 0; i < block.Length; i++)
                Assert.Equal(Quantized(samples[start + i]), block.Window[i]);
        }
    }

    [Fact]
    public void Append_SliceBoundaries_DoNotChangeChunks()
    {
        // 分片大小只影响何时攒满，绝不能影响块的内容（采集回调的批次大小由设备决定）
        long n = 40L * TranscriptionChunking.SampleRate;
        var samples = MakeSamples((int)n);

        var a = Feed(samples, 320);
        var b = Feed(samples, 9999);
        var c = Feed(samples, 1);

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Count, c.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Index, b[i].Index);
            Assert.Equal(a[i].Frames, b[i].Frames);
            Assert.Equal(a[i].Length, b[i].Length);
            Assert.Equal(a[i].Previous, b[i].Previous);
            Assert.Equal(a[i].Previous, c[i].Previous);
            for (int d = 0; d < a[i].Length; d++)
            {
                Assert.Equal(a[i].Window[d], b[i].Window[d]);
                Assert.Equal(a[i].Window[d], c[i].Window[d]);
            }
        }
    }

    [Fact]
    public void Suspend_ThenResume_RealignsToChunkBoundary_AndKeepsSampleCount()
    {
        // 3 块的音频：喂到 0.7 块时暂停，暂停期间再喂 0.8 块（只计数），恢复后应跳到块 2 重新开始
        long n = (long)(TranscriptionChunking.SamplesPerChunk * 3);
        var samples = MakeSamples((int)n);
        var blocks = new List<Captured>();
        var buffer = new PcmBlockBuffer(block => blocks.Add(new Captured(block.Index, block.Window, block.WindowLength, block.PreviousSample, block.FrameCount)));

        int half = (int)(TranscriptionChunking.SamplesPerChunk * 0.7);
        buffer.Append(Pcm16(samples, 0, half), half * 2);

        buffer.Suspend();
        Assert.True(buffer.IsSuspended);
        // 暂停期间的采样只计数
        int pause = (int)(TranscriptionChunking.SamplesPerChunk * 0.8);
        buffer.Append(Pcm16(samples, half, pause), pause * 2);
        long expectedTotal = half + pause;
        Assert.Equal(expectedTotal, buffer.TotalSamples);

        buffer.Resume();
        Assert.False(buffer.IsSuspended);

        int rest = (int)(n - expectedTotal);
        buffer.Append(Pcm16(samples, (int)expectedTotal, rest), rest * 2);
        buffer.Complete();

        Assert.Equal(n, buffer.TotalSamples); // 对齐的基准：全部采样都数到了
        // 恢复后第一个块必须是"下一个块边界"那一块，且基准采样正确（= 边界前一个采样）
        var first = Assert.Single(blocks);
        Assert.Equal(2, first.Index);
        Assert.Equal(Quantized(samples[TranscriptionChunking.SamplesPerChunk * 2 - 1]), first.Previous);
        // 被中断的那一块作废、留给课后补算
        Assert.Contains(0, buffer.SkippedChunks);
        Assert.True(buffer.SkippedCount >= 1);
    }

    [Fact]
    public void AddLostSamples_KeepsAlignment_AndSkipsAffectedChunk()
    {
        long n = (long)(TranscriptionChunking.SamplesPerChunk * 3);
        var samples = MakeSamples((int)n);
        var blocks = new List<Captured>();
        var buffer = new PcmBlockBuffer(block => blocks.Add(new Captured(block.Index, block.Window, block.WindowLength, block.PreviousSample, block.FrameCount)));

        // 喂到第 0 块真正产出（窗口需要"一块 + 一个帧长"才满）
        int chunk0 = TranscriptionChunking.MaxWindowSamples;
        buffer.Append(Pcm16(samples, 0, chunk0), chunk0 * 2);
        Assert.Single(blocks);
        Assert.Equal(0, blocks[0].Index);

        // 队列溢出丢了 3 万个采样（落在第 1 块内部）
        const int lost = 30000;
        buffer.AddLostSamples(lost);
        long accounted = chunk0 + lost;
        Assert.Equal(accounted, buffer.TotalSamples);

        // 剩下的数据继续喂（真实时间轴从第 accounted 个采样接着走）
        int rest = (int)(n - accounted);
        buffer.Append(Pcm16(samples, (int)accounted, rest), rest * 2);
        buffer.Complete();

        Assert.Equal(n, buffer.TotalSamples);
        // 缺口所在的第 1 块不产出（课后补算），第 2 块照常产出且块号仍与真实时间轴一致
        var indices = blocks.Select(b => b.Index).ToList();
        Assert.Equal(new[] { 0, 2 }, indices);
        Assert.Contains(1, buffer.SkippedChunks);
        // 第 2 块的基准采样 = 真实的边界前一个采样（跳过循环里取到，没有因为丢帧而丢失基准）
        Assert.Equal(Quantized(samples[TranscriptionChunking.SamplesPerChunk * 2 - 1]), blocks[1].Previous);
    }

    [Fact]
    public void AddLostSamples_WhenBoundaryLandsInGap_SkipsBoundaryChunkToo()
    {
        // 正好丢到块边界：边界前一个采样已经丢了 → 边界那一块也必须作废（课后补算）
        var samples = MakeSamples(TranscriptionChunking.SamplesPerChunk * 3);
        var blocks = new List<Captured>();
        var buffer = new PcmBlockBuffer(block => blocks.Add(new Captured(block.Index, block.Window, block.WindowLength, block.PreviousSample, block.FrameCount)));

        buffer.Append(Pcm16(samples, 0, 1000), 2000);
        // 丢到"正好一个块边界"为止
        long lost = TranscriptionChunking.SamplesPerChunk - 1000;
        buffer.AddLostSamples(lost);
        Assert.Equal(TranscriptionChunking.SamplesPerChunk, buffer.TotalSamples);

        int rest = samples.Length - TranscriptionChunking.SamplesPerChunk;
        buffer.Append(Pcm16(samples, TranscriptionChunking.SamplesPerChunk, rest), rest * 2);
        buffer.Complete();

        // 第 1 块（边界落在丢失段末尾）作废；第 2 块正常产出
        Assert.Equal(new[] { 2 }, blocks.Select(b => b.Index).ToArray());
        Assert.Contains(1, buffer.SkippedChunks);
    }

    [Fact]
    public void Complete_OnShortAudio_ProducesNoChunks()
    {
        var buffer = new PcmBlockBuffer(_ => throw new InvalidOperationException("不该产出任何块"));
        var samples = MakeSamples(300); // 不足一帧
        buffer.Append(Pcm16(samples, 0, samples.Length), samples.Length * 2);
        buffer.Complete();
        Assert.Equal(0, buffer.EmittedChunks);
    }
}
