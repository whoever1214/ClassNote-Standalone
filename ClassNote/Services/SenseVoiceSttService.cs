using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
///
/// **进程级单例（v0.7）**：模型 241MB、加载耗时实测 6.05s（真机冷盘可到 33s），
/// 内存占用约 600MB。旧实现每个录音会话都 new 一个实例，于是每次录音都要重付一次加载，
/// 且 ORT 的 InferenceSession 跨实例不释放（bench BT-1：3 个实例累积到 1.8GB）。
/// 现在全进程只有一个 <see cref="Shared"/> 实例，模型只在首次使用时加载一次。
/// </summary>
public sealed class SenseVoiceSttService : ISttService
{
    /// <summary>进程级共享实例（唯一的模型加载点）。</summary>
    public static SenseVoiceSttService Shared { get; } = new();

    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly string _tokensPath;
    private readonly string _mvnPath;
    private readonly int _intraOpThreads;

    /// <summary>闲置检查周期：每分钟看一眼（判断本身是常数时间，开小一点让释放更及时）。</summary>
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    private InferenceSession? _session;
    private string[]? _tokens;
    private float[]? _mean;
    private float[]? _scale;
    private Timer? _idleTimer;
    private readonly object _lock = new();

    /// <summary>
    /// 闲置释放器：空闲一段时间后把模型（约 600MB）还给操作系统，下次用到再加载。
    /// 托盘常驻的应用不该整天占着 600MB——尤其是 8GB 内存的机器。
    /// </summary>
    private readonly ModelIdleReleaser _releaser;

    /// <summary>闲置多久后释放模型；取消自动释放请设为 <see cref="TimeSpan.Zero"/>。</summary>
    public static readonly TimeSpan DefaultIdleReleaseAfter = TimeSpan.FromMinutes(10);

    public SenseVoiceSttService() : this(ClassroomPolicy.IntraOpThreads(Environment.ProcessorCount)) { }

    /// <summary>
    /// 显式指定 intra-op 线程数（测试与 devtools 用；生产路径走默认值——
    /// 默认值把"最多占一半逻辑核"这条课堂资源保证钉死在会话创建时）。
    /// </summary>
    public SenseVoiceSttService(int intraOpThreads)
    {
        _intraOpThreads = Math.Max(1, intraOpThreads);
        _releaser = new ModelIdleReleaser(ReleaseModelCore, idleAfter: DefaultIdleReleaseAfter);

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

    /// <summary>模型是否已经加载进内存（用于判断"现在停录是否还要等加载"）。</summary>
    public bool IsModelLoaded
    {
        get { lock (_lock) return _session != null; }
    }

    /// <summary>当前是否有推理在进行（诊断用；推理期间不会释放模型）。</summary>
    public bool IsTranscribing => _releaser.IsBusy;

    /// <summary>已经闲置释放过多少次（诊断用）。</summary>
    public long IdleReleaseCount => _releaser.ReleaseCount;

    /// <summary>闲置多久后释放模型（0 = 关闭自动释放）。</summary>
    public TimeSpan IdleReleaseAfter
    {
        get => _releaser.IdleAfter;
        set => _releaser.IdleAfter = value;
    }

    /// <summary>本实例使用的 intra-op 线程数（诊断/报告用）。</summary>
    public int IntraOpThreads => _intraOpThreads;

    /// <summary>
    /// 立刻释放模型（幂等）。已经释放过时返回 false。
    /// 通常不需要外部调用——闲置阈值到点会自动释放；这个方法供"我要它马上还内存"的场景使用。
    /// </summary>
    public bool ReleaseModelIfIdle() => _releaser.TryRelease();

    /// <summary>
    /// 预热：在后台线程提前把模型加载进内存。
    /// 录课堂音频时机器本来就有大把空闲，把 6–33s 的加载挪到"开始录音"之后，
    /// 而不是堆在"点结束"那一刻与转写抢时间。
    /// 另一条同样重要的路径：**闲置释放之后的下一次使用**——录音开始时会再次预热，
    /// 因此"释放"对用户是不可见的（不会在点结束录音时才等 6.7 秒）。
    /// 失败不影响后续（真正用到时会再试一次并由调用方汇报）。
    /// </summary>
    public Task PrewarmAsync()
        => Task.Run(() =>
        {
            _releaser.Touch();   // 预热本身算活动：否则刚预热完就可能被判定闲置
            try { EnsureLoaded(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[STT] 预热失败: {ex.Message}"); }
        });

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
        // 整段转写是一个"活动"：期间（可能几十秒到十几分钟）绝不能被闲置释放打断
        _releaser.EnterActivity();
        try
        {
            EnsureLoaded();

            // 读 WAV -> 16kHz mono float
            float[] samples = ReadWav16kMono(wavPath);
            if (samples.Length < 200)
                return "";

            return TranscribeAllChunks(samples);
        }
        finally
        {
            _releaser.ExitActivity();
        }
    }

    /// <summary>
    /// 整段音频的批量转写：按 <see cref="TranscriptionChunking"/> 逐块推理后拼接。
    /// 与边录边转写路径共用同一个 <see cref="TranscribeChunk"/>，两条路因此天然一致
    /// （这是 v0.7 "增量结果 = 批量结果"的保证方式：不是靠对拍，而是靠只有一份实现）。
    /// </summary>
    internal string TranscribeAllChunks(float[] samples)
    {
        long total = samples.Length;
        int chunks = TranscriptionChunking.ChunkCount(total);
        var cleaned = new List<string>(chunks);

        for (int i = 0; i < chunks; i++)
        {
            int start = TranscriptionChunking.ChunkStartSample(i);
            float? previous = start > 0 ? samples[start - 1] : null;
            var text = TranscribeChunk(samples, start,
                TranscriptionChunking.WindowSampleCount(i, total),
                previous,
                TranscriptionChunking.FramesInChunk(i, total));
            if (text != null)
                cleaned.Add(text);
        }

        return JoinChunks(cleaned);
    }

    /// <summary>
    /// 边录边转写的入口：窗口第 0 个采样就是块的第一个采样，窗口长度由调用方给出
    /// （缓冲区容量固定，有效长度才是真实数据量）。
    /// </summary>
    internal string? TranscribeChunkFromWindow(float[] window, int windowLength, float? previousSample, int frameCount)
        => TranscribeChunk(window, 0, windowLength, previousSample, frameCount);

    /// <summary>
    /// 对一个"块窗口"做推理。窗口内第 0 个采样必须正好是块的第一个采样
    /// （增量路径就是这样攒缓冲的），<paramref name="previousSample"/> 是块前一个采样。
    /// </summary>
    /// <returns>该块清洗后的文本；窗口不足一帧（<see cref="FbankExtractor.ApplyLfr"/> 返回空）时返回 null，
    /// 与批量路径里 <c>continue</c> 的语义一致（该块不参与拼接）。</returns>
    internal string? TranscribeChunk(float[] samples, int startSample, int windowCount, float? previousSample, int frameCount)
    {
        // 单块推理同样是一个活动。嵌套调用（整段 → 逐块）由计数器叠加，语义仍是"有推理在跑"。
        _releaser.EnterActivity();
        try
        {
            EnsureLoaded();

            var feats = FbankExtractor.ComputeFbank(samples, startSample, windowCount, previousSample);
            if (feats.Length == 0)
                return null;

            // 取该块的帧：窗口通常多算 1 帧（尾部多读了一个帧长），只取属于本块的 frameCount 帧
            int take = Math.Min(Math.Max(0, frameCount), feats.Length);
            if (take == 0)
                return null;

            var block = new float[take][];
            Array.Copy(feats, 0, block, 0, take);

            var lfr = FbankExtractor.ApplyLfr(block, EnsureMean(), EnsureScale());
            if (lfr.Length == 0)
                return null;

            return Clean(RunInference(lfr));
        }
        finally
        {
            _releaser.ExitActivity();
        }
    }

    /// <summary>
    /// 块文本拼接规则（**唯一的拼接出处**）：非空块之间用单个空格连接后 trim。
    /// 空块被丢弃；这是旧实现 <c>string.Join(" ", cleanedChunks.Where(非空))</c> 的原样保留。
    /// </summary>
    internal static string JoinChunks(IEnumerable<string> cleanedChunks)
        => string.Join(" ", cleanedChunks.Where(c => !string.IsNullOrWhiteSpace(c))).Trim();

    /// <summary>空转写的判定（"这一段没听到话"），增量路径据此决定要不要继续追加。</summary>
    internal static bool IsEmptyTranscript(string? text) => string.IsNullOrWhiteSpace(text);

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

        // 同一进程里可能有两条路（麦克风 / 系统声音）同时转写：ORT 的 Run 是线程安全的，
        // 但输出张量必须各自 using 释放，不能跨调用共享。
        using var results = Session.Run(inputs);
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

    /// <summary>已加载的会话（内部用；未加载时抛，调用方应先 EnsureLoaded）。</summary>
    private InferenceSession Session => _session!;

    private float[] EnsureMean()
    {
        EnsureLoaded();
        return _mean!;
    }

    private float[] EnsureScale()
    {
        EnsureLoaded();
        return _scale!;
    }

    /// <summary>
    /// 加载模型（幂等、线程安全）。会话选项刻意按"课堂不与放映抢 CPU"配置：
    /// · intra-op 线程数 = 一半逻辑核（<see cref="ClassroomPolicy.IntraOpThreads"/>），另一半永远留给前台应用；
    /// · 关闭线程池自旋（ORT 默认开着自旋等待，会在没人干活时也持续烧 CPU、抬功耗——
    ///   官方文档明确写这是"更快但消耗更多 CPU 与电量"的选项）；
    /// · inter-op 保持 1 线程 + 顺序执行：本模型几乎是单链，并行算子调度只会多抢核。
    ///
    /// 加载成功后拉起**闲置释放定时器**：托盘常驻的应用不该整天占着约 600MB。
    /// </summary>
    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_session != null)
                return;

            var options = new SessionOptions
            {
                IntraOpNumThreads = _intraOpThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

            _session = new InferenceSession(_modelPath, options);

            var tokensJson = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(_tokensPath));
            _tokens = tokensJson!.ToArray();

            (_mean, _scale) = ParseMvn(File.ReadAllText(_mvnPath));

            StartIdleTimer();
        }
    }

    /// <summary>
    /// 拉起闲置检查定时器（每分钟看一眼）。只在模型加载着的时候存在，释放时一并停掉。
    /// 回调里必须自己吞异常——<see cref="Timer"/> 回调抛出的异常会直接终止进程。
    /// </summary>
    private void StartIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = new Timer(_ =>
        {
            try { _releaser.TryRelease(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[STT] 闲置释放检查异常: {ex.Message}"); }
        }, null, IdleCheckInterval, IdleCheckInterval);
    }

    /// <summary>释放模型本体（由 <see cref="ModelIdleReleaser"/> 在"不忙且已闲置"时调用）。</summary>
    private void ReleaseModelCore()
    {
        lock (_lock)
        {
            if (_session == null)
                return;

            // ORT 的原生会话必须显式 Dispose，否则那约 600MB 不会还给操作系统
            try { _session.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[STT] 释放会话失败: {ex.Message}"); }

            _session = null;
            _tokens = null;
            _mean = null;
            _scale = null;

            _idleTimer?.Dispose();
            _idleTimer = null;

            System.Diagnostics.Debug.WriteLine(
                $"[STT] 已闲置释放模型（第 {_releaser.ReleaseCount + 1} 次）；下次使用时会重新加载。");
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
