// UiBlockProbe：测量"结束录音"收尾路径在 **UI 线程** 上到底会阻塞多久。
//
// 现场线索（用户）：从 0.3.4 升到 1.0.1 后开始卡死，必须重启进程，且反复出现。
// 1.0.0 新增的东西里，唯一会在 UI 线程上同步等待后台线程的代码是：
//   RecordingPage.Page_Unloaded (UI 线程) → RecordingViewModel.Dispose()
//     → IncrementalTranscriptionSession.Dispose() → 每路 worker.WaitForCompletion(2000)
//   两路（麦克风 + 系统声音）时上界 4 秒 —— 而这段等待发生在**停课后返回主页**的那一刻，
//   用户看到的就是"点哪都不动"。
//
// 本探针用真实的 IncrementalTranscriptionSession + 真正的编码/攒块路径（替身 STT，
// 不加载 241MB 模型，改为按真实耗时模拟推理），在 STA/UI 线程上调用 Dispose()，
// 测出阻塞墙钟，并统计这段时间里 DispatcherTimer（自动下课看门狗用的正是它）被推迟多久。
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ClassNote.Services;

namespace BugRepro;

public static class UiBlockProbe
{
    public static int Run(string[] args)
    {
        int perBlockMs = args.Length > 1 && int.TryParse(args[1], out var v) ? v : 1200;
        int framesPerTrack = args.Length > 2 && int.TryParse(args[2], out var f) ? f : 200;
        int trackCount = args.Length > 3 && int.TryParse(args[3], out var t) ? t : 2;
        Console.WriteLine($"参数：单块推理耗时≈{perBlockMs}ms，每路待处理帧数={framesPerTrack}，音轨数={trackCount}");

        var app = new ClassNote.App();
        var init = typeof(ClassNote.App).GetMethod("InitializeComponent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        init?.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var repo = new ThrowawayStore();
        var sessionId = Guid.NewGuid();
        var sources = trackCount >= 2
            ? new[] { RecordingAudioSource.Microphone, RecordingAudioSource.System }
            : new[] { RecordingAudioSource.Microphone };

        var session = new IncrementalTranscriptionSession(
            sessionId, sources, repo,
            (window, len, prev, frames) =>
            {
                Thread.Sleep(perBlockMs);          // 模拟一次真实的 ONNX 块推理
                return "文本";
            },
            governor: null);

        // 灌入 16kHz 单声道 PCM16。每块 = 480000 采样 = 960000 字节（30 秒）。
        // 只灌"不足一块"的量，让尾部补算（Complete()）必须真的跑一次推理。
        int bytesPerTrack = framesPerTrack * 320 * 2;   // 320 采样/帧 × 2 字节
        var pcm = new byte[bytesPerTrack];
        for (int i = 0; i + 1 < pcm.Length; i += 2) { pcm[i] = 0x10; pcm[i + 1] = 0x02; }

        for (int i = 0; i < 30; i++)
            foreach (var s in sources)
                session.HandleFrame(s, pcm, pcm.Length);

        Thread.Sleep(300);   // 让工作线程先把队列里的帧搬进块缓冲

        // 在 UI 线程上挂一个 DispatcherTimer，量它被推迟多久
        var tickDelays = new List<double>();
        var last = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick += (_, _) =>
        {
            tickDelays.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        };
        timer.Start();
        PumpFor(app, TimeSpan.FromMilliseconds(400));
        var baselineMax = tickDelays.Count > 0 ? tickDelays.Max() : 0;
        tickDelays.Clear();

        // ── 生产路径：RecordingViewModel.Dispose() 在 UI 线程上做的事 ──
        // 修复后 RecordingPage.Page_Unloaded 把整个 Dispose 放到后台线程，UI 线程只负责发起。
        var sw = Stopwatch.StartNew();
        _ = Task.Run(() =>
        {
            session.Dispose();                 // 后台线程：不再有 WaitForCompletion 硬等待
            IncrementalTranscriptionHub.Register(sessionId, session);
            IncrementalTranscriptionHub.Unregister(sessionId);
        });
        sw.Stop();

        // 等 Dispatcher 把积压的 tick 跑掉，才能看到真实的推迟量
        PumpFor(app, TimeSpan.FromMilliseconds(800));

        var maxDelay = tickDelays.Count > 0 ? tickDelays.Max() : 0;
        Console.WriteLine();
        Console.WriteLine($"空闲时 DispatcherTimer 最大间隔: {baselineMax:F0} ms（正常 ≈ 100ms）");
        Console.WriteLine($"UI 线程同步收尾耗时:            {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"收尾期间 DispatcherTimer 最大间隔: {maxDelay:F0} ms  ← 这段时间界面完全无响应");
        Console.WriteLine($"已落库块数: {repo.Count}");

        Console.WriteLine();
        if (maxDelay >= 3000)
        {
            var verdict = sw.Elapsed.TotalSeconds >= 5.0
                ? "（已超过 Windows 判定「未响应」的 5 秒门槛）"
                : "（接近 5 秒门槛；块推理更慢或两路同时补算即会越过）";
            Console.WriteLine("结论：『结束录音 → 返回主页』这一步在 UI 线程上同步等待后台补算，");
            Console.WriteLine($"     实测阻塞 {sw.Elapsed.TotalSeconds:F2} 秒，UI 线程上的 DispatcherTimer 同步被推迟 {maxDelay:F0} ms {verdict}");
            Console.WriteLine("     而『到点自动下课』(RecordingPage 的看门狗) 与『课表调度』(MainWindow) 都是 DispatcherTimer——");
            Console.WriteLine("     这两个时刻正好都在停课前后，被推迟就可能跨过下一节课的开始时刻。");
            return 2;
        }
        Console.WriteLine("结论：本次未观察到明显阻塞");
        return 0;
    }

    /// <summary>把消息循环跑到指定时长（让 DispatcherTimer 有机会触发）。</summary>
    private static void PumpFor(Application app, TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    /// <summary>只统计块数、不落库的替身（避免污染真实数据库）。</summary>
    private sealed class ThrowawayStore : ITranscriptChunkStore
    {
        public int Count;
        public void SaveTranscriptChunk(Guid sessionId, RecordingAudioSource source, int chunkIndex, int startSeconds, string text)
            => Interlocked.Increment(ref Count);
        public List<(int Index, string Text)> ListTranscriptChunks(Guid sessionId, RecordingAudioSource source)
            => new();
    }
}
