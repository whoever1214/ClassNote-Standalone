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

        // Assert — 无筛选时可见列表与全部记录一致
        Assert.Equal(3, _vm.FilteredSessions.Count);
        Assert.Equal(3, _vm.VisibleCount);
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
        Assert.Equal(20, _vm.FilteredSessions.Count);
    }

    [Fact]
    public async Task LoadSessions_ApiError_ListEmpty()
    {
        // Arrange
        _mockApi.Setup(x => x.ListSessionsAsync()).ThrowsAsync(new Exception("API down"));

        // Act — should not throw
        await _vm.LoadSessionsAsync();

        // Assert
        Assert.Empty(_vm.FilteredSessions);
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
        Assert.Single(_vm.FilteredSessions);
    }

    [Fact]
    public void HasPendingSessions_TrueWhenProcessing()
    {
        // Arrange
        LoadWith(new Session { Id = Guid.NewGuid(), Course = "数学", Status = "processing" });

        // Assert
        Assert.True(_vm.HasPendingSessions);
    }

    [Fact]
    public void HasPendingSessions_FalseWhenEnded()
    {
        // Arrange — ended 是终态，不应再触发主页轮询（EV-01 修复后的行为）
        LoadWith(new Session { Id = Guid.NewGuid(), Course = "数学", Status = "ended" });

        // Assert
        Assert.False(_vm.HasPendingSessions);
    }

    [Fact]
    public void HasPendingSessions_FalseWhenAllTerminal()
    {
        // Arrange
        LoadWith(
            new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
            new Session { Id = Guid.NewGuid(), Course = "语文", Status = "failed" });

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
    public async Task SelectAll_SelectsAndClearsAllSessions()
    {
        // Arrange — 经 LoadSessionsAsync 装载，模拟真实运行时可见列表已同步
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session>
        {
            new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
            new() { Id = Guid.NewGuid(), Course = "英语", Status = "completed" },
        });
        await _vm.LoadSessionsAsync();

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
        var selected = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" };
        var kept = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { selected, kept });
        await _vm.LoadSessionsAsync();
        selected.IsSelected = true;

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
        var withNote = new Session { Id = Guid.NewGuid(), Title = "数学 导数", Course = "数学", Status = "completed" };
        var withoutNote = new Session { Id = Guid.NewGuid(), Title = "物理", Course = "物理", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { withNote, withoutNote });
        _mockApi.Setup(x => x.ExportNotePdfAsync(withNote.Id)).ReturnsAsync(new byte[] { 1, 2, 3 });
        _mockApi.Setup(x => x.ExportNotePdfAsync(withoutNote.Id)).ReturnsAsync((byte[]?)null);
        await _vm.LoadSessionsAsync();
        withNote.IsSelected = true;
        withoutNote.IsSelected = true;

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

    // ── 课程筛选（需求 3）────────────────────────────────────

    [Fact]
    public async Task CourseFilter_DefaultsToAllCourses_AndKeepsRecentOrder()
    {
        // Arrange — 库里按时间倒序返回（最近在前）
        var newest = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed", StartTime = new DateTime(2026, 8, 30, 23, 34, 0) };
        var older = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed", StartTime = new DateTime(2026, 8, 29, 10, 0, 0) };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { newest, older });

        // Act
        await _vm.LoadSessionsAsync();

        // Assert — 默认「全部课程」，顺序保持最近在前
        Assert.Equal(MainViewModel.AllCourses, _vm.CourseFilter);
        Assert.False(_vm.IsFiltered);
        Assert.Equal(new[] { newest.Id, older.Id }, _vm.FilteredSessions.Select(s => s.Id));
    }

    [Fact]
    public async Task CourseFilter_ShowsOnlyMatchingCourse()
    {
        // Arrange
        var math = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" };
        var english = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { math, english });
        await _vm.LoadSessionsAsync();

        // Act
        _vm.CourseFilter = "英语";

        // Assert
        Assert.True(_vm.IsFiltered);
        Assert.Single(_vm.FilteredSessions);
        Assert.Equal(english.Id, _vm.FilteredSessions[0].Id);
        Assert.Equal(1, _vm.VisibleCount);
        // 全部记录仍在内存中，切回「全部课程」即可恢复
        Assert.True(_vm.HasAnySessions);

        _vm.CourseFilter = MainViewModel.AllCourses;
        Assert.Equal(2, _vm.FilteredSessions.Count);
    }

    [Fact]
    public async Task CourseFilter_NoMatch_YieldsEmptyVisibleListButKeepsRecords()
    {
        // Arrange
        _mockApi.Setup(x => x.ListSessionsAsync())
            .ReturnsAsync(new List<Session> { new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" } });
        await _vm.LoadSessionsAsync();

        // Act
        _vm.CourseFilter = "地理";

        // Assert — 区分「一条都没有」与「筛选后为空」两种空状态
        Assert.Empty(_vm.FilteredSessions);
        Assert.True(_vm.HasAnySessions);
        Assert.Equal(MainViewModel.DefaultCourses.Length + 1, _vm.CourseFilters.Count);
    }

    [Fact]
    public async Task CourseFilters_ContainBuiltInCoursesAndCustomCourseNames()
    {
        // Arrange — 库里出现内置课程之外的自定义课程名
        _mockApi.Setup(x => x.ListSessionsAsync())
            .ReturnsAsync(new List<Session> { new() { Id = Guid.NewGuid(), Course = "自习（自定义）", Status = "completed" } });

        // Act
        await _vm.LoadSessionsAsync();

        // Assert
        Assert.Equal(MainViewModel.AllCourses, _vm.CourseFilters[0]);
        Assert.Contains("数学", _vm.CourseFilters);
        Assert.Contains("自习（自定义）", _vm.CourseFilters);
    }

    [Fact]
    public async Task Selection_SurvivesFilterSwitch()
    {
        // Arrange
        var math = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" };
        var english = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { math, english });
        await _vm.LoadSessionsAsync();
        math.IsSelected = true;
        Assert.Equal(1, _vm.SelectedCount);

        // Act — 切到英语（勾选的那条被筛掉）
        _vm.CourseFilter = "英语";

        // Assert — 计数只反映可见记录
        Assert.Equal(0, _vm.SelectedCount);
        Assert.False(_vm.HasSelection);

        // Act — 切回来
        _vm.CourseFilter = MainViewModel.AllCourses;

        // Assert — 勾选状态没有丢
        Assert.Equal(1, _vm.SelectedCount);
        Assert.True(math.IsSelected);
    }

    [Fact]
    public async Task SelectAll_OnlyAffectsVisibleRecords()
    {
        // Arrange
        var math = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" };
        var english = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { math, english });
        await _vm.LoadSessionsAsync();
        _vm.CourseFilter = "数学";

        // Act
        _vm.SelectAll = true;

        // Assert — 被筛掉的英语记录不应被勾选（避免误删）
        Assert.True(math.IsSelected);
        Assert.False(english.IsSelected);
        Assert.Equal(1, _vm.SelectedCount);

        // Act — 批量删除只作用于可见记录
        await _vm.DeleteSelectedAsync();
        _mockApi.Verify(x => x.DeleteSessionAsync(math.Id), Times.Once);
        _mockApi.Verify(x => x.DeleteSessionAsync(english.Id), Times.Never);
    }

    [Fact]
    public async Task SelectAll_StateRecomputedAfterFilterChange()
    {
        // Arrange
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session>
        {
            new() { Id = Guid.NewGuid(), Course = "数学", Status = "completed" },
            new() { Id = Guid.NewGuid(), Course = "英语", Status = "completed" },
        });
        await _vm.LoadSessionsAsync();
        _vm.SelectAll = true;
        Assert.True(_vm.SelectAll);

        // Act — 筛选后可见集合变化，三态应重新计算（单条命中 → 全选）
        _vm.CourseFilter = "数学";

        // Assert
        Assert.True(_vm.SelectAll);
        Assert.Equal(1, _vm.SelectedCount);
    }

    [Fact]
    public async Task HasPendingSessions_IgnoresFilter()
    {
        // Arrange — 进行中的记录属于数学，用户正在看英语
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session>
        {
            new() { Id = Guid.NewGuid(), Course = "数学", Status = "processing" },
            new() { Id = Guid.NewGuid(), Course = "英语", Status = "completed" },
        });
        await _vm.LoadSessionsAsync();

        // Act
        _vm.CourseFilter = "英语";

        // Assert — 轮询不能因为筛选停下，否则进度永远不刷新
        Assert.Single(_vm.FilteredSessions);
        Assert.True(_vm.HasPendingSessions);
    }

    // ── 单条记录右键操作（需求 4）─────────────────────────────

    [Fact]
    public async Task DeleteSessionAsync_DeletesOnlyThatRecord()
    {
        // Arrange
        var target = new Session { Id = Guid.NewGuid(), Course = "数学", Status = "completed" };
        var other = new Session { Id = Guid.NewGuid(), Course = "英语", Status = "completed" };
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(new List<Session> { target, other });
        await _vm.LoadSessionsAsync();

        // Act
        await _vm.DeleteSessionAsync(target);

        // Assert
        _mockApi.Verify(x => x.DeleteSessionAsync(target.Id), Times.Once);
        _mockApi.Verify(x => x.DeleteSessionAsync(other.Id), Times.Never);
    }

    [Fact]
    public async Task ExportSessionPdf_WritesFileNamedByTitle()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid(), Title = "数学 导数", Course = "数学", Status = "completed" };
        _mockApi.Setup(x => x.ExportNotePdfAsync(session.Id)).ReturnsAsync(new byte[] { 1, 2, 3 });

        var dir = Path.Combine(Path.GetTempPath(), "ClassNoteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Act
            var path = await _vm.ExportSessionPdfAsync(session, dir);

            // Assert
            Assert.NotNull(path);
            Assert.Equal("数学 导数.pdf", Path.GetFileName(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportSessionPdf_ReturnsNullWhenNoNote()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid(), Title = "物理", Course = "物理", Status = "processing" };
        _mockApi.Setup(x => x.ExportNotePdfAsync(session.Id)).ReturnsAsync((byte[]?)null);

        var dir = Path.Combine(Path.GetTempPath(), "ClassNoteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Act
            var path = await _vm.ExportSessionPdfAsync(session, dir);

            // Assert — 界面据此提示"尚未生成笔记"，而不是静默失败
            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportSessionPdf_SameTitleTwice_DoesNotOverwrite()
    {
        // Arrange
        var a = new Session { Id = Guid.NewGuid(), Title = "数学", Course = "数学", Status = "completed" };
        var b = new Session { Id = Guid.NewGuid(), Title = "数学", Course = "数学", Status = "completed" };
        _mockApi.Setup(x => x.ExportNotePdfAsync(It.IsAny<Guid>())).ReturnsAsync(new byte[] { 1 });

        var dir = Path.Combine(Path.GetTempPath(), "ClassNoteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Act
            var first = await _vm.ExportSessionPdfAsync(a, dir);
            var second = await _vm.ExportSessionPdfAsync(b, dir);

            // Assert
            Assert.Equal("数学.pdf", Path.GetFileName(first));
            Assert.Equal("数学 (2).pdf", Path.GetFileName(second));
            Assert.Equal(2, Directory.GetFiles(dir, "*.pdf").Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 经 LoadSessionsAsync 装载给定记录，使可见列表按默认「全部课程」同步就绪
    /// （避免直接改主集合造成列表与筛选条件不一致）。
    /// </summary>
    private void LoadWith(params Session[] sessions)
    {
        _mockApi.Setup(x => x.ListSessionsAsync()).ReturnsAsync(sessions.ToList());
        _vm.LoadSessionsAsync().GetAwaiter().GetResult();
    }
}
