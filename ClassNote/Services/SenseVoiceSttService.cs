using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using Newtonsoft.Json;

namespace ClassNote.Services;

/// <summary>
/// 本地 STT（语音转文字）服务，基于 SenseVoice-Small ONNX (INT8) + onnxruntime（CPU，纯本地）。
/// 中文识别更准、自带标点，无需 Whisper.net 与 GGML 模型。
/// 模型文件位于 models/sensevoice/ 目录（model_quant.onnx / tokens.json / am.mvn）。
/// </summary>
public sealed class SenseVoiceSttService : ISttService
{
    private const int ChunkSeconds = 30;

    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly string _tokensPath;
    private readonly string _mvnPath;

    private InferenceSession? _session;
    private string[]? _tokens;
    private float[]? _mean;
    private float[]? _scale;
    private readonly object _lock = new();

    public SenseVoiceSttService()
    {
        // 模型目录：优先加载目录下的 "models/sensevoice"，否则用 %LOCALAPPDATA%/ClassNote/models/sensevoice
        string exeDir = AppContext.BaseDirectory;
        string bundled = Path.Combine(exeDir, "models", "sensevoice");
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote", "models", "sensevoice");

        _modelDir = Directory.Exists(bundled) ? bundled : appData;
        _modelPath = Path.Combine(_modelDir, "model_quant.onnx");
        _tokensPath = Path.Combine(_modelDir, "tokens.json");
        _mvnPath = Path.Combine(_modelDir, "am.mvn");
    }

    public bool IsModelReady =>
        File.Exists(_modelPath) && File.Exists(_tokensPath) && File.Exists(_mvnPath);

    public async Task<string> TranscribeAsync(string wavPath, IProgress<string>? progress = null)
    {
        if (!IsModelReady)
        {
            progress?.Report($"语音识别模型缺失（{_modelDir}）");
            return "";
        }
        if (!File.Exists(wavPath) || new FileInfo(wavPath).Length == 0)
            return "";

        progress?.Report("正在转写语音…");
        return await Task.Run(() => Transcribe(wavPath));
    }

    private string Transcribe(string wavPath)
    {
        EnsureLoaded();

        // 读 WAV -> 16kHz mono float
        float[] samples = ReadWav16kMono(wavPath);
        if (samples.Length < 200)
            return "";

        var feats = FbankExtractor.ComputeFbank(samples);
        if (feats.Length == 0)
            return "";

        int chunkFrames = ChunkSeconds * 100; // 100 fbank frames/second
        var cleanedChunks = new List<string>();
        var rawChunks = new List<string>();

        for (int start = 0; start < feats.Length; start += chunkFrames)
        {
            int end = Math.Min(start + chunkFrames, feats.Length);
            var block = new float[end - start][];
            Array.Copy(feats, start, block, 0, end - start);

            var lfr = FbankExtractor.ApplyLfr(block, _mean!, _scale!);
            if (lfr.Length == 0)
                continue;

            string raw = RunInference(lfr);
            rawChunks.Add(raw);
            cleanedChunks.Add(Clean(raw));
        }

        return string.Join(" ", cleanedChunks.Where(c => !string.IsNullOrWhiteSpace(c))).Trim();
    }

    private string RunInference(float[][] lfr)
    {
        int T = lfr.Length;
        int dim = lfr[0].Length;

        var speech = new DenseTensor<float>(new[] { 1, T, dim });
        for (int t = 0; t < T; t++)
            for (int d = 0; d < dim; d++)
                speech[0, t, d] = lfr[t][d];

        var lengths = new DenseTensor<int>(new[] { 1 });
        lengths[0] = T;
        var language = new DenseTensor<int>(new[] { 1 });
        language[0] = 0;
        var textnorm = new DenseTensor<int>(new[] { 1 });
        textnorm[0] = 14;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech", speech),
            NamedOnnxValue.CreateFromTensor("speech_lengths", lengths),
            NamedOnnxValue.CreateFromTensor("language", language),
            NamedOnnxValue.CreateFromTensor("textnorm", textnorm),
        };

        using var results = _session!.Run(inputs);
        var logits = results.First(r => r.Name == "ctc_logits").AsTensor<float>();
        var encLens = results.First(r => r.Name == "encoder_out_lens").AsTensor<int>();

        int outLen = Math.Min(encLens[0], logits.Dimensions[1]);
        int vocab = logits.Dimensions[2];

        var tokens = new List<string>();
        int prev = -1;
        for (int t = 0; t < outLen; t++)
        {
            int argmax = 0;
            float maxVal = float.MinValue;
            for (int v = 0; v < vocab; v++)
            {
                float val = logits[0, t, v];
                if (val > maxVal) { maxVal = val; argmax = v; }
            }
            if (argmax != prev && argmax != 0 && argmax < _tokens!.Length)
                tokens.Add(_tokens[argmax]);
            prev = argmax;
        }
        return string.Concat(tokens);
    }

    private static string Clean(string raw)
    {
        foreach (var marker in new[] { "<|withitn|>", "<|woitn|>" })
        {
            int idx = raw.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                return raw.Substring(idx + marker.Length).Trim();
        }
        return raw.Trim();
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_session != null)
                return;

            _session = new InferenceSession(_modelPath);

            var tokensJson = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(_tokensPath));
            _tokens = tokensJson!.ToArray();

            (_mean, _scale) = ParseMvn(File.ReadAllText(_mvnPath));
        }
    }

    private static (float[] mean, float[] scale) ParseMvn(string text)
    {
        float[] Parse(string tag)
        {
            int idx = text.IndexOf("<" + tag + ">", StringComparison.Ordinal);
            int ob = text.IndexOf('[', idx);
            int cb = text.IndexOf(']', ob);
            string body = text.Substring(ob + 1, cb - ob - 1);
            return body.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(float.Parse).ToArray();
        }
        return (Parse("AddShift"), Parse("Rescale"));
    }

    private static float[] ReadWav16kMono(string path)
    {
        using var reader = new WaveFileReader(path);
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;

        var bytes = new byte[reader.Length];
        int read = reader.Read(bytes, 0, bytes.Length);

        var samples = new float[read / 2];
        for (int i = 0; i + 1 < read; i += 2)
        {
            short s = BitConverter.ToInt16(bytes, i);
            samples[i / 2] = s / 32768f;
        }

        // 仅支持 16kHz 单声道；若不同，这里简化取原样（录音端已固定 16kHz mono）
        return samples;
    }
}
