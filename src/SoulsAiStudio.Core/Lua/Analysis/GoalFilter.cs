namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>
/// Goal name filter that accepts shorthands (goal navigator, Project panel outline): <c>ken09</c> or <c>k9</c> finds
/// <c>Kengeki09</c>, <c>act1</c> finds <c>Act01</c> and <c>Act10</c>–<c>Act19</c>, <c>kact</c> finds <c>Kengeki_Activate</c>.
/// <para>
/// The name is cut into segments at case changes, letter/digit changes and underscores (<c>Kengeki_Activate</c> →
/// Kengeki, Activate; <c>Act05</c> → Act, 05). The query is cut into letter and digit runs; each run must match a
/// later segment, in order: letters as the start of a letter segment, digits by value (leading zeros ignored) as the
/// start of a number segment. A plain substring match always counts too.
/// </para>
/// </summary>
public static class GoalFilter
{
    public static bool Matches(string name, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        // Query tokens: digit runs, and letter runs (not split on case: "kAct" and "kact" are the same shorthand).
        var tokens = new List<string>();
        foreach (var part in query.Split(new[] { ' ', '_', '.' }, StringSplitOptions.RemoveEmptyEntries))
            for (int i = 0; i < part.Length;)
            {
                int j = i + 1;
                while (j < part.Length && char.IsDigit(part[j]) == char.IsDigit(part[i])) j++;
                tokens.Add(part[i..j]);
                i = j;
            }
        return Match(Segments(name), 0, tokens, 0, 0);
    }

    /// <summary>
    /// Tokens from <paramref name="t"/> (at offset <paramref name="off"/> inside it) against segments from <paramref name="s"/>.
    /// A letter token may be spread over consecutive segments as prefixes ("kact" = K-engeki + Act-ivate), so it backtracks.
    /// </summary>
    private static bool Match(List<string> segs, int s, List<string> tokens, int t, int off)
    {
        if (t == tokens.Count) return true;
        string tok = tokens[t];
        bool digits = char.IsDigit(tok[0]);
        for (int i = s; i < segs.Count; i++)
        {
            string seg = segs[i];
            if (char.IsDigit(seg[0]) != digits) continue;
            if (digits)
            {
                // "05" means exactly 05 (not 50); "5" means 5, 05, 50–59.
                bool ok = tok[0] == '0' ? seg.StartsWith(tok, StringComparison.Ordinal)
                    : NoLeadingZeros(seg).StartsWith(tok, StringComparison.Ordinal);
                if (ok && Match(segs, i + 1, tokens, t + 1, 0)) return true;
                continue;
            }
            // Longest first: the rest of the token, then shorter prefixes continuing on later segments.
            string rest = tok[off..];
            for (int n = Math.Min(rest.Length, seg.Length); n >= 1; n--)
            {
                if (!seg.StartsWith(rest[..n], StringComparison.OrdinalIgnoreCase)) continue;
                bool done = n == rest.Length;
                if (done ? Match(segs, i + 1, tokens, t + 1, 0) : Match(segs, i + 1, tokens, t, off + n)) return true;
            }
            if (off > 0) return false; // a token spread over segments must use consecutive ones
        }
        return false;
    }

    private static string NoLeadingZeros(string digits)
    {
        string t = digits.TrimStart('0');
        return t.Length == 0 ? "0" : t;
    }

    /// <summary>Letter runs (split before an uppercase letter that follows a lowercase one) and digit runs; '_' and other symbols separate.</summary>
    public static List<string> Segments(string text)
    {
        var result = new List<string>();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool boundary = i == text.Length || !char.IsLetterOrDigit(text[i]);
            if (!boundary && start >= 0)
            {
                char p = text[i - 1], c = text[i];
                boundary = char.IsDigit(p) != char.IsDigit(c) || (char.IsLower(p) && char.IsUpper(c));
                if (boundary) { result.Add(text[start..i]); start = i; continue; }
            }
            if (boundary)
            {
                if (start >= 0) result.Add(text[start..i]);
                start = -1;
            }
            else if (start < 0) start = i;
        }
        return result;
    }
}
