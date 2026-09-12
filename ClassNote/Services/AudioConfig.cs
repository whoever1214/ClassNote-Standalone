namespace ClassNote.Services;

/// <summary>
/// 录音声音来源：
/// 麦克风（教室现场）、系统声音（设备外放/在线课程/视频）、或两者混合。
/// </summary>
public enum AudioSourceKind
{
    /// <summary>仅麦克风（默认，与旧版行为一致）。</summary>
    Microphone = 0,

    /// <summary>仅系统声音（WASAPI 回环采集扬声器输出）。</summary>
    System = 1,

    /// <summary>麦克风 + 系统声音混合成一路（现场人声与设备声同时保留）。</summary>
    Both = 2,
}

/// <summary>一次录音的采集配置（来源 + 设备稳定 ID）。</summary>
/// <param name="Source">采集来源。</param>
/// <param name="MicId">麦克风稳定 ID；空 = 系统默认采集设备。来源不需要麦克风时忽略。</param>
/// <param name="OutputDeviceId">扬声器/回环设备稳定 ID；空 = 系统默认播放设备。来源不需要系统声音时忽略。</param>
/// <param name="MicName">
/// 麦克风显示名（仅用于配置里 ID 缺失/失效时按名称回退匹配，选录始终以 MicId 为准）。
/// </param>
public sealed record RecordingConfig(
    AudioSourceKind Source = AudioSourceKind.Microphone,
    string? MicId = null,
    string? OutputDeviceId = null,
    string? MicName = null)
{
    /// <summary>默认配置：麦克风 + 系统默认设备。</summary>
    public static RecordingConfig Default { get; } = new();

    /// <summary>是否需要麦克风。</summary>
    public bool NeedsMicrophone => Source is AudioSourceKind.Microphone or AudioSourceKind.Both;

    /// <summary>是否需要系统声音（回环）。</summary>
    public bool NeedsSystemAudio => Source is AudioSourceKind.System or AudioSourceKind.Both;

    /// <summary>采集路数（1 或 2），用于资源预算与界面提示。</summary>
    public int ChannelCount => (NeedsMicrophone ? 1 : 0) + (NeedsSystemAudio ? 1 : 0);
}

/// <summary>声音来源的解析/显示文案（枚举与配置字符串之间的唯一转换点）。</summary>
public static class AudioSourceKinds
{
    /// <summary>来源下拉框的选项文案（顺序即界面顺序）。</summary>
    public static readonly string[] DisplayNames =
    {
        "麦克风（教室现场）",
        "系统声音（设备外放）",
        "麦克风 + 系统声音（混合）",
    };

    /// <summary>枚举 → 界面文案。</summary>
    public static string ToDisplayName(AudioSourceKind kind) => DisplayNames[(int)Normalize(kind)];

    /// <summary>界面文案 → 枚举；无法识别时回退麦克风（旧版默认行为）。</summary>
    public static AudioSourceKind FromDisplayName(string? displayName)
    {
        int idx = Array.IndexOf(DisplayNames, displayName ?? "");
        return idx >= 0 ? (AudioSourceKind)idx : AudioSourceKind.Microphone;
    }

    /// <summary>枚举 → 持久化字符串（写入 settings.json，取值稳定不随文案变化）。</summary>
    public static string ToStorage(AudioSourceKind kind) => Normalize(kind).ToString();

    /// <summary>持久化字符串 → 枚举；缺失/损坏时回退麦克风（兼容旧版 settings.json）。</summary>
    public static AudioSourceKind FromStorage(string? value)
        => Enum.TryParse<AudioSourceKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : AudioSourceKind.Microphone;

    private static AudioSourceKind Normalize(AudioSourceKind kind)
        => Enum.IsDefined(kind) ? kind : AudioSourceKind.Microphone;
}

/// <summary>设备下拉框的"按已保存偏好定位下标"逻辑（设置窗口与录音配置窗口共用，行为一致）。</summary>
public static class AudioDeviceSelection
{
    /// <summary>下拉框里代表"系统默认设备"的第一项文案。</summary>
    public const string SystemDefaultLabel = "（系统默认）";

    /// <summary>
    /// 按稳定 ID 优先、显示名兜底，反查设备在「含系统默认占位项」的下拉框中的下标。
    /// 查不到（设备已拔出/改名）时返回 0（系统默认项），绝不返回越界下标。
    /// </summary>
    public static int ResolveIndex(string[] ids, string[] names, string? savedId, string? savedName)
    {
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            int byId = Array.IndexOf(ids, savedId);
            if (byId > 0)
                return byId;
        }
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            int byName = Array.IndexOf(names, savedName);
            if (byName > 0)
                return byName;
        }
        return 0;
    }
}
