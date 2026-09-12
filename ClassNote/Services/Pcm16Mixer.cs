using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace ClassNote.Services;

/// <summary>
/// 把多路 16kHz 单声道 PCM16 按样本对齐相加（带饱和截断），**直接产出 PCM16 字节**。
///
/// 为什么不再实现 <see cref="ISampleProvider"/>：该接口的契约是 [-1,1] 浮点，而落盘需要的是
/// int16 字节。走"int16 → 浮点 → 按字节还原 int16"的往返不但白费 CPU，还曾是 v0.5.0 起
/// "混音录音被录成噪声"的根因——当时把浮点缓冲的前一半**字节**当 PCM16 写盘，
/// 写下去的是 IEEE-754 位模式而不是音频。这里直接在整数域完成相加与截断，
/// 转换与写盘之间不再有可误解的中间表示。
///
/// 可测性：本类型只依赖 NAudio 的 <see cref="BufferedWaveProvider"/>（可在单测里直接构造），
/// 因此"混出来的字节到底对不对"不再需要真实声卡才能验证。
/// </summary>
internal sealed class Pcm16Mixer
{
    private readonly IReadOnlyList<BufferedWaveProvider> _channels;
    private readonly byte[] _scratch = new byte[64 * 1024];

    public Pcm16Mixer(IReadOnlyList<BufferedWaveProvider> channels) => _channels = channels;

    /// <summary>
    /// 读满 <paramref name="countSamples"/> 个样本写入 <paramref name="destination"/>，
    /// 返回写入的字节数（= countSamples × 2；数据不足的通道按静音补零，
    /// 保证录音时长与真实时钟一致，不会因为某一路的静音期把整体"拖住"）。
    /// </summary>
    public int Read(byte[] destination, int offset, int countSamples)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (countSamples <= 0) return 0;

        int neededBytes = countSamples * 2; // 各路缓冲都是 16bit 单声道
        var accumulator = new int[countSamples];

        foreach (var channel in _channels)
        {
            int remaining = neededBytes;
            int samples = 0;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, _scratch.Length);
                chunk -= chunk % 2; // 只按整样本搬，残字节留下次
                if (chunk <= 0)
                    break;

                int read = channel.Read(_scratch, 0, chunk);
                if (read <= 0)
                    break;

                for (int i = 0; i + 1 < read; i += 2)
                {
                    int idx = samples + i / 2;
                    if (idx >= countSamples)
                        break;
                    accumulator[idx] += BitConverter.ToInt16(_scratch, i);
                }
                samples += read / 2;
                remaining -= read;
            }
        }

        // 相加可能溢出 int16（两路各自接近满量程）：夹到 short 范围而不是回绕，
        // 回绕会把"很响"变成"很响的噪声"
        for (int i = 0; i < countSamples; i++)
        {
            int value = Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
            destination[offset + i * 2] = (byte)(value & 0xFF);
            destination[offset + i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return neededBytes;
    }
}
