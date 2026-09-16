using System.Diagnostics;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 「从 0.3.4 升到 1.0.1 后开始卡死、必须重启进程、且反复出现」的回归测试。
///
/// 已定位的 UI 线程硬阻塞：录音结束离开录音页时
///   <c>RecordingPage.Page_Unloaded</c>(UI 线程) → <c>RecordingViewModel.Dispose()</c>
///   → <c>IncrementalTranscriptionSession.Dispose()</c>
/// 旧实现在这里对**每一路**音轨调用 <c>worker.WaitForCompletion(2000)</c>：
/// 单路麦克风 2 秒、两路（麦克风 + 系统声音，也就是默认配置）**4 秒**，
/// 而且这是固定开销 —— 不管尾部补算是不是早就做完了都要等满。
///
/// 双重危害：
/// 1. UI 线程被占住 → 课表调度与"到点自动下课"看门狗（都是 DispatcherTimer）一起被推迟，
///    录音于是跨进下一节课（见 <see cref="ScheduledRecordingHandoffTests"/>）；
/// 2. 占住超过 5 秒时 Windows 直接给窗口挂上"未响应"，用户只能杀进程 —— 即现场的"卡死"。
///
/// 本组用例把"释放不得阻塞调用方"这条不变量钉死：即使工作线程正忙着一块很慢的推理，
/// <c>Dispose()</c> 也必须立刻返回（尾部补算由课后管线等，它有 120 秒预算且在后台线程上）。
/// </summary>
public class IncrementalTranscriptionDisposeTests
{
    /// <summary>永远算不完的推理：模拟真机上约 5 秒/块的 ONNX 推理。</summary>
    private static string? SlowTranscribe(float[] window, int length, float? previous, int frames)
    {
        Thread.Sleep(5000);
        return "文本";
    }

    private static IncrementalTranscriptionSession MakeBusySession(ITranscriptChunkStore store, int tracks = 2)
    {
        var sources = tracks >= 2
            ? new[] { RecordingAudioSource.Microphone, RecordingAudioSource.System }
            : new[] { RecordingAudioSource.Microphone };

        var session = new IncrementalTranscriptionSession(
            Guid.NewGuid(), sources, store, SlowTranscribe, governor: null);

        // 灌入"不足一整块"的音频：停止时必须触发一次尾部补算（也就是那次慢推理）
        var pcm = new byte[320 * 2 * 200];
        for (int i = 0; i + 1 < pcm.Length; i += 2) { pcm[i] = 0x10; pcm[i + 1] = 0x02; }
        for (int i = 0; i < 20; i++)
            foreach (var s in sources)
                session.HandleFrame(s, pcm, pcm.Length);

        Thread.Sleep(200);   // 让工作线程把帧搬进块缓冲并开始尾部推理
        return session;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Dispose_DoesNotBlockCaller_EvenWhileWorkersAreStillTranscribing(int tracks)
    {
        var store = new CountingStore();
        var session = MakeBusySession(store, tracks);

        var sw = Stopwatch.StartNew();
        session.Dispose();
        sw.Stop();

        // 旧实现：1 路 ≥2000ms、2 路 ≥4000ms。修复后必须接近瞬时。
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Dispose 阻塞了 {sw.ElapsedMilliseconds}ms（{tracks} 路）——UI 线程上会直接表现为界面卡住");
    }

    [Fact]
    public void Dispose_IsIdempotent_AndSignalsInputComplete()
    {
        var store = new CountingStore();
        var session = MakeBusySession(store, tracks: 1);

        session.Dispose();
        session.Dispose();   // 不得抛，也不得再次等待

        // 输入完结信号已发出：再喂帧也不会被处理（工作线程进入收尾并退出）
        session.HandleFrame(RecordingAudioSource.Microphone, new byte[640], 640);

        Assert.True(session.WaitForCompletion(TimeSpan.FromSeconds(30)),
            "工作线程应当在处理完尾部后退出（Dispose 只是不等它，不是杀掉它）");
    }

    /// <summary>只计数、不落库的替身（单测绝不写真实数据库）。</summary>
    private sealed class CountingStore : ITranscriptChunkStore
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public void SaveTranscriptChunk(Guid sessionId, RecordingAudioSource source, int chunkIndex, int startSeconds, string text)
            => Interlocked.Increment(ref _count);

        public List<(int Index, string Text)> ListTranscriptChunks(Guid sessionId, RecordingAudioSource source)
            => new();
    }
}
