namespace AndroidManager.Core.Models;

public sealed class MagiskModuleInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string VersionCode { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public bool RemovePending { get; init; }
    public bool UpdatePending { get; init; }
    public bool InstallPending { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string StatusLabel =>
        RemovePending ? "Kaldırılacak (reboot)"
        : InstallPending ? "Kurulacak (reboot)"
        : UpdatePending ? "Güncellenecek (reboot)"
        : IsEnabled ? "Açık" : "Kapalı";

    public string StatusColor =>
        RemovePending ? "#F44336"
        : InstallPending || UpdatePending ? "#FF9800"
        : IsEnabled ? "#4CAF50" : "#9E9E9E";
}

public sealed class MagiskModuleZipInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string FilePath { get; init; } = "";
}

public sealed class MagiskEnvironment
{
    public bool CorePresent { get; init; }
    public bool ManagerHidden { get; init; }
    public string Version { get; init; } = "";
    public string BinaryPath { get; init; } = "";
    public string HiddenManagerPackage { get; init; } = "";
    public string ModulesPath { get; init; } = "/data/adb/modules";
    public string Summary { get; init; } = "";
}

public sealed class MagiskModuleSnapshot
{
    public MagiskEnvironment Environment { get; init; } = new();
    public IReadOnlyList<MagiskModuleInfo> Modules { get; init; } = [];
}

public sealed class MagiskModuleInstallResult
{
    public bool Success { get; init; }
    public string FilePath { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public string Engine { get; init; } = "";
    public string Message { get; init; } = "";
    public bool RebootRequired { get; init; }
}
