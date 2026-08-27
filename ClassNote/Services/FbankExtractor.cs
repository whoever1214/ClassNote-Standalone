using System;
using System.Numerics;

namespace ClassNote.Services;

/// <summary>
/// 复现 SenseVoice 前端（WavFrontend）的 fbank 特征提取 + LFR + CMVN。
/// 参数与 server/models/sensevoice-onnx/config.yaml 一致：
///   16kHz, hamming window, n_mels=80, frame 25ms / shift 10ms, LFR m=7 n=6。
/// </summary>
public static class FbankExtractor
{
    // ---- mel filterbank (Slaney mel) ----
    private const int NumBins = 80;
    private const int SampleRate = 16000;
    private const int FrameLength = 400;  // 25ms
    private const int FrameShift = 160;   // 10ms
    private const int FftSize = 512;      // pow2 >= 400
    private const double LowFreq = 20.0;
    private const double HighFreq = 0.0;  // 0 -> Nyquist (8000)

    // LFR
    private const int LfrM = 7;
    private const int LfrN = 6;

    private static double[][]? _melFilters;

    private static double MelScale(double freq) => 2595.0 * Math.Log10(1.0 + freq / 700.0);
    private static double MelToFreq(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    /// <summary>Slaney-style mel filterbank, shape [NumBins, FftSize/2+1].</summary>
    private static double[][] MelFilterbank()
    {
        int nFreq = FftSize / 2 + 1;
        double high = HighFreq > 0 ? HighFreq : SampleRate / 2.0;

        double melLow = MelScale(LowFreq);
        double melHigh = MelScale(high);

        var melPoints = new double[NumBins + 2];
        for (int i = 0; i < NumBins + 2; i++)
            melPoints[i] = melLow + (melHigh - melLow) * i / (NumBins + 1);

        var freqPoints = new double[NumBins + 2];
        for (int i = 0; i < NumBins + 2; i++)
            freqPoints[i] = MelToFreq(melPoints[i]);

        // bin center frequencies
        var bins = new double[NumBins + 2];
        for (int i = 0; i < NumBins + 2; i++)
            bins[i] = (FftSize + 1) * freqPoints[i] / SampleRate;  // FFT bin index (continuous)

        var filters = new double[NumBins][];
        for (int b = 0; b < NumBins; b++)
        {
            filters[b] = new double[nFreq];
            for (int f = 0; f < nFreq; f++)
            {
                double left = bins[b], center = bins[b + 1], right = bins[b + 2];
                double v;
                if (f <= left || f >= right) v = 0.0;
                else if (f <= center) v = (f - left) / (center - left);
                else v = (right - f) / (right - center);
                // area normalization (Slaney)
                double width = right - left;
                filters[b][f] = width > 0 ? v * 2.0 / width : 0.0;
            }
        }
        return filters;
    }

    private static double[][] EnsureFilters() => _melFilters ??= MelFilterbank();

    /// <summary>Bit-reversal in-place radix-2 FFT.</summary>
    private static void FftInPlace(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    /// <summary>Compute fbank features from 16kHz mono WAV (float samples). Shape [frames, 80].</summary>
    public static float[][] ComputeFbank(float[] samples)
    {
        int n = samples.Length;
        if (n < FrameLength)
            return Array.Empty<float[]>();

        // preemphasis
        var pre = new float[n];
        pre[0] = samples[0];
        for (int i = 1; i < n; i++)
            pre[i] = (float)(samples[i] - 0.97 * samples[i - 1]);

        int numFrames = 1 + (n - FrameLength) / FrameShift;
        var mel = EnsureFilters();
        var hamming = new double[FrameLength];
        for (int i = 0; i < FrameLength; i++)
            hamming[i] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (FrameLength - 1));

        var result = new float[numFrames][];
        var frame = new Complex[FftSize];

        for (int t = 0; t < numFrames; t++)
        {
            int baseIdx = t * FrameShift;
            double mean = 0;
            for (int i = 0; i < FrameLength; i++) mean += pre[baseIdx + i];
            mean /= FrameLength;  // remove DC offset

            double winNorm = 0;
            Array.Clear(frame, 0, FftSize);  // 每帧前先清零，避免 400..511 残留上一帧数据
            for (int i = 0; i < FrameLength; i++)
            {
                frame[i] = new Complex((pre[baseIdx + i] - mean) * hamming[i], 0);
                winNorm += hamming[i];
            }

            // FFT
            FftInPlace(frame);

            // power spectrum (one-sided)
            var power = new double[FftSize / 2 + 1];
            power[0] = frame[0].Magnitude * frame[0].Magnitude / FftSize;
            for (int k = 1; k < FftSize / 2; k++)
                power[k] = frame[k].Magnitude * frame[k].Magnitude / FftSize * 2.0;
            power[FftSize / 2] = frame[FftSize / 2].Magnitude * frame[FftSize / 2].Magnitude / FftSize;

            // mel filterbank + log
            var fbank = new float[NumBins];
            for (int b = 0; b < NumBins; b++)
            {
                double energy = 0;
                var m = mel[b];
                for (int k = 0; k <= FftSize / 2; k++)
                    energy += m[k] * power[k];
                fbank[b] = (float)Math.Log(Math.Max(energy, 1e-10));
            }
            result[t] = fbank;
        }
        return result;
    }

    /// <summary>LFR (m=7, n=6) 拼接 + CMVN 归一化，输出形状 [T', 560] 的 float 数组。</summary>
    public static float[][] ApplyLfr(float[][] fbank, float[] mean, float[] scale)
    {
        int T = fbank.Length;
        if (T < LfrM) return Array.Empty<float[]>();
        int newT = (T - LfrM) / LfrN + 1;
        var outFeats = new float[newT][];
        for (int i = 0; i < newT; i++)
        {
            var row = new float[LfrM * NumBins];
            for (int m = 0; m < LfrM; m++)
            {
                var src = fbank[i * LfrN + m];
                Array.Copy(src, 0, row, m * NumBins, NumBins);
            }
            // CMVN
            for (int k = 0; k < row.Length; k++)
                row[k] = (row[k] - mean[k]) / scale[k];
            outFeats[i] = row;
        }
        return outFeats;
    }
}
