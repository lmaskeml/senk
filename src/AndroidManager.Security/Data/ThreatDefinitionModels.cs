using AndroidManager.Core.Models;

namespace AndroidManager.Security.Data;

public sealed class ThreatDefinitions
{
    public string Version { get; set; } = "1.0.0";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public List<ThreatDefinition> KnownMalware { get; set; } = [];
    public List<ThreatDefinition> SuspiciousPackages { get; set; } = [];
    public List<ThreatDefinition> CertificateIocs { get; set; } = [];
    public List<PermissionRisk> DangerousPermissions { get; set; } = [];
    public List<PermissionCombination> DangerousCombinations { get; set; } = [];
    public List<string> KnownC2Servers { get; set; } = [];
    public List<string> KnownC2Ips { get; set; } = [];
    public List<StringRule> StringRules { get; set; } = [];
}

public sealed class ThreatDefinition
{
    public string Name { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string HashMd5 { get; set; } = "";
    public string HashSha256 { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public ThreatType Type { get; set; }
    public ThreatSeverity Severity { get; set; }
    public string Description { get; set; } = "";
}

public sealed class PermissionRisk
{
    public string Permission { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ThreatSeverity Severity { get; init; }
    public string Description { get; init; } = "";

    public PermissionRisk() { }

    public PermissionRisk(string perm, string name, ThreatSeverity sev, string desc)
    {
        Permission = perm;
        DisplayName = name;
        Severity = sev;
        Description = desc;
    }
}

public sealed class PermissionCombination
{
    public string Name { get; set; } = "";
    public List<string> Permissions { get; set; } = [];
    public ThreatSeverity Severity { get; set; }
    public string Description { get; set; } = "";
    public int ScoreWeight { get; set; } = 25;
}

public sealed class StringRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public int ScoreWeight { get; set; } = 10;
    public ThreatSeverity Severity { get; set; } = ThreatSeverity.Medium;
    public string Description { get; set; } = "";
}
