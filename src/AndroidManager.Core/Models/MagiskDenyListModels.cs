namespace AndroidManager.Core.Models;

public sealed class MagiskDenyListEntry
{
    public string Package { get; init; } = "";
    public string Process { get; init; } = "";

    public string Display =>
        string.IsNullOrWhiteSpace(Process) ||
        string.Equals(Package, Process, StringComparison.Ordinal)
            ? Package
            : $"{Package} | {Process}";
}

public sealed class MagiskDenyListSnapshot
{
    public bool EnforceEnabled { get; init; }
    public IReadOnlyList<MagiskDenyListEntry> Entries { get; init; } = [];
    public string Raw { get; init; } = "";
}

public sealed class MagiskDenyListPinResult
{
    public bool Success { get; init; }
    public int AddedOrPresent { get; init; }
    public bool EnforceDisabled { get; init; }
    public string Message { get; init; } = "";
}
