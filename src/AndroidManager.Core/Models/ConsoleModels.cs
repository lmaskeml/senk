namespace AndroidManager.Core.Models;

public enum LogcatLevel
{
    Verbose = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
    Silent = 6
}

public sealed class LogcatStartOptions
{
    public LogcatLevel MinLevel { get; init; } = LogcatLevel.Verbose;
    public string? TagFilter { get; init; }
    public string? TextFilter { get; init; }
    public string Buffer { get; init; } = "main";
    public bool ClearBeforeStart { get; init; }
}

public sealed class LogcatLine
{
    public DateTime ReceivedAt { get; init; } = DateTime.Now;
    public string Timestamp { get; init; } = "";
    public string Pid { get; init; } = "";
    public string Tid { get; init; } = "";
    public LogcatLevel Level { get; init; } = LogcatLevel.Info;
    public string Tag { get; init; } = "";
    public string Message { get; init; } = "";
    public string Raw { get; init; } = "";

    public string LevelLetter => Level switch
    {
        LogcatLevel.Verbose => "V",
        LogcatLevel.Debug => "D",
        LogcatLevel.Info => "I",
        LogcatLevel.Warn => "W",
        LogcatLevel.Error => "E",
        LogcatLevel.Fatal => "F",
        _ => "?"
    };
}

public sealed class BootStatusInfo
{
    public string BootMode { get; init; } = "—";
    public string BootReason { get; init; } = "—";
    public string VerifiedBootState { get; init; } = "—";
    public string FlashLocked { get; init; } = "—";
    public string Secure { get; init; } = "—";
    public string SecurityPatch { get; init; } = "—";
    public string Serial { get; init; } = "—";
    public bool IsConnected { get; init; }
}
