using System.Text.RegularExpressions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Data;

namespace AndroidManager.Security.Engines;

/// <summary>
/// Lightweight string-rule engine (YARA-style patterns without native YARA dependency).
/// </summary>
public sealed class RuleEngine
{
    private readonly ThreatDatabase _db;

    public RuleEngine(ThreatDatabase db) => _db = db;

    public List<ThreatEvidence> Match(string content)
    {
        var hits = new List<ThreatEvidence>();
        if (string.IsNullOrEmpty(content)) return hits;

        foreach (var rule in _db.StringRules)
        {
            try
            {
                if (Regex.IsMatch(content, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    hits.Add(new ThreatEvidence
                    {
                        Code = rule.Id,
                        Description = $"{rule.Name}: {rule.Description}",
                        ScoreWeight = rule.ScoreWeight
                    });
                }
            }
            catch
            {
                // invalid pattern — skip
            }
        }

        return hits;
    }
}
