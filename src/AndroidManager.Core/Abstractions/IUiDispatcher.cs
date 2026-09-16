namespace AndroidManager.Core.Abstractions;

/// <summary>
/// Marshals work onto the UI thread without coupling Core to WPF.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action);
    Task InvokeAsync(Func<Task> action);
    Task<T> InvokeAsync<T>(Func<T> action);

    /// <summary>UI işini gözlemlenmiş fire-and-forget olarak çalıştırır.</summary>
    void Observe(Action action);

    /// <summary>Async UI işini gözlemlenmiş fire-and-forget olarak çalıştırır.</summary>
    void Observe(Func<Task> action);
}
