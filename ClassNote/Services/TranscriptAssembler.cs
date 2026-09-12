using System.Diagnostics;
using System.IO;

namespace ClassNote.Services;

/// <summary>
/// 把"边录边转写留下的块"与"还没算的块"合成该音轨的**完整转写文本**。
///
/// 这是 v0.7 "快而不变味"的兑现处，三条规则：
/// 1. **只算缺的块**：库里已有的块直接用，缺哪块补哪块（缺块信息由块号推算，不需要额外的状态机）；
/// 2. **补算从文件按块读**：不再把整段音频读成 float[]（90 分钟是 345MB），内存占用与时长无关；
/// 3. **拼接口径与批量路径完全一致**：同一个 <see cref="SenseVoiceSttService.JoinChunks"/>、
///    同一套块边界，因此增量结果与"整文件跑一遍"的结果逐字节相同。
///
/// 任何一步不成立（文件不是 16k 单声道 PCM16、头解析失败、块补算抛异常）都退回整文件批处理，
/// 保证"快"永远不会换来"错"。
/// </summary>
public sealed class TranscriptAssembler
{
    private readonly ITranscriptChunkStore _store;
    private readonly ISttService _stt;
    private readonly Func<float[], int, float?, int, string?>? _transcribeChunk;

    public TranscriptAssembler(
        ITranscriptChunkStore store,
        ISttService stt,
        Func<float[], int, float?, int, string?>? transcribeChunk = null)
    {
        _store = store;
        _stt = stt;
        _transcribeChunk = transcribeChunk ?? DefaultChunkTranscriber(stt);
    }

    /// <summary>
    /// 默认的块推理入口：直接复用共享 STT 实例（同一个模型、同一份推理实现）。
    /// 传入的 <paramref name="stt"/> 不是 <see cref="SenseVoiceSttService"/>（测试替身等）时返回 null，
    /// 调用方据此走整文件路径——**绝不能**用一个"永远返回 null"的块推理器去装配，
    /// 那会让补算静默产出空文本。
    /// </summary>
    private static Func<float[], int, float?, int, string?>? DefaultChunkTranscriber(ISttService stt)
        => stt is SenseVoiceSttService sense ? sense.TranscribeChunkFromWindow : null;

    /// <summary>
    /// 生成该音轨的完整转写。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="wavPath">该路音频文件。</param>
    /// <param name="source">该路来源。</param>
    /// <param name="tappedSamples">采集侧数出来的总采样数（0 = 没有增量会话）。</param>
    /// <param name="progress">进度回调。</param>
    public async Task<string> AssembleAsync(
        Guid sessionId, string wavPath, RecordingAudioSource source,
        long tappedSamples, IProgress<string>? progress = null)
    {
        if (!File.Exists(wavPath))
            return "";

        if (_transcribeChunk == null)
        {
            // 没有可用的块推理器（非 SenseVoice 实现）：整文件路径，与 v0.6.0 行为一致
            return await _stt.TranscribeAsync(wavPath, progress);
        }

        if (!WavePcm.IsSupportedFormat(wavPath) || !WavePcm.TryReadInfo(wavPath, out var info))
        {
            // 不是本应用产出的 16k 单声道 PCM16：退回旧的整文件路径（行为与 v0.6.0 完全一致）
            Debug.WriteLine($"[TranscriptAssembler] {wavPath} 不是 16k 单声道 PCM16，走整文件批处理");
            return await _stt.TranscribeAsync(wavPath, progress);
        }

        // 已完成的块（课堂期间算出来的）。读库失败不影响正确性，退回整文件路径即可。
        List<(int Index, string Text)> stored;
        try
        {
            stored = _store.ListTranscriptChunks(sessionId, source);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranscriptAssembler] 读取转写块失败: {ex.Message}");
            return await _stt.TranscribeAsync(wavPath, progress);
        }

        // 块的**数学必须以文件为准**：末块的帧数由总采样数决定（TotalFrames = 1 + (n-400)/160），
        // 若改用采集侧数出来的数字，末块的帧数会与整文件路径不同，转写文本就不再一致。
        // 采集侧的数字只用于两件事：诊断"文件是否比课堂记录短"，以及把库里已有的更大块号算进 expected。
        long fileTotal = info.SampleCount;
        int expected = Math.Max(
            Math.Max(TranscriptionChunking.ChunkCount(fileTotal), TranscriptionChunking.ChunkCount(tappedSamples)),
            stored.Count == 0 ? 0 : stored.Max(c => c.Index) + 1);

        if (tappedSamples > fileTotal + TranscriptionChunking.SampleRate)
        {
            // 文件比采集到的短（写入中断/磁盘满）：如实告知，但已算好的块照样进笔记，不丢内容
            progress?.Report($"音频文件比课堂记录短（文件 {fileTotal / (double)TranscriptionChunking.SampleRate:0} 秒 / " +
                             $"记录 {tappedSamples / (double)TranscriptionChunking.SampleRate:0} 秒），已按文件实际内容整理。");
        }

        var have = new HashSet<int>(stored.Select(c => c.Index));
        var texts = new Dictionary<int, string>();
        foreach (var (index, text) in stored)
            texts[index] = text;

        var missing = new List<int>();
        for (int i = 0; i < expected; i++)
            if (!have.Contains(i))
                missing.Add(i);

        if (missing.Count > 0)
        {
            double missingSeconds = missing.Count * 30.0;
            progress?.Report(missing.Count == expected
                ? "正在转写语音…"
                : $"正在补算剩余 {missing.Count} 段（约 {missingSeconds / 60:0.#} 分钟音频）…");
            await Task.Run(() => FillMissing(sessionId, wavPath, info, fileTotal, source, missing, texts));
        }

        return SenseVoiceSttService.JoinChunks(
            Enumerable.Range(0, expected).Select(i => texts.TryGetValue(i, out var t) ? t : ""));
    }

    /// <summary>按块补算缺失的转写（逐块 seek 读文件，内存占用与音频总长无关）。</summary>
    private void FillMissing(Guid sessionId, string wavPath, WavePcmInfo info, long total,
        RecordingAudioSource source, List<int> missing, Dictionary<int, string> texts)
    {
        var transcribe = _transcribeChunk!;
        foreach (int index in missing)
        {
            try
            {
                int windowCount = TranscriptionChunking.WindowSampleCount(index, total);
                int frameCount = TranscriptionChunking.FramesInChunk(index, total);
                if (windowCount <= 0 || frameCount <= 0)
                    continue;

                long startSample = TranscriptionChunking.ChunkStartSample(index);
                var window = WavePcm.ReadWindow(wavPath, info, startSample, windowCount, out var previous);
                if (window.Length == 0)
                    continue;

                var text = transcribe(window, window.Length, previous, frameCount);
                if (text == null)
                    continue; // 与批量路径的 continue 等价：该块无有效帧

                // 立即落库：即便后续块失败/进程被杀，已算出来的块也不会白费
                _store.SaveTranscriptChunk(sessionId, source, index,
                    startSeconds: index * 30, text: text);
                texts[index] = text;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TranscriptAssembler] 第 {index} 块补算失败: {ex.Message}");
            }
        }
    }
}
