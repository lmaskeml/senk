namespace AndroidManager.Core.Abstractions;

/// <summary>Kişiler ekranından SMS sekmesine alıcı aktarımı.</summary>
public interface ISmsComposeBridge
{
    string? PendingAddress { get; }
    string? PendingDisplayName { get; }

    void RequestCompose(string address, string? displayName = null);

    event Action? ComposeRequested;

    void ClearPending();
}
