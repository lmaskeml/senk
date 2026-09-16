using System.Windows;
using System.Windows.Threading;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;

namespace AndroidManager.Shell.Services;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher()
    {
        _dispatcher = Application.Current?.Dispatcher
                      ?? throw new InvalidOperationException("WPF Application.Current yok.");
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public Task InvokeAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    public Task InvokeAsync(Func<Task> action)
    {
        if (CheckAccess())
            return action();

        return _dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (CheckAccess())
            return Task.FromResult(action());

        return _dispatcher.InvokeAsync(action).Task;
    }

    public void Observe(Action action) => ObservedTask.Run(InvokeAsync(action));

    public void Observe(Func<Task> action) => ObservedTask.Run(InvokeAsync(action));
}
