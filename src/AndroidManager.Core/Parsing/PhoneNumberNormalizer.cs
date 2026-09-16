namespace AndroidManager.Core.Parsing;

/// <summary>Telefon numarası normalizasyonu ve eşleştirme (SMS ↔ rehber).</summary>
public static class PhoneNumberNormalizer
{
    public static string Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        var trimmed = phone.Trim();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsDigit(ch) || ch == '+')
                sb.Append(ch);
        }

        return sb.Length > 0 ? sb.ToString() : trimmed;
    }

    public static string DigitsOnly(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        var sb = new System.Text.StringBuilder(phone.Length);
        foreach (var ch in phone)
        {
            if (char.IsDigit(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Türkiye (+90 / 0…) dahil numara eşleştirmesi.</summary>
    public static bool TryMatch(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
            return false;

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        var da = CanonicalNational(DigitsOnly(a));
        var db = CanonicalNational(DigitsOnly(b));
        if (da.Length == 0 || db.Length == 0)
            return false;

        if (da == db)
            return true;

        var min = Math.Min(da.Length, db.Length);
        if (min >= 7 && da[^min..] == db[^min..])
            return true;

        return false;
    }

    public static string? LookupDisplayName(
        IReadOnlyDictionary<string, string> index,
        string? phone)
    {
        if (index.Count == 0 || string.IsNullOrWhiteSpace(phone))
            return null;

        var key = Normalize(phone);
        if (key.Length > 0 && index.TryGetValue(key, out var exact))
            return exact;

        foreach (var kv in index)
        {
            if (TryMatch(key, kv.Key))
                return kv.Value;
        }

        return null;
    }

    /// <summary>Rehber indeksine eklenecek tüm anahtar varyantları.</summary>
    public static IEnumerable<string> IndexKeysFor(string? phone)
    {
        var normalized = Normalize(phone);
        if (normalized.Length == 0)
            yield break;

        yield return normalized;

        var digits = DigitsOnly(normalized);
        if (digits.Length == 0)
            yield break;

        yield return digits;

        var canonical = CanonicalNational(digits);
        if (canonical.Length > 0)
            yield return canonical;

        if (canonical.Length == 10)
        {
            yield return "0" + canonical;
            yield return "90" + canonical;
            yield return "+90" + canonical;
        }
    }

    private static string CanonicalNational(string digits)
    {
        if (digits.Length == 0)
            return string.Empty;

        var d = digits;
        while (d.Length > 10 && d.StartsWith('0'))
            d = d[1..];

        if (d.StartsWith("90", StringComparison.Ordinal) && d.Length > 10)
            d = d[2..];

        if (d.Length > 10)
            d = d[^10..];

        return d;
    }
}
