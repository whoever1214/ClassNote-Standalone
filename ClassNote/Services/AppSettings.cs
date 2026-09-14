using System.IO;
using System.Text.Json;

namespace ClassNote.Services;

/// <summary>
/// 客户端本地应用设置（单机模式）。替代原服务端 .env 配置，持久化到
/// %LOCALAPPDATA%/ClassNote/settings.json。LLM API Key 由用户在客户端配置。
/// </summary>
public class AppSettingsData
{
    /// <summary>LLM API Key（用户配置，仅存本地）。</summary>
    public string LlmApiKey { get; set; } = "";

    /// <summary>OpenAI 兼容 API 基础地址，默认指向 DeepSeek。</summary>
    public string LlmBaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>对话/笔记生成模型。</summary>
    public string LlmModel { get; set; } = "deepseek-chat";

    /// <summary>可选：Vision 模型，用于截图补充理解。</summary>
    public string LlmVisionModel { get; set; } = "";

    /// <summary>
    /// LLM 请求超时（秒）。本地模型推理较慢时调大；默认 1800（30 分钟）。
    /// 0 或负数视为未配置，按默认值处理（兼容旧版 settings.json）。
    /// </summary>
    public int LlmTimeoutSeconds { get; set; } = 1800;

    /// <summary>定时记录总开关：开启后依据每周课表到点自动开始 / 结束录音。</summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>是否随 Windows 登录自启动并驻留托盘（定时记录需要后台运行）。</summary>
    public bool ScheduleLaunchAtStartup { get; set; }

    /// <summary>
    /// 默认声音来源（麦克风 / 系统声音 / 混合）。存枚举名而非界面文案，
    /// 文案随界面调整时不会让用户的选择失效；缺失或损坏时回退麦克风。
    /// </summary>
    public string RecordingSource { get; set; } = nameof(AudioSourceKind.Microphone);

    /// <summary>默认麦克风稳定 ID（空 = 系统默认采集设备）。手动录音与定时录音共用。</summary>
    public string RecordingMicId { get; set; } = "";

    /// <summary>默认麦克风显示名（仅用于设置界面展示，选录以 RecordingMicId 为准）。</summary>
    public string RecordingMicName { get; set; } = "";

    /// <summary>系统声音回环所用的播放设备稳定 ID（空 = 系统默认播放设备）。</summary>
    public string RecordingOutputDeviceId { get; set; } = "";

    /// <summary>播放设备显示名（仅用于设置界面展示，选录以 RecordingOutputDeviceId 为准）。</summary>
    public string RecordingOutputDeviceName { get; set; } = "";

    /// <summary>
    /// 边录边转写档位（关闭 / 自动 / 极速），存枚举名。
    ///
    /// 默认「自动」：课堂上边录边按块转写，但**永远只占一半逻辑核**，
    /// 并且检测到全屏放映 / 视频播放 / 电池供电时自动降速让路。
    /// 关闭时行为与 v0.6.0 完全一致（全部留到课后整文件转写）。
    /// </summary>
    public string ClassroomTranscription { get; set; } = nameof(ClassroomTranscriptionMode.Auto);

    /// <summary>
    /// 截图 OCR 引擎（内置 Windows OCR / 内网 PaddleOCR 服务），存枚举名。
    /// 旧 settings.json 没有这个字段 → 反序列化后是空串 → <see cref="OcrEngines.FromStorage"/> 回退内置引擎，
    /// 与升级前的行为完全一致。
    /// </summary>
    public string OcrEngine { get; set; } = nameof(OcrEngineKind.Windows);

    /// <summary>远程 OCR 服务地址（仅 <see cref="OcrEngineKind.PaddleHttp"/> 使用）。</summary>
    public string OcrServiceUrl { get; set; } = "";

    /// <summary>远程 OCR 的请求编码方式，存枚举名；必须与服务器实际部署方式一致。</summary>
    public string OcrRequestFormat { get; set; } = nameof(ClassNote.Services.OcrRequestFormat.JsonBase64);

    /// <summary>远程 OCR 服务的访问密钥（可留空；落盘前经 DPAPI 加密，与 LLM Key 同等对待）。</summary>
    public string OcrServiceApiKey { get; set; } = "";

    /// <summary>远程 OCR 单张截图的请求超时（秒）。</summary>
    public int OcrTimeoutSeconds { get; set; } = OcrEngines.DefaultPaddleTimeoutSeconds;

    /// <summary>
    /// 远程 OCR 失败（连不上 / 超时 / 返回结构无法解析）时是否回退到内置 Windows OCR。
    /// 默认开：宁可用精度差一点的本地结果，也不要因为内网服务抖动而让整节课的截图没有文字。
    /// </summary>
    public bool OcrFallbackToWindows { get; set; } = true;

    /// <summary>旧字段（v0.4.x 的「定时记录默认麦克风」）：只用于升级时迁移到录音设置，不再写入。</summary>
    [Obsolete("v0.5.0 起改用 RecordingMicId/RecordingMicName；此属性仅用于读取旧 settings.json 做迁移。")]
    public string ScheduleMicId { get; set; } = "";

    /// <summary>旧字段（v0.4.x 的「定时记录默认麦克风」显示名）：只用于升级时迁移，不再写入。</summary>
    [Obsolete("v0.5.0 起改用 RecordingMicId/RecordingMicName；此属性仅用于读取旧 settings.json 做迁移。")]
    public string ScheduleMicName { get; set; } = "";
}

/// <summary>
/// 应用设置存储服务：线程安全的读/写，序列化为 JSON 文件。
/// </summary>
public sealed class AppSettings
{
    private static readonly Lazy<AppSettings> _instance = new(() => new AppSettings());
    public static AppSettings Instance => _instance.Value;

    private readonly string _filePath;
    private readonly object _lock = new();
    private AppSettingsData _data;

    private AppSettings()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassNote");
        _filePath = Path.Combine(dir, "settings.json");
        _data = Load();
    }

    /// <summary>当前设置快照。修改属性后需调用 Save() 持久化。</summary>
    public AppSettingsData Data
    {
        get { lock (_lock) return Clone(_data); }
    }

    /// <summary>读取单个值（线程安全）。</summary>
    public AppSettingsData Snapshot()
    {
        lock (_lock) return Clone(_data);
    }

    /// <summary>原子更新并持久化。</summary>
    public void Update(Action<AppSettingsData> mutate)
    {
        lock (_lock)
        {
            var copy = Clone(_data);
            mutate(copy);
            Save(copy);
            _data = copy;
        }
    }

    public bool HasLlmKey => !string.IsNullOrWhiteSpace(Snapshot().LlmApiKey);

    /// <summary>
    /// 把设置里的录音项转成采集配置（手动录音与定时录音的唯一来源，
    /// 避免两处各自拼装导致行为不一致）。
    /// 注意必须带上 <c>RecordingMicName</c>：设备稳定 ID 失效（USB 换口、重装驱动、
    /// MME 回退下标漂移）时，采集侧只能靠"显示名"回退匹配——漏传这一项会让那条后路永久失效，
    /// 表现为"静默录到别的设备"。
    /// </summary>
    public static RecordingConfig ToRecordingConfig(AppSettingsData settings) => new(
        Source: AudioSourceKinds.FromStorage(settings.RecordingSource),
        MicId: string.IsNullOrWhiteSpace(settings.RecordingMicId) ? null : settings.RecordingMicId,
        OutputDeviceId: string.IsNullOrWhiteSpace(settings.RecordingOutputDeviceId) ? null : settings.RecordingOutputDeviceId,
        MicName: string.IsNullOrWhiteSpace(settings.RecordingMicName) ? null : settings.RecordingMicName);

    private AppSettingsData Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<AppSettingsData>(json);
                if (loaded != null)
                {
                    // 落盘的是 DPAPI 密文，读入时解密为内存明文（旧版明文兼容读取）
                    loaded.LlmApiKey = DpapiKeyProtector.Unprotect(loaded.LlmApiKey);
                    loaded.OcrServiceApiKey = DpapiKeyProtector.Unprotect(loaded.OcrServiceApiKey);
                    MigrateLegacyFields(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // 设置文件损坏时回退到默认值，不阻断应用启动
        }
        return new AppSettingsData();
    }

    /// <summary>
    /// 旧版字段迁移：v0.4.x 的「定时记录默认麦克风」升级为通用录音项。
    /// 只在新字段为空时搬运，避免覆盖用户已经保存过的新配置；迁移后清空旧字段，
    /// 下次保存即从 settings.json 中消失。public 是为了能被单元测试覆盖
    /// （静默丢失用户已选的麦克风属于升级事故，必须有回归测试）。
    /// </summary>
    public static void MigrateLegacyFields(AppSettingsData data)
    {
#pragma warning disable CS0618 // 迁移用的旧字段
        if (string.IsNullOrWhiteSpace(data.RecordingMicId) && !string.IsNullOrWhiteSpace(data.ScheduleMicId))
        {
            data.RecordingMicId = data.ScheduleMicId;
            if (string.IsNullOrWhiteSpace(data.RecordingMicName))
                data.RecordingMicName = data.ScheduleMicName;
        }
        data.ScheduleMicId = "";
        data.ScheduleMicName = "";
#pragma warning restore CS0618
    }

    private void Save(AppSettingsData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        // 落盘副本：Key 经 DPAPI 加密后写入，内存中的明文不受影响
        var forDisk = Clone(data);
        forDisk.LlmApiKey = DpapiKeyProtector.Protect(forDisk.LlmApiKey);
        forDisk.OcrServiceApiKey = DpapiKeyProtector.Protect(forDisk.OcrServiceApiKey);
        var json = JsonSerializer.Serialize(forDisk, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private static AppSettingsData Clone(AppSettingsData src) => new()
    {
        LlmApiKey = src.LlmApiKey,
        LlmBaseUrl = src.LlmBaseUrl,
        LlmModel = src.LlmModel,
        LlmVisionModel = src.LlmVisionModel,
        LlmTimeoutSeconds = src.LlmTimeoutSeconds,
        ScheduleEnabled = src.ScheduleEnabled,
        ScheduleLaunchAtStartup = src.ScheduleLaunchAtStartup,
        RecordingSource = src.RecordingSource,
        RecordingMicId = src.RecordingMicId,
        RecordingMicName = src.RecordingMicName,
        RecordingOutputDeviceId = src.RecordingOutputDeviceId,
        RecordingOutputDeviceName = src.RecordingOutputDeviceName,
        ClassroomTranscription = src.ClassroomTranscription,
        OcrEngine = src.OcrEngine,
        OcrServiceUrl = src.OcrServiceUrl,
        OcrRequestFormat = src.OcrRequestFormat,
        OcrServiceApiKey = src.OcrServiceApiKey,
        OcrTimeoutSeconds = src.OcrTimeoutSeconds,
        OcrFallbackToWindows = src.OcrFallbackToWindows,
    };
}
