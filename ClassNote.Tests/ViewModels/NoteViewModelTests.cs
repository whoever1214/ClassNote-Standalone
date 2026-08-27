using ClassNote.Models;
using ClassNote.Services;
using ClassNote.ViewModels;
using Moq;
using Xunit;

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

    [Fact]
    public async Task LoadNote_ApiError_SetsErrorMessage_DoesNotThrow()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        _mockApi.Setup(x => x.GetNoteAsync(sessionId)).ThrowsAsync(new Exception("API down"));

        // Act — should not throw (anymore)
        await _vm.LoadNoteAsync(sessionId);

        // Assert
        Assert.True(_vm.HasError);
        Assert.Contains("加载笔记失败", _vm.ErrorMessage);
        Assert.False(_vm.IsLoading);
    }

    [Fact]
    public async Task LoadNote_RawHtml_IsEscaped()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var note = new Note
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ContentMarkdown = "<script>alert(1)</script>",
        };
        _mockApi.Setup(x => x.GetNoteAsync(sessionId)).ReturnsAsync(note);

        // Act
        await _vm.LoadNoteAsync(sessionId);

        // Assert — raw <script> 不得原样出现在输出 HTML 中（DisableHtml 生效）
        Assert.DoesNotContain("<script>alert(1)</script>", _vm.MarkdownHtml);
    }
}
