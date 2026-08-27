using ClassNote.ViewModels;
using Xunit;

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

    [Fact]
    public void Execute_Exception_DoesNotCrash_CallsHandler()
    {
        // Arrange
        Exception? captured = null;
        var cmd = new RelayCommand(
            () => throw new InvalidOperationException("boom"),
            onException: ex => captured = ex);

        // Act — should not throw despite the exception inside the delegate
        cmd.Execute(null);

        // Assert
        Assert.NotNull(captured);
        Assert.IsType<InvalidOperationException>(captured);
    }

    [Fact]
    public async Task Execute_Reentrancy_SecondCallSkipped()
    {
        // Arrange
        var tcs = new TaskCompletionSource();
        var calls = 0;
        var cmd = new RelayCommand(async () =>
        {
            calls++;
            await tcs.Task;
        });

        // Act — 第一次执行挂起在 await；第二次应被重入保护拦截
        cmd.Execute(null);
        cmd.Execute(null);
        tcs.SetResult();
        await Task.Delay(20); // 让第一次执行完成

        // Assert
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CanExecute_FalseWhileExecuting()
    {
        // Arrange
        var tcs = new TaskCompletionSource();
        var cmd = new RelayCommand(async () => await tcs.Task);

        // Act
        cmd.Execute(null);

        // Assert
        Assert.False(cmd.CanExecute(null));

        // Cleanup
        tcs.SetResult();
    }
}

