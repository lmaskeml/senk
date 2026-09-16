namespace AndroidManager.Core.Services;

/// <summary>Windows: sekaiacg erofs-utils Cygwin_x86_64 paketindeki extract.erofs.</summary>
public static class ErofsUtilsPathResolver
{
    private static readonly string[] ExtractToolFileNames =
    [
        "extract.erofs.exe",
        "extract.erofs",
        "dump.erofs.exe",
        "dump.erofs"
    ];

    private static readonly string[] PackToolFileNames =
    [
        "mkfs.erofs.exe",
        "mkfs.erofs"
    ];

    /// <summary>Tek hedef klasör: &lt;app&gt;/tools/erofs — iç içe erofs/erofs üretilmez.</summary>
    public static string GetBundledToolsDirectory()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDir, "tools", "erofs");
    }

    public static string? ResolveExtractErofsPath() =>
        ResolveFirstExisting(ExtractToolFileNames, "EROFS_EXTRACT_PATH");

    public static string? ResolveMkfsErofsPath() =>
        ResolveFirstExisting(PackToolFileNames, "EROFS_MKFS_PATH");

    public static IEnumerable<string> EnumerateCandidatePaths()
    {
        foreach (var candidate in EnumerateNamedToolPaths(ExtractToolFileNames, "EROFS_EXTRACT_PATH"))
            yield return candidate;
    }

    private static string? ResolveFirstExisting(IReadOnlyList<string> names, string? envVarName)
    {
        foreach (var candidate in EnumerateNamedToolPaths(names, envVarName))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateNamedToolPaths(
        IReadOnlyList<string> names,
        string? envVarName)
    {
        if (!string.IsNullOrWhiteSpace(envVarName))
        {
            var fromEnv = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                yield return Environment.ExpandEnvironmentVariables(fromEnv.Trim().Trim('"'));
        }

        foreach (var dir in EnumerateSearchDirectories())
        {
            foreach (var name in names)
                yield return Path.Combine(dir, name);
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
                yield return Path.Combine(dir.Trim(), name);
        }
    }

    /// <summary>
    /// Kullanıcı dosyayı tools/erofs veya yanlışlıkla tools/erofs/erofs altına koymuş olabilir.
    /// Repo tools/erofs (kaynak) de taranır.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSearchDirectories()
    {
        var dirs = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            var full = Path.GetFullPath(path);
            if (dirs.Any(d => d.Equals(full, StringComparison.OrdinalIgnoreCase)))
                return;
            dirs.Add(full);
        }

        var bundled = GetBundledToolsDirectory();
        Add(bundled);
        Add(Path.Combine(bundled, "erofs")); // eski hatalı iç içe klasör

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Add(Path.Combine(baseDir, "tools"));
        Add(baseDir);

        var repoTools = FindRepoToolsDirectory(baseDir);
        if (repoTools is not null)
        {
            Add(Path.Combine(repoTools, "erofs"));
            Add(Path.Combine(repoTools, "erofs", "erofs"));
            Add(repoTools);
        }

        return dirs;
    }

    private static string? FindRepoToolsDirectory(string baseDir)
    {
        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var tools = Path.Combine(current.FullName, "tools");
            if (Directory.Exists(tools))
                return tools;

            current = current.Parent;
        }

        return null;
    }
}
