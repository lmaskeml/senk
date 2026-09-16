namespace AndroidManager.Security.Engines;

public static class OemWhitelist
{
    private static readonly string[] Prefixes =
    [
        "android.",
        "com.android.",
        "com.google.",
        "com.google.android.",
        "com.samsung.",
        "com.sec.",
        "com.xiaomi.",
        "com.miui.",
        "com.huawei.",
        "com.honor.",
        "com.oppo.",
        "com.coloros.",
        "com.oneplus.",
        "com.oplus.",
        "com.vivo.",
        "com.bbk.",
        "com.motorola.",
        "com.sony.",
        "com.asus.",
        "com.lge.",
        "com.nokia.",
        "com.realme.",
        "com.nothing.",
        "com.qualcomm.",
        "com.mediatek.",
        "vendor.qti.",
        "com.qti."
    ];

    public static bool IsLikelyOemOrPlatform(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return false;
        return Prefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
