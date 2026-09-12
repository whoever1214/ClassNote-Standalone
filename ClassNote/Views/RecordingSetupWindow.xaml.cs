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
        ApplySource(null);
    }

    /// <summary>
    /// 按来源切换设备选择器：麦克风来源列麦克风；系统声音来源列播放设备（回环）;
    /// 混合来源只能二选一（窗口空间有限），此处列麦克风，系统声音走设置里的默认播放设备。
    /// </summary>
    private void ApplySource(RecordingConfig? initial)
    {
        var source = SelectedSource;
        bool usesMicList = source is AudioSourceKind.Microphone or AudioSourceKind.Both;

        DeviceLabel.Text = usesMicList ? "麦克风" : "系统声音来源设备";
        DeviceCombo.ItemsSource = usesMicList ? _micNames : _outputNames;
        var ids = usesMicList ? _micIds : _outputIds;

        // 预设设备：麦克风来源按传入配置（弹窗打开前已从设置里取好）；系统声音来源列播放设备
        int index = AudioDeviceSelection.ResolveIndex(ids, usesMicList ? _micNames : _outputNames,
            usesMicList ? initial?.MicId : null, usesMicList ? initial?.MicName : null);
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
            AudioSourceKind.System when _outputNames.Length > 0 =>
                $"已检测到 {_outputNames.Length} 个播放设备；采集所选设备正在输出的声音。",
            AudioSourceKind.System =>
                "未检测到播放设备，无法采集系统声音。",
            AudioSourceKind.Both =>
                $"麦克风：已检测到 {_micNames.Length} 个输入设备；系统声音固定使用「设置 → 录音设置」中的播放设备。",
            _ when _micNames.Length > 0 =>
                $"已检测到 {_micNames.Length} 个输入设备。",
            _ =>
                "未检测到麦克风，录音将不可用。",
        };
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
        bool usesMicList = source is AudioSourceKind.Microphone or AudioSourceKind.Both;

        int idx = DeviceCombo.SelectedIndex;
        string? deviceName = idx >= 0 && idx < DeviceCombo.Items.Count
            ? DeviceCombo.Items[idx] as string
            : null;
        string? deviceId = idx >= 0 && idx < (usesMicList ? _micIds.Length : _outputIds.Length)
            ? (usesMicList ? _micIds[idx] : _outputIds[idx])
            : null;

        Result = new RecordingSetupResult
        {
            Course = CourseCombo.SelectedItem as string ?? "",
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim(),
            MicName = usesMicList ? deviceName : null,
            MicId = usesMicList ? deviceId : null,
            AudioConfig = new RecordingConfig(
                source,
                MicId: usesMicList ? deviceId : null,
                OutputDeviceId: source == AudioSourceKind.System ? deviceId : null),
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
