using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClassNote.Services;

namespace NoteBench;

/// <summary>
/// 笔记生成链路的实测工具（v0.7 提速方案的验收工具）。
///
/// 它回答两个必须用真实模型回答、单测回答不了的问题：
/// 1. **边录边转写与整文件转写是否逐字相同**（identity 命令：逐块对拍 + 整体对拍）；
/// 2. 各阶段真实耗时与实时因子是多少（stt 命令：冷加载、温调用、每块耗时）。
///
/// 用法：
///   NoteBench identity &lt;wav&gt; [--slices 320,640] [--pause-at 0.5]
///   NoteBench stt      &lt;wav&gt;
///   NoteBench chunks   &lt;wav&gt;
///   NoteBench all      &lt;wav&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string command = args[0].ToLowerInvariant();
        string? wav = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var options = ParseOptions(args);

        if (command is "chunks" && wav != null)
            return ChunkPlan(wav);

        if (wav == null || !File.Exists(wav))
        {
            Console.Error.WriteLine($"找不到音频文件：{wav ?? "(未指定)"}");
            return 2;
        }

        return command switch
        {
            "identity" => Identity(wav, options),
            "stt" => Stt(wav, options),
            "classroom" => Classroom(wav, options),
            "memory" => Memory(wav, options),
            "all" => All(wav, options),
            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            NoteBench —— ClassNote 笔记链路实测工具

              NoteBench chunks    <wav>                              只看分块计划（不需要模型）
              NoteBench stt       <wav> [--threads N]                冷加载 + 温转写 + 实时因子
              NoteBench identity  <wav> [--slices 320,640] [--pause-at 0.5]
                                                                     边录边转写 vs 整文件：逐块 + 整体对拍
              NoteBench classroom <wav> [--threads N] [--mode auto]  按真实时间模拟一堂课：
                                                                     测课堂期间 CPU 占用与课后残余等待，
                                                                     并走生产代码（会话 + 资源调度 + 库）验证一致性
              NoteBench memory    <wav> [--idle-seconds 6] [--timeout 90]
                                                                     模型闲置释放实测：加载 → 等定时器自动释放 →
                                                                     看内存是否真的还回去 → 再转写一次证明重载后结果不变
              NoteBench all       <wav>                              以上全部（除 classroom / memory）

            选项：
              --slices a,b,c   模拟采集回调的分片大小（采样数），默认 320,1600,640
              --pause-at x     在音频进度 x（0~1）处暂停一次并恢复，验证重对齐后仍一致
              --threads N      ONNX intra-op 线程数，默认取"一半逻辑核"
              --mode x         auto / aggressive / off（课堂占用档位），默认 auto
            """);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                continue;
            string key = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            options[key] = value;
        }
        return options;
    }

    // ── 分块计划（纯数学）─────────────────────────────────────

    private static int ChunkPlan(string wav)
    {
        if (!WavePcm.TryReadInfo(wav, out var info))
        {
            Console.Error.WriteLine("无法解析该 WAV（需要 16kHz 单声道 PCM16）");
            return 1;
        }

        int chunks = TranscriptionChunking.ChunkCount(info.SampleCount);
        Console.WriteLine($"文件      : {Path.GetFileName(wav)}");
        Console.WriteLine($"时长      : {info.SampleCount / (double)TranscriptionChunking.SampleRate:F2} 秒");
        Console.WriteLine($"采样数    : {info.SampleCount}");
        Console.WriteLine($"块数      : {chunks}（每块 30 秒 = {TranscriptionChunking.SamplesPerChunk} 采样）");
        Console.WriteLine($"末块帧数  : {TranscriptionChunking.FramesInChunk(chunks - 1, info.SampleCount)}");
        Console.WriteLine($"格式达标  : {WavePcm.IsSupportedFormat(wav)}");
        return 0;
    }

    // ── STT 计时 ─────────────────────────────────────────────

    private static int Stt(string wav, Dictionary<string, string> options)
    {
        var service = options.TryGetValue("threads", out var t) && int.TryParse(t, out var threads)
            ? new SenseVoiceSttService(threads)
            : new SenseVoiceSttService();
        Console.WriteLine($"intra-op 线程: {service.IntraOpThreads}（逻辑核 {Environment.ProcessorCount} ⇒ 默认永远留一半）");
        Console.WriteLine($"模型就绪    : {service.IsModelReady}");

        var cold = Stopwatch.StartNew();
        string coldText = service.TranscribeAsync(wav).GetAwaiter().GetResult();
        cold.Stop();

        var warm = Stopwatch.StartNew();
        string warmText = service.TranscribeAsync(wav).GetAwaiter().GetResult();
        warm.Stop();

        double seconds = AudioSeconds(wav);
        double modelLoad = cold.Elapsed.TotalSeconds - warm.Elapsed.TotalSeconds;

        Console.WriteLine($"音频时长    : {seconds:F2} 秒");
        Console.WriteLine($"冷调用      : {cold.Elapsed.TotalSeconds:F2} 秒（含模型加载）");
        Console.WriteLine($"温调用      : {warm.Elapsed.TotalSeconds:F2} 秒");
        Console.WriteLine($"模型加载(推): {modelLoad:F2} 秒");
        Console.WriteLine($"实时因子RTF : {warm.Elapsed.TotalSeconds / seconds:F3}");
        Console.WriteLine($"一致性      : {(coldText == warmText ? "冷/温结果相同 ✓" : "冷/温结果不同 ✗")}");
        Console.WriteLine($"转写(n={warmText.Length}): {Clip(warmText)}");
        return 0;
    }

    // ── 课堂模拟：按真实时间喂帧，走生产代码（会话 + 资源调度 + SQLite）──

    /// <summary>课堂资源探针：固定"空闲 + 插电"（要看让路效果时用 --mode off）。</summary>
    private sealed class IdleProbe : IClassroomResourceProbe
    {
        public bool IsFullscreenAppForeground() => false;
        public bool IsScreenVideoPlaying() => false;
        public bool IsOnBattery() => false;
        public double OwnCpuUtilization() => 0;
    }

    private static int Classroom(string wav, Dictionary<string, string> options)
    {
        if (!WavePcm.IsSupportedFormat(wav) || !WavePcm.TryReadInfo(wav, out var info))
        {
            Console.Error.WriteLine("无法解析该 WAV（需要 16kHz 单声道 PCM16）");
            return 1;
        }

        var mode = ClassroomTranscriptionModes.FromStorage(options.GetValueOrDefault("mode") ?? "Auto");
        var service = options.TryGetValue("threads", out var t) && int.TryParse(t, out var threads)
            ? new SenseVoiceSttService(threads)
            : new SenseVoiceSttService();

        // 临时库：绝不碰用户真实的 classnote.db
        var dir = Path.Combine(Path.GetTempPath(), "classnote_notebench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var repo = new LocalRepository(Path.Combine(dir, "bench.db"));
        var sessionId = Guid.NewGuid();

        using var governor = new ClassroomResourceGovernor(new IdleProbe(), mode);
        governor.Start();

        var session = new IncrementalTranscriptionSession(
            sessionId, new[] { RecordingAudioSource.Microphone }, repo,
            service.TranscribeChunkFromWindow, governor);

        // 采集侧：20ms 一帧，按真实时间节流（与真实设备的回调节奏一致）
        const int frameSamples = 320;
        var pcm = ReadPcm16(wav);
        int frames = pcm.Length / 2 / frameSamples;
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var pacer = Stopwatch.StartNew();

        for (int i = 0; i < frames; i++)
        {
            var slice = new byte[frameSamples * 2];
            Buffer.BlockCopy(pcm, i * frameSamples * 2, slice, 0, slice.Length);
            session.HandleFrame(RecordingAudioSource.Microphone, slice, slice.Length);

            double targetMs = (i + 1) * 1000.0 * frameSamples / TranscriptionChunking.SampleRate;
            int sleep = (int)Math.Round(targetMs - pacer.Elapsed.TotalMilliseconds);
            if (sleep > 1)
                Thread.Sleep(sleep);
        }
        wall.Stop();
        var cpuDuring = process.TotalProcessorTime - cpuBefore;
        double utilization = cpuDuring.TotalSeconds / (wall.Elapsed.TotalSeconds * Environment.ProcessorCount);

        // 下课：告知输入完结，测"用户还要等多久"
        var tail = Stopwatch.StartNew();
        session.SignalInputComplete();
        bool finished = session.WaitForCompletion(TimeSpan.FromSeconds(600));
        tail.Stop();

        var progress = session.Snapshot().SingleOrDefault();
        double audioSeconds = info.SampleCount / (double)TranscriptionChunking.SampleRate;

        // 走生产装配器：库里已有的块 + 补算缺的块
        var assembler = new TranscriptAssembler(repo, service, service.TranscribeChunkFromWindow);
        var assembled = assembler.AssembleAsync(sessionId, wav, RecordingAudioSource.Microphone,
            session.TotalSamplesFor(RecordingAudioSource.Microphone)).GetAwaiter().GetResult();

        // 与整文件路径对拍（真实模型）
        var batch = service.TranscribeAsync(wav).GetAwaiter().GetResult();

        Console.WriteLine("── 课堂模拟（按真实时间喂帧，走生产代码）──");
        Console.WriteLine($"档位          : {ClassroomTranscriptionModes.ToDisplayName(mode)}，intra-op = {service.IntraOpThreads} 线程 / {Environment.ProcessorCount} 逻辑核");
        Console.WriteLine($"音频时长      : {audioSeconds:F1} 秒（模拟课堂时长 {wall.Elapsed.TotalSeconds:F1} 秒）");
        Console.WriteLine($"课堂期间 CPU  : 累计 {cpuDuring.TotalSeconds:F1} 核·秒 ⇒ 平均占整机 {utilization:P1}");
        Console.WriteLine($"课堂进度      : 已转写 {progress?.SecondsCovered ?? 0:F0} 秒 / 落库 {progress?.ChunksStored ?? 0} 块" +
                          (progress?.SkippedChunks > 0 ? $" / 跳过 {progress.SkippedChunks} 块" : ""));
        Console.WriteLine($"课后残余等待  : {tail.Elapsed.TotalSeconds:F1} 秒（收尾{(finished ? "完成" : "超时")}）");
        Console.WriteLine($"装配结果      : {assembled.Length} 字");
        Console.WriteLine($"与整文件相同  : {(assembled == batch ? "是 ✓" : "否 ✗")}");
        if (assembled != batch)
        {
            Console.WriteLine($"整文件: {Clip(batch)}");
            Console.WriteLine($"装配  : {Clip(assembled)}");
            return 1;
        }

        try { Directory.Delete(dir, recursive: true); } catch { }
        return 0;
    }

    // ── 模型闲置释放实测 ─────────────────────────────────────

    /// <summary>
    /// 验证"闲置 N 分钟后释放模型、下次使用自动重载"这条策略真的成立：
    /// 加载 → **等生产定时器自己触发释放**（不手动调 TryRelease）→ 量内存是否还回去 →
    /// 再转写一次并与释放前的文本逐字比对，证明重载对结果无影响。
    /// </summary>
    private static int Memory(string wav, Dictionary<string, string> options)
    {
        double idleSeconds = options.TryGetValue("idle-seconds", out var s) && double.TryParse(s, out var v) ? v : 6;
        int timeoutSeconds = options.TryGetValue("timeout", out var t) && int.TryParse(t, out var tv) ? tv : 90;

        var service = SenseVoiceSttService.Shared;
        var process = Process.GetCurrentProcess();

        double Baseline() => process.WorkingSet64 / 1024.0 / 1024.0;
        double Private() { process.Refresh(); return process.PrivateMemorySize64 / 1024.0 / 1024.0; }
        void Trim()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
        }

        Trim();
        double wsIdle = Baseline(), privIdle = Private();
        Console.WriteLine($"基线        : WS {wsIdle:F0} MB / 私有 {privIdle:F0} MB");

        var loadWatch = Stopwatch.StartNew();
        service.PrewarmAsync().Wait();
        loadWatch.Stop();
        Trim();
        double wsLoaded = Baseline(), privLoaded = Private();
        Console.WriteLine($"加载后      : WS {wsLoaded:F0} MB / 私有 {privLoaded:F0} MB" +
                          $"（+{wsLoaded - wsIdle:F0} MB / +{privLoaded - privIdle:F0} MB，耗时 {loadWatch.Elapsed.TotalSeconds:F1} s）");
        Console.WriteLine($"模型已加载  : {service.IsModelLoaded}");

        // 释放前的转写结果（用于证明重载后结果不变）
        string before = service.TranscribeAsync(wav).GetAwaiter().GetResult();

        // 关键：不手动释放，让**生产定时器**（每分钟一次）按阈值自己动手
        service.IdleReleaseAfter = TimeSpan.FromSeconds(idleSeconds);
        Console.WriteLine();
        Console.WriteLine($"把闲置阈值设为 {idleSeconds:F0} 秒，等生产定时器自动释放（最多等 {timeoutSeconds} 秒）…");

        var wait = Stopwatch.StartNew();
        bool released = false;
        while (wait.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (!service.IsModelLoaded)
            {
                released = true;
                break;
            }
            Thread.Sleep(1000);
        }
        wait.Stop();

        if (!released)
        {
            Console.WriteLine($"✗ {timeoutSeconds} 秒内没有观察到自动释放（IsModelLoaded 仍为 true）");
            return 1;
        }
        Console.WriteLine($"✓ 自动释放已发生：等待 {wait.Elapsed.TotalSeconds:F0} 秒后 IsModelLoaded={service.IsModelLoaded}" +
                          $"（累计释放 {service.IdleReleaseCount} 次）");

        Trim();
        double wsReleased = Baseline(), privReleased = Private();
        Console.WriteLine($"释放后      : WS {wsReleased:F0} MB / 私有 {privReleased:F0} MB" +
                          $"（相对加载后 {wsReleased - wsLoaded:+0;-0} MB / {privReleased - privLoaded:+0;-0} MB）");

        // 重载：下一次使用应当自动加载，且结果与释放前逐字相同
        var reloadWatch = Stopwatch.StartNew();
        string after = service.TranscribeAsync(wav).GetAwaiter().GetResult();
        reloadWatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"重载+转写   : {reloadWatch.Elapsed.TotalSeconds:F1} 秒（其中模型加载约 {Math.Max(0, reloadWatch.Elapsed.TotalSeconds - (before.Length > 0 ? 0 : 0)):F1} 秒内）");
        Console.WriteLine($"重载后已加载: {service.IsModelLoaded}");
        Console.WriteLine($"文本一致    : {(after == before ? "是 ✓（释放/重载对结果无影响）" : "否 ✗")}");
        Console.WriteLine($"转写长度    : 释放前 {before.Length} 字 / 重载后 {after.Length} 字");

        bool ok = after == before && service.IsModelLoaded;
        Console.WriteLine();
        Console.WriteLine(ok ? "闲置释放策略实测通过 ✓" : "闲置释放策略实测失败 ✗");
        return ok ? 0 : 1;
    }

    // ── 对拍：边录边转写 vs 整文件 ────────────────────────────

    private static int Identity(string wav, Dictionary<string, string> options)
    {
        if (!WavePcm.IsSupportedFormat(wav) || !WavePcm.TryReadInfo(wav, out var info))
        {
            Console.Error.WriteLine("无法解析该 WAV（需要 16kHz 单声道 PCM16）");
            return 1;
        }

        var slices = (options.GetValueOrDefault("slices") ?? "320,1600,640")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s.Trim()))
            .ToArray();
        double pauseAt = options.TryGetValue("pause-at", out var raw) && double.TryParse(raw, out var p) ? p : -1;

        var pcm = ReadPcm16(wav);
        var service = new SenseVoiceSttService();

        // 1) 整文件路径（生产代码，公开入口）
        var batchWatch = Stopwatch.StartNew();
        string batchJoined = service.TranscribeAsync(wav).GetAwaiter().GetResult();
        batchWatch.Stop();

        // 2) 整文件路径的**逐块**结果（同一个内部块推理入口，用于定位差异在哪一块）
        var samples = WavePcm.ReadAll(wav, info);
        var batchChunks = new List<string?>();
        var batchChunkWatch = Stopwatch.StartNew();
        int total = TranscriptionChunking.ChunkCount(info.SampleCount);
        for (int k = 0; k < total; k++)
        {
            int start = TranscriptionChunking.ChunkStartSample(k);
            batchChunks.Add(service.TranscribeChunk(samples, start,
                TranscriptionChunking.WindowSampleCount(k, info.SampleCount),
                start > 0 ? samples[start - 1] : null,
                TranscriptionChunking.FramesInChunk(k, info.SampleCount)));
        }
        batchChunkWatch.Stop();

        // 3) 边录边转写路径：用生产用的 PcmBlockBuffer 按采集回调的节奏喂
        var incremental = new List<(int Index, string? Text)>();
        var chunkTimes = new List<double>();
        int pauseSample = pauseAt > 0 ? (int)(info.SampleCount * pauseAt) : -1;
        int pauseChunk = -1;
        bool paused = false;
        bool pauseDone = false;
        var buffer = new PcmBlockBuffer(block =>
        {
            var watch = Stopwatch.StartNew();
            var text = service.TranscribeChunkFromWindow(block.Window, block.WindowLength, block.PreviousSample, block.FrameCount);
            watch.Stop();
            incremental.Add((block.Index, text));
            if (chunkTimes.Count < 200) chunkTimes.Add(watch.Elapsed.TotalMilliseconds);
        });

        var incrementalWatch = Stopwatch.StartNew();
        int position = 0;
        int sliceIndex = 0;
        while (position < pcm.Length / 2)
        {
            int take = Math.Min(slices[sliceIndex % slices.Length], pcm.Length / 2 - position);

            // 只暂停一次（暂停点过去之后不再触发，否则会来回暂停/恢复）
            if (!pauseDone && pauseSample > 0 && position + take > pauseSample)
            {
                int stop = pauseSample - position;
                if (stop > 0)
                {
                    buffer.Append(pcm, stop * 2);
                    position += stop;
                }
                pauseDone = true;
                paused = true;
                pauseChunk = buffer.NextChunkIndex;
                buffer.Suspend();
                Console.WriteLine($"· 在第 {pauseChunk} 块处暂停（模拟检测到全屏放映）");
                continue;
            }

            buffer.Append(pcm.AsSpan(position * 2, take * 2).ToArray(), take * 2);
            position += take;
            sliceIndex++;

            // 暂停一会儿（至少攒够一块的数据）后恢复：走生产代码的"跳到下一个块边界"
            if (paused && position - pauseSample > TranscriptionChunking.SamplesPerChunk)
            {
                buffer.Resume();
                paused = false;
                Console.WriteLine($"· 已恢复（跳到块 {buffer.NextChunkIndex} 重新开始）");
            }
        }
        if (paused)
        {
            buffer.Resume();
            paused = false;
        }
        buffer.Complete();
        incrementalWatch.Stop();

        // 4) 比较
        var produced = incremental.ToDictionary(x => x.Index, x => x.Text);
        var mismatches = new List<int>();
        for (int k = 0; k < total; k++)
        {
            if (!produced.TryGetValue(k, out var incText))
                continue; // 暂停作废的块：课后补算会重算，不属于差异
            if (batchChunks[k] != incText)
                mismatches.Add(k);
        }

        string incrementalJoined = SenseVoiceSttService.JoinChunks(
            Enumerable.Range(0, total).Select(k => produced.TryGetValue(k, out var t) ? t ?? "" : ""));

        // 暂停过的块按设计不产出（课后补算会重算它），因此那种情况下只对拍**实际产出**的块
        bool identical = mismatches.Count == 0 && (pauseChunk >= 0 || incrementalJoined == batchJoined);

        Console.WriteLine("── 对拍结果 ──────────────────────────────");
        Console.WriteLine($"音频时长      : {info.SampleCount / (double)TranscriptionChunking.SampleRate:F2} 秒 / {total} 块");
        Console.WriteLine($"整文件(公开)  : {batchWatch.Elapsed.TotalSeconds:F2} 秒，{batchJoined.Length} 字");
        Console.WriteLine($"整文件(逐块)  : {batchChunkWatch.Elapsed.TotalSeconds:F2} 秒");
        Console.WriteLine($"边录边转写    : {incrementalWatch.Elapsed.TotalSeconds:F2} 秒，产出 {produced.Count} 块" +
                          (pauseChunk >= 0 ? $"，跳过 {buffer.SkippedChunks.Count} 块（课后补算）" : ""));
        Console.WriteLine($"逐块差异      : {(mismatches.Count == 0 ? "无 ✓" : string.Join(",", mismatches) + " ✗")}");
        Console.WriteLine($"整体文本相同  : " + (pauseChunk >= 0
            ? "（含暂停：跳过块按设计交由课后补算，只对拍实际产出的块）"
            : incrementalJoined == batchJoined ? "是 ✓" : "否 ✗"));
        Console.WriteLine($"总采样对齐    : {buffer.TotalSamples} / {info.SampleCount} " +
                          (buffer.TotalSamples == info.SampleCount ? "✓" : "✗"));
        if (chunkTimes.Count > 0)
        {
            Console.WriteLine($"单块推理      : 均 {chunkTimes.Average():F0} ms / 最小 {chunkTimes.Min():F0} / 最大 {chunkTimes.Max():F0}");
            Console.WriteLine($"实测 RTF      : {chunkTimes.Sum() / 1000.0 / (info.SampleCount / (double)TranscriptionChunking.SampleRate):F3}");
        }
        if (!identical)
        {
            Console.WriteLine("—— 差异定位 ——");
            Console.WriteLine($"整文件: {Clip(batchJoined)}");
            Console.WriteLine($"增量  : {Clip(incrementalJoined)}");
            return 1;
        }

        Console.WriteLine("结论：边录边转写与整文件转写**逐字相同** ✓");
        return 0;
    }

    private static int All(string wav, Dictionary<string, string> options)
    {
        int chunks = ChunkPlan(wav);
        if (chunks != 0) return chunks;
        Console.WriteLine();
        int stt = Stt(wav, options);
        if (stt != 0) return stt;
        Console.WriteLine();
        return Identity(wav, options);
    }

    // ── 工具 ─────────────────────────────────────────────────

    private static double AudioSeconds(string wav)
        => WavePcm.TryReadInfo(wav, out var info)
            ? info.SampleCount / (double)TranscriptionChunking.SampleRate
            : 0;

    /// <summary>只取 data 块负载（跳过 44/46 字节头），用于模拟采集侧喂进来的字节流。</summary>
    private static byte[] ReadPcm16(string wav)
    {
        if (!WavePcm.TryReadInfo(wav, out var info))
            return Array.Empty<byte>();

        using var fs = File.OpenRead(wav);
        fs.Seek(info.DataOffset, SeekOrigin.Begin);
        var bytes = new byte[info.DataBytes];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = fs.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == bytes.Length ? bytes : bytes[..read];
    }

    private static string Clip(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= 120 ? oneLine : oneLine[..120] + "…";
    }
}
