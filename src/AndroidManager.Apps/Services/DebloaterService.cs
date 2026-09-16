using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Apps.Services;

public sealed partial class DebloaterService : IDebloaterService
{
    private readonly IAdbService _adb;
    private readonly ILogger _logger;

    public DebloaterService(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<DebloaterService>();
    }

    public async Task<IReadOnlyList<DebloatCandidate>> GetDebloatableAppsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();

        var systemRaw = await _adb.ExecuteShellAsync("pm list packages -s", cancellationToken)
            .ConfigureAwait(false);
        var disabledRaw = await _adb.ExecuteShellAsync("pm list packages -d --user 0", cancellationToken)
            .ConfigureAwait(false);

        var systemPkgs = ParsePackageNames(systemRaw);
        var disabled = new HashSet<string>(ParsePackageNames(disabledRaw), StringComparer.OrdinalIgnoreCase);

        // Also include known bloat that may be listed as third-party (Facebook, etc.).
        var thirdPartyRaw = await _adb.ExecuteShellAsync("pm list packages -3", cancellationToken)
            .ConfigureAwait(false);
        var thirdParty = ParsePackageNames(thirdPartyRaw);

        var packages = systemPkgs
            .Concat(thirdParty.Where(p => DebloatCatalog.IsKnown(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var list = new List<DebloatCandidate>();
        foreach (var pkg in packages)
        {
            if (DebloatCatalog.IsBlocked(pkg))
                continue;

            var isSystem = systemPkgs.Contains(pkg, StringComparer.OrdinalIgnoreCase);
            var (category, reason, isKnown) = DebloatCatalog.Describe(pkg, isSystem);
            if (category == "Kritik")
                continue;

            if (!isKnown && !isSystem)
                continue;

            list.Add(new DebloatCandidate
            {
                PackageName = pkg,
                AppName = FormatAppName(pkg),
                Category = category,
                Reason = reason,
                IsKnownBloat = isKnown,
                IsDisabledForUser = disabled.Contains(pkg),
                IsSystemApp = isSystem,
                Risk = DebloatCatalog.GetRisk(category, isKnown)
            });
        }

        return list
            .OrderBy(c => c.Risk)
            .ThenByDescending(c => c.IsKnownBloat)
            .ThenBy(c => c.Category)
            .ThenBy(c => c.AppName)
            .ToList();
    }

    public async Task<DebloatResult> DisableAppAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (string.IsNullOrWhiteSpace(packageName))
            return Fail(packageName, "Paket adı boş.");
        if (DebloatCatalog.IsBlocked(packageName))
            return Fail(packageName, "Bu paket güvenlik nedeniyle engellendi.");

        var output = await _adb.ExecuteShellAsync(
                $"pm disable-user --user 0 {packageName}",
                cancellationToken)
            .ConfigureAwait(false);

        var ok = output.Contains("disabled", StringComparison.OrdinalIgnoreCase);
        _logger.Information("Debloat disable-user {Pkg}: {Output}", packageName, output.Trim());
        return new DebloatResult
        {
            Success = ok,
            PackageName = packageName,
            Message = ok ? "Devre dışı bırakıldı (pm disable-user)." : output.Trim()
        };
    }

    public async Task<DebloatResult> EnableAppAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (string.IsNullOrWhiteSpace(packageName))
            return Fail(packageName, "Paket adı boş.");

        // Prefer install-existing for packages removed via uninstall --user 0.
        var restore = await _adb.ExecuteShellAsync(
                $"cmd package install-existing --user 0 {packageName}",
                cancellationToken)
            .ConfigureAwait(false);

        var enabled = await _adb.ExecuteShellAsync(
                $"pm enable {packageName}",
                cancellationToken)
            .ConfigureAwait(false);

        var ok = enabled.Contains("enabled", StringComparison.OrdinalIgnoreCase)
                 || restore.Contains("installed for user", StringComparison.OrdinalIgnoreCase)
                 || (!LooksLikeError(restore) && !string.IsNullOrWhiteSpace(restore));

        _logger.Information("Debloat enable {Pkg}: enable={Enable} restore={Restore}",
            packageName, enabled.Trim(), restore.Trim());

        return new DebloatResult
        {
            Success = ok,
            PackageName = packageName,
            Message = ok ? "Etkinleştirildi / geri yüklendi." : $"{enabled.Trim()} | {restore.Trim()}"
        };
    }

    public async Task<DebloatResult> UninstallForUserAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (string.IsNullOrWhiteSpace(packageName))
            return Fail(packageName, "Paket adı boş.");
        if (DebloatCatalog.IsBlocked(packageName))
            return Fail(packageName, "Bu paket güvenlik nedeniyle engellendi.");

        var output = await _adb.ExecuteShellAsync(
                $"pm uninstall -k --user 0 {packageName}",
                cancellationToken)
            .ConfigureAwait(false);

        var ok = output.Contains("Success", StringComparison.OrdinalIgnoreCase);
        _logger.Information("Debloat uninstall-user {Pkg}: {Output}", packageName, output.Trim());
        return new DebloatResult
        {
            Success = ok,
            PackageName = packageName,
            Message = ok
                ? "Kullanıcı 0 için kaldırıldı (veri korundu, geri yüklenebilir)."
                : output.Trim()
        };
    }

    public async Task<IReadOnlyList<DebloatCandidate>> GetDisabledAppsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        var raw = await _adb.ExecuteShellAsync("pm list packages -d --user 0", cancellationToken)
            .ConfigureAwait(false);

        return ParsePackageNames(raw)
            .Where(p => !DebloatCatalog.IsBlocked(p))
            .Select(pkg =>
            {
                var (category, reason, isKnown) = DebloatCatalog.Describe(pkg, isSystem: true);
                return new DebloatCandidate
                {
                    PackageName = pkg,
                    AppName = FormatAppName(pkg),
                    Category = category,
                    Reason = reason,
                    IsKnownBloat = isKnown,
                    IsDisabledForUser = true,
                    IsSystemApp = true,
                    Risk = DebloatCatalog.GetRisk(category, isKnown)
                };
            })
            .OrderBy(c => c.AppName)
            .ToList();
    }

    public Task<IReadOnlyList<DebloatCategory>> GetCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DebloatCategory>>(DebloatCatalog.GetCategories());

    private void EnsureDevice()
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("DeBloater için bağlı bir cihaz seçin.");
    }

    private static DebloatResult Fail(string packageName, string message) => new()
    {
        Success = false,
        PackageName = packageName,
        Message = message
    };

    private static bool LooksLikeError(string output) =>
        output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Error", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Unknown", StringComparison.OrdinalIgnoreCase);

    private static List<string> ParsePackageNames(string raw) =>
        PackageOnlyRegex()
            .Matches(raw)
            .Select(m => m.Groups["pkg"].Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FormatAppName(string packageName)
    {
        var known = DebloatCatalog.TryGetFriendlyName(packageName);
        if (!string.IsNullOrWhiteSpace(known))
            return known!;

        var parts = packageName.Split('.');
        return parts.Length > 0
            ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(parts[^1])
            : packageName;
    }

    [GeneratedRegex(@"package:(?:.+=)?(?<pkg>[\w.]+)", RegexOptions.Compiled)]
    private static partial Regex PackageOnlyRegex();
}
