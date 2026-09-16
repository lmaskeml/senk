namespace AndroidManager.Core.Services;

/// <summary>lpmake.exe — dynamic super.img üretimi (SDK build-tools veya tools/lpmake).</summary>
public static class LpmakePathResolver
{
    public static string GetBundledToolsDirectory()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "tools", "lpmake"),
            Path.Combine(baseDir, "tools"),
            FindRepoToolsDirectory(baseDir)
        };

        foreach (var dir in candidates)
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        return Path.Combine(baseDir, "tools", "lpmake");
    }

    public static string? ResolveLpmakePath()
    {
        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>SDK build-tools'tan tools/lpmake/ altına kopyalar.</summary>
    public static string? TryProvisionBundledCopy()
    {
        var existing = ResolveLpmakePath();
        if (existing is not null)
            return existing;

        var source = FindInAndroidSdkBuildTools();
        if (source is null)
            return null;

        var destDir = GetBundledToolsDirectory();
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "lpmake.exe");
        File.Copy(source, dest, overwrite: true);
        return File.Exists(dest) ? dest : null;
    }

    public static IReadOnlyList<string> EnumerateCandidatePaths()
    {
        var list = new List<string>();
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (list.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(path);
        }

        Add(Environment.GetEnvironmentVariable("LPMAKE_PATH"));

        Add(Path.Combine(baseDir, "tools", "lpmake", "lpmake.exe"));
        Add(Path.Combine(baseDir, "tools", "lpmake.exe"));

        var repoTools = FindRepoToolsDirectory(baseDir);
        if (repoTools is not null)
        {
            Add(Path.Combine(repoTools, "lpmake", "lpmake.exe"));
            Add(Path.Combine(repoTools, "lpmake.exe"));
        }

        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (!string.IsNullOrWhiteSpace(fastboot))
        {
            var dir = Path.GetDirectoryName(fastboot);
            if (!string.IsNullOrWhiteSpace(dir))
                Add(Path.Combine(dir, "lpmake.exe"));
        }

        var sdk = FindInAndroidSdkBuildTools();
        if (sdk is not null)
            Add(sdk);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(Path.Combine(dir.Trim(), "lpmake.exe"));
        }

        return list;
    }

    private static string? FindInAndroidSdkBuildTools()
    {
        var roots = new List<string>();
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME")
                          ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(androidHome))
            roots.Add(androidHome);

        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk"));

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var buildTools = Path.Combine(root, "build-tools");
            if (!Directory.Exists(buildTools))
                continue;

            foreach (var versionDir in Directory.EnumerateDirectories(buildTools).OrderByDescending(d => d))
            {
                var candidate = Path.Combine(versionDir, "lpmake.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? FindRepoToolsDirectory(string startDir)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent!)
            {
                var tools = Path.Combine(dir.FullName, "tools");
                if (Directory.Exists(tools))
                    return tools;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
