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
        // 设备 ID 默认空数组（各测试用 SetupMics 显式提供）
        _mockAudio.Setup(x => x.GetInputDeviceIds()).Returns(Array.Empty<string>());
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(Array.Empty<string>());

        _tmpDir = Path.Combine(Path.GetTempPath(), $"classnote_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    /// <summary>同时设置设备显示名与稳定 ID（一一对应；未提供 ID 时用 "id:{name}" 合成）。</summary>
    private void SetupMics(string[] names, string[]? ids = null)
    {
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(names);
        _mockAudio.Setup(x => x.GetInputDeviceIds()).Returns(ids ?? names.Select(n => "id:" + n).ToArray());
    }

    private RecordingViewModel CreateVm(string? micName = null, string? micId = null,
        AudioSourceKind source = AudioSourceKind.Microphone)
    {
        var config = new RecordingConfig(source, micId, MicName: micName);
        return new RecordingViewModel(_sessionId, _mockApi.Object, _mockAudio.Object,
            _mockScreenshot.Object, _mockUpload.Object, config, _mockProcessor.Object);
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
        SetupMics(new[] { "Mic 1", "Mic 2" }, new[] { "id-1", "id-2" });

        // Act
        var vm = CreateVm();

        // Assert
        Assert.Equal("Mic 1", vm.SelectedMic);
        Assert.Equal("id-1", vm.SelectedMicId);
        Assert.Equal(2, vm.AvailableMics.Length);
    }

    [Fact]
    public void Constructor_SelectsSpecifiedMic()
    {
        // Arrange
        SetupMics(new[] { "Mic 1", "Mic 2", "Mic 3" }, new[] { "id-1", "id-2", "id-3" });

        // Act
        var vm = CreateVm("Mic 2");

        // Assert
        Assert.Equal("Mic 2", vm.SelectedMic);
        Assert.Equal("id-2", vm.SelectedMicId);
    }

    [Fact]
    public void Constructor_SelectsMicByStableId()
    {
        // Arrange — 名称与 ID 不再依赖枚举顺序：即使名称匹配不上，按稳定 ID 也能选中正确设备
        SetupMics(new[] { "Mic A", "USB 麦克风", "Mic C" }, new[] { "wasapi-1", "wasapi-usb", "wasapi-3" });

        // Act — 传入在设置窗口选中的稳定 ID（USB 麦克风）
        var vm = CreateVm(micId: "wasapi-usb");

        // Assert
        Assert.Equal("USB 麦克风", vm.SelectedMic);
        Assert.Equal("wasapi-usb", vm.SelectedMicId);
    }

    [Fact]
    public void Constructor_InvalidMicId_FallsBackToFirst()
    {
        // Arrange
        SetupMics(new[] { "Mic 1", "Mic 2" }, new[] { "id-1", "id-2" });

        // Act — ID 失效（设备已拔出）时不应崩溃，回退到第一个设备
        var vm = CreateVm(micId: "wasapi-gone");

        // Assert
        Assert.Equal("Mic 1", vm.SelectedMic);
        Assert.Equal("id-1", vm.SelectedMicId);
    }

    [Fact]
    public void Constructor_InvalidMic_FallsBackToFirst()
    {
        // Arrange
        SetupMics(new[] { "Mic 1", "Mic 2" }, new[] { "id-1", "id-2" });

        // Act
        var vm = CreateVm("不存在的麦克风");

        // Assert
        Assert.Equal("Mic 1", vm.SelectedMic);
    }

    [Fact]
    public void Constructor_NoMics_EmptySelection()
    {
        // Arrange
        SetupMics(Array.Empty<string>(), Array.Empty<string>());

        // Act
        var vm = CreateVm();

        // Assert
        Assert.Equal("", vm.SelectedMic);
        Assert.Equal("", vm.SelectedMicId);
    }

    [Fact]
    public void SelectedMic_Change_SyncsDeviceId()
    {
        // Arrange
        SetupMics(new[] { "Mic 1", "Mic 2" }, new[] { "id-1", "id-2" });
        var vm = CreateVm();

        // Act — 模拟录音页下拉切换到第二个麦克风
        vm.SelectedMic = "Mic 2";

        // Assert
        Assert.Equal("id-2", vm.SelectedMicId);
    }

    /// <summary>启动录音时传给采集服务的配置（按麦克风 ID 精确匹配）。</summary>
    private static RecordingConfig ConfigWithMic(string micId)
        => It.Is<RecordingConfig>(c => c.MicId == micId);

    [Fact]
    public async Task StartRecording_Success()
    {
        // Arrange
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        var vm = CreateVm();

        // Act
        await vm.StartRecordingAsync();

        // Assert
        Assert.Equal("录音中", vm.StatusText);
        _mockAudio.Verify(x => x.StartRecording(It.IsAny<string>(), ConfigWithMic("id-1")), Times.Once);
    }

    [Fact]
    public async Task StartRecording_UsesPassedDeviceId()
    {
        // Arrange — 用户在配置窗口选中的 USB 麦克风 ID 应原样传给录音服务，而不是按名称二次解析
        SetupMics(new[] { "内置麦克风", "USB 麦克风" }, new[] { "wasapi-builtin", "wasapi-usb" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        var vm = CreateVm(micName: "USB 麦克风", micId: "wasapi-usb");

        // Act
        await vm.StartRecordingAsync();

        // Assert
        _mockAudio.Verify(x => x.StartRecording(It.IsAny<string>(), ConfigWithMic("wasapi-usb")), Times.Once);
    }

    [Fact]
    public async Task StartRecording_NoMics_FailsWithMessage()
    {
        // Arrange
        SetupMics(Array.Empty<string>(), Array.Empty<string>());
        var vm = CreateVm();

        // Act
        await vm.StartRecordingAsync();

        // Assert
        Assert.Equal("录音启动失败：未检测到可用麦克风", vm.StatusText);
        _mockAudio.Verify(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>()), Times.Never);
    }

    [Fact]
    public async Task StartRecording_SystemAudioOnly_DoesNotRequireMicrophone()
    {
        // Arrange — 无麦克风也应能录系统声音（不依赖采集端点）
        SetupMics(Array.Empty<string>(), Array.Empty<string>());
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        var vm = CreateVm(source: AudioSourceKind.System);

        // Act
        await vm.StartRecordingAsync();

        // Assert
        Assert.Equal("录音中", vm.StatusText);
        _mockAudio.Verify(x => x.StartRecording(It.IsAny<string>(),
            It.Is<RecordingConfig>(c => c.Source == AudioSourceKind.System && c.NeedsMicrophone == false)), Times.Once);
    }

    [Fact]
    public void SourceDisplayName_ReflectsConfiguredSource()
    {
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });

        Assert.Equal(AudioSourceKinds.ToDisplayName(AudioSourceKind.Microphone),
            CreateVm(source: AudioSourceKind.Microphone).SourceDisplayName);
        Assert.Equal(AudioSourceKinds.ToDisplayName(AudioSourceKind.Both),
            CreateVm(source: AudioSourceKind.Both).SourceDisplayName);
    }

    [Fact]
    public async Task StartRecording_AudioFails()
    {
        // Arrange
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(false);
        _mockAudio.Setup(x => x.LastError).Returns("WASAPI 采集启动失败: 设备被占用");
        var vm = CreateVm();

        // Act
        await vm.StartRecordingAsync();

        // Assert — 失败原因透传给用户
        Assert.Equal("录音启动失败：WASAPI 采集启动失败: 设备被占用", vm.StatusText);
    }

    [Fact]
    public async Task StopRecording_StopsAllAndProcesses()
    {
        // Arrange
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockApi.Setup(x => x.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();

        // Act
        await vm.StopRecordingAsync();
        if (vm.BackgroundProcessingTask != null)
            await vm.BackgroundProcessingTask;

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
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockUpload.Setup(x => x.UploadAudioAsync(_sessionId, It.IsAny<string>(), "audio.wav"))
            .ReturnsAsync(true);
        _mockApi.Setup(x => x.EndSessionAsync(_sessionId, It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();
        var audioFile = CreateAudioFile(120);

        // Act
        await vm.StopRecordingAsync();
        if (vm.BackgroundProcessingTask != null)
            await vm.BackgroundProcessingTask;

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
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        _mockAudio.Setup(x => x.GetOutputPath()).Returns((string?)null);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockApi.Setup(x => x.EndSessionAsync(_sessionId, It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();

        // Act
        await vm.StopRecordingAsync();
        if (vm.BackgroundProcessingTask != null)
            await vm.BackgroundProcessingTask;

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
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<RecordingConfig>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockUpload.Setup(x => x.UploadAudioAsync(_sessionId, It.IsAny<string>(), "audio.wav"))
            .ReturnsAsync(false); // 直传失败，应入队兜底
        _mockApi.Setup(x => x.EndSessionAsync(_sessionId, It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();
        var audioFile = CreateAudioFile(120);

        // Act
        await vm.StopRecordingAsync();
        if (vm.BackgroundProcessingTask != null)
            await vm.BackgroundProcessingTask;

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
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
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
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
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
        SetupMics(new[] { "Mic 1" }, new[] { "id-1" });
        var vm = CreateVm();

        // Act
        vm.ElapsedSeconds = 3661; // 1h 1min 1s

        // Assert
        Assert.Equal("01:01:01", vm.ElapsedDisplay);
    }
}
