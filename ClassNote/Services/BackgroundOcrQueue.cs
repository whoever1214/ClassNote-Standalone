using System.Diagnostics;

namespace ClassNote.Services;

/// <summary>
/// 课后 OCR 的"提前做"队列：截图一落库就在后台把它 OCR 掉，文字写回截图行。
///
/// 为什么值得单独做：旧实现把**所有**截图的 OCR 都堆在录音结束之后、且排在 STT 后面串行执行
/// （一节课 100+ 张，本机 107ms/张，慢机/4K 屏更久），用户等笔记时它还没开始跑。
/// 改成录音期间随手做完之后，处理管线里这一段变成"读现成结果"，耗时≈0。
///
/// 单线程串行：Windows.Media.Ocr 的识别引擎是进程级共享实例，并发调用的安全性没有承诺；
/// OCR 本身很轻（约 1% 单核占用），串行足够快，也不需要参与课堂资源让路。
/// </summary>
public sealed class BackgroundOcrQueue : IDisposable
{
    private readonly IOcrService _ocr;
    private readonly Action<Guid, int, string> _store;
    private readonly Queue<(Guid SessionId, int SeqNo, byte[] Image)> _items = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread? _thread;
    private readonly int _maxQueue;
    private volatile bool _completed;
    private volatile bool _stopped;
    private bool _idle = true;

    /// <summary>成功写回的 OCR 结果数。</summary>
    public long ProcessedCount { get; private set; }

    /// <summary>识别失败 / 入队被拒的数量（管线随后会自己补做）。</summary>
    public long SkippedCount { get; private set; }

    /// <param name="ocr">OCR 实现。</param>
    /// <param name="store">OCR 结果写回（会话、截图序号、文本）。</param>
    /// <param name="maxQueue">队列上限：满了就丢弃（管线会补做），绝不阻塞截图保存。</param>
    public BackgroundOcrQueue(IOcrService ocr, Action<Guid, int, string> store, int maxQueue = 1024)
    {
        _ocr = ocr;
        _store = store;
        _maxQueue = Math.Max(1, maxQueue);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClassNote.Ocr",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>截图落库后调用（在 UI 线程上）：只入队，不做识别。</summary>
    public void Enqueue(Guid sessionId, int seqNo, byte[] imageData)
    {
        if (_stopped || imageData == null || imageData.Length == 0)
            return;

        lock (_lock)
        {
            if (_items.Count >= _maxQueue)
            {
                SkippedCount++;
                return;
            }
            _idle = false;
            _items.Enqueue((sessionId, seqNo, imageData));
        }
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>不会再有新截图（录音结束）时调用。</summary>
    public void Complete()
    {
        _completed = true;
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// 等队列排空（处理管线在 OCR 阶段前调用）。超时返回 false——调用方据此对缺 OCR 的截图自行补做，
    /// 因此这里"等不到"只会慢一点，不会丢内容。
    /// </summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            lock (_lock)
            {
                if (_idle)
                    return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }

    private void Run()
    {
        while (!_stopped)
        {
            if (!_signal.Wait(200))
            {
                if (DrainedAndCompleted())
                    return;
                continue;
            }

            // 取空为止；**取空之后再标空闲**——标记必须在手里那张图也识别完之后，
            // 否则等队列的管线会在这张图写回之前就走了，然后自己重复识别一遍。
            while (TryDequeue(out var item))
                Process(item);
            MarkIdle();

            if (DrainedAndCompleted())
                return;
        }
    }

    private bool TryDequeue(out (Guid SessionId, int SeqNo, byte[] Image) item)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                item = default;
                return false;
            }
            item = _items.Dequeue();
            return true;
        }
    }

    /// <summary>队列已空且当前这批都处理完了 → 标记空闲，让 <see cref="WaitIdle"/> 返回。</summary>
    private void MarkIdle()
    {
        lock (_lock)
        {
            if (_items.Count == 0)
                _idle = true;
        }
    }

    /// <summary>已完成且队列为空 → 工作线程可以退出（并标记空闲，让等待方立刻返回）。</summary>
    private bool DrainedAndCompleted()
    {
        if (!_completed)
            return false;
        lock (_lock)
        {
            if (_items.Count > 0)
                return false;
            _idle = true;
            return true;
        }
    }

    private void Process((Guid SessionId, int SeqNo, byte[] Image) item)
    {
        try
        {
            var text = _ocr.RecognizeAsync(item.Image).GetAwaiter().GetResult() ?? "";
            _store(item.SessionId, item.SeqNo, text);
            ProcessedCount++;
        }
        catch (Exception ex)
        {
            // 单张失败不阻塞：这张截图的 OCR 会由处理管线自己补做
            SkippedCount++;
            Debug.WriteLine($"[BackgroundOcrQueue] 截图 {item.SeqNo} OCR 失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stopped = true;
        Complete();
        // 立刻标记空闲：等队列的调用方（处理管线）不该因为录音页被关掉而白等整个超时——
        // 它随后会自己把缺 OCR 的截图补做掉，结果是慢一点，而不是丢内容。
        lock (_lock)
        {
            _idle = true;
        }
    }
}
