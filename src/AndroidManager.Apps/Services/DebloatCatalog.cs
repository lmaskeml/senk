using AndroidManager.Core.Models;

namespace AndroidManager.Apps.Services;

internal static class DebloatCatalog
{
    // Known OEM/carrier/social bloat — safe candidates for user-0 uninstall (-k keeps data).
    private static readonly Dictionary<string, (string Category, string Reason)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.google.android.videos"] = ("Google", "Google TV / Play Movies"),
        ["com.google.android.music"] = ("Medya", "Play Music"),
        ["com.google.android.apps.tachyon"] = ("Google", "Google Duo / Meet"),
        ["com.google.android.apps.magazines"] = ("Google", "Newsstand / News"),
        ["com.samsung.android.bixby.agent"] = ("Samsung", "Bixby Agent"),
        ["com.samsung.android.app.spage"] = ("Samsung", "Bixby Home / Free"),
        ["com.samsung.android.game.gametools"] = ("Samsung", "Game Tools"),
        ["com.samsung.android.kidsinstaller"] = ("Samsung", "Samsung Kids"),
        ["com.miui.msa.global"] = ("Reklam / Analitik", "MSA reklam"),
        ["com.miui.analytics"] = ("Reklam / Analitik", "MIUI Analytics"),
        ["com.xiaomi.gamecenter"] = ("Xiaomi", "Game Center"),
        ["com.facebook.appmanager"] = ("Reklam / Analitik", "Facebook App Manager"),
        ["com.facebook.services"] = ("Reklam / Analitik", "Facebook Services"),
        ["com.facebook.katana"] = ("Sosyal", "Facebook uygulaması"),
        ["com.facebook.system"] = ("Sosyal", "Facebook sistem servisi"),
        ["com.facebook.orca"] = ("Sosyal", "Messenger"),
        ["com.instagram.android"] = ("Sosyal", "Instagram (önceden yüklü)"),
        ["com.whatsapp"] = ("Sosyal", "WhatsApp (önceden yüklü — dikkat)"),
        ["com.google.android.apps.youtube.music"] = ("Google", "YouTube Music"),
        ["com.google.android.apps.maps"] = ("Google", "Maps (isteğe bağlı)"),
        ["com.google.android.apps.photos"] = ("Google", "Google Photos"),
        ["com.google.android.apps.docs"] = ("Google", "Drive"),
        ["com.google.android.apps.books"] = ("Google", "Play Books"),
        ["com.google.android.apps.plus"] = ("Google", "Google+"),
        ["com.google.android.gm"] = ("Google", "Gmail (dikkat)"),
        ["com.android.chrome"] = ("Google", "Chrome (dikkat)"),
        ["com.samsung.android.game.gamehome"] = ("Samsung", "Game Launcher"),
        ["com.samsung.android.app.tips"] = ("Samsung", "İpuçları"),
        ["com.samsung.android.ardrawing"] = ("Samsung", "AR Doodle"),
        ["com.samsung.android.aremojieditor"] = ("Samsung", "AR Emoji"),
        ["com.samsung.android.app.watchmanager"] = ("Samsung", "Galaxy Wearable"),
        ["com.xiaomi.mipicks"] = ("Xiaomi", "GetApps / Mi Picks"),
        ["com.mi.global.shop"] = ("Xiaomi", "Mi Store"),
        ["com.miui.yellowpage"] = ("Xiaomi", "Yellow Pages"),
        ["com.coloros.gamespace"] = ("OPPO/ColorOS", "Game Space"),
        ["com.heytap.market"] = ("OPPO/ColorOS", "App Market"),
        ["com.oppo.market"] = ("OPPO/ColorOS", "OPPO Market"),
        ["com.vivo.browser"] = ("vivo", "vivo Browser"),
        ["com.vivo.appstore"] = ("vivo", "vivo App Store"),
        ["com.huawei.appmarket"] = ("Huawei", "AppGallery"),
        ["com.huawei.hwid"] = ("Huawei", "Huawei ID (dikkat)"),
        ["com.netflix.mediaclient"] = ("Operatör/OEM", "Netflix ön yükleme"),
        ["com.amazon.mShop.android.shopping"] = ("Operatör/OEM", "Amazon Shopping"),
        ["com.amazon.appmanager"] = ("Operatör/OEM", "Amazon App Manager"),
        ["com.linkedin.android"] = ("Sosyal", "LinkedIn"),
        ["com.microsoft.office.outlook"] = ("Üretici", "Outlook ön yükleme"),
        ["com.skype.raider"] = ("Üretici", "Skype"),
        ["com.spotify.music"] = ("Medya", "Spotify ön yükleme"),
        ["com.android.vending"] = ("Kritik", "Play Store — kaldırmayın"),
        ["com.google.android.gms"] = ("Kritik", "Play Services — kaldırmayın"),
        ["com.android.settings"] = ("Kritik", "Ayarlar — kaldırmayın"),
        ["com.android.systemui"] = ("Kritik", "SystemUI — kaldırmayın"),
        ["com.android.phone"] = ("Kritik", "Telefon — kaldırmayın"),
    };

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.android.vending",
        "com.google.android.gms",
        "com.google.android.gsf",
        "com.android.settings",
        "com.android.systemui",
        "com.android.phone",
        "com.android.launcher",
        "com.android.launcher3",
        "com.google.android.permissioncontroller",
        "com.android.permissioncontroller",
        "com.android.providers.settings",
        "android",
    };

    public static bool IsBlocked(string packageName) => Blocked.Contains(packageName);

    public static bool IsKnown(string packageName) => Known.ContainsKey(packageName);

    public static string? TryGetFriendlyName(string packageName) =>
        Known.TryGetValue(packageName, out var known) ? known.Reason.Split('(')[0].Trim() : null;

    public static (string Category, string Reason, bool IsKnown) Describe(string packageName, bool isSystem)
    {
        if (Known.TryGetValue(packageName, out var known))
            return (known.Category, known.Reason, true);

        if (isSystem)
            return ("Sistem", "Sistem / OEM paketi", false);

        return ("Diğer", "Kullanıcı veya bilinmeyen paket", false);
    }

    public static DebloatRisk GetRisk(string category, bool isKnown) => category switch
    {
        "Kritik" => DebloatRisk.Danger,
        "Google" => DebloatRisk.Caution,
        "Sistem" => DebloatRisk.Caution,
        "Reklam / Analitik" or "Sosyal" or "Samsung" or "Xiaomi" or "OPPO/ColorOS" or "vivo"
            or "Huawei" or "Operatör/OEM" or "Üretici" or "Medya" => DebloatRisk.Safe,
        _ when isKnown => DebloatRisk.Safe,
        _ => DebloatRisk.Caution
    };

    public static IReadOnlyList<DebloatCategory> GetCategories() =>
    [
        new() { Name = "Reklam / Analitik", Icon = "📊", Color = "#F44336" },
        new() { Name = "Sosyal", Icon = "💬", Color = "#2196F3" },
        new() { Name = "Google", Icon = "🌐", Color = "#4285F4" },
        new() { Name = "Samsung", Icon = "⭐", Color = "#1565C0" },
        new() { Name = "Xiaomi", Icon = "🔴", Color = "#D32F2F" },
        new() { Name = "Medya", Icon = "🎵", Color = "#9C27B0" },
        new() { Name = "OPPO/ColorOS", Icon = "📱", Color = "#FF5722" },
        new() { Name = "vivo", Icon = "📱", Color = "#607D8B" },
        new() { Name = "Huawei", Icon = "📱", Color = "#CF0A2C" },
        new() { Name = "Operatör/OEM", Icon = "📡", Color = "#795548" },
        new() { Name = "Üretici", Icon = "📦", Color = "#607D8B" },
        new() { Name = "Sistem", Icon = "⚙", Color = "#455A64" },
        new() { Name = "Diğer", Icon = "📦", Color = "#607D8B" }
    ];

    public static bool IsLikelyBloat(string packageName, bool isSystem)
    {
        if (IsBlocked(packageName))
            return false;
        if (Known.ContainsKey(packageName))
            return Known[packageName].Category is not "Kritik";
        return isSystem;
    }
}
