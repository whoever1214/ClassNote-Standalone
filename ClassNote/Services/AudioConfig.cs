namespace ClassNote.Services;

/// <summary>
/// 录音声音来源（v0.6.0 起固定为三种）：
/// 仅麦克风（教室现场）、仅系统声音（设备外放 / 在线课程 / 视频）、麦克风和系统声音（分轨录制，见 RecordingAudio）。
/// </summary>
public enum AudioSourceKind
{
    /// <summary>仅麦克风（默认，与旧版行为一致）。</summary>
    Microphone = 0,

    /// <summary>仅系统声音（WASAPI 回环采集所选播放设备的输出）。</summary>
    System = 1,

    /// <summary>
    /// 麦克风 + 系统声音。**分轨存两个 WAV 文件**（各 16kHz 单声道），逐路转写后带来源标注交给 LLM，
    /// 使模型能分辨"老师现场说的"与"课件/网课里播的"；混成一路再拆是做不到的。
    /// </summary>
    Both = 2,
}

/// <summary>录音产物的一路来源（用于给转写结果打标注，让 LLM 分得清谁在说话）。</summary>
public enum RecordingAudioSource
{
    /// <summary>麦克风：教室现场人声。</summary>
    Microphone = 0,

    /// <summary>系统声音：课件 / 在线课程 / 视频里播出的声音。</summary>
    System = 1,
}

/// <summary>
/// 一次录音落盘的音频文件集合。「麦克风和系统声音」来源会分轨存两个文件，
/// 交由 STT 分别转写后带来源标注送进提示词。
/// </summary>
/// <param name="Files">各来源对应的 WAV 路径（顺序即转写顺序）。</param>
public sealed record RecordingAudio(IReadOnlyList<RecordingAudioFile> Files)
{
    /// <summary>无音频（未采集到任何来源）时的空集合。</summary>
    public static RecordingAudio None { get; } = new(Array.Empty<RecordingAudioFile>());

    /// <summary>是否没有任何音频文件。</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>麦克风那一路的路径（没有则为 null）。</summary>
    public string? MicrophonePath
        => Files.FirstOrDefault(f => f.Source == RecordingAudioSource.Microphone)?.Path;

    /// <summary>系统声音那一路的路径（没有则为 null）。</summary>
    public string? SystemPath
        => Files.FirstOrDefault(f => f.Source == RecordingAudioSource.System)?.Path;
}

/// <summary>单个来源的录音文件。</summary>
/// <param name="Source">这一路是哪来的。</param>
/// <param name="Path">WAV 路径。</param>
public sealed record RecordingAudioFile(RecordingAudioSource Source, string Path);

/// <summary>
/// 多来源转写在提示词里的分区标注。定义集中在此，保证"生成端拼出来的标记"
/// 与"提示词端识别/叮嘱的标记"永远一致（拼错一个字就会静默退化成不给标注）。
/// </summary>
public static class TranscriptSections
{
    /// <summary>麦克风那一路的标注行。</summary>
    public const string MicrophoneLabel = "【麦克风（教室现场）】";

    /// <summary>系统声音那一路的标注行。</summary>
    public const string SystemLabel = "【系统声音（课件/网课播放）】";

    /// <summary>某一路的标注行文案。</summary>
    public static string LabelFor(RecordingAudioSource source)
        => source == RecordingAudioSource.System ? SystemLabel : MicrophoneLabel;

    /// <summary>
    /// 把多路转写拼成带来源标注的分区文本。**只保留非空的一路**：
    /// 空分区的标注行会让模型以为"那一侧确实没内容"，反而干扰判断。
    /// </summary>
    public static string Render(IReadOnlyList<TranscriptTrack> tracks)
    {
        var parts = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => LabelFor(t.Source) + "\n" + t.Text.Trim())
            .ToArray();
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// 判断一段转写是否是"多来源标注文本"（两个标注行都出现才算）。
    /// 提示词据此决定是否加入"分辨谁在说话"的要求。
    /// </summary>
    public static bool IsMultiSource(string? transcript)
        => !string.IsNullOrEmpty(transcript)
           && transcript.Contains(MicrophoneLabel, StringComparison.Ordinal)
           && transcript.Contains(SystemLabel, StringComparison.Ordinal);

    /// <summary>多来源时给模型的分辨要求（单来源录音不适用，故按需注入）。</summary>
    public const string MultiSourceRule =
        "6. 分辨来源：素材按【麦克风（教室现场）】与【系统声音（课件/网课播放）】分区给出，" +
        "两侧各自独立转写、没有逐句对齐关系，不要假设同一位置的句子在讲同一件事。" +
        "凡涉及来源（老师现场讲的、课件上写的、视频里播放的），必须用「现场」「课件」这类来源词标明，" +
        "不要混为一谈；两边出现同一内容时保留信息更完整的表述并注明来源，不要当成两个知识点。\n";
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
    /// <summary>
    /// 来源下拉框的选项文案（顺序即界面顺序，必须与 <see cref="AudioSourceKind"/> 的取值一一对应——
    /// 设置窗口用 <c>SelectedIndex</c> 直接映射枚举，顺序错位会静默录错来源）。
    /// </summary>
    public static readonly string[] DisplayNames =
    {
        "仅麦克风",
        "仅系统声音",
        "麦克风和系统声音",
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
    /// 按稳定 ID 优先、显示名兜底，反查设备在下拉框中的下标。
    /// 查不到（设备已拔出/改名）时返回 0，绝不返回越界下标。
    ///
    /// ⚠️ 关于 0 的两层含义：设置窗口的列表**第 0 项就是「（系统默认）」占位项**，
    /// 此时 0 表示"用系统默认设备"；录音配置窗口用的是**没有占位项**的列表，
    /// 此时 0 恰好是列表里的第一个真实设备。两种用法当前都能得到正确结果，
    /// 但这依赖"没命中就回退到 0"这条约定——所以这里刻意写 <c>&gt; 0</c> 而不是 <c>&gt;= 0</c>。
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
