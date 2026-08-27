using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;
using Xunit;

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

    [Fact]
    public async Task RefreshCommand_ReloadsSessions()
    {
        // Arrange
        var sessions = new List<Session>
        {
            new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
        };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(sessions);

        // Act
        _vm.RefreshCommand.Execute(null);
        // RefreshCommand runs async void -> wait for the async work to settle
        await Task.Delay(200);

        // Assert
        Assert.Single(_vm.RecentSessions);
    }

    [Fact]
    public void HasPendingSessions_TrueWhenProcessing()
    {
        // Arrange
        _vm.RecentSessions.Add(new Session { Id = Guid.NewGuid(), Course = "数学", Status = "processing" });

        // Assert
        Assert.True(_vm.HasPendingSessions);
    }

    [Fact]
    public void HasPendingSessions_FalseWhenEnded()
    {
        // Arrange — ended 是终态，不应再触发主页轮询（EV-01 修复后的行为）
        _vm.RecentSessions.Add(new Session { Id = Guid.NewGuid(), Course = "数学", Status = "ended" });

        // Assert
        Assert.False(_vm.HasPendingSessions);
    }

    [Fact]
    public void HasPendingSessions_FalseWhenAllTerminal()
    {
        // Arrange
        _vm.RecentSessions.Add(new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" });
        _vm.RecentSessions.Add(new Session { Id = Guid.NewGuid(), Course = "语文", Status = "failed" });

        // Assert
        Assert.False(_vm.HasPendingSessions);
    }

    [Fact]
    public void HasPendingSessions_FalseWhenEmpty()
    {
        // Assert
        Assert.False(_vm.HasPendingSessions);
    }
}
