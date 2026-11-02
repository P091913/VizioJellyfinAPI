using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TvRemote;

public enum MatchKind { Found, Ambiguous, NotFound }

public sealed record MatchResult(MatchKind Kind, JfItem? Item, IReadOnlyList<JfItem> Candidates);

/// <summary>Scores Jellyfin search results against a spoken title.</summary>
public static class TitleMatcher
{
    private const double MinScore = 45;
    private const double TieMargin = 5;

    public static MatchResult Pick(ParsedCommand cmd, IEnumerable<JfItem> items)
    {
        var scored = items
            .DistinctBy(i => i.Id)
            .Select(i => (Item: i, Score: Score(cmd, i)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0 || scored[0].Score < MinScore)
        {
            var near = scored.Where(x => x.Score >= 25).Take(3).Select(x => x.Item).ToList();
            return new(MatchKind.NotFound, null, near);
        }

        var best = scored[0];
        var tied = scored.TakeWhile(x => best.Score - x.Score < TieMargin).Take(5).Select(x => x.Item).ToList();
        return tied.Count > 1
            ? new(MatchKind.Ambiguous, null, tied)
            : new(MatchKind.Found, best.Item, [best.Item]);
    }

    /// <summary>Search terms to send to Jellyfin, most specific first.</summary>
    public static IReadOnlyList<string> SearchVariants(ParsedCommand cmd)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
        }

        Add(cmd.Title);
        Add(cmd.TitleWithoutYear);
        var norm = Normalize(cmd.TitleWithoutYear ?? cmd.Title ?? "");
        Add(norm);
        Add(norm.Replace(' ', '-'));   // "scooby doo" -> "scooby-doo"
        return list;
    }

    /// <summary>First 4–5 letters of the first word, for when every full-title search misses.</summary>
    public static string? PrefixVariant(ParsedCommand cmd)
    {
        var first = Normalize(cmd.TitleWithoutYear ?? cmd.Title ?? "").Split(' ')[0];
        return first.Length >= 5 ? first[..Math.Min(5, first.Length - 1)] : null;
    }

    public static double Score(ParsedCommand cmd, JfItem item)
    {
        var score = TextScore(cmd.Title ?? "", item.Name);
        if (cmd.YearHint is int year && cmd.TitleWithoutYear is { } t)
        {
            var yearAdjust = item.ProductionYear is int y && Math.Abs(y - year) <= 1 ? 15 : -15;
            score = Math.Max(score, TextScore(t, item.Name) + yearAdjust);
        }
        return score;
    }

    public static double TextScore(string query, string name)
    {
        var q = Normalize(query);
        var n = Normalize(name);
        if (q.Length == 0 || n.Length == 0) return 0;
        if (n == q) return 100;

        var qc = q.Replace(" ", "");
        var nc = n.Replace(" ", "");
        if (nc == qc) return 98;                                  // "spiderman" vs "spider-man"
        if (n.StartsWith(q + " ") || nc.StartsWith(qc)) return 85; // "scooby doo" vs "scooby-doo 2"
        if (n.Contains(q)) return 70;
        if (n.Length >= 4 && q.Contains(n)) return 60;

        var qt = q.Split(' ').ToHashSet();
        var nt = n.Split(' ').ToHashSet();
        var union = qt.Union(nt).Count();
        return union == 0 ? 0 : 50.0 * qt.Intersect(nt).Count() / union;
    }

    public static string Normalize(string s)
    {
        var lower = s.ToLowerInvariant().Replace("&", " and ");
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        var n = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return n.StartsWith("the ") ? n[4..] : n;
    }
}
