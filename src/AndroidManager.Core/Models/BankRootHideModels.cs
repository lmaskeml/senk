namespace AndroidManager.Core.Models;

public enum BankHideCheckStatus
{
    Pass,
    Fail,
    Warn,
    Unknown
}

public sealed class BankHideCheckItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public BankHideCheckStatus Status { get; init; } = BankHideCheckStatus.Unknown;
    public string FixHint { get; init; } = "";
}

public sealed class BankRootHideAudit
{
    public IReadOnlyList<BankHideCheckItem> Checks { get; init; } = [];
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public int WarnCount { get; init; }
    public bool ReadyForYkb =>
        FailCount == 0 &&
        Checks.Any(c => c.Id == "ykb_denylist" && c.Status == BankHideCheckStatus.Pass);

    public string Summary =>
        FailCount == 0
            ? (WarnCount > 0
                ? $"Hazirliga yakin ({PassCount} OK, {WarnCount} uyari)"
                : $"Tam stack OK ({PassCount} kontrol)")
            : $"{FailCount} kritik eksik — YKB tespit edebilir";
}

public sealed class BankRootHideActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}
