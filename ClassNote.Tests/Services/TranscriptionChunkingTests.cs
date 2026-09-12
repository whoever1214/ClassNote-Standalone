using System;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 分块数学与"增量 = 批量"的证据测试。
///
/// 这一组是整个 v0.7 提速改动里**唯一**能证明"边录边转写不会改变转写结果"的东西：
/// 推理本身（SenseVoice）两条路共用一份实现，所以只要证明
/// 「喂给它的窗口/块/帧数与整文件路径逐个相同」，就能推出文本相同。
/// </summary>
public class TranscriptionChunkingTests
{
    /// <summary>确定性素材（不用随机数，失败可复现）。</summary>
    private static float[] MakeSamples(int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = (float)(Math.Sin(i * 0.01) * 0.4 + Math.Sin(i * 0.0031) * 0.2);
        return samples;
    }

    [Fact]
    public void ChunkCount_MatchesLegacyLoop()
    {
        // 旧实现是 for (start = 0; start < frames.Length; start += 3000)，逐个长度对齐
        foreach (int seconds in new[] { 1, 29, 30, 31, 59, 60, 61, 90, 120, 121, 300 })
        {
            long n = (long)seconds * TranscriptionChunking.SampleRate;
            int totalFrames = TranscriptionChunking.TotalFrames(n);

            int legacyChunks = 0;
            for (int start = 0; start < totalFrames; start += TranscriptionChunking.FramesPerChunk)
                legacyChunks++;

            Assert.Equal(legacyChunks, TranscriptionChunking.ChunkCount(n));

            // 每块帧数也必须与"整文件算完再按 3000 帧切"逐块一致
            for (int k = 0; k < TranscriptionChunking.ChunkCount(n); k++)
            {
                int expected = Math.Min(TranscriptionChunking.FramesPerChunk,
                    Math.Max(0, totalFrames - k * TranscriptionChunking.FramesPerChunk));
                Assert.Equal(expected, TranscriptionChunking.FramesInChunk(k, n));
            }
        }
    }

    [Fact]
    public void WindowSampleCount_IsChunkPlusOneFrameTail()
    {
        // 60 分钟：第 100 块的起点仍在音频内，窗口应为"一块 + 一个帧长"
        long n = 60L * 60 * TranscriptionChunking.SampleRate;
        Assert.Equal(TranscriptionChunking.MaxWindowSamples, TranscriptionChunking.WindowSampleCount(0, n));
        Assert.Equal(TranscriptionChunking.MaxWindowSamples, TranscriptionChunking.WindowSampleCount(100, n));
        // 越界的块号没有窗口（尾块之外不臆造数据）
        Assert.Equal(0, TranscriptionChunking.WindowSampleCount(1000, n));
    }

    /// <summary>
    /// 核心断言：按块算 fbank 与"整文件算完再切块"**逐帧、逐元素完全相同**。
    /// 覆盖了预加重跨块传染（块首帧要用块前一个采样）这条最容易写错的边界。
    /// </summary>
    [Fact]
    public void ComputeFbank_RangedChunks_MatchWholeFileFrames()
    {
        const int seconds = 65; // 2.16 块：既有整块，也有末尾不足一块
        long n = (long)seconds * TranscriptionChunking.SampleRate;
        var samples = MakeSamples((int)n);

        var whole = FbankExtractor.ComputeFbank(samples);
        Assert.Equal(TranscriptionChunking.TotalFrames(n), whole.Length);

        int chunks = TranscriptionChunking.ChunkCount(n);
        Assert.Equal(3, chunks);

        for (int k = 0; k < chunks; k++)
        {
            int start = TranscriptionChunking.ChunkStartSample(k);
            int count = TranscriptionChunking.WindowSampleCount(k, n);
            float? previous = start > 0 ? samples[start - 1] : null;

            var ranged = FbankExtractor.ComputeFbank(samples, start, count, previous);
            int frames = TranscriptionChunking.FramesInChunk(k, n);

            for (int t = 0; t < frames; t++)
            {
                var expected = whole[k * TranscriptionChunking.FramesPerChunk + t];
                var actual = ranged[t];
                Assert.Equal(expected.Length, actual.Length);
                for (int d = 0; d < expected.Length; d++)
                    Assert.Equal(expected[d], actual[d]); // 逐元素精确相等
            }
        }
    }

    /// <summary>
    /// 反向保护：故意漏掉"块前一个采样"会改变块首帧——证明上面那条断言不是"恒真"的空测试。
    /// </summary>
    [Fact]
    public void ComputeFbank_MissingPreviousSample_ChangesFirstFrame()
    {
        long n = 65L * TranscriptionChunking.SampleRate;
        var samples = MakeSamples((int)n);
        var whole = FbankExtractor.ComputeFbank(samples);

        int start = TranscriptionChunking.ChunkStartSample(1);
        int count = TranscriptionChunking.WindowSampleCount(1, n);

        var withPrevious = FbankExtractor.ComputeFbank(samples, start, count, samples[start - 1]);
        var without = FbankExtractor.ComputeFbank(samples, start, count, previousSample: null);

        bool differs = false;
        for (int d = 0; d < withPrevious[0].Length && !differs; d++)
            differs = withPrevious[0][d] != without[0][d];

        Assert.True(differs, "漏掉块前一个采样时首帧必须不同，否则说明这条回归测不出边界错误");
        // 带上前一个采样才与整文件路径一致
        Assert.Equal(whole[TranscriptionChunking.FramesPerChunk][0], withPrevious[0][0]);
    }
}
