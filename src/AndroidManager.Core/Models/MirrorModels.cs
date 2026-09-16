namespace AndroidManager.Core.Models;

public sealed class ScrcpyOptions
{
    public string? Serial { get; init; }
    public int MaxFps { get; init; } = 60;
    public int BitRate { get; init; } = 8;
    public int MaxSize { get; init; } = 1080;
    public bool StayAwake { get; init; } = true;
    public bool ShowTouches { get; init; } = false;
    public bool NoControl { get; init; } = false;
    public string? RecordPath { get; init; }
}

public sealed class DeviceResolution
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Dpi { get; init; }
    public double AspectRatio => Width > 0 ? Height / (double)Width : 0;
}

public sealed class MirrorErrorEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
    public Exception? Error { get; init; }
}

public enum AndroidKeyCode
{
    Home = 3,
    Back = 4,
    Call = 5,
    EndCall = 6,
    VolumeUp = 24,
    VolumeDown = 25,
    Power = 26,
    Camera = 27,
    Menu = 82,
    Enter = 66,
    Delete = 67,
    Tab = 61,
    RecentApps = 187,
    Brightness = 220,
    Screenshot = 120
}
