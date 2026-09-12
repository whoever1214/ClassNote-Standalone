namespace ClassNote.Services;

/// <summary>一个可交给 STT 推理的块。</summary>
/// <param name="Index">块号（与批量路径的块号一致，从 0 起）。</param>
/// <param name="Window">窗口采样（块起点起算，长度可取到 <see cref="TranscriptionChunking.MaxWindowSamples"/>）。
/// **由缓冲区复用**，回调内同步用完即弃，不要保留引用。</param>
/// <param name="WindowLength">窗口内有效采样数。</param>
/// <param name="PreviousSample">块前一个采样（预加重用；块 0 为 null）。</param>
/// <param name="FrameCount">本块应当产出的 fbank 帧数。</param>
public readonly record struct PcmBlock(
    int Index,
    float[] Window,
    int WindowLength,
    float? PreviousSample,
    int FrameCount);

/// <summary>
/// 把边录边采到的 PCM16 字节攒成"与批量路径逐字节一致"的转写块。
///
/// 三条不变量（都被 <c>PcmBlockBufferTests</c> 锁住）：
/// 1. 块边界与批量路径完全相同：块 k 覆盖采样 [k*480000, (k+1)*480000)，
///    窗口多读一个帧长（400 采样）以便算满块内最后一帧，多算的 1 帧在取用时丢弃；
/// 2. 预加重跨块传染被正确处理：每个块都带上"块前一个采样"
///    （x[i] - 0.97 * x[i-1] 里那个 x[i-1]），块 0 传 null 与旧行为一致；
/// 3. **暂停不破坏对齐**：暂停期间仍然计数（只是不做推理），恢复时跳到下一个块边界重新开始，
///    因此暂停前后产出的块号始终与整段音频的真实时间轴对齐。
/// </summary>
public sealed class PcmBlockBuffer
{
    private readonly Action<PcmBlock> _process;
    private readonly float[] _window = new float[TranscriptionChunking.MaxWindowSamples];
    private readonly List<int> _skipped = new();

    private int _windowLength;
    private int _chunkIndex;
    private float? _chunkPrevious;   // 当前块起点前一个采样（预加重用）
    private float? _lastSample;      // 迄今最后一个采样（暂停/跳过期间维持）
    private long _totalSamples;      // 迄今收到的采样总数（含暂停期间）
    private long _pendingSkip;       // 恢复后还需跳过多少采样才对齐到块边界
    private int _suppressedChunk = -1;
    private int _skippedCount;

    /// <param name="process">块就绪时的处理回调（同步执行；内部应完成推理与落库）。</param>
    public PcmBlockBuffer(Action<PcmBlock> process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    /// <summary>迄今收到的采样总数（**含暂停期间**，这是块号对齐的基准）。</summary>
    public long TotalSamples => _totalSamples;

    /// <summary>下一个要产出的块号。</summary>
    public int NextChunkIndex => _chunkIndex;

    /// <summary>已实际产出（调用过回调）的块数。</summary>
    public int EmittedChunks { get; private set; }

    /// <summary>是否处于暂停（计数但不推理）。</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>因数据缺口而只能跳过、留给课后补算的块号（正常路径下为空）。</summary>
    public IReadOnlyList<int> SkippedChunks => _skipped;

    /// <summary>被跳过的块数（线程安全的计数，供工作线程上报进度）。</summary>
    public int SkippedCount => Volatile.Read(ref _skippedCount);

    /// <summary>当前窗口里已填充的采样数（进度展示用）。</summary>
    public int WindowLength => _windowLength;

    /// <summary>
    /// 追加一段 PCM16 字节（16kHz 单声道，小端）。
    /// 采集线程只负责把字节扔进队列，真正的转换/推理在消费线程上发生。
    /// </summary>
    public void Append(byte[] pcm16, int byteCount)
    {
        if (pcm16 == null || byteCount <= 0)
            return;

        int total = Math.Min(byteCount, pcm16.Length) / 2;
        int index = 0;

        while (index < total)
        {
            int remaining = total - index;

            // 暂停：只计数、只记住最后一个采样，不做任何转换与推理
            if (IsSuspended)
            {
                _lastSample = SampleAt(pcm16, index + remaining - 1);
                _totalSamples += remaining;
                return;
            }

            // 恢复后的对齐：跳过若干采样，直到落在块边界上
            if (_pendingSkip > 0)
            {
                int skip = (int)Math.Min(_pendingSkip, remaining);
                float carry = SampleAt(pcm16, index + skip - 1);
                _lastSample = carry;
                _chunkPrevious = carry;   // 边界前一个采样 = 最后一个被跳过的采样
                _totalSamples += skip;
                _pendingSkip -= skip;
                index += skip;
                continue;
            }

            int room = TranscriptionChunking.MaxWindowSamples - _windowLength;
            int take = Math.Min(room, remaining);
            for (int k = 0; k < take; k++)
                _window[_windowLength + k] = SampleAt(pcm16, index + k);

            _lastSample = _window[_windowLength + take - 1];
            _windowLength += take;
            _totalSamples += take;
            index += take;

            if (_windowLength == TranscriptionChunking.MaxWindowSamples)
                EmitChunk(TranscriptionChunking.FramesPerChunk);
        }
    }

    /// <summary>
    /// 暂停推理（课堂上检测到全屏放映 / 手动暂停）。
    /// 已填了一部分的块会被作废——它的后半段数据不会再补齐，因此**不落库**，由课后补算。
    /// </summary>
    public void Suspend()
    {
        if (IsSuspended)
            return;
        IsSuspended = true;
        InvalidateCurrentChunk();
    }

    /// <summary>
    /// 恢复推理：跳到下一个块边界重新开始，保证块号仍与真实时间轴对齐。
    /// </summary>
    /// <param name="carryUnknown">
    /// 恢复前的数据是否出现过缺口（队列溢出丢帧）。缺口会让"边界前一个采样"不可知，
    /// 此时边界那一个块无法算出与批量路径一致的文本，只能整块留给课后补算。
    /// </param>
    public void Resume(bool carryUnknown = false)
    {
        if (!IsSuspended)
            return;

        long boundary = NextBoundary(_totalSamples);
        _pendingSkip = boundary - _totalSamples;
        _chunkIndex = (int)(boundary / TranscriptionChunking.SamplesPerChunk);
        _windowLength = 0;
        _chunkPrevious = _pendingSkip > 0 ? null : _lastSample;
        IsSuspended = false;

        // 恢复前出现过数据缺口：边界块的预加重基准不可知，整块作废（课后补算）
        _suppressedChunk = carryUnknown ? _chunkIndex : -1;
        if (carryUnknown)
        {
            _skipped.Add(_chunkIndex);
            Interlocked.Increment(ref _skippedCount);
        }
    }

    /// <summary>
    /// 告知"有若干采样根本没有送达"（队列溢出丢帧）。
    ///
    /// 丢掉的是真实存在过的音频，因此必须**照样计入 <see cref="TotalSamples"/>**，
    /// 否则之后所有块号都会整体前移，产出的文本虽然"看着没错"，却与批量路径对不上、
    /// 与录音时间轴也对不上。计入之后跳到下一个块边界继续。
    ///
    /// 只有一种情况需要额外作废一个块：**下一个块边界恰好落在丢失段里**
    /// （块号 × 480000 正好不大于已收到的采样数），此时"边界前一个采样"已经丢了，
    /// 那一块的预加重基准无从得知。边界在丢失段之后时，跳过循环会顺带取到最后那个跳过的采样，
    /// 基准是**确知**的，不需要作废——这里刻意按这个判据决定，而不是"一丢帧就扔掉一整块"。
    /// </summary>
    public void AddLostSamples(long samples)
    {
        if (samples <= 0)
            return;

        _totalSamples += samples;
        bool boundaryInsideGap = NextBoundary(_totalSamples) == _totalSamples;

        if (!IsSuspended)
            Suspend();
        Resume(carryUnknown: boundaryInsideGap);
    }

    /// <summary>
    /// 录音结束：把尾部不足一块的部分按整段总采样数补算出来。
    /// 帧数由 <see cref="TranscriptionChunking.FramesInChunk"/> 给出，与批量路径的末尾块一致。
    /// </summary>
    public void Complete()
    {
        if (IsSuspended || _windowLength <= 0)
            return;

        int frames = TranscriptionChunking.FramesInChunk(_chunkIndex, _totalSamples);
        if (frames > 0)
            EmitChunk(frames);
        _windowLength = 0;
    }

    private void EmitChunk(int frameCount)
    {
        int index = _chunkIndex;
        float? previous = _chunkPrevious;
        int windowLength = _windowLength;

        // 下一个块的预加重基准 = 本块最后一个采样之前、恰好落在块边界前一个采样
        float? nextPrevious = windowLength >= TranscriptionChunking.SamplesPerChunk
            ? _window[TranscriptionChunking.SamplesPerChunk - 1]
            : _lastSample;

        if (_suppressedChunk == index)
        {
            _suppressedChunk = -1;   // 只作废这一个块，后续块正常产出
        }
        else
        {
            EmittedChunks++;
            _process(new PcmBlock(index, _window, windowLength, previous, frameCount));
        }

        // 推进到下一块：把窗口尾部溢出的 400 个采样搬到开头当作下一窗口的前缀
        _chunkIndex = index + 1;
        int carry = windowLength - TranscriptionChunking.SamplesPerChunk;
        if (carry > 0 && carry < TranscriptionChunking.MaxWindowSamples)
            Array.Copy(_window, TranscriptionChunking.SamplesPerChunk, _window, 0, carry);
        _windowLength = Math.Max(0, carry);
        _chunkPrevious = nextPrevious;
    }

    /// <summary>已经攒够一部分、但在暂停发生时被迫作废的块：不落库，交给课后补算。</summary>
    private void InvalidateCurrentChunk()
    {
        if (_windowLength > 0 && _windowLength < TranscriptionChunking.MaxWindowSamples)
        {
            if (!_skipped.Contains(_chunkIndex))
            {
                _skipped.Add(_chunkIndex);
                Interlocked.Increment(ref _skippedCount);
            }
            _windowLength = 0;
        }
    }

    private static long NextBoundary(long samples)
    {
        long per = TranscriptionChunking.SamplesPerChunk;
        if (samples <= 0)
            return 0;
        return samples % per == 0 ? samples : ((samples / per) + 1) * per;
    }

    /// <summary>从 PCM16 小端字节里取第 i 个采样（与写盘口径一致：s / 32768f）。</summary>
    private static float SampleAt(byte[] pcm16, int sampleIndex)
        => BitConverter.ToInt16(pcm16, sampleIndex * 2) / 32768f;
}
