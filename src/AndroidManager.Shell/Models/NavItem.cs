namespace AndroidManager.Shell.Models;

public sealed class NavItem
{
    public required string Label { get; init; }
    public string? ViewName { get; init; }
    public string? IconKind { get; init; }
    /// <summary>Optional Prism navigation parameter (e.g. tab=debloat).</summary>
    public string? InitialTab { get; init; }
    public bool IsHeader => ViewName is null;
}
