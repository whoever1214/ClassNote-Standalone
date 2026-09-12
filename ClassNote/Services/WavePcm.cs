using System.IO;

namespace ClassNote.Services;

/// <summary>WAV 里 PCM 数据的位置与规模。</summary>
/// <param name="DataOffset">data 块负载的起始字节偏移。</param>
/// <param name="DataBytes">可用的负载字节数。</param>
/// <param name="SampleCount">可用采样数（单声道 16bit = 字节数 / 2）。</param>
public sealed record WavePcmInfo(int DataOffset, long DataBytes, long SampleCount);

/// <summary>
/// 按需读取 WAV 里的 PCM 片段（16kHz 单声道 PCM16）。
///
/// 为什么需要它：课后补算只需要"缺的那一块"，若为此把整段 90 分钟音频读成 float[]
/// 就是 345MB 的瞬时占用（旧实现正是如此）。这里改成按块 seek + 只读需要的窗口，
/// 内存占用与音频时长无关。
/// </summary>
public static class WavePcm
{
    /// <summary>采样率（录音端固定 16kHz）。</summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// 解析 WAV 头，定位 data 块。
    /// **只信文件长度、不信头部声明的长度**：录音文件在写入过程中头部长度是 0（NAudio 在
    /// Dispose 时才回填），崩溃/断电残留的文件头部也可能与实际长度不符，而以实际字节数为准
    /// 永远不会把真实存在的音频当成不存在。声明值比实际短时取声明值（多出来的可能是别的东西）。
    /// </summary>
    public static bool TryReadInfo(string path, out WavePcmInfo info)
    {
        info = new WavePcmInfo(0, 0, 0);
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 12)
                return false;
            if (new string(br.ReadChars(4)) != "RIFF")
                return false;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE")
                return false;

            while (fs.Position + 8 <= fs.Length)
            {
                var chunkId = new string(br.ReadChars(4));
                int declared = br.ReadInt32();
                if (declared < 0)
                    return false;

                if (chunkId == "data")
                {
                    long available = fs.Length - fs.Position;
                    long usable = declared > 0 ? Math.Min(declared, available) : available;
                    if (usable <= 0)
                        return false;
                    info = new WavePcmInfo((int)fs.Position, usable, usable / 2);
                    return true;
                }

                fs.Position += declared + (declared % 2); // 块长按偶数字节对齐
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>该文件是不是"能按块读"的 PCM16 单声道 16kHz（否则调用方应走旧的整文件路径）。</summary>
    public static bool IsSupportedFormat(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 12) return false;
            if (new string(br.ReadChars(4)) != "RIFF") return false;
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") return false;

            while (fs.Position + 8 <= fs.Length)
            {
                var chunkId = new string(br.ReadChars(4));
                int size = br.ReadInt32();
                if (size < 0) return false;

                if (chunkId == "fmt ")
                {
                    int format = br.ReadInt16();
                    int channels = br.ReadInt16();
                    int sampleRate = br.ReadInt32();
                    return format == 1 && channels == 1 && sampleRate == SampleRate;
                }

                fs.Position += size + (size % 2);
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// 读取一个窗口：[<paramref name="startSample"/>, startSample + <paramref name="sampleCount"/>)。
    /// </summary>
    /// <param name="previousSample">
    /// 窗口前一个采样（startSample &gt; 0 时用于预加重；越界或读不到时为 null）。
    /// 预加重 x[i] - 0.97*x[i-1] 会跨块传染，漏掉它会让每块首帧差一个采样。
    /// </param>
    /// <returns>实际读到的采样数组（可能短于请求量——音频末尾）。</returns>
    public static float[] ReadWindow(string path, WavePcmInfo info, long startSample, int sampleCount,
        out float? previousSample)
    {
        previousSample = null;
        if (sampleCount <= 0 || startSample < 0 || startSample >= info.SampleCount)
            return Array.Empty<float>();

        long available = info.SampleCount - startSample;
        int want = (int)Math.Min(sampleCount, available);
        if (want <= 0)
            return Array.Empty<float>();

        using var fs = File.OpenRead(path);
        long byteOffset = info.DataOffset + startSample * 2L;

        // 窗口前一个采样：与窗口一起读（一次 IO 拿两段数据，避免为 1 个采样多开一次流）
        long readFrom = startSample > 0 ? byteOffset - 2 : byteOffset;
        int extra = startSample > 0 ? 1 : 0;

        fs.Seek(readFrom, SeekOrigin.Begin);
        var bytes = new byte[(want + extra) * 2];
        int read = ReadFully(fs, bytes, bytes.Length);
        if (read < 2)
            return Array.Empty<float>();

        int samplesRead = read / 2;
        int skip = extra == 1 && samplesRead > 1 ? 1 : 0;
        if (skip == 1)
            previousSample = BitConverter.ToInt16(bytes, 0) / 32768f;

        int resultCount = samplesRead - skip;
        var samples = new float[resultCount];
        for (int i = 0; i < resultCount; i++)
            samples[i] = BitConverter.ToInt16(bytes, (skip + i) * 2) / 32768f;

        return samples;
    }

    /// <summary>整段读成 float[]（与 <see cref="SenseVoiceSttService"/> 的批量路径口径一致）。</summary>
    public static float[] ReadAll(string path, WavePcmInfo info)
    {
        var samples = new float[info.SampleCount];
        using var fs = File.OpenRead(path);
        fs.Seek(info.DataOffset, SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(info.DataBytes, 1 << 20)];
        int index = 0;
        while (index < samples.Length)
        {
            int want = (int)Math.Min(bytes.Length, (samples.Length - index) * 2L);
            int read = ReadFully(fs, bytes, want);
            if (read <= 0)
                break;
            for (int i = 0; i + 1 < read; i += 2)
                samples[index++] = BitConverter.ToInt16(bytes, i) / 32768f;
        }
        return samples;
    }

    /// <summary>把 PCM16 字节转成 float 采样（与写盘时的转换口径一致：<c>s / 32768f</c>）。</summary>
    public static int ConvertToFloat(byte[] pcm16, int byteCount, float[] destination, int destinationOffset)
    {
        int samples = Math.Min(byteCount, destination.Length - destinationOffset) / 2;
        samples = Math.Min(samples, pcm16.Length / 2);
        for (int i = 0; i < samples; i++)
            destination[destinationOffset + i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
        return samples;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }
}
