namespace AndroidManager.Core.Models;

public sealed record ThemeAccentColor(string Hex, string DisplayName, string MaterialName);

public static class ThemeAccents
{
    public static IReadOnlyList<ThemeAccentColor> All { get; } =
    [
        new("#22D3EE", "Cyan", "Cyan"),
        new("#1976D2", "Mavi", "Blue"),
        new("#00838F", "Turkuaz", "Teal"),
        new("#388E3C", "Yeşil", "Green"),
        new("#7B1FA2", "Mor", "Purple"),
        new("#E53935", "Kırmızı", "Red"),
        new("#F57C00", "Turuncu", "Orange"),
        new("#AD1457", "Pembe", "Pink"),
        new("#5D4037", "Kahverengi", "Brown"),
        new("#455A64", "Gri-Mavi", "BlueGrey")
    ];
}
