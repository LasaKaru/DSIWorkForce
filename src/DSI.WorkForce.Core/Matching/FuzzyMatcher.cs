using System.Text;

namespace DSI.WorkForce.Core.Matching;

/// <summary>Similarity matching used to snap noisy OCR text onto master data (items, employees, machines).</summary>
public static class FuzzyMatcher
{
    /// <summary>Normalized similarity 0..1 based on Levenshtein distance.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        var dist = Levenshtein(a, b);
        return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    public static (T? Best, double Score) Best<T>(string input, IEnumerable<T> candidates, Func<T, string> key) where T : class
    {
        T? best = null;
        var bestScore = 0.0;
        foreach (var c in candidates)
        {
            var s = Similarity(input, key(c));
            if (s > bestScore) { best = c; bestScore = s; }
        }
        return (best, bestScore);
    }

    /// <summary>
    /// Canonical form for item descriptions: upper-case, alphanumerics only, common shop-floor abbreviations expanded.
    /// "29 2.10 SRI 94 Full Black" and "29x2-10 SRI 94 F/B" both become "292 10SRI94FB".
    /// </summary>
    public static string CanonicalItem(string s)
    {
        var u = s.ToUpperInvariant()
            .Replace("FULL BLACK", "FB")
            .Replace("F/B", "FB")
            .Replace("F.B", "FB");
        var sb = new StringBuilder(u.Length);
        foreach (var ch in u)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>"P-24", "p 24", "P24" -> "P24"</summary>
    public static string CanonicalCode(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToUpperInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }
}
