using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace ClassNote.Services;

/// <summary>
/// 转写块的持久化（按会话 + 音轨 + 块号存储）。
/// 它同时充当**有效性记录**：库里存在的块号就是"已经算好且可信"的块，
/// 缺哪个块号，课后补算就重算哪一个——暂停、丢帧、崩溃重启都因此天然可恢复，
/// 不需要额外维护"哪些块作废"的状态机。
/// </summary>
public interface ITranscriptChunkStore
{
    /// <summary>写入 / 覆盖一个块的转写文本。</summary>
    void SaveTranscriptChunk(Guid sessionId, RecordingAudioSource source, int chunkIndex, int startSeconds, string text);

    /// <summary>按块号升序读出已完成的块。</summary>
    List<(int Index, string Text)> ListTranscriptChunks(Guid sessionId, RecordingAudioSource source);
}

/// <summary>单条音轨的转写进度（录音页展示 + 课后补算的输入）。</summary>
/// <param name="Source">这一路来源。</param>
/// <param name="SecondsCovered">已经处理过的音频秒数（含暂停期间）。</param>
/// <param name="ChunksStored">已落库的块数。</param>
/// <param name="SkippedChunks">因暂停/丢帧作废、需要课后补算的块数。</param>
/// <param name="DroppedBytes">队列溢出丢掉的字节数（正常为 0）。</param>
public sealed record TranscriptTrackProgress(
    RecordingAudioSource Source, double SecondsCovered, int ChunksStored, int SkippedChunks, long DroppedBytes);

/// <summary>
/// 有界帧队列：**采集线程只做一次内存拷贝就返回**（采集回调里绝不能等待），
/// 容量按字节而不是按帧数限制——设备帧长差异很大，按帧限容会让"能缓冲多久"变得不可预测。
/// 溢出只计数不抛异常：丢掉的采样会在消费侧重对齐（见 <see cref="PcmBlockBuffer.AddLostSamples"/>），
/// 对齐后受影响的块交给课后补算，绝不会算出一段错位的转写。
/// </summary>
public sealed class BoundedFrameQueue
{
    private readonly Queue<(byte[] Data, int Count)> _items = new();
    private readonly object _lock = new();
    private readonly long _capacityBytes;
    private readonly SemaphoreSlim _signal = new(0);
    private long _queuedBytes;
    private long _droppedBytes;

    public BoundedFrameQueue(long capacityBytes)
    {
        _capacityBytes = Math.Max(64 * 1024, capacityBytes);
    }

    /// <summary>默认容量：16MB ≈ 每路 8 分钟音频，足够吸收一次让路冷却而不丢帧。</summary>
    public static long DefaultCapacityBytes => 16L * 1024 * 1024;

    /// <summary>累计丢弃的字节数（0 = 从未丢帧）。</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>当前排队字节数。</summary>
    public long QueuedBytes { get { lock (_lock) return _queuedBytes; } }

    /// <summary>入队（采集线程调用；不阻塞、不等待消费方）。</summary>
    public void Enqueue(byte[] data, int count)
    {
        if (data == null || count <= 0)
            return;

        lock (_lock)
        {
            if (_queuedBytes + count > _capacityBytes)
            {
                Interlocked.Add(ref _droppedBytes, count);
                return;
            }
            _items.Enqueue((data, count));
            _queuedBytes += count;
        }

        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* 计数溢出（int 上限）不影响读取循环 */ }
    }

    /// <summary>取出最早的一帧；<paramref name="waitMs"/> 内没有则返回 false。</summary>
    public bool TryDequeue(out byte[] data, out int count, int waitMs)
    {
        data = Array.Empty<byte>();
        count = 0;

        if (!_signal.Wait(waitMs))
            return false;

        lock (_lock)
        {
            if (_items.Count == 0)
                return false;
            var item = _items.Dequeue();
            _queuedBytes -= item.Count;
            data = item.Data;
            count = item.Count;
            return true;
        }
    }
}

/// <summary>
/// 单路音轨的转写工作线程：消费帧队列 → 攒块 → 交给 STT → 落库。
///
/// 它与 <see cref="ClassroomResourceGovernor"/> 的配合方式：
/// · 预算为 0（关闭 / 手动暂停 / 用户明确要求停）→ 只计数不推理（<see cref="PcmBlockBuffer.Suspend"/>）；
/// · 预算 > 0 → 每产出一个块，按占空比歇一段时间再跑下一块（<see cref="ClassroomPolicy.CooldownMs"/>）；
/// · 停止时把尾部不足一块的部分补算完整，之后线程退出。
/// </summary>
internal sealed class TranscriptTrackWorker
{
    private readonly RecordingAudioSource _source;
    private readonly ITranscriptChunkStore _store;
    private readonly Guid _sessionId;
    private readonly Func<float[], int, float?, int, string?> _transcribe;
    private readonly ClassroomResourceGovernor? _governor;
    private readonly BoundedFrameQueue _queue;
    private readonly PcmBlockBuffer _buffer;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _done = new(false);
    private volatile bool _inputComplete;
    private long _accountedDrops;
    private long _storedChunks;
    private int _skippedChunks;
    private double _blockWallMs;

    public TranscriptTrackWorker(
        Guid sessionId,
        RecordingAudioSource source,
        ITranscriptChunkStore store,
        Func<float[], int, float?, int, string?> transcribe,
        ClassroomResourceGovernor? governor)
    {
        _sessionId = sessionId;
        _source = source;
        _store = store;
        _transcribe = transcribe;
        _governor = governor;
        _queue = new BoundedFrameQueue(BoundedFrameQueue.DefaultCapacityBytes);
        _buffer = new PcmBlockBuffer(ProcessBlock);

        // 低于常规优先级：课堂上这台机器还要放 PPT / 视频，抢核时我们先让
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"ClassNote-STT-{source}",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>采集线程入口：拷贝一帧并返回（拷贝是为了不依赖采集侧缓冲的生命周期）。</summary>
    public void Enqueue(byte[] pcm16, int count)
    {
        if (count <= 0)
            return;
        var copy = new byte[count];
        Buffer.BlockCopy(pcm16, 0, copy, 0, count);
        _queue.Enqueue(copy, count);
    }

    /// <summary>告知"不会再有新数据了"（设备已停），工作线程处理完尾部即退出。</summary>
    public void SignalInputComplete() => _inputComplete = true;

    /// <summary>等待本路处理结束（尾部块补算完成）。</summary>
    public bool WaitForCompletion(int timeoutMs) => _done.Wait(timeoutMs);

    public TranscriptTrackProgress Snapshot()
    {
        double seconds = _buffer.TotalSamples / (double)TranscriptionChunking.SampleRate;
        return new TranscriptTrackProgress(
            _source,
            seconds,
            (int)Interlocked.Read(ref _storedChunks),
            Math.Max(_skippedChunks, _buffer.SkippedCount),
            _queue.DroppedBytes);
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                ApplyBudget();

                if (!_queue.TryDequeue(out var data, out var count, 200))
                {
                    if (_inputComplete)
                        break;
                    continue;
                }

                AccountDroppedFrames();

                int emittedBefore = _buffer.EmittedChunks;
                _buffer.Append(data, count);

                // 产出了一个块 → 按占空比让路（课堂上把 CPU 还回去）
                if (_buffer.EmittedChunks > emittedBefore && !_inputComplete)
                {
                    int cooldown = ClassroomPolicy.CooldownMs(_blockWallMs, _governor?.Current.Duty ?? 1.0);
                    if (cooldown > 0)
                        Thread.Sleep(cooldown);
                }
            }

            _buffer.Complete();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IncrementalTranscription] {_source} 转写线程异常: {ex}");
        }
        finally
        {
            _done.Set();
        }
    }

    /// <summary>按当前预算决定"继续推理"还是"只计数"。</summary>
    private void ApplyBudget()
    {
        if (_governor == null)
            return;
        bool shouldRun = !_governor.Current.IsPaused;

        if (!shouldRun && !_buffer.IsSuspended)
        {
            _buffer.Suspend();
        }
        else if (shouldRun && _buffer.IsSuspended)
        {
            // 恢复前若发生过丢帧，边界块的预加重基准不可知 → 整块留给课后补算
            _buffer.Resume(carryUnknown: _queue.DroppedBytes > _accountedDrops);
        }
    }

    /// <summary>把队列丢掉的采样补进计数，使块号仍与真实时间轴对齐。</summary>
    private void AccountDroppedFrames()
    {
        long dropped = _queue.DroppedBytes;
        if (dropped <= _accountedDrops)
            return;
        long lostBytes = dropped - _accountedDrops;
        _accountedDrops = dropped;
        _buffer.AddLostSamples(lostBytes / 2);
        _skippedChunks = Math.Max(_skippedChunks, _buffer.SkippedChunks.Count);
    }

    /// <summary>块就绪：推理 + 落库（在工作线程上同步执行）。</summary>
    private void ProcessBlock(PcmBlock block)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var text = _transcribe(block.Window, block.WindowLength, block.PreviousSample, block.FrameCount);
            if (text == null)
                return; // 该块不足一帧（与批量路径的 continue 等价），不落库

            int startSeconds = block.Index * TranscriptionChunking.FramesPerChunk / 100; // 100 帧/秒
            _store.SaveTranscriptChunk(_sessionId, _source, block.Index, startSeconds, text);
            Interlocked.Increment(ref _storedChunks);
        }
        catch (Exception ex)
        {
            // 单块失败不影响其它块：该块不落库 → 课后补算会重新算它
            Debug.WriteLine($"[IncrementalTranscription] 第 {block.Index} 块转写失败: {ex.Message}");
        }
        finally
        {
            _blockWallMs = stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}

/// <summary>
/// 一次录音的边录边转写会话：管理各路工作线程，向录音页暴露进度，并向课后管线交接结果。
/// </summary>
public sealed class IncrementalTranscriptionSession : IDisposable
{
    private readonly Guid _sessionId;
    private readonly Dictionary<RecordingAudioSource, TranscriptTrackWorker> _workers = new();
    private readonly object _lock = new();
    private bool _inputComplete;
    private bool _disposed;

    public IncrementalTranscriptionSession(
        Guid sessionId,
        IEnumerable<RecordingAudioSource> sources,
        ITranscriptChunkStore store,
        Func<float[], int, float?, int, string?> transcribe,
        ClassroomResourceGovernor? governor = null)
    {
        _sessionId = sessionId;
        foreach (var source in sources)
            _workers[source] = new TranscriptTrackWorker(sessionId, source, store, transcribe, governor);
    }

    /// <summary>采集线程调用：把一帧 PCM 交给对应音轨。</summary>
    public void HandleFrame(RecordingAudioSource source, byte[] pcm16, int count)
    {
        if (_disposed || _inputComplete)
            return;
        TranscriptTrackWorker? worker;
        lock (_lock)
        {
            if (!_workers.TryGetValue(source, out worker))
                return;
        }
        worker.Enqueue(pcm16, count);
    }

    /// <summary>录音结束：告知输入完结（**不阻塞**，尾部补算在后台继续）。</summary>
    public void SignalInputComplete()
    {
        lock (_lock)
        {
            if (_inputComplete)
                return;
            _inputComplete = true;
            foreach (var worker in _workers.Values)
                worker.SignalInputComplete();
        }
    }

    /// <summary>等待尾部补算完成；超时返回 false（调用方据此把剩余块交给课后补算）。</summary>
    public bool WaitForCompletion(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        bool all = true;
        foreach (var worker in _workers.Values)
        {
            int remaining = (int)Math.Max(0, timeout.TotalMilliseconds - elapsed.Elapsed.TotalMilliseconds);
            if (!worker.WaitForCompletion(remaining))
                all = false;
        }
        return all;
    }

    /// <summary>各路进度快照（录音页展示）。</summary>
    public IReadOnlyList<TranscriptTrackProgress> Snapshot()
    {
        lock (_lock)
            return _workers.Values.Select(w => w.Snapshot()).ToList();
    }

    /// <summary>
    /// 采集侧数出来的该路总采样数（**权威值**）：课后装配据此算出"一共有多少块"。
    /// 比读 WAV 头可靠——录音文件的头部长度是 Dispose 时才回填的，中断残留的文件不可信。
    /// </summary>
    public long TotalSamplesFor(RecordingAudioSource source)
    {
        lock (_lock)
        {
            return _workers.TryGetValue(source, out var worker)
                ? (long)(worker.Snapshot().SecondsCovered * TranscriptionChunking.SampleRate)
                : 0;
        }
    }

    /// <summary>
    /// 释放本会话：**只做标记与信号，绝不等待工作线程**。
    ///
    /// ⚠️ 历史实现这里对每路调用 <c>worker.WaitForCompletion(2000)</c>，而本方法是从
    /// UI 线程（<c>RecordingPage.Page_Unloaded</c> → <c>RecordingViewModel.Dispose</c>）调用的：
    /// 两路来源（麦克风 + 系统声音）就是 **4 秒** UI 线程硬阻塞，实测可复现
    /// （<c>devtools/BugRepro ui-block</c>：1 路 2.00s / 2 路 4.01s，期间 DispatcherTimer 同步被推迟，
    /// 即界面完全无响应）。这类"结束录音时卡死几秒"正是用户报告的现场问题。
    ///
    /// 那个等待本来也是多余的：尾部补算由课后管线负责，
    /// <see cref="NoteProcessor.ProcessAsync"/> 里已经有
    /// <c>WaitForCompletion(TimeSpan.FromSeconds(120))</c> —— 在那里等（后台线程、2 分钟预算）
    /// 才是正确的等法。这里只保证"不会再有新数据进来"，工作线程处理完尾部自行退出。
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        SignalInputComplete();
    }
}

/// <summary>
/// 活跃增量转写会话的注册表：录音侧登记，课后管线按 sessionId 取用。
/// 存在的理由是不改动 <see cref="INoteProcessor"/> 的签名（那个签名被现有测试与调用点依赖），
/// 同时让课后管线能在"没有增量结果"时无差别退回原来的整文件批处理。
/// </summary>
public static class IncrementalTranscriptionHub
{
    private static readonly ConcurrentDictionary<Guid, IncrementalTranscriptionSession> Sessions = new();

    public static void Register(Guid sessionId, IncrementalTranscriptionSession session)
        => Sessions[sessionId] = session;

    public static IncrementalTranscriptionSession? Get(Guid sessionId)
        => Sessions.TryGetValue(sessionId, out var session) ? session : null;

    public static void Unregister(Guid sessionId)
    {
        if (Sessions.TryRemove(sessionId, out var session))
            session.Dispose();
    }
}
