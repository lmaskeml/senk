using AndroidManager.Core.Abstractions;

namespace AndroidManager.Messages.Services;

public sealed class SmsComposeBridge : ISmsComposeBridge
{
    public string? PendingAddress { get; private set; }
    public string? PendingDisplayName { get; private set; }

    public event Action? ComposeRequested;

    public void RequestCompose(string address, string? displayName = null)
    {
        PendingAddress = address.Trim();
        PendingDisplayName = displayName?.Trim();
        ComposeRequested?.Invoke();
    }

    public void ClearPending()
    {
        PendingAddress = null;
        PendingDisplayName = null;
    }
}
