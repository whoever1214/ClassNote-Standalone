using System.Windows;

namespace ClassNote.Views;

/// <summary>
/// Modal configuration dialog shown before recording starts.
/// Lets the user confirm the course, enter an optional title, and pick a microphone.
/// </summary>
public partial class RecordingSetupWindow : Window
{
    private readonly string[] _micIds;

    /// <summary>Result after the user clicks "开始录制". Null if canceled.</summary>
    public RecordingSetupResult? Result { get; private set; }

    public RecordingSetupWindow(string currentCourse, string[] courses, string[] mics, string[]? micIds = null)
    {
        InitializeComponent();
        _micIds = micIds ?? Array.Empty<string>();

        CourseCombo.ItemsSource = courses;
        int courseIdx = Array.IndexOf(courses, currentCourse);
        CourseCombo.SelectedIndex = courseIdx >= 0 ? courseIdx : 0;

        MicCombo.ItemsSource = mics;
        if (mics.Length > 0)
        {
            MicCombo.SelectedIndex = 0;
            MicHint.Text = "已检测到 " + mics.Length + " 个输入设备";
        }
        else
        {
            MicCombo.SelectedIndex = -1;
            MicHint.Text = "未检测到麦克风，录音将不可用";
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = MicCombo.SelectedIndex;
        Result = new RecordingSetupResult
        {
            Course = CourseCombo.SelectedItem as string ?? "",
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim(),
            MicName = MicCombo.SelectedItem as string,
            MicId = idx >= 0 && idx < _micIds.Length ? _micIds[idx] : null,
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
}
