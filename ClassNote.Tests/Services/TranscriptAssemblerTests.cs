using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 增量装配的等价性测试——"边录边转写的结果与整文件跑一遍完全相同"这条承诺的落点。
///
/// 手法：不去加载 241MB 的 ONNX 模型，而是给两条路喂**同一个确定的假推理器**
/// （按"块号 + 块前采样 + 窗口内容"生成文本），再比较最终拼接结果。
/// 真实实现里两条路也共用同一个 <c>TranscribeChunk</c>，所以"输入相同 ⇒ 输出相同"成立，
/// 而这里要验证的正是**输入相同**（窗口、预加重基准、帧数、块号顺序）。
/// </summary>
public class TranscriptAssemblerTests
{
    private sealed class InMemoryStore : ITranscriptChunkStore
    {
        public Dictionary<(Guid, RecordingAudioSource, int), string> Chunks { get; } = new();

        public void SaveTranscriptChunk(Guid sessionId, RecordingAudioSource source, int chunkIndex, int startSeconds, string text)
            => Chunks[(sessionId, source, chunkIndex)] = text;

        public List<(int Index, string Text)> ListTranscriptChunks(Guid sessionId, RecordingAudioSource source)
            => Chunks.Where(kv => kv.Key.Item1 == sessionId && kv.Key.Item2 == source)
                     .Select(kv => (kv.Key.Item3, kv.Value))
                     .OrderBy(t => t.Item1)
                     .ToList();
    }

    private sealed class FakeStt : ISttService
    {
        public int Calls { get; private set; }
        public bool IsModelReady => true;

        public Task<string> TranscribeAsync(string wavPath, IProgress<string>? progress = null)
        {
            Calls++;
            return Task.FromResult("WHOLE-FILE");
        }
    }

    /// <summary>确定性假推理器：文本同时编码了块号、帧数与块前采样，任何输入差异都会反映到结果里。</summary>
    private static string FakeTranscribe(float[] window, int length, float? previous, int frameCount)
        => $"#{frameCount}|{(previous.HasValue ? previous.Value.ToString("F4") : "none")}|{window[0]:F4}";

    private static (string Path, long Samples) WriteWav(int samples)
    {
        var dir = Path.Combine(Path.GetTempPath(), "classnote_assembler_test");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"a-{Guid.NewGuid():N}.wav");

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = samples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(18);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(16000);
        bw.Write(32000);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write((short)0);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        for (int i = 0; i < samples; i++)
        {
            short value = (short)(Math.Sin(i * 0.01) * 20000);
            bw.Write(value);
        }
        bw.Flush();
        return (path, samples);
    }

    private static TranscriptAssembler MakeAssembler(InMemoryStore store, FakeStt stt)
        => new(store, stt, FakeTranscribe);

    /// <summary>批量路径（= <c>SenseVoiceSttService.TranscribeAllChunks</c> 的窗口取法）用同一假推理器应得的结果。</summary>
    private static string BatchReference(string wavPath, long total)
    {
        Assert.True(WavePcm.TryReadInfo(wavPath, out var info));
        var samples = WavePcm.ReadAll(wavPath, info);
        var chunks = new List<string>();
        int count = TranscriptionChunking.ChunkCount(total);
        for (int k = 0; k < count; k++)
        {
            int start = TranscriptionChunking.ChunkStartSample(k);
            int windowCount = TranscriptionChunking.WindowSampleCount(k, total);
            int frames = TranscriptionChunking.FramesInChunk(k, total);
            if (windowCount <= 0 || frames <= 0)
                continue;

            var window = new float[windowCount];
            Array.Copy(samples, start, window, 0, Math.Min(windowCount, samples.Length - start));
            float? previous = start > 0 ? samples[start - 1] : null;
            chunks.Add(FakeTranscribe(window, window.Length, previous, frames));
        }
        return SenseVoiceSttService.JoinChunks(chunks);
    }

    [Fact]
    public async Task Assemble_WithNothingPrecomputed_MatchesBatchPath()
    {
        // 65 秒 → 3 块；库里一块都没有：应当补算出全部 3 块，结果与批量路径一致
        long total = 65L * TranscriptionChunking.SampleRate;
        var (path, _) = WriteWav((int)total);
        var store = new InMemoryStore();
        var stt = new FakeStt();

        var text = await MakeAssembler(store, stt).AssembleAsync(Guid.NewGuid(), path, RecordingAudioSource.Microphone, total);

        Assert.Equal(BatchReference(path, total), text);
        Assert.Equal(3, store.Chunks.Count);
        Assert.Equal(0, stt.Calls); // 能按块处理时不该退回整文件
    }

    [Fact]
    public async Task Assemble_ReusesStoredChunks_AndOnlyFillsMissingOnes()
    {
        long total = 65L * TranscriptionChunking.SampleRate;
        var (path, _) = WriteWav((int)total);
        var sessionId = Guid.NewGuid();
        var store = new InMemoryStore();
        // 模拟"课堂期间已经算好了第 0、2 块"
        store.Chunks[(sessionId, RecordingAudioSource.Microphone, 0)] = "PRE-0";
        store.Chunks[(sessionId, RecordingAudioSource.Microphone, 2)] = "PRE-2";

        var text = await MakeAssembler(store, new FakeStt()).AssembleAsync(sessionId, path, RecordingAudioSource.Microphone, total);

        Assert.Equal(TranscriptionChunking.ChunkCount(total), store.Chunks.Count); // 只补了缺的第 1 块
        var parts = text.Split(' ');
        Assert.Equal("PRE-0", parts[0]);
        Assert.Equal("PRE-2", parts[2]);
        Assert.StartsWith("#", parts[1]); // 第 1 块是补算出来的
    }

    [Fact]
    public async Task Assemble_AllChunksPrecomputed_DoesNotTouchAudioFile()
    {
        long total = 65L * TranscriptionChunking.SampleRate;
        var (path, _) = WriteWav((int)total);
        var sessionId = Guid.NewGuid();
        var store = new InMemoryStore();
        for (int k = 0; k < TranscriptionChunking.ChunkCount(total); k++)
            store.Chunks[(sessionId, RecordingAudioSource.Microphone, k)] = $"PRE-{k}";

        var text = await MakeAssembler(store, new FakeStt()).AssembleAsync(sessionId, path, RecordingAudioSource.Microphone, total);

        // 这就是"下课时几乎不用等"的形态：全部复用，结果为三块按顺序拼接
        Assert.Equal("PRE-0 PRE-1 PRE-2", text);
    }

    [Fact]
    public async Task Assemble_UnsupportedFormat_FallsBackToWholeFile()
    {
        // 48kHz 的文件不是本应用产出的形态：必须退回旧的整文件路径，而不是按 16k 硬读
        var dir = Path.Combine(Path.GetTempPath(), "classnote_assembler_test");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"bad-{Guid.NewGuid():N}.wav");
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + 2000);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(18);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(48000);
            bw.Write(96000);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write((short)0);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(2000);
            bw.Write(new byte[2000]);
            bw.Flush();
        }

        var stt = new FakeStt();
        var text = await MakeAssembler(new InMemoryStore(), stt)
            .AssembleAsync(Guid.NewGuid(), path, RecordingAudioSource.Microphone, 0);

        Assert.Equal("WHOLE-FILE", text);
        Assert.Equal(1, stt.Calls);
    }

    [Fact]
    public async Task Assemble_TappedCountLargerThanFile_DoesNotLoseContent()
    {
        long fileSamples = 40L * TranscriptionChunking.SampleRate; // 40 秒 = 2 块
        var (path, _) = WriteWav((int)fileSamples);
        var store = new InMemoryStore();

        // 采集侧数出来比文件长（理论上的偏大）：多出来的块读不到数据 → 产出为空 → 被过滤，不丢已有内容
        var text = await MakeAssembler(store, new FakeStt())
            .AssembleAsync(Guid.NewGuid(), path, RecordingAudioSource.Microphone, fileSamples + TranscriptionChunking.SamplesPerChunk);

        Assert.Equal(BatchReference(path, fileSamples), text);
    }

    [Fact]
    public async Task Assemble_MissingFile_ReturnsEmpty()
    {
        var text = await MakeAssembler(new InMemoryStore(), new FakeStt())
            .AssembleAsync(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.wav"),
                RecordingAudioSource.Microphone, 0);
        Assert.Equal("", text);
    }

    [Fact]
    public void JoinChunks_DropsWhitespaceChunks_AndJoinsInOrder()
    {
        // 拼接口径（与旧实现逐字一致）：丢弃空白块、用单个空格连接、整体 trim。
        // 生产路径上的每个块文本都已经过 Clean() 去首尾空白，所以拼出来不会有多余空格。
        Assert.Equal("a b", SenseVoiceSttService.JoinChunks(new[] { "a", "", "  ", "b" }));
        Assert.Equal("a b c", SenseVoiceSttService.JoinChunks(new[] { "a", "b", "", "c" }));
        Assert.Equal("", SenseVoiceSttService.JoinChunks(new[] { "", "   " }));
    }
}
