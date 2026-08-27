# 客户端单元测试实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 ClassNote WPF 客户端建立 23 个 xUnit + Moq 单元测试，覆盖全部 ViewModel 的纯逻辑。

**Architecture:** 新建 `ClassNote.Tests` 测试项目，抽取 3 个服务接口（IAudioService/IScreenshotService/IUploadService），重构 RecordingViewModel 为接口注入，然后按依赖顺序写 5 个测试文件。

**Tech Stack:** .NET 8, WPF, xUnit 2.9, Moq 4.20, coverlet

## 全局约束

- 测试项目 TargetFramework `net8.0-windows`，UseWPF=true
- 纯离线测试，不依赖服务端、数据库、音频设备、屏幕
- 所有 API 调用通过 Moq Mock `IApiService`
- 所有硬件服务（AudioService/ScreenshotService）通过 Moq Mock 新接口
- csproj 引用的 NuGet: xUnit, Moq, coverlet.collector, Microsoft.NET.Test.Sdk
- 测试项目引用 `ClassNote.csproj` 作为 ProjectReference
- 测试文件命名规则：`{被测类名}Tests.cs`
- 每个测试方法格式：`{MethodName}_{Scenario}_{ExpectedResult}`

---

## 文件结构

### 新增文件

```
ClassNote.Tests/
├── ClassNote.Tests.csproj
└── ViewModels/
    ├── RelayCommandTests.cs
    ├── LoginViewModelTests.cs
    ├── MainViewModelTests.cs
    ├── NoteViewModelTests.cs
    └── RecordingViewModelTests.cs

ClassNote/Services/
├── IAudioService.cs           # NEW
├── IScreenshotService.cs      # NEW
└── IUploadService.cs          # NEW
```

### 修改文件

| 文件 | 变更 |
|------|------|
| `ClassNote/Services/AudioService.cs` | `: IAudioService` 加到类声明 |
| `ClassNote/Services/ScreenshotService.cs` | `: IScreenshotService` 加到类声明 |
| `ClassNote/Services/UploadService.cs` | `: IUploadService` 加到类声明 |
| `ClassNote/ViewModels/RecordingViewModel.cs` | 构造函数改为接口注入 |
| `ClassNote/Views/RecordingPage.xaml.cs` | 调用方改为在页面内组装依赖 |

---

### Task 1: 测试项目脚手架

**Files:**
- Create: `ClassNote.Tests/ClassNote.Tests.csproj`
- Create: `ClassNote/Services/IAudioService.cs`
- Create: `ClassNote/Services/IScreenshotService.cs`
- Create: `ClassNote/Services/IUploadService.cs`

**Interfaces:**
- Produces: `IAudioService` — `GetInputDevices()`, `StartRecording()`, `StopRecording()`, `GetFileBytes()`, `Dispose()`
- Produces: `IScreenshotService` — `Start()`, `Stop()`, `event ScreenshotCaptured`, `Dispose()`
- Produces: `IUploadService` — `UploadScreenshotAsync()`, `FlushQueueAsync()`, `Dispose()`

- [ ] **Step 1: 创建测试项目目录**

```bash
mkdir -p ClassNote.Tests/ViewModels
```

- [ ] **Step 2: 创建 ClassNote.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.1" />
    <PackageReference Include="Moq" Version="4.20.70" />
    <PackageReference Include="coverlet.collector" Version="6.2.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClassNote\ClassNote.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 创建 IAudioService.cs**

```csharp
using NAudio.Wave;

namespace ClassNote.Services;

public interface IAudioService : IDisposable
{
    string[] GetInputDevices();
    bool StartRecording(string outputPath, int deviceIndex = 0);
    void StopRecording();
    byte[] GetFileBytes();
}
```

- [ ] **Step 4: 创建 IScreenshotService.cs**

```csharp
namespace ClassNote.Services;

public interface IScreenshotService : IDisposable
{
    void Start(int initialIntervalMs = 10000);
    void Stop();
    event EventHandler<ScreenshotResult> ScreenshotCaptured;
}
```

- [ ] **Step 5: 创建 IUploadService.cs**

```csharp
namespace ClassNote.Services;

public interface IUploadService : IDisposable
{
    Task<bool> UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp,
        string type, byte[] imageData, string? url);
    Task FlushQueueAsync();
}
```

- [ ] **Step 6: 验证项目可编译**

Run: `dotnet build ClassNote.Tests/ClassNote.Tests.csproj`
Expected: Build succeeded

- [ ] **Step 7: 提交**

```bash
git add ClassNote.Tests/ ClassNote/Services/IAudioService.cs ClassNote/Services/IScreenshotService.cs ClassNote/Services/IUploadService.cs
git commit -m "feat: add test project scaffold and service interfaces"
```

---

### Task 2: 修改现有服务实现接口 + 重构 RecordingViewModel

**Files:**
- Modify: `ClassNote/Services/AudioService.cs` — 加 `: IAudioService`
- Modify: `ClassNote/Services/ScreenshotService.cs` — 加 `: IScreenshotService`
- Modify: `ClassNote/Services/UploadService.cs` — 加 `: IUploadService`
- Modify: `ClassNote/ViewModels/RecordingViewModel.cs` — 构造函数改为接口注入
- Modify: `ClassNote/Views/RecordingPage.xaml.cs` — 调用方适配

**Interfaces:**
- Consumes: `IAudioService`, `IScreenshotService`, `IUploadService` (from Task 1)
- Produces: `RecordingViewModel(Guid, IApiService, IAudioService, IScreenshotService, IUploadService)`

- [ ] **Step 1: AudioService 加接口声明**

编辑 `ClassNote/Services/AudioService.cs`，将类声明改为：
```csharp
public class AudioService : IAudioService, IDisposable
```
（其他代码不动）

- [ ] **Step 2: ScreenshotService 加接口声明**

编辑 `ClassNote/Services/ScreenshotService.cs`，将类声明改为：
```csharp
public class ScreenshotService : IScreenshotService, IDisposable
```
（其他代码不动）

- [ ] **Step 3: UploadService 加接口声明**

编辑 `ClassNote/Services/UploadService.cs`，将类声明改为：
```csharp
public class UploadService : IUploadService, IDisposable
```
（其他代码不动。注意 `IUploadService` 已经有 `IDisposable`，`UploadService` 已有 `Dispose()` 方法，无冲突）

- [ ] **Step 4: 重构 RecordingViewModel 构造函数**

编辑 `ClassNote/ViewModels/RecordingViewModel.cs`，修改字段类型和构造函数：

```csharp
using ClassNote.Services;

namespace ClassNote.ViewModels;
public class RecordingViewModel : BaseViewModel
{
    private readonly IAudioService _audio;              // 改了类型
    private readonly IScreenshotService _screenshot;    // 改了类型
    private readonly IUploadService _upload;            // 改了类型
    private readonly IApiService _api;                  // 没变
    private readonly Guid _sessionId;

    // ── 新构造函数 ──
    public RecordingViewModel(Guid sessionId, IApiService api, IAudioService audio,
        IScreenshotService screenshot, IUploadService upload)
    {
        _sessionId = sessionId;
        _api = api;
        _audio = audio;
        _screenshot = screenshot;
        _upload = upload;
        _screenshot.ScreenshotCaptured += OnScreenshotCaptured;
        AvailableMics = _audio.GetInputDevices();
        if (AvailableMics.Length > 0) SelectedMic = AvailableMics[0];
    }

    // ── 以下代码完全不变 ──
    // ... (StatusText, ScreenshotCount, ElapsedSeconds, etc.)
    // ... (StartRecordingAsync, StopRecordingAsync, OnScreenshotCaptured, etc.)
    // ... (GetAudioData, Dispose)
}
```

特别注意：删除原有的 `private readonly AudioService _audio = new();` 等字段初始化，以及旧的构造函数。

保留原有的完整字段声明：
```csharp
private string _statusText = "准备中";
private int _screenshotCount;
private double _elapsedSeconds;
private string _selectedMic = "";
private string[] _availableMics = Array.Empty<string>();
private System.Timers.Timer? _elapsedTimer;
```

保留所有属性和方法（StatusText, ScreenshotCount, ElapsedSeconds, ElapsedDisplay, SelectedMic, AvailableMics, StartRecordingAsync, StopRecordingAsync, OnScreenshotCaptured, GetAudioData, Dispose）**完全不变**。

- [ ] **Step 5: 适配 RecordingPage.xaml.cs**

编辑 `ClassNote/Views/RecordingPage.xaml.cs`，修改构造函数内部：

```csharp
public RecordingPage(Guid sessionId, string baseUrl, string token, string course)
{
    InitializeComponent();

    CourseLabel.Text = course;

    // 在页面内组装依赖
    var api = new ApiService(baseUrl);
    api.SetToken(token);
    _viewModel = new RecordingViewModel(
        sessionId,
        api,
        new AudioService(),
        new ScreenshotService(),
        new UploadService(baseUrl, token)
    );
    DataContext = _viewModel;

    _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
    _uiTimer.Tick += (_, _) =>
        RecordingDot.Fill = RecordingDot.Fill == Brushes.Red ? Brushes.Transparent : Brushes.Red;
}
```

（外部调用方 `MainWindow.xaml.cs` 不需要改 — 它传的 `(Guid, string, string, string)` 签名未变）

- [ ] **Step 6: 验证编译通过**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings

- [ ] **Step 7: 提交**

```bash
git add ClassNote/Services/AudioService.cs ClassNote/Services/ScreenshotService.cs ClassNote/Services/UploadService.cs ClassNote/ViewModels/RecordingViewModel.cs ClassNote/Views/RecordingPage.xaml.cs
git commit -m "refactor: extract service interfaces, inject into RecordingViewModel"
```

---

### Task 3: RelayCommandTests

**Files:**
- Create: `ClassNote.Tests/ViewModels/RelayCommandTests.cs`

**Interfaces:**
- Consumes: `ClassNote.ViewModels.RelayCommand`
- Tests: `Execute`, `CanExecute`, `CanExecuteChanged`

- [ ] **Step 1: 创建 RelayCommandTests.cs**

```csharp
using ClassNote.ViewModels;

namespace ClassNote.Tests.ViewModels;

public class RelayCommandTests
{
    [Fact]
    public void Execute_InvokesAction()
    {
        // Arrange
        var invoked = false;
        var cmd = new RelayCommand(() =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        // Act
        cmd.Execute(null);

        // Assert
        Assert.True(invoked);
    }

    [Fact]
    public void CanExecute_DefaultTrue()
    {
        // Arrange
        var cmd = new RelayCommand(() => Task.CompletedTask);

        // Act & Assert
        Assert.True(cmd.CanExecute(null));
    }

    [Fact]
    public void CanExecute_RespectsPredicate()
    {
        // Arrange
        var cmd = new RelayCommand(() => Task.CompletedTask, () => false);

        // Act & Assert
        Assert.False(cmd.CanExecute(null));
    }
}
```

- [ ] **Step 2: 运行测试验证通过**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj --filter "FullyQualifiedName~RelayCommand" -v n`
Expected: 3 passed

- [ ] **Step 3: 提交**

```bash
git add ClassNote.Tests/ViewModels/RelayCommandTests.cs
git commit -m "test: add RelayCommand unit tests"
```

---

### Task 4: LoginViewModelTests

**Files:**
- Create: `ClassNote.Tests/ViewModels/LoginViewModelTests.cs`

**Interfaces:**
- Consumes: `ClassNote.ViewModels.LoginViewModel`, `ClassNote.Services.IApiService`
- Tests: Login success, failure, network error, loading state

- [ ] **Step 1: 创建 LoginViewModelTests.cs**

```csharp
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;

namespace ClassNote.Tests.ViewModels;

public class LoginViewModelTests
{
    private readonly Mock<IApiService> _mockApi;
    private readonly LoginViewModel _vm;

    public LoginViewModelTests()
    {
        _mockApi = new Mock<IApiService>();
        _vm = new LoginViewModel(_mockApi.Object);
    }

    [Fact]
    public async Task Login_Success_ReturnsTrue()
    {
        // Arrange
        _mockApi.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync("valid_token");
        _vm.Username = "admin";
        _vm.Password = "admin123";

        // Act
        var result = await _vm.LoginAsync();

        // Assert
        Assert.True(result);
        Assert.False(_vm.IsLoggingIn);
        Assert.Empty(_vm.Error);
    }

    [Fact]
    public async Task Login_Failure_SetsErrorMessage()
    {
        // Arrange
        _mockApi.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new UnauthorizedAccessException("Invalid credentials"));
        _vm.Username = "admin";
        _vm.Password = "wrong";

        // Act
        var result = await _vm.LoginAsync();

        // Assert
        Assert.False(result);
        Assert.Contains("登录失败", _vm.Error);
        Assert.False(_vm.IsLoggingIn);
    }

    [Fact]
    public async Task Login_NetworkError_HandledGracefully()
    {
        // Arrange
        _mockApi.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new HttpRequestException("Connection refused"));
        _vm.Username = "admin";
        _vm.Password = "admin123";

        // Act
        var result = await _vm.LoginAsync();

        // Assert
        Assert.False(result);
        Assert.NotEmpty(_vm.Error);
        Assert.False(_vm.IsLoggingIn);
    }

    [Fact]
    public async Task Login_SetsIsLoggingInDuringExecution()
    {
        // Arrange
        var taskStarted = new TaskCompletionSource();
        var taskGate = new TaskCompletionSource<string>();

        _mockApi.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(async () =>
                {
                    taskStarted.TrySetResult();
                    return await taskGate.Task;
                });
        _vm.Username = "admin";
        _vm.Password = "admin123";

        // Act
        var loginTask = _vm.LoginAsync();

        // Wait until the VM has set IsLoggingIn = true
        await taskStarted.Task;
        Assert.True(_vm.IsLoggingIn);

        // Release the gate
        taskGate.TrySetResult("token");
        await loginTask;

        // Assert
        Assert.False(_vm.IsLoggingIn);
    }
}
```

- [ ] **Step 2: 运行测试验证通过**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj --filter "FullyQualifiedName~LoginViewModel" -v n`
Expected: 4 passed

- [ ] **Step 3: 提交**

```bash
git add ClassNote.Tests/ViewModels/LoginViewModelTests.cs
git commit -m "test: add LoginViewModel unit tests"
```

---

### Task 5: MainViewModelTests

**Files:**
- Create: `ClassNote.Tests/ViewModels/MainViewModelTests.cs`

**Interfaces:**
- Consumes: `ClassNote.ViewModels.MainViewModel`, `ClassNote.Services.IApiService`
- Tests: 5 cases (load, limit, error, start success, start error)

- [ ] **Step 1: 创建 MainViewModelTests.cs**

```csharp
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;

namespace ClassNote.Tests.ViewModels;

public class MainViewModelTests
{
    private readonly Mock<IApiService> _mockApi;
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _mockApi = new Mock<IApiService>();
        _vm = new MainViewModel(_mockApi.Object);
    }

    [Fact]
    public async Task LoadSessions_PopulatesRecentList()
    {
        // Arrange
        var sessions = new List<Session>
        {
            new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
            new() { Id = Guid.NewGuid(), Course = "英语", Status = "completed" },
            new() { Id = Guid.NewGuid(), Course = "物理", Status = "recording" },
        };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(sessions);

        // Act
        await _vm.LoadSessionsAsync();

        // Assert
        Assert.Equal(3, _vm.RecentSessions.Count);
    }

    [Fact]
    public async Task LoadSessions_LimitsTo20Items()
    {
        // Arrange
        var sessions = Enumerable.Range(0, 30)
            .Select(i => new Session { Id = Guid.NewGuid(), Course = "数学", Status = "done" })
            .ToList();
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(sessions);

        // Act
        await _vm.LoadSessionsAsync();

        // Assert
        Assert.Equal(20, _vm.RecentSessions.Count);
    }

    [Fact]
    public async Task LoadSessions_ApiError_ListEmpty()
    {
        // Arrange
        _mockApi.Setup(x => x.ListSessionsAsync()).ThrowsAsync(new Exception("API down"));

        // Act — should not throw
        await _vm.LoadSessionsAsync();

        // Assert
        Assert.Empty(_vm.RecentSessions);
        Assert.False(_vm.IsLoading);
    }

    [Fact]
    public async Task StartRecording_ReturnsSessionId()
    {
        // Arrange
        var expectedId = Guid.NewGuid();
        _mockApi.Setup(x => x.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(expectedId);

        // Act
        var result = await _vm.StartRecordingAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedId, result.Value);
    }

    [Fact]
    public async Task StartRecording_ApiError_ReturnsNull()
    {
        // Arrange
        _mockApi.Setup(x => x.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ThrowsAsync(new Exception("API down"));

        // Act
        var result = await _vm.StartRecordingAsync();

        // Assert
        Assert.Null(result);
    }
}
```

- [ ] **Step 2: 运行测试验证通过**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj --filter "FullyQualifiedName~MainViewModel" -v n`
Expected: 5 passed

- [ ] **Step 3: 提交**

```bash
git add ClassNote.Tests/ViewModels/MainViewModelTests.cs
git commit -m "test: add MainViewModel unit tests"
```

---

### Task 6: NoteViewModelTests

**Files:**
- Create: `ClassNote.Tests/ViewModels/NoteViewModelTests.cs`

**Interfaces:**
- Consumes: `ClassNote.ViewModels.NoteViewModel`, `ClassNote.Services.IApiService`
- Tests: 3 cases (populated note, null note, null markdown)

- [ ] **Step 1: 创建 NoteViewModelTests.cs**

```csharp
using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;

namespace ClassNote.Tests.ViewModels;

public class NoteViewModelTests
{
    private readonly Mock<IApiService> _mockApi;
    private readonly NoteViewModel _vm;

    public NoteViewModelTests()
    {
        _mockApi = new Mock<IApiService>();
        _vm = new NoteViewModel(_mockApi.Object);
    }

    [Fact]
    public async Task LoadNote_PopulatesProperties()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var note = new Note
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ContentMarkdown = "# 导数\n\n导数定义：f'(x) = lim...",
            Summary = "导数章节笔记",
        };
        _mockApi.Setup(x => x.GetNoteAsync(sessionId)).ReturnsAsync(note);

        // Act
        await _vm.LoadNoteAsync(sessionId);

        // Assert
        Assert.NotNull(_vm.Note);
        Assert.Equal(note.Id, _vm.Note.Id);
        Assert.NotEmpty(_vm.MarkdownHtml);
        // MarkdownHtml should contain HTML-encoded markdown
        Assert.Contains("导数", _vm.MarkdownHtml);
    }

    [Fact]
    public async Task LoadNote_NotReady_ReturnsNull()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _mockApi.Setup(x => x.GetNoteAsync(sessionId)).ReturnsAsync((Note?)null);

        // Act
        await _vm.LoadNoteAsync(sessionId);

        // Assert
        Assert.Null(_vm.Note);
        Assert.Empty(_vm.MarkdownHtml);
    }

    [Fact]
    public async Task LoadNote_NullMarkdown_NoCrash()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var note = new Note
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ContentMarkdown = null,  // 笔记已创建但内容为空
        };
        _mockApi.Setup(x => x.GetNoteAsync(sessionId)).ReturnsAsync(note);

        // Act — should not throw
        await _vm.LoadNoteAsync(sessionId);

        // Assert
        Assert.NotNull(_vm.Note);
        Assert.Empty(_vm.MarkdownHtml);
    }
}
```

- [ ] **Step 2: 运行测试验证通过**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj --filter "FullyQualifiedName~NoteViewModel" -v n`
Expected: 3 passed

- [ ] **Step 3: 提交**

```bash
git add ClassNote.Tests/ViewModels/NoteViewModelTests.cs
git commit -m "test: add NoteViewModel unit tests"
```

---

### Task 7: RecordingViewModelTests

**Files:**
- Create: `ClassNote.Tests/ViewModels/RecordingViewModelTests.cs`

**Interfaces:**
- Consumes: `ClassNote.ViewModels.RecordingViewModel`, `IApiService`, `IAudioService`, `IScreenshotService`, `IUploadService`
- Tests: 8 cases (mic selection, start/stop, screenshot event, dispose, timer)

- [ ] **Step 1: 创建 RecordingViewModelTests.cs**

```csharp
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;

namespace ClassNote.Tests.ViewModels;

public class RecordingViewModelTests
{
    private readonly Mock<IApiService> _mockApi;
    private readonly Mock<IAudioService> _mockAudio;
    private readonly Mock<IScreenshotService> _mockScreenshot;
    private readonly Mock<IUploadService> _mockUpload;
    private readonly Guid _sessionId;

    public RecordingViewModelTests()
    {
        _sessionId = Guid.NewGuid();
        _mockApi = new Mock<IApiService>();
        _mockAudio = new Mock<IAudioService>();
        _mockScreenshot = new Mock<IScreenshotService>();
        _mockUpload = new Mock<IUploadService>();
    }

    private RecordingViewModel CreateVm()
    {
        return new RecordingViewModel(_sessionId, _mockApi.Object, _mockAudio.Object,
            _mockScreenshot.Object, _mockUpload.Object);
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
    public async Task StopRecording_StopsAllAndFlushes()
    {
        // Arrange
        _mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1" });
        _mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
        _mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
        _mockApi.Setup(x => x.EndSessionAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        var vm = CreateVm();
        await vm.StartRecordingAsync();

        // Act
        await vm.StopRecordingAsync();

        // Assert
        _mockAudio.Verify(x => x.StopRecording(), Times.Once);
        _mockScreenshot.Verify(x => x.Stop(), Times.Once);
        _mockUpload.Verify(x => x.FlushQueueAsync(), Times.Once);
        _mockApi.Verify(x => x.EndSessionAsync(_sessionId), Times.Once);
        Assert.Equal("已停止", vm.StatusText);
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
        _mockScreenshot.Raise(x => x.ScreenshotCaptured += null, capturedArgs);

        // Assert
        _mockUpload.Verify(x => x.UploadScreenshotAsync(
            _sessionId,
            It.IsAny<int>(),
            It.IsAny<double>(),
            "newslide",
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
```

- [ ] **Step 2: 运行测试验证通过**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj --filter "FullyQualifiedName~RecordingViewModel" -v n`
Expected: 8 passed

- [ ] **Step 3: 提交**

```bash
git add ClassNote.Tests/ViewModels/RecordingViewModelTests.cs
git commit -m "test: add RecordingViewModel unit tests"
```

---

### Task 8: 全量运行验证

- [ ] **Step 1: 运行全部测试**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj -v n`
Expected: 23 passed, 0 failed

- [ ] **Step 2: 验证覆盖率报告（可选）**

Run: `dotnet test ClassNote.Tests/ClassNote.Tests.csproj /p:CollectCoverage=true /p:CoverletOutputFormat=opencover`
Expected: Coverage results generated

- [ ] **Step 3: 最终提交**

```bash
git add ClassNote.Tests/
git commit -m "chore: add all test projects and verify 23/23 passing"
```
