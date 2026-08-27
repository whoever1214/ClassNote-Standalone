# 客户端测试设计文档

> 日期：2026-07-27
> 版本：v1.0
> 状态：设计完成，待实施

---

## 1. 概述

### 1.1 目标

为 ClassNote C# WPF 客户端建立全面的单元测试覆盖，确保 ViewModel 层逻辑的正确性和健壮性。

### 1.2 范围

- **测试框架：** xUnit
- **Mock 框架：** Moq
- **代码覆盖目标：** ViewModel 逻辑层 ~95%
- **测试环境：** 纯离线，不依赖服务端和硬件设备

### 1.3 技术选型

| 选项 | 选择 | 理由 |
|------|------|------|
| 测试框架 | xUnit | .NET 生态主流，与 Moq 配合好，社区活跃 |
| Mock 框架 | Moq | 最广泛使用的 .NET Mock 库，支持接口 Mock、回调验证 |
| 断言 | xUnit 内置 + FluentAssertions（可选） | xUnit 原生断言足够，后续可补充 |
| 覆盖率 | coverlet | 与 xUnit 集成好，支持报告生成 |
| 测试运行 | dotnet test CLI + VS Test Explorer | 兼容 CI 和 IDE |

---

## 2. 重构设计

### 2.1 接口抽取

为三个硬件/IO 依赖的服务抽取接口，使 `RecordingViewModel` 可 Mock：

```csharp
public interface IAudioService : IDisposable
{
    string[] GetInputDevices();
    bool StartRecording(string outputPath, int deviceIndex = 0);
    void StopRecording();
    byte[] GetFileBytes();
}

public interface IScreenshotService : IDisposable
{
    void Start(int initialIntervalMs = 10000);
    void Stop();
    event EventHandler<ScreenshotResult> ScreenshotCaptured;
}

public interface IUploadService : IDisposable
{
    Task<bool> UploadScreenshotAsync(Guid sessionId, int seqNo, double timestamp,
        string type, byte[] imageData, string? url);
    Task FlushQueueAsync();
}
```

### 2.2 实现类变更

每行只加 `: IInterfaceName`，无逻辑变更：

| 文件 | 变更 |
|------|------|
| `AudioService.cs` | `public class AudioService : **IAudioService,** IDisposable` |
| `ScreenshotService.cs` | `public class ScreenshotService : **IScreenshotService,** IDisposable` |
| `UploadService.cs` | `public class UploadService : **IUploadService,** IDisposable` |

### 2.3 RecordingViewModel 构造函数变更

```csharp
// 改造前
public RecordingViewModel(Guid sessionId, string baseUrl, string token, string course)
{
    _upload = new UploadService(baseUrl, token);
    _api = new ApiService(baseUrl);
    _audio = new AudioService();
    _screenshot = new ScreenshotService();
    ...
}

// 改造后
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
```

### 2.4 调用方适配

在 `App.xaml.cs` 或页面代码后置中组装依赖：

```csharp
var api = new ApiService(baseUrl);
api.SetToken(token);
var vm = new RecordingViewModel(
    sessionId,
    api,
    new AudioService(),
    new ScreenshotService(),
    new UploadService(baseUrl, token)
);
```

---

## 3. 项目结构

```
ClassNote.Tests/
├── ClassNote.Tests.csproj
├── ViewModels/
│   ├── LoginViewModelTests.cs
│   ├── MainViewModelTests.cs
│   ├── NoteViewModelTests.cs
│   ├── RecordingViewModelTests.cs
│   └── RelayCommandTests.cs
```

### 3.1 项目文件

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

---

## 4. 测试用例

### 4.1 LoginViewModelTests

| # | 测试方法 | 场景 | Mock 设置 | 断言 |
|---|---------|------|-----------|------|
| 1 | `Login_Success_ReturnsTrue` | 用户名密码正确 | `LoginAsync` 返回 `"valid_token"` | `true`，`IsLoggingIn == false` |
| 2 | `Login_Failure_SetsErrorMessage` | 凭据错误 | `LoginAsync` 抛 `UnauthorizedAccessException` | `false`，Error 包含"登录失败" |
| 3 | `Login_NetworkError_HandledGracefully` | 网络异常 | `LoginAsync` 抛 `HttpRequestException` | `false`，Error 非空 |
| 4 | `Login_SetsIsLoggingInDuringExecution` | Loading 状态 | 通过延迟回调验证中间状态 | `IsLoggingIn` 先 true 后 false |

### 4.2 MainViewModelTests

| # | 测试方法 | 场景 | Mock 设置 | 断言 |
|---|---------|------|-----------|------|
| 5 | `LoadSessions_PopulatesRecentList` | 正常加载历史 | `ListSessionsAsync` 返回 3 条 | `RecentSessions.Count == 3` |
| 6 | `LoadSessions_LimitsTo20Items` | 超过上限 | 返回 30 条 | `RecentSessions.Count == 20` |
| 7 | `LoadSessions_ApiError_ListEmpty` | 接口异常 | 抛异常 | 列表为空，不抛出异常 |
| 8 | `StartRecording_ReturnsSessionId` | 开始记录成功 | `CreateSessionAsync` 返回 Guid | 返回值非 null |
| 9 | `StartRecording_ApiError_ReturnsNull` | 创建失败 | 抛异常 | 返回 null |

### 4.3 NoteViewModelTests

| # | 测试方法 | 场景 | Mock 设置 | 断言 |
|---|---------|------|-----------|------|
| 10 | `LoadNote_PopulatesProperties` | 笔记已生成 | `GetNoteAsync` 返回含 Markdown 的 Note | `Note != null`，MarkdownHtml 含编码内容 |
| 11 | `LoadNote_NotReady_ReturnsNull` | 笔记尚未生成 | 返回 null | `Note == null`，`MarkdownHtml == ""` |
| 12 | `LoadNote_NullMarkdown_NoCrash` | 笔记无内容 | 返回 Note 但 ContentMarkdown 为 null | 不抛异常，`MarkdownHtml == ""` |

### 4.4 RecordingViewModelTests

| # | 测试方法 | 场景 | Mock 设置 | 断言 |
|---|---------|------|-----------|------|
| 13 | `Constructor_SelectsFirstMic` | 有麦克风设备 | `GetInputDevices()` → `["Mic1", "Mic2"]` | `SelectedMic == "Mic1"` |
| 14 | `Constructor_NoMics_EmptySelection` | 无麦克风 | `GetInputDevices()` → `[]` | `SelectedMic == ""` |
| 15 | `StartRecording_Success` | 录音启动成功 | `StartRecording` → true | `StatusText == "录音中"` |
| 16 | `StartRecording_AudioFails` | 录音设备失败 | `StartRecording` → false | `StatusText == "录音启动失败"` |
| 17 | `StopRecording_StopsAllAndFlushes` | 正常停止 | 全部成功 | 验证 `StopRecording`、`Stop`、`FlushQueueAsync`、`EndSessionAsync` 均被调用 |
| 18 | `ScreenshotCaptured_UploadsImage` | 截图事件触发 | 模拟触发 `ScreenshotCaptured` | `UploadScreenshotAsync` 被调用 |
| 19 | `Dispose_CleansResources` | 释放资源 | — | 所有 Dispose 被调用 |
| 20 | `ElapsedTime_UpdatesDisplay` | 计时器推进 | — | `ElapsedDisplay` 格式正确 |

### 4.5 RelayCommandTests

| # | 测试方法 | 场景 | 断言 |
|---|---------|------|------|
| 21 | `Execute_InvokesAction` | 执行命令 | `_execute` 被调用 |
| 22 | `CanExecute_DefaultTrue` | 无 predicate | `CanExecute(null) == true` |
| 23 | `CanExecute_RespectsPredicate` | 有 predicate | `() => false` → `CanExecute(null) == false` |

**汇总：23 个测试，覆盖 5 个类，零外部依赖。**

---

## 5. Mock 策略

### 5.1 IApiService Mock 模板

```csharp
var mockApi = new Mock<IApiService>();

// 登录成功
mockApi.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
       .ReturnsAsync("token123");

// 登录失败
mockApi.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
       .ThrowsAsync(new UnauthorizedAccessException("Invalid credentials"));

// 获取笔记
mockApi.Setup(x => x.GetNoteAsync(It.IsAny<Guid>()))
       .ReturnsAsync(new Note { ContentMarkdown = "# Test" });
```

### 5.2 IAudioService Mock 模板

```csharp
var mockAudio = new Mock<IAudioService>();
mockAudio.Setup(x => x.GetInputDevices()).Returns(new[] { "Mic 1", "Mic 2" });
mockAudio.Setup(x => x.StartRecording(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
```

### 5.3 IScreenshotService Mock 模板

```csharp
var mockScreenshot = new Mock<IScreenshotService>();

// 模拟触发事件
mockScreenshot.Setup(x => x.Start(It.IsAny<int>()))
    .Callback(() => mockScreenshot.Raise(x => x.ScreenshotCaptured += null,
        new ScreenshotResult { ImageData = new byte[] { 1, 2, 3 }, Type = ScreenshotType.NewSlide }));
```

### 5.4 IUploadService Mock 模板

```csharp
var mockUpload = new Mock<IUploadService>();
mockUpload.Setup(x => x.UploadScreenshotAsync(
    It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<double>(),
    It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>()))
    .ReturnsAsync(true);
mockUpload.Setup(x => x.FlushQueueAsync()).Returns(Task.CompletedTask);
```

---

## 6. 错误场景覆盖矩阵

| 错误场景 | 组件 | 测试覆盖 |
|---------|------|---------|
| API 登录凭据错误 | LoginVM | ✅ `Login_Failure_SetsErrorMessage` |
| API 网络不可达 | LoginVM, MainVM | ✅ `Login_NetworkError_HandledGracefully`、`LoadSessions_ApiError_ListEmpty` |
| API 创建失败 | MainVM | ✅ `StartRecording_ApiError_ReturnsNull` |
| 笔记未生成 | NoteVM | ✅ `LoadNote_NotReady_ReturnsNull` |
| 录音设备不可用 | RecordingVM | ✅ `StartRecording_AudioFails`、`Constructor_NoMics_EmptySelection` |
| 资源泄漏 | RecordingVM | ✅ `Dispose_CleansResources` |

---

## 7. 非功能性需求

- **测试执行时间：** < 5 秒（23 个纯逻辑测试，无 IO/网络）
- **离线运行：** 完全离线，无需数据库、服务端、音频设备
- **CI 兼容：** `dotnet test` 单命令运行，支持 Azure DevOps / GitHub Actions
- **可维护性：** 每个测试独立 Arrange-Act-Assert，不共享状态

---

## 8. 实施步骤

1. 新建 `ClassNote.Tests` 测试项目 + csproj
2. 在 `AudioService.cs`、`ScreenshotService.cs`、`UploadService.cs` 添加接口声明
3. 新建 3 个接口文件：`IAudioService.cs`、`IScreenshotService.cs`、`IUploadService.cs`
4. 重构 `RecordingViewModel` 构造函数为接口注入
5. 写测试文件（按依赖顺序）：
   - `RelayCommandTests.cs`
   - `LoginViewModelTests.cs`
   - `MainViewModelTests.cs`
   - `NoteViewModelTests.cs`
   - `RecordingViewModelTests.cs`
6. 运行全部测试，验证通过
7. 适配调用方代码（MainPage.xaml.cs / App.xaml.cs）

---

## 9. 附录

### 9.1 测试文件目录结构预期

```
ClassNote.Tests/
├── ClassNote.Tests.csproj
└── ViewModels/
    ├── LoginViewModelTests.cs
    ├── MainViewModelTests.cs
    ├── NoteViewModelTests.cs
    ├── RecordingViewModelTests.cs
    └── RelayCommandTests.cs
```

### 9.2 新增接口文件预期

```
ClassNote/Services/
├── IAudioService.cs          # NEW
├── IScreenshotService.cs     # NEW
├── IUploadService.cs         # NEW
├── IApiService.cs            # EXISTING
├── ApiService.cs             # EXISTING
├── AudioService.cs           # MODIFY (+ : IAudioService)
├── ScreenshotService.cs      # MODIFY (+ : IScreenshotService)
└── UploadService.cs          # MODIFY (+ : IUploadService)
```
