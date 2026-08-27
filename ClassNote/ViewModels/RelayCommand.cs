using System.Windows.Input;

namespace ClassNote.ViewModels;

/// <summary>
/// 异步命令实现：防止 async void 异常直接令进程崩溃，并提供重入保护
/// （执行期间 CanExecute 返回 false，避免连点触发并发执行）。
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onException;
    private bool _isExecuting;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onException = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_isExecuting)
            return; // 重入保护：上一次执行尚未结束

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // async void 中未捕获的异常会冒泡到 Dispatcher 导致进程崩溃。
            // 优先交给调用方注入的处理器，否则退化为记录调试日志。
            if (_onException != null)
                _onException(ex);
            else
                System.Diagnostics.Debug.WriteLine($"[RelayCommand] 命令执行失败: {ex}");
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
