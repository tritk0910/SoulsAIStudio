namespace SoulsAiStudio.Editor.Completion;

/// <summary>
/// Filters and ranks suggestions for a typed prefix. Ranks (best first): case-sensitive prefix, case-insensitive
/// prefix, camel-hump / underscore acronym (<c>GSE</c> → <c>GetSpecialEffect…</c>, <c>gcr</c> → <c>GOAL_COMMON_ComboRepeat</c>),
/// substring. Within a rank: shorter label, then ordinal (case-insensitive). Reuses its buffers: no allocation per
/// call once warmed up.
/// </summary>
public sealed class CompletionFilter
{
    public const int RankPrefixExact = 0, RankPrefix = 1, RankHump = 2, RankSubstring = 3;

    /// <summary>Display order inside one rank: shorter label first, then case-insensitive, then ordinal.</summary>
    public static readonly IComparer<CompletionItem> LabelOrder = Comparer<CompletionItem>.Create(CompareLabels);

    static int CompareLabels(CompletionItem? x, CompletionItem? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        int c = x.Label.Length.CompareTo(y.Label.Length);
        if (c != 0)
            return c;
        c = string.Compare(x.Label, y.Label, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(x.Label, y.Label);
    }

    readonly List<CompletionItem>[] _buckets = { new(), new(), new(), new() };
    readonly List<CompletionItem> _results = new();

    /// <summary>Results of the last <see cref="Run"/>, best first.</summary>
    public IReadOnlyList<CompletionItem> Results => _results;

    /// <summary>
    /// Filters <paramref name="items"/> by <paramref name="prefix"/> (empty = everything) into <see cref="Results"/>,
    /// at most <paramref name="max"/>. Single pass into one bucket per rank. When <paramref name="presorted"/> is true
    /// the items are already in <see cref="LabelOrder"/> (as <see cref="CompletionSet"/> keeps them), so no sorting
    /// happens per keystroke; otherwise each bucket is sorted.
    /// </summary>
    public IReadOnlyList<CompletionItem> Run(ReadOnlySpan<char> prefix, CompletionItem[] items, bool presorted = false, int max = 500)
    {
        foreach (var bucket in _buckets)
            bucket.Clear();
        foreach (var it in items)
        {
            int r = Rank(it.Label, prefix);
            if (r >= 0)
                _buckets[r].Add(it);
        }

        _results.Clear();
        foreach (var bucket in _buckets)
        {
            if (_results.Count >= max)
                break;
            if (!presorted)
                bucket.Sort(LabelOrder);
            for (int i = 0; i < bucket.Count && _results.Count < max; i++)
                _results.Add(bucket[i]);
        }
        return _results;
    }

    /// <summary>Rank of <paramref name="label"/> for <paramref name="query"/>, or -1 if it does not match.</summary>
    public static int Rank(ReadOnlySpan<char> label, ReadOnlySpan<char> query)
    {
        if (query.IsEmpty)
            return RankPrefixExact;
        if (query.Length > label.Length)
            return -1;
        if (label.StartsWith(query, StringComparison.Ordinal))
            return RankPrefixExact;
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return RankPrefix;
        if (HumpMatch(label, query))
            return RankHump;
        if (label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            return RankSubstring;
        return -1;
    }

    /// <summary>
    /// True when every query char matches, in order, either the start of a "word" of the label (first char, an upper
    /// case letter after a lower case one, a char after '_' or a digit run start) or the char right after the previous
    /// match. Case-insensitive. Example: <c>gsea</c> matches <c>GetSpecialEffectActivateInterruptType</c>.
    /// </summary>
    public static bool HumpMatch(ReadOnlySpan<char> label, ReadOnlySpan<char> query)
    {
        int qi = 0, last = -2;
        for (int i = 0; i < label.Length && qi < query.Length; i++)
        {
            bool contiguous = i == last + 1;
            if (!contiguous && !IsHumpStart(label, i))
                continue;
            if (char.ToUpperInvariant(label[i]) == char.ToUpperInvariant(query[qi]))
            {
                qi++;
                last = i;
            }
            else if (contiguous && qi > 0)
            {
                // the run broke: only hump starts can match from here on
                last = -2;
                if (IsHumpStart(label, i) && char.ToUpperInvariant(label[i]) == char.ToUpperInvariant(query[qi]))
                {
                    qi++;
                    last = i;
                }
            }
        }
        return qi == query.Length;
    }

    static bool IsHumpStart(ReadOnlySpan<char> s, int i)
    {
        if (i == 0)
            return true;
        char p = s[i - 1], c = s[i];
        if (c == '_')
            return false;
        if (p == '_')
            return true;
        if (char.IsUpper(c) && !char.IsUpper(p))
            return true;
        if (char.IsDigit(c) && !char.IsDigit(p))
            return true;
        return false;
    }
}
