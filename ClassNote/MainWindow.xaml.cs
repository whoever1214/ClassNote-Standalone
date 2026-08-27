using System.Windows;
using ClassNote.Views;

namespace ClassNote;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 单机模式：启动直接进入主页面（无需服务端、无需认证）
        NavigateToMainPage();
    }

    /// <summary>
    /// Navigate to the main page (session list and course selection).
    /// </summary>
    public void NavigateToMainPage()
    {
        var mainPage = new MainPage();
        mainPage.StartRecordingRequested += OnMainPageStartRecording;
        mainPage.SessionSelected += OnMainPageSessionSelected;
        MainFrame.Navigate(mainPage);
    }

    /// <summary>
    /// Navigate to the recording page for a new session.
    /// </summary>
    public void NavigateToRecordingPage(Guid sessionId, string course, string? micName)
    {
        var recordingPage = new RecordingPage(sessionId, course, micName);
        recordingPage.RecordingEnded += OnRecordingEnded;
        MainFrame.Navigate(recordingPage);
    }

    /// <summary>
    /// Navigate to the note viewing page for a given session.
    /// </summary>
    public void NavigateToNoteViewPage(Guid sessionId)
    {
        var notePage = new NoteViewPage(sessionId);
        notePage.BackRequested += OnNoteViewBackRequested;
        MainFrame.Navigate(notePage);
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void OnMainPageStartRecording(object? sender, (Guid SessionId, string Course, string? MicName) args)
    {
        NavigateToRecordingPage(args.SessionId, args.Course, args.MicName);
    }

    private void OnMainPageSessionSelected(object? sender, Guid sessionId)
    {
        NavigateToNoteViewPage(sessionId);
    }

    private void OnRecordingEnded(object? sender, System.EventArgs e)
    {
        NavigateToMainPage();
    }

    private void OnNoteViewBackRequested(object? sender, System.EventArgs e)
    {
        NavigateToMainPage();
    }
}
