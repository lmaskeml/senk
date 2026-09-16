namespace AndroidManager.Core;

/// <summary>
/// adb.exe / fastboot.exe yol çözümlemesi. Uygulama ile komut satırının aynı binary'yi kullanmasını sağlar.
/// </summary>
public static class PlatformToolsPathResolver
{
    private static readonly string[] WellKnownFastbootPaths =
    [
        @"C:\Program Files (x86)\Minimal ADB and Fastboot\fastboot.exe",
        @"C:\Program Files\Minimal ADB and Fastboot\fastboot.exe",
    ];

    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AndroidManager",
        "settings.json");

    public static string ResolveFastbootPath(string? configuredFastbootPath = null, string? configuredAdbPath = null)
    {
        if (configuredFastbootPath is null && configuredAdbPath is null)
            (configuredFastbootPath, configuredAdbPath) = TryLoadPathsFromSettingsFile();

        var fromSettings = ResolveExistingExecutable(configuredFastbootPath);
        if (fromSettings is not null)
            return fromSettings;

        var fromEnv = ResolveExistingExecutable(Environment.GetEnvironmentVariable("FASTBOOT_PATH"));
        if (fromEnv is not null)
            return fromEnv;

        foreach (var candidate in WellKnownFastbootPaths)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "fastboot.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sdkCandidate = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "fastboot.exe");
        if (File.Exists(sdkCandidate))
            return sdkCandidate;

        var adbPath = ResolveAdbPath(configuredAdbPath);
        if (!IsBundledToolPath(adbPath))
        {
            var sibling = SiblingFastboot(adbPath);
            if (sibling is not null)
                return sibling;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "adb", "fastboot.exe"),
                     Path.Combine(baseDir, "tools", "adb", "fastboot.exe"),
                     Path.Combine(baseDir, "tools", "platform-tools", "fastboot.exe"),
                     Path.Combine(baseDir, "fastboot.exe")
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "fastboot";
    }

    public static string ResolveAdbPath(string? configuredAdbPath = null)
    {
        var fromSettings = ResolveExistingExecutable(configuredAdbPath);
        if (fromSettings is not null)
            return fromSettings;

        var fromEnv = ResolveExistingExecutable(Environment.GetEnvironmentVariable("ADB_PATH"));
        if (fromEnv is not null)
            return fromEnv;

        var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb", "adb.exe");
        if (File.Exists(bundled))
            return bundled;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sdkCandidate = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(sdkCandidate))
            return sdkCandidate;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "adb.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return bundled;
    }

    public static string ResolveScrcpyPath(string? configuredScrcpyPath = null)
    {
        var fromSettings = ResolveExistingExecutable(configuredScrcpyPath);
        if (fromSettings is not null)
            return fromSettings;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "tools", "scrcpy", "scrcpy.exe"),
                     Path.Combine(baseDir, "scrcpy.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var wingetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetRoot))
        {
            var winget = Directory.GetFiles(wingetRoot, "scrcpy.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (winget is not null)
                return winget;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "scrcpy.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(baseDir, "tools", "scrcpy", "scrcpy.exe");
    }

    public static bool IsScrcpyBundleComplete(string scrcpyExePath)
    {
        if (string.IsNullOrWhiteSpace(scrcpyExePath) || !File.Exists(scrcpyExePath))
            return false;

        var dir = Path.GetDirectoryName(scrcpyExePath);
        if (string.IsNullOrWhiteSpace(dir))
            return false;

        return File.Exists(Path.Combine(dir, "scrcpy-server"))
               || File.Exists(Path.Combine(dir, "scrcpy-server.jar"));
    }

    public static (string? FastbootPath, string? AdbPath) TryLoadPathsFromSettingsFile()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return (null, null);

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsFilePath));
            var root = doc.RootElement;

            static string? ReadPath(System.Text.Json.JsonElement el, string name) =>
                el.TryGetProperty(name, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.GetString()
                    : null;

            return (ReadPath(root, "FastbootPath"), ReadPath(root, "AdbPath"));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? SiblingFastboot(string adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            return null;

        var dir = Path.GetDirectoryName(adbPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        var fastboot = Path.Combine(dir, "fastboot.exe");
        return File.Exists(fastboot) ? fastboot : null;
    }

    private static string? ResolveExistingExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

        if (File.Exists(path))
            return path;

        if (Directory.Exists(path))
        {
            var scrcpyInDir = Path.Combine(path, "scrcpy.exe");
            if (File.Exists(scrcpyInDir))
                return scrcpyInDir;

            var inDir = Path.Combine(path, "fastboot.exe");
            if (File.Exists(inDir))
                return inDir;

            var adbInDir = Path.Combine(path, "adb.exe");
            if (File.Exists(adbInDir))
                return SiblingFastboot(adbInDir);
        }

        var relative = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        if (File.Exists(relative))
            return relative;

        return null;
    }

    private static bool IsBundledToolPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath))
            return true;

        var baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var toolDir = Path.GetFullPath(Path.GetDirectoryName(fullPath) ?? fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return toolDir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
    }
}
