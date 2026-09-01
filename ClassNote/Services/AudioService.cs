using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassNote.Services;

/// <summary>
/// 录音采集服务：WASAPI 优先（现代 Windows 上 USB 外接麦克风以 WASAPI 暴露最可靠，
/// 且设备带稳定 ID 可精确选择），MME（WaveInEvent）作为回退。
/// 输出统一为 16kHz 单声道 PCM16 WAV（STT 管线要求）。
/// WASAPI 路径通过 AutoConvertPcm 让音频引擎完成重采样，避免自建重采样链
/// （NAudio 的 WdlResamplingSampleProvider 在输入耗尽时会无限补零产出，不能用于流式录制）。
/// </summary>
public class AudioService : IAudioService
{
    private IWaveIn? _recorder;
    private WaveFileWriter? _writer;
    private string? _outputPath;
    private string? _lastError;
    private readonly object _writeLock = new();

    public event EventHandler<byte[]>? AudioDataAvailable;

    /// <summary>最近一次 StartRecording 失败的原因；成功时为 null。</summary>
    public string? LastError => _lastError;

    /// <summary>可用的录音设备显示名，与 GetInputDeviceIds() 一一对应。</summary>
    public string[] GetInputDevices() => GetDevices().Select(d => d.Name).ToArray();

    /// <summary>
    /// 与 GetInputDevices() 一一对应的稳定设备标识：
    /// WASAPI 设备为其稳定 ID（形如 {0.0.1.00000000}.{GUID}），
    /// MME 回退设备为合成标识 "mme:{index}"。
    /// </summary>
    public string[] GetInputDeviceIds() => GetDevices().Select(d => d.Id).ToArray();

    /// <summary>
    /// 按稳定设备标识开始录音。设备不可用时尝试回退到系统默认采集设备，
    /// 仍失败则返回 false 并通过 LastError 说明原因（避免"选了 USB 麦克风却录到内置麦"的静默错配）。
    /// </summary>
    public bool StartRecording(string outputPath, string deviceId)
    {
        StopRecording(); // 清理可能残留的旧录制
        _lastError = null;
        _outputPath = outputPath;

        try
        {
            if (TryStartWasapi(outputPath, deviceId))
                return true;
        }
        catch (Exception ex)
        {
            _lastError = "WASAPI 采集启动失败: " + ex.Message;
        }

        try
        {
            if (TryStartMme(outputPath, deviceId))
                return true;
            _lastError = (_lastError == null ? "" : _lastError + "；")
                       + "MME 回退失败: "
                       + (string.IsNullOrEmpty(LastMmeError) ? "无法启动所选设备" : LastMmeError);
        }
        catch (Exception ex)
        {
            _lastError = (_lastError == null ? "" : _lastError + "；")
                       + "MME 回退失败: " + ex.Message;
        }

        StopRecording();
        return false;
    }

    /// <summary>按 MME 设备索引启动（兼容旧调用方；内部转为 mme:{index} 标识）。</summary>
    public bool StartRecording(string outputPath, int deviceIndex = 0)
        => StartRecording(outputPath, "mme:" + deviceIndex);

    public void StopRecording()
    {
        // 1. 先请求停止：WasapiCapture.StopRecording() 仅置状态立即返回；
        //    WaveInEvent.StopRecording() 会等采集线程收尾（通常很快）。
        var recorder = _recorder;
        _recorder = null;
        try { recorder?.StopRecording(); } catch { /* 停止失败不影响清理 */ }

        // 2. Dispose 放到后台线程执行：NAudio WasapiCapture.Dispose() 会 Join 采集线程，
        //    部分机器（异常音频端点/虚拟声卡）上 audioClient.Stop() 可能永久阻塞，
        //    不能占用 UI 线程（录音停止流程在 UI 线程上调用本方法）。
        if (recorder != null)
        {
            var captured = recorder;
            _ = Task.Run(() =>
            {
                try { captured.Dispose(); } catch { /* 后台清理失败不致命 */ }
            });
        }

        // 3. 与采集线程通过同一把锁串行化，关闭 writer（WaveFileWriter.Dispose 会补写 WAV 头）。
        //    采集线程已被请求停止，此后最多还有一个在途数据块，由锁保证不会写到已关闭的 writer。
        lock (_writeLock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    public byte[] GetFileBytes() => _outputPath != null && File.Exists(_outputPath) ? File.ReadAllBytes(_outputPath) : Array.Empty<byte>();

    public string? GetOutputPath() => _outputPath;

    public void Dispose() { StopRecording(); }

    // ── 设备枚举 ──────────────────────────────────────────────

    private static List<AudioDeviceInfo> GetDevices()
    {
        var list = new List<AudioDeviceInfo>();

        // WASAPI：稳定 ID + 友好名（USB 麦克风、OPS 一体机声卡等现代设备首选）
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (dev == null) continue;
                var name = dev.FriendlyName;
                if (string.IsNullOrWhiteSpace(name)) name = "录音设备";
                list.Add(new AudioDeviceInfo(dev.ID, name));
            }
        }
        catch { /* WASAPI 不可用时回退 MME */ }

        if (list.Count > 0)
            return list;

        // MME 回退：设备无稳定 ID，用 "mme:{index}" 合成
        int count;
        try { count = WaveInEvent.DeviceCount; }
        catch { return list; }
        for (int i = 0; i < count; i++)
        {
            string name;
            try { name = WaveInEvent.GetCapabilities(i).ProductName; }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(name)) name = "录音设备 " + (i + 1);
            list.Add(new AudioDeviceInfo("mme:" + i, name));
        }
        return list;
    }

    // ── WASAPI 采集（首选）────────────────────────────────────

    private bool TryStartWasapi(string outputPath, string deviceId)
    {
        bool hasExplicitId = !string.IsNullOrWhiteSpace(deviceId)
                             && !deviceId.StartsWith("mme:", StringComparison.Ordinal);
        if (!hasExplicitId && !string.IsNullOrWhiteSpace(deviceId))
            return false; // 明确是 MME 标识，交给 MME 路径

        using var enumerator = new MMDeviceEnumerator();

        MMDevice? device = null;
        if (hasExplicitId)
        {
            try { device = enumerator.GetDevice(deviceId); }
            catch { device = null; }
        }

        if (device == null)
        {
            // 指定的 USB 设备已拔出 / 未指定：回退系统默认采集设备（尽力而为，不静默录错设备）
            try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
            catch
            {
                try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
                catch { device = null; }
            }
            if (device == null)
            {
                _lastError = "未找到可用录音设备（默认设备不可用）";
                return false;
            }
        }

        var capture = new WasapiCapture(device, useEventSync: false);
        // 请求 16kHz 单声道 PCM16：WasapiCapture 默认带 AutoConvertPcm 标志，
        // 共享模式下由 WASAPI 引擎完成重采样/混音，数据块直接落盘，无需自建重采样链。
        capture.WaveFormat = new WaveFormat(16000, 16, 1);
        _recorder = capture;
        _writer = new WaveFileWriter(outputPath, capture.WaveFormat);

        capture.DataAvailable += OnWasapiDataAvailable;
        try
        {
            capture.StartRecording();
            return true;
        }
        catch
        {
            // 启动失败：释放本次残留状态，避免影响后续 MME 回退
            try { capture.DataAvailable -= OnWasapiDataAvailable; } catch { }
            try { capture.Dispose(); } catch { }
            _recorder = null;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            throw;
        }
    }

    private void OnWasapiDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;
        AudioDataAvailable?.Invoke(this, e.Buffer);
        lock (_writeLock)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    // ── MME 采集（回退）───────────────────────────────────────

    private string LastMmeError = "";

    private bool TryStartMme(string outputPath, string deviceId)
    {
        LastMmeError = "";
        int index = 0;
        if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.StartsWith("mme:", StringComparison.Ordinal))
        {
            if (!int.TryParse(deviceId.AsSpan(4), out index))
                index = -1;
        }
        else if (!string.IsNullOrWhiteSpace(deviceId) && int.TryParse(deviceId, out var parsed))
        {
            index = parsed;
        }

        int count;
        try { count = WaveInEvent.DeviceCount; }
        catch (Exception ex) { LastMmeError = "无法枚举 MME 设备: " + ex.Message; return false; }

        if (index < 0 || index >= count)
        {
            LastMmeError = $"录音设备索引 {index} 超出范围（当前 {count} 个设备）";
            return false;
        }

        var recorder = new WaveInEvent
        {
            DeviceNumber = index,
            WaveFormat = new WaveFormat(16000, 1),
        };
        recorder.DataAvailable += (_, e) =>
        {
            AudioDataAvailable?.Invoke(this, e.Buffer);
            lock (_writeLock)
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            }
        };
        _recorder = recorder;
        _writer = new WaveFileWriter(outputPath, recorder.WaveFormat);
        try
        {
            recorder.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            LastMmeError = ex.Message;
            try { recorder.Dispose(); } catch { }
            _recorder = null;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            return false;
        }
    }

    private sealed record AudioDeviceInfo(string Id, string Name);
}
