using System.Windows;
using System.Windows.Controls;
using ClassNote.Services;

namespace ClassNote.Views;

/// <summary>
/// Modal configuration dialog shown before recording starts.
/// Lets the user confirm the course, enter an optional title, and pick the sound source
/// (microphone / system audio) plus the exact device.
/// </summary>
public partial class RecordingSetupWindow : Window
{
    private readonly string[] _micIds;
    private readonly string[] _micNames;
    private readonly string[] _outputIds;
    private readonly string[] _outputNames;

    /// <summary>打开弹窗时的初始配置（来自「设置 → 录音设置」）。用于保留用户未在本次弹窗中改动的那一路设备。</summary>
    private readonly RecordingConfig? _initial;
    private bool _suppressSourceChanged;

    /// <summary>Result after the user clicks "开始录制". Null if canceled.</summary>
    public RecordingSetupResult? Result { get; private set; }

    public RecordingSetupWindow(
        string currentCourse,
        string[] courses,
        string[] mics,
        string[]? micIds = null,
        string[]? outputs = null,
        string[]? outputIds = null,
        RecordingConfig? initial = null)
    {
        InitializeComponent();
        _initial = initial;
        _micNames = mics;
        _outputNames = outputs ?? Array.Empty<string>();
        // 名称与稳定 ID 必须一一对应：调用方漏传/长度不符时用空 ID 补齐，
        // 否则设备列表有名字却选不中任何一项（下拉框渲染为空白）
        _micIds = NormalizeIds(micIds, _micNames.Length);
        _outputIds = NormalizeIds(outputIds, _outputNames.Length);

        CourseCombo.ItemsSource = courses;
        int courseIdx = Array.IndexOf(courses, currentCourse);
        CourseCombo.SelectedIndex = courseIdx >= 0 ? courseIdx : 0;

        SourceCombo.ItemsSource = AudioSourceKinds.DisplayNames;
        _suppressSourceChanged = true;
        SourceCombo.SelectedIndex = (int)(initial?.Source ?? AudioSourceKind.Microphone);
        _suppressSourceChanged = false;

        // 麦克风下拉：无"系统默认"占位项——录音页的默认值本身就是系统默认设备
        DeviceCombo.ItemsSource = mics;
        ApplySource(initial);
    }

    /// <summary>当前选中的声音来源。</summary>
    private AudioSourceKind SelectedSource => (AudioSourceKind)Math.Max(0, SourceCombo.SelectedIndex);

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceChanged)
            return;
        // 传 _initial 而不是 null：用户在这里改选来源时，"另一路"的设备仍要沿用
        // 「设置 → 录音设置」里保存的值。传 null 会让下拉框回到第 0 项（= 系统默认设备），
        // 也就是把"用户配好的播放设备"丢掉——恰好复现"回环录成静音"这个本版要修的缺陷。
        ApplySource(_initial);
    }

    /// <summary>
    /// 按来源切换设备选择器：
    /// · 仅麦克风 → 列输入设备；
    /// · 仅系统声音 → 列播放设备（回环来源）；
    /// · 麦克风和系统声音 → 列播放设备（回环需要显式指定"哪个设备在出声"，
    ///   选错就录成静音），麦克风沿用「设置 → 录音设置」里的默认设备，不再占用一列。
    /// </summary>
    private void ApplySource(RecordingConfig? initial)
    {
        var source = SelectedSource;
        bool usesMicList = source is AudioSourceKind.Microphone;

        DeviceLabel.Text = usesMicList ? "麦克风" : "系统声音来源设备";
        DeviceCombo.ItemsSource = usesMicList ? _micNames : _outputNames;
        var ids = usesMicList ? _micIds : _outputIds;
        var names = usesMicList ? _micNames : _outputNames;

        // 预设设备：麦克风来源按传入配置；系统声音来源（含"麦克风和系统声音"）按已保存的播放设备
        int index = AudioDeviceSelection.ResolveIndex(ids, names,
            usesMicList ? initial?.MicId : initial?.OutputDeviceId,
            usesMicList ? initial?.MicName : null);
        if (ids.Length == 0)
        {
            DeviceCombo.SelectedIndex = -1;
        }
        else
        {
            DeviceCombo.SelectedIndex = Math.Clamp(index, 0, ids.Length - 1);
        }
        DeviceHint.Text = source switch
        {
            AudioSourceKind.System or AudioSourceKind.Both when _outputNames.Length > 0 =>
                $"已检测到 {_outputNames.Length} 个播放设备；采集所选设备正在输出的声音" +
                (usesMicList ? "。" : "，请选实际在出声的那个。"),
            AudioSourceKind.System or AudioSourceKind.Both =>
                "未检测到播放设备，无法采集系统声音。",
            _ when _micNames.Length > 0 =>
                $"已检测到 {_micNames.Length} 个输入设备。",
            _ =>
                "未检测到麦克风，录音将不可用。",
        };
        if (source == AudioSourceKind.Both)
        {
            DeviceHint.Text += "麦克风使用「设置 → 录音设置」中选定的设备（两路会合成为一路）。";
        }
    }

    /// <summary>把设备稳定 ID 数组对齐到显示名数量（缺失项补空串 = 该设备走系统默认）。</summary>
    private static string[] NormalizeIds(string[]? ids, int nameCount)
    {
        if (nameCount <= 0)
            return Array.Empty<string>();
        var result = new string[nameCount];
        for (int i = 0; i < nameCount; i++)
            result[i] = ids != null && i < ids.Length && ids[i] != null ? ids[i] : "";
        return result;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource;
        bool usesMicList = source is AudioSourceKind.Microphone;

        int idx = DeviceCombo.SelectedIndex;
        string? deviceName = idx >= 0 && idx < DeviceCombo.Items.Count
            ? DeviceCombo.Items[idx] as string
            : null;
        string? deviceId = idx >= 0 && idx < (usesMicList ? _micIds.Length : _outputIds.Length)
            ? (usesMicList ? _micIds[idx] : _outputIds[idx])
            : null;

        // 只覆盖本次来源真正用到的那一路设备，另一路沿用设置里的默认值
        // （旧实现里"麦克风和系统声音"的播放设备被丢弃，回环只能落到系统默认设备）
        var config = new RecordingConfig(
            source,
            MicId: source switch
            {
                AudioSourceKind.Microphone => deviceId,
                AudioSourceKind.Both => _initial?.MicId,
                _ => null,
            },
            OutputDeviceId: source switch
            {
                AudioSourceKind.System or AudioSourceKind.Both => deviceId,
                _ => null,
            },
            MicName: source switch
            {
                AudioSourceKind.Microphone => deviceName,
                AudioSourceKind.Both => _initial?.MicName,
                _ => null,
            });

        Result = new RecordingSetupResult
        {
            Course = CourseCombo.SelectedItem as string ?? "",
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim(),
            // 这两项为兼容既有调用方保留（与 AudioConfig 保持一致）
            MicName = config.MicName,
            MicId = config.MicId,
            AudioConfig = config,
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

/// <summary>User configuration captured from the setup dialog.</summary>
public class RecordingSetupResult
{
    public string Course { get; set; } = "";
    public string? Title { get; set; }
    public string? MicName { get; set; }

    /// <summary>所选麦克风的稳定设备标识（WASAPI ID 或 mme:{index}）；未选择时为 null。</summary>
    public string? MicId { get; set; }

    /// <summary>本次录音的采集配置（来源 + 设备）；未指定时按麦克风来源处理。</summary>
    public RecordingConfig AudioConfig { get; set; } = RecordingConfig.Default;
}
