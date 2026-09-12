// 开发辅助工具：真实设备上的录音冒烟验证（不入库）。
// 依次尝试「麦克风 / 系统声音 / 混合」三种来源，录 2 秒后校验产物 WAV 的
// 格式（16kHz 单声道 PCM16）、时长与是否有声音信号。
// 用法：AudioProbe [输出目录] [每种来源录音秒数]
using System.IO;
using ClassNote.Services;

public static class Program
{
    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "classnote-audio-probe");
        double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 2.0;
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
        failures += Run(audio, outDir, seconds, "mic", new RecordingConfig(AudioSourceKind.Microphone));
        failures += Run(audio, outDir, seconds, "system", new RecordingConfig(AudioSourceKind.System));
        failures += Run(audio, outDir, seconds, "both", new RecordingConfig(AudioSourceKind.Both));

        Console.WriteLine(failures == 0 ? "AUDIO_PROBE_OK" : $"AUDIO_PROBE_FAILURES={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int Run(AudioService audio, string outDir, double seconds, string tag, RecordingConfig config)
    {
        var path = Path.Combine(outDir, $"{tag}.wav");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        Console.WriteLine($"--- {tag}: source={config.Source} channels={config.ChannelCount} ---");
        int frames = 0;
        audio.AudioDataAvailable += (_, _) => Interlocked.Increment(ref frames);

        if (!audio.StartRecording(path, config))
        {
            Console.WriteLine($"  START_FAILED: {audio.LastError}");
            return 1;
        }

        Thread.Sleep((int)(seconds * 1000));
        audio.StopRecording();

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
        bool lengthOk = Math.Abs(info.DurationSeconds - seconds) < 1.0;
        bool hasSignal = info.Peak > 0;
        Console.WriteLine($"  file={new FileInfo(path).Length} bytes format={info.SampleRate}Hz/{info.Channels}ch/" +
                          $"{info.BitsPerSample}bit duration={info.DurationSeconds:F2}s peak={info.Peak} " +
                          $"frames(20ms)={Volatile.Read(ref frames)}");
        Console.WriteLine($"  format-ok={formatOk} length-ok={lengthOk} has-signal={hasSignal}");
        return formatOk && lengthOk ? 0 : 1;
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
