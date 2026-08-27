using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using System.IO;
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

    [Fact]
    public void SelectAll_SelectsAndClearsAllSessions()
    {
        // Arrange
        _vm.RecentSessions.Add(new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" });
        _vm.RecentSessions.Add(new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" });

        // Act — 全选
        _vm.SelectAll = true;

        // Assert
        Assert.Equal(2, _vm.SelectedCount);
        Assert.True(_vm.HasSelection);
        Assert.True(_vm.SelectAll);

        // Act — 取消全选
        _vm.SelectAll = false;

        // Assert
        Assert.Equal(0, _vm.SelectedCount);
        Assert.False(_vm.HasSelection);
        Assert.False(_vm.SelectAll);
    }

    [Fact]
    public async Task IndividualSelection_UpdatesCountAndSelectAllTriState()
    {
        // Arrange — 通过 LoadSessionsAsync 装载，模拟真实运行时会订阅勾选状态变更
        var a = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" };
        var b = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { a, b });
        await _vm.LoadSessionsAsync();

        // Act — 只勾选一条 → 部分选中（三态应为 null）
        a.IsSelected = true;

        // Assert
        Assert.Equal(1, _vm.SelectedCount);
        Assert.Null(_vm.SelectAll);

        // Act — 两条都勾选 → 全选
        b.IsSelected = true;

        // Assert
        Assert.True(_vm.SelectAll);
    }

    [Fact]
    public async Task DeleteSelectedAsync_DeletesOnlySelected()
    {
        // Arrange
        var selected = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed", IsSelected = true };
        var kept = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _vm.RecentSessions.Add(selected);
        _vm.RecentSessions.Add(kept);

        // Act
        await _vm.DeleteSelectedAsync();

        // Assert
        _mockApi.Verify(x => x.DeleteSessionAsync(selected.Id), Times.Once);
        _mockApi.Verify(x => x.DeleteSessionAsync(kept.Id), Times.Never);
    }

    [Fact]
    public async Task ExportSelectedPdf_ExportsNotesAndSkipsMissing()
    {
        // Arrange
        var withNote = new Session { Id = Guid.NewGuid(), Title = "数学 导数", Course = "数学", Status = "completed", IsSelected = true };
        var withoutNote = new Session { Id = Guid.NewGuid(), Title = "物理", Course = "物理", Status = "completed", IsSelected = true };
        _mockApi.Setup(x => x.ExportNotePdfAsync(withNote.Id)).ReturnsAsync(new byte[] { 1, 2, 3 });
        _mockApi.Setup(x => x.ExportNotePdfAsync(withoutNote.Id)).ReturnsAsync((byte[]?)null);
        _vm.RecentSessions.Add(withNote);
        _vm.RecentSessions.Add(withoutNote);

        var dir = Path.Combine(Path.GetTempPath(), "ClassNoteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Act
            var (exported, skipped) = await _vm.ExportSelectedPdfAsync(dir);

            // Assert
            Assert.Equal(1, exported);
            Assert.Equal(1, skipped);
            Assert.Single(Directory.GetFiles(dir, "*.pdf"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}