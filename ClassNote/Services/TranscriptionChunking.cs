namespace ClassNote.Services;

/// <summary>
/// 转写分块的**唯一定义**：批量整文件路径与边录边转写路径共用同一套分块数学，
/// 两条路因此产出**逐字节一致**的文本。
///
/// 为什么必须共用：v0.7 把转写从"课后整文件跑一遍"改成"课上按块增量跑"，
/// 如果增量路径的分块边界与批量路径差一个采样，同一段录音会得到两份不同的笔记。
/// 分块规则一旦分叉，正确性就再也无法用"对拍"证明，只能靠肉眼比对——所以这里把
/// 边界、窗口长度、每块帧数全部收敛成纯函数，由 <c>TranscriptionChunkingTests</c> 锁死。
///
/// 与旧实现的关系：旧代码把整文件读成 float[]，算完 fbank 后按
/// <c>ChunkSeconds * 100 = 3000</c> 帧切块。本类把"3000 帧一块"解析成等价的
/// 采样窗口，语义完全不变：
///   块 k 的 fbank 帧区间 = [k*3000, min(total, (k+1)*3000))
/// 其中 total = 1 + (n - 400) / 160（n = 总采样数），与旧实现逐帧对齐。
/// </summary>
public static class TranscriptionChunking
{
    /// <summary>采样率（录音端固定 16kHz）。</summary>
    public const int SampleRate = 16000;

    /// <summary>fbank 帧长（25ms）。</summary>
    public const int FrameLength = 400;

    /// <summary>fbank 帧移（10ms）。</summary>
    public const int FrameShift = 160;

    /// <summary>每块帧数 = 30 秒 × 100 帧/秒（与旧实现的 ChunkSeconds = 30 等价）。</summary>
    public const int FramesPerChunk = 3000;

    /// <summary>每块推进的采样数 = 3000 帧 × 160 采样/帧 = 480000（30 秒）。</summary>
    public const int SamplesPerChunk = FramesPerChunk * FrameShift;

    /// <summary>
    /// 单个窗口额外多取的尾部采样数：块内最后一帧需要它自己那 400 个采样，
    /// 因此算满 3000 帧必须读到 块起点 + 480000 + 400 个采样（多出的 1 帧在取用时丢弃）。
    /// </summary>
    public const int WindowTailSamples = FrameLength;

    /// <summary>单块窗口最多包含的采样数（<see cref="SamplesPerChunk"/> + <see cref="WindowTailSamples"/>）。</summary>
    public const int MaxWindowSamples = SamplesPerChunk + WindowTailSamples;

    /// <summary>块数：按 480000 采样一块向上取整（与旧实现的 <c>start += chunkFrames</c> 循环一致）。</summary>
    public static int ChunkCount(long totalSamples)
        => totalSamples <= 0 ? 0 : (int)((totalSamples + SamplesPerChunk - 1) / SamplesPerChunk);

    /// <summary>块 k 的首个采样在整段音频里的偏移。</summary>
    public static int ChunkStartSample(int chunkIndex) => chunkIndex * SamplesPerChunk;

    /// <summary>
    /// 整段音频的 fbank 帧总数，与 <see cref="FbankExtractor.ComputeFbank(float[])"/> 的
    /// <c>1 + (n - 400) / 160</c> 完全一致。
    /// </summary>
    public static int TotalFrames(long totalSamples)
        => totalSamples < FrameLength ? 0 : 1 + (int)((totalSamples - FrameLength) / FrameShift);

    /// <summary>块 k 应当产出的帧数（末尾块可能不足 3000 帧）。</summary>
    public static int FramesInChunk(int chunkIndex, long totalSamples)
    {
        int total = TotalFrames(totalSamples);
        int start = chunkIndex * FramesPerChunk;
        if (start >= total)
            return 0;
        return Math.Min(FramesPerChunk, total - start);
    }

    /// <summary>
    /// 块 k 的窗口需要读取的采样数（自块起点算起）：正常是
    /// <see cref="MaxWindowSamples"/>，接近音频末尾时截到实际剩余量。
    /// 末尾块因此天然与整文件路径的"最后一个不足 3000 帧的块"对齐。
    /// </summary>
    public static int WindowSampleCount(int chunkIndex, long totalSamples)
    {
        long start = ChunkStartSample(chunkIndex);
        long available = totalSamples - start;
        if (available <= 0)
            return 0;
        return (int)Math.Min(MaxWindowSamples, available);
    }
}
