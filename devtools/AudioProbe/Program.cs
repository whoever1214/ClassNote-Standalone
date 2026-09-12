// 开发辅助工具：真实设备上的录音冒烟验证（不入库）。
// 来源命名与产品一致：仅麦克风 / 仅系统声音 / 麦克风和系统声音（见 AudioSourceKinds.DisplayNames）。
// 对每种来源录 N 秒，校验产物 WAV 的格式（16kHz 单声道 PCM16）、时长与峰值。
//
// 用法：AudioProbe [输出目录] [每种来源录音秒数] [--silent-ok] [--all-devices]
//   --silent-ok    允许系统声音用例 peak=0（安静机器上回环本来就没声音）
//   --all-devices  额外逐个播放设备跑「仅系统声音」，用来找出"到底哪个设备在出声"
//                  （回环录成静音最常见的根因就是选错了播放设备）
using System.IO;
using ClassNote.Services;

public static class Program
{
    public static int Main(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        bool silentOk = args.Contains("--silent-ok");
        bool allDevices = args.Contains("--all-devices");

        var outDir = positional.Length > 0 ? positional[0] : Path.Combine(Path.GetTempPath(), "classnote-audio-probe");
        double seconds = positional.Length > 1 && double.TryParse(positional[1], out var s) ? s : 2.0;
        Directory.CreateDirectory(outDir);

        using var audio = new AudioService();
        var micNames = audio.GetInputDevices();
        var micIds = audio.GetInputDeviceIds();
        var outputNames = audio.GetOutputDevices();
        var outputIds = audio.GetOutputDeviceIds();

        Console.WriteLine($"mics={micNames.Length} outputs={outputNames.Length} mmeFallback={audio.IsUsingMmeFallback}");
        for (int i = 0; i < micNames.Length; i++)
            Console.WriteLine($"  mic[{i}] {micNames[i]}  ({Truncate(micIds[i])})");
        for (int i = 0; i < outputNames.Length; i++)
            Console.WriteLine($"  out[{i}] {outputNames[i]}  ({Truncate(outputIds[i])})");

        int failures = 0;

        // 1) 仅麦克风：单路直写路径
        failures += Run(audio, outDir, seconds, "mic", new RecordingConfig(AudioSourceKind.Microphone), silentOk);

        // 2) 仅系统声音：单路直写 + 回环，未指定设备 → 走默认播放设备角色回退
        failures += Run(audio, outDir, seconds, "system-default",
            new RecordingConfig(AudioSourceKind.System), silentOk);

        // 3) 麦克风和系统声音：产品路径是**分轨录制**（各写一个文件），不是合成一路。
        //    必须用 StartRecordingDual，否则冒烟测的是一条产品上已不再使用的合成路径。
        failures += RunDual(audio, outDir, seconds, "mic-and-system",
            new RecordingConfig(AudioSourceKind.Both), silentOk);

        // 4) 显式指定播放设备 + 麦克风和系统声音：
        //    回归 v0.6.0 修掉的缺陷——此时选定的播放设备过去会被丢弃，回环只能落到系统默认设备
        if (outputIds.Length > 0)
        {
            failures += RunDual(audio, outDir, seconds, "mic-and-system-explicit-out",
                new RecordingConfig(AudioSourceKind.Both, OutputDeviceId: outputIds[0]), silentOk);
        }
        else
        {
            Console.WriteLine("--- 跳过：未检测到播放设备，无法验证显式播放设备路由 ---");
        }

        if (allDevices)
        {
            for (int i = 0; i < outputIds.Length; i++)
            {
                var tag = $"system-out{i}";
                Console.WriteLine($"  --all-devices: {tag} = {outputNames[i]}");
                Run(audio, outDir, seconds, tag,
                    new RecordingConfig(AudioSourceKind.System, OutputDeviceId: outputIds[i]), silentOk);
            }
        }

        Console.WriteLine(failures == 0 ? "AUDIO_PROBE_OK" : $"AUDIO_PROBE_FAILURES={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int Run(AudioService audio, string outDir, double seconds, string tag,
        RecordingConfig config, bool silentOk)
    {
        var path = Path.Combine(outDir, $"{tag}.wav");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        Console.WriteLine($"--- {tag}: source={config.Source} channels={config.ChannelCount} ---");
        int frames = 0;
        // 每次 Run 用独立处理器并在结束后摘掉：旧实现直接 += 匿名 lambda，
        // 处理器会逐次累积，frames 计数从第二轮起就偏大
        EventHandler<byte[]> onData = (_, _) => Interlocked.Increment(ref frames);
        audio.AudioDataAvailable += onData;

        try
        {
            if (!audio.StartRecording(path, config))
            {
                Console.WriteLine($"  START_FAILED: {audio.LastError}");
                return 1;
            }

            // 录音起来了但发生了降级（例如指定播放设备失效、回退到系统默认设备）必须打出来——
            // 否则"配了 A 设备却录到 B 设备"在冒烟输出里完全看不出来
            if (!string.IsNullOrEmpty(audio.LastWarning))
                Console.WriteLine("  WARNING: " + audio.LastWarning);

            Thread.Sleep((int)(seconds * 1000));
            audio.StopRecording();
        }
        finally
        {
            audio.AudioDataAvailable -= onData;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine("  NO_FILE");
            return 1;
        }

        var info = ReadWav(path);
        if (info == null)
        {
            Console.WriteLine("  UNREADABLE_WAV");
            return 1;
        }

        bool formatOk = info.SampleRate == 16000 && info.Channels == 1 && info.BitsPerSample == 16;
        bool hasSignal = info.Peak > 0;
        // --silent-ok 只对"需要系统声音"的用例生效：麦克风用例录成静音几乎必然是故障，
        // 一起放过会让这个冒烟测试失去意义
        bool allowSilent = silentOk && config.NeedsSystemAudio;
        // 一个采样都没有（data 块为 0）：说明这段时间里播放设备**没有在渲染任何音频**
        // ——WASAPI 回环对空闲端点一个包都不发，所以"文件 0 秒"是事实，不是缺陷。
        // 真机上确认过：同一台机器，端点空闲时 callbacks=0、文件 46 字节；
        // 一放音立刻 callbacks=95、时长 5.96s。要验证回环，必须先在所选设备上放音。
        bool emptyRecording = info.DurationSeconds <= 0.05;
        // 时长容差比旧版收紧到 0.3s：单路直写路径的时长必须严格跟随设备时钟，
        // 容差放宽会把"写入节奏跑偏"的问题盖过去
        bool lengthOk = Math.Abs(info.DurationSeconds - seconds) < 0.3
                        || (allowSilent && emptyRecording);
        Console.WriteLine($"  file={new FileInfo(path).Length} bytes format={info.SampleRate}Hz/{info.Channels}ch/" +
                          $"{info.BitsPerSample}bit duration={info.DurationSeconds:F2}s peak={info.Peak} " +
                          $"callbacks={Volatile.Read(ref frames)}");
        Console.WriteLine($"  format-ok={formatOk} length-ok={lengthOk} has-signal={hasSignal}");

        if (emptyRecording)
        {
            Console.WriteLine("  NOTE: 没有采集到任何采样数据（data 块为 0）——这段时间播放设备没有在渲染音频" +
                              "（回环对空闲端点不发包）。要验证回环，请先在所选播放设备上放音。");
        }
        else if (!hasSignal && !allowSilent)
        {
            Console.WriteLine("  NOTE: peak=0 —— 该用例在这段录音里完全没有信号。" +
                              "麦克风用例出现这一行说明麦克风没录到东西（检查设备选择与系统静音）；" +
                              "回环用例出现这一行通常是选错了播放设备（用 --all-devices 逐个试，" +
                              "或加 --silent-ok 表示本机确实安静）。");
        }

        bool signalOk = hasSignal || allowSilent;
        return formatOk && lengthOk && signalOk ? 0 : 1;
    }

    /// <summary>
    /// 分轨录制用例（产品路径）：麦克风与系统声音**各写一个文件**，
    /// 逐个校验格式 / 时长 / 信号。
    /// 冒烟重点：两个文件都必须存在且良构——这就是"两路转写能分别标注来源"的前提；
    /// 若某一路全程静音，它的 WAV 会是 0 秒（回环空闲不发包），另一路不该受影响。
    /// </summary>
    private static int RunDual(AudioService audio, string outDir, double seconds,
        string tagPrefix, RecordingConfig config, bool silentOk)
    {
        Console.WriteLine($"--- {tagPrefix}（分轨）: source={config.Source} channels={config.ChannelCount} ---");

        var files = new List<RecordingAudioFile>
        {
            new(RecordingAudioSource.Microphone, Path.Combine(outDir, $"{tagPrefix}_mic.wav")),
            new(RecordingAudioSource.System, Path.Combine(outDir, $"{tagPrefix}_system.wav")),
        };
        foreach (var f in files)
        {
            try { if (File.Exists(f.Path)) File.Delete(f.Path); } catch { }
        }

        int callbacks = 0;
        EventHandler<byte[]> onData = (_, _) => Interlocked.Increment(ref callbacks);
        audio.AudioDataAvailable += onData;

        RecordingAudio recorded;
        try
        {
            recorded = audio.StartRecordingDual(config, files);
            if (recorded.IsEmpty)
            {
                Console.WriteLine($"  START_FAILED: {audio.LastError}");
                return 1;
            }
            if (!string.IsNullOrEmpty(audio.LastWarning))
                Console.WriteLine("  WARNING: " + audio.LastWarning);

            Thread.Sleep((int)(seconds * 1000));
            audio.StopRecording();
        }
        finally
        {
            audio.AudioDataAvailable -= onData;
        }

        if (recorded.Files.Count != 2)
        {
            Console.WriteLine($"  EXPECTED_2_TRACKS but got {recorded.Files.Count}");
            return 1;
        }

        int failures = 0;
        foreach (var file in recorded.Files)
        {
            var label = file.Source == RecordingAudioSource.System ? "system" : "mic";
            var info = File.Exists(file.Path) ? ReadWav(file.Path) : null;
            if (info == null)
            {
                Console.WriteLine($"  {label}: NO_FILE_OR_UNREADABLE");
                failures++;
                continue;
            }

            bool formatOk = info.SampleRate == 16000 && info.Channels == 1 && info.BitsPerSample == 16;
            // 麦克风那一路录成静音几乎必然是故障（--silent-ok 也不放过）；
            // 系统声音那一路在没放音时本来就该是 0 秒
            bool allowSilent = file.Source == RecordingAudioSource.System;
            bool emptyRecording = info.DurationSeconds <= 0.05;
            bool lengthOk = Math.Abs(info.DurationSeconds - seconds) < 0.3 || (allowSilent && emptyRecording);
            bool hasSignal = info.Peak > 0;

            Console.WriteLine($"  {label}: file={new FileInfo(file.Path).Length} bytes " +
                              $"format={info.SampleRate}Hz/{info.Channels}ch/{info.BitsPerSample}bit " +
                              $"duration={info.DurationSeconds:F2}s peak={info.Peak}");
            if (!formatOk || !lengthOk || (!hasSignal && !allowSilent))
            {
                Console.WriteLine($"  {label}: FAILED format-ok={formatOk} length-ok={lengthOk} has-signal={hasSignal}");
                failures++;
            }
            else if (!hasSignal)
            {
                Console.WriteLine($"  {label}: 无信号（该路这段时间没有声音，符合预期）");
            }
        }

        Console.WriteLine($"  两路文件均已产出 callbacks={Volatile.Read(ref callbacks)}");
        return failures == 0 ? 0 : 1;
    }

    private sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, double DurationSeconds, int Peak);

    /// <summary>极简 WAV 解析（只认标准 PCM 头），用于校验产物格式与内容幅度。</summary>
    private static WavInfo? ReadWav(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "RIFF") return null;
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") return null;

        int sampleRate = 0, channels = 0, bits = 0, dataBytes = 0;
        long dataStart = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (chunkId == "fmt ")
            {
                br.ReadInt16(); // audio format
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (chunkId == "data")
            {
                dataStart = fs.Position;
                long available = fs.Length - fs.Position;
                dataBytes = (int)Math.Min(size, available);
                break;
            }
            else
            {
                br.ReadBytes(size);
            }
        }

        double duration = sampleRate > 0 && channels > 0 && bits > 0
            ? dataBytes / (double)(sampleRate * channels * (bits / 8))
            : 0;

        int peak = 0;
        if (bits == 16 && dataBytes > 0)
        {
            fs.Position = dataStart;
            var samples = dataBytes / 2;
            for (int i = 0; i < samples; i++)
            {
                short v = br.ReadInt16();
                int abs = Math.Abs((int)v);
                if (abs > peak) peak = abs;
            }
        }
        return new WavInfo(sampleRate, channels, bits, duration, peak);
    }

    private static string Truncate(string s) => s.Length <= 46 ? s : s[..46] + "…";
}
