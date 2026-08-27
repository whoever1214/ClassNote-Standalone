using System.IO;
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;
using Xunit;

namespace ClassNote.Tests.ViewModels;

public class RecordingViewModelTests
{
    private readonly Mock<IApiService> _mockApi;
    private readonly Mock<IAudioService> _mockAudio;
    private readonly Mock<IScreenshotService> _mockScreenshot;
    private readonly Mock<IUploadService> _mockUpload;
    private readonly Mock<INoteProcessor> _mockProcessor;
    private readonly Guid _sessionId;
    private readonly string _tmpDir;

    public RecordingViewModelTests()
    {
        _sessionId = Guid.NewGuid();
        _mockApi = new Mock<IApiService>();
        _mockAudio = new Mock<IAudioService>();
        _mockScreenshot = new Mock<IScreenshotService>();
        _mockUpload = new Mock<IUploadService>();
        _mockProcessor = new Mock<INoteProcessor>();

        // Default setup: UploadScreenshotAsync returns true so async void handlers don't NRE
        _mockUpload.Setup(x => x.UploadScreenshotAsync(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<double>(),
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        // 音频相关默认 stub：避免未 setup 的方法返回 null Task 导致 NRE
        _mockUpload.Setup(x => x.UploadAudioAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockUpload.Setup(x => x.EnqueueAudioAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockUpload.Setup(x => x.FlushAudioQueueAsync()).Returns(Task.CompletedTask);
        // 本地处理管线默认无操作
        _mockProcessor.Setup(x => x.ProcessAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IProgress<string>?>()))
            .Returns(Task.CompletedTask);

        _tmpDir = Path.Combine(Path.GetTempPath(), $"classnote_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    private RecordingViewModel CreateVm(string? micName = null)
    {
        return new RecordingViewModel(_sessionId, _mockApi.Object, _mockAudio.Object,
            _mockScreenshot.Object, _mockUpload.Object, micName, _mockProcessor.Object);
    }

    /// <summary>在临时目录创建录音文件并让 GetOutputPath 指向它。</summary>
    private string CreateAudioFile(int size = 100)
    {
        var path = Path.Combine(_tmpDir, "audio.wav");
        File.WriteAllBytes(path, new byte[size]);
        _mockAudio.Setup(x => x.GetOutputPath()).Returns(path);
        return path;
    }

    private void Cleanup()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    [Fact]
    public void Constructor_SelectsFirstMic()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1", "Mic 2" });

        // Act
        var vm = CreateVm();

        // Assert
        Assert.Equal("Mic 1", vm.SelectedMic);
        Assert.Equal(2, vm.AvailableMics.Length);
    }

    [Fact]
    public void Constructor_SelectsSpecifiedMic()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1", "Mic 2", "Mic 3" });

        // Act
        var vm = CreateVm("Mic 2");

        // Assert
        Assert.Equal("Mic 2", vm.SelectedMic);
    }

    [Fact]
    public void Constructor_InvalidMic_FallsBackToFirst()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1", "Mic 2" });

        // Act
        var vm = CreateVm("不存在的麦克风");

        // Assert
        Assert.Equal("Mic 1", vm.SelectedMic);
    }

    [Fact]
    public void Constructor_NoMics_EmptySelection()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(Array.Empty<string>());

        // Act
        var vm = CreateVm();

        // Assert
        Assert.Equal("", vm.SelectedMic);
    }

    [Fact]
    public async Task StartRecording_Success()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        var vm = CreateVm();

        // Act
        await vm.StartRecordingAsync();

        // Assert
        Assert.Equal("录音中", vm.StatusText);
    }

    [Fact]
    public async Task StartRecording_AudioFails()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(false);
        var vm = CreateVm();

        // Act
        await vm.StartRecordingAsync();

        // Assert
        Assert.Equal("录音启动失败", vm.StatusText);
    }

    [Fact]
    public async Task StopRecording_StopsAllAndProcesses()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockApi.Setup(x => x.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();

        // Act
        await vm.StopRecordingAsync();

        // Assert
        _mockAudio.Verify(x => x.StopRecording(), Times.Once);
        _mockScreenshot.Verify(x => x.Stop(), Times.Once);
        _mockUpload.Verify(x => x.FlushQueueAsync(), Times.Once);
        _mockApi.Verify(x => x.EndSessionAsync(_sessionId, It.IsAny<int>()), Times.Once);
        _mockProcessor.Verify(x => x.ProcessAsync(_sessionId, It.IsAny<string?>(), It.IsAny<IProgress<string>?>()), Times.Once);
        Assert.Equal("已停止", vm.StatusText);
        Cleanup();
    }

    [Fact]
    public async Task StopRecording_UploadsAudio_BeforeProcess()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockUpload.Setup(x => x.UploadAudioAsync(_sessionId, It.IsAny<string>(), "audio.wav"))
            .ReturnsAsync(true);
        _mockApi.Setup(x => x.EndSessionAsync(_sessionId, It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();
        var audioFile = CreateAudioFile(120);

        // Act
        await vm.StopRecordingAsync();

        // Assert
        _mockUpload.Verify(x => x.UploadAudioAsync(_sessionId, audioFile, "audio.wav"), Times.Once);
        _mockUpload.Verify(x => x.EnqueueAudioAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockApi.Verify(x => x.EndSessionAsync(_sessionId, It.IsAny<int>()), Times.Once);
        Assert.Equal("已停止", vm.StatusText);
        Cleanup();
    }

    [Fact]
    public async Task StopRecording_NoAudio_SkipsUpload()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        _mockAudio.Setup(x => x.GetOutputPath()).Returns((string?)null);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockApi.Setup(x => x.EndSessionAsync(_sessionId, It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();

        // Act
        await vm.StopRecordingAsync();

        // Assert
        _mockUpload.Verify(x => x.UploadAudioAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockUpload.Verify(x => x.EnqueueAudioAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockApi.Verify(x => x.EndSessionAsync(_sessionId, It.IsAny<int>()), Times.Once);
        Assert.Equal("已停止", vm.StatusText);
        Cleanup();
    }

    [Fact]
    public async Task StopRecording_AudioUploadFails_Enqueues_StillEnds()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockUpload.Setup(x => x.UploadAudioAsync(_sessionId, It.IsAny<string>(), "audio.wav"))
            .ReturnsAsync(false); // 直传失败，应入队兜底
        _mockApi.Setup(x => x.EndSessionAsync(_sessionId, It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();
        var audioFile = CreateAudioFile(120);

        // Act
        await vm.StopRecordingAsync();

        // Assert — 音频失败不应中断结束流程，且已登记入队
        _mockUpload.Verify(x => x.EnqueueAudioAsync(_sessionId, audioFile, "audio.wav"), Times.Once);
        _mockApi.Verify(x => x.EndSessionAsync(_sessionId, It.IsAny<int>()), Times.Once);
        Assert.Equal("已停止", vm.StatusText);
        Cleanup();
    }

    [Fact]
    public void ScreenshotCaptured_UploadsImage()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        var vm = CreateVm();
        var capturedArgs = new ScreenshotResult
        {
            ImageData = new byte[] { 1, 2, 3 },
            Type = ScreenshotType.NewSlide,
        };

        // Act — 模拟截图服务触发事件
        _mockScreenshot.Raise(x => x.ScreenshotCaptured += null, _mockScreenshot.Object, capturedArgs);

        // Assert
        _mockUpload.Verify(x => x.UploadScreenshotAsync(
            _sessionId,
            It.IsAny<int>(),
            It.IsAny<double>(),
            "new_slide",
            new byte[] { 1, 2, 3 },
            null
        ), Times.Once);
    }

    [Fact]
    public void Dispose_CleansResources()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        var vm = CreateVm();

        // Act
        vm.Dispose();

        // Assert
        _mockAudio.Verify(x => x.Dispose(), Times.Once);
        _mockScreenshot.Verify(x => x.Dispose(), Times.Once);
        _mockUpload.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void ElapsedTime_UpdatesDisplay()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        var vm = CreateVm();

        // Act
        vm.ElapsedSeconds = 3661; // 1h 1min 1s

        // Assert
        Assert.Equal("01:01:01", vm.ElapsedDisplay);
    }
}
