using System.Text.RegularExpressions;

namespace Tmax.Core.AiSessions;

/// <summary>"foo AND bar NOT baz" / "-baz" filter syntax (port of src/shared/and-filter.ts).</summary>
public static partial class AndFilter
{
    public readonly record struct Token(string Term, bool Negate);

    public static List<Token> Tokenize(string? query)
    {
        var raw = (query ?? "").Trim();
        var result = new List<Token>();
        if (raw.Length == 0) return result;
        var negateNext = false;
        foreach (var part in Operators().Split(raw))
        {
            var kw = part.Trim().ToUpperInvariant();
            if (kw == "AND") { negateNext = false; continue; }
            if (kw == "NOT") { negateNext = true; continue; }
            var t = part.Trim();
            if (t.Length == 0) continue;
            var negate = negateNext;
            negateNext = false;
            if (t.Length > 1 && t[0] == '-' && !char.IsWhiteSpace(t[1])) { negate = true; t = t[1..].Trim(); }
            if (t.Length == 0) continue;
            result.Add(new Token(t.ToLowerInvariant(), negate));
        }
        return result;
    }

    public static bool MatchesAll(string haystackLower, IReadOnlyList<Token> tokens)
    {
        foreach (var (term, negate) in tokens)
        {
            var present = haystackLower.Contains(term, StringComparison.Ordinal);
            if (negate ? present : !present) return false;
        }
        return true;
    }

    [GeneratedRegex(@"\b(AND|NOT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Operators();
}
