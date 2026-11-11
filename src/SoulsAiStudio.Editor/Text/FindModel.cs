namespace SoulsAiStudio.Editor.Text;

/// <summary>Plain-text search over a <see cref="TextBuffer"/>. Single-line queries only. No allocations when searching.</summary>
internal sealed class FindModel
{
    char[] _query = new char[64];
    int _queryLength;
    long _countVersion = -1;
    int _countQueryStamp = -1;
    int _queryStamp;
    int _matchCount;

    public bool CaseSensitive { get; private set; }

    public ReadOnlySpan<char> Query => _query.AsSpan(0, _queryLength);

    public bool HasQuery => _queryLength > 0;

    StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public void SetQuery(ReadOnlySpan<char> query, bool caseSensitive)
    {
        int nl = query.IndexOfAny('\r', '\n');
        if (nl >= 0)
            query = query[..nl];
        if (caseSensitive == CaseSensitive && query.SequenceEqual(Query))
            return;
        if (_query.Length < query.Length)
            _query = new char[Math.Max(query.Length, _query.Length * 2)];
        query.CopyTo(_query);
        _queryLength = query.Length;
        CaseSensitive = caseSensitive;
        _queryStamp++;
    }

    /// <summary>Next match at/after <paramref name="from"/> (wraps). Returns false if none.</summary>
    public bool FindNext(TextBuffer buffer, Coordinates from, out Coordinates start, out Coordinates end)
    {
        start = end = default;
        if (!HasQuery)
            return false;
        from = buffer.Sanitize(from);
        int lines = buffer.LineCount;
        for (int k = 0; k <= lines; k++)
        {
            int line = (from.Line + k) % lines;
            var span = buffer.GetLineSpan(line);
            int startCol = k == 0 ? from.Column : 0;
            if (k == lines)
            {
                // wrapped back to the first line: only the part before 'from'
                span = span[..Math.Min(span.Length, from.Column + _queryLength - 1)];
                startCol = 0;
            }
            if (startCol > span.Length)
                continue;
            int idx = span[startCol..].IndexOf(Query, Comparison);
            if (idx >= 0)
            {
                start = new Coordinates(line, startCol + idx);
                end = new Coordinates(line, startCol + idx + _queryLength);
                return true;
            }
        }
        return false;
    }

    /// <summary>Previous match strictly before <paramref name="before"/> (wraps). Returns false if none.</summary>
    public bool FindPrev(TextBuffer buffer, Coordinates before, out Coordinates start, out Coordinates end)
    {
        start = end = default;
        if (!HasQuery)
            return false;
        before = buffer.Sanitize(before);
        int lines = buffer.LineCount;
        for (int k = 0; k <= lines; k++)
        {
            int line = ((before.Line - k) % lines + lines) % lines;
            var span = buffer.GetLineSpan(line);
            if (k == 0)
                span = span[..before.Column];
            int idx = span.LastIndexOf(Query, Comparison);
            if (k == lines && idx >= 0 && idx < before.Column)
                idx = -1; // wrapped onto the start line: only matches after 'before' are new
            if (idx >= 0)
            {
                start = new Coordinates(line, idx);
                end = new Coordinates(line, idx + _queryLength);
                return true;
            }
        }
        return false;
    }

    /// <summary>Writes the start column of each match in <paramref name="line"/> into <paramref name="starts"/>; returns the count.</summary>
    public int MatchesInLine(ReadOnlySpan<char> line, Span<int> starts)
    {
        if (!HasQuery)
            return 0;
        int n = 0, col = 0;
        while (n < starts.Length && col <= line.Length - _queryLength)
        {
            int idx = line[col..].IndexOf(Query, Comparison);
            if (idx < 0)
                break;
            starts[n++] = col + idx;
            col += idx + Math.Max(1, _queryLength);
        }
        return n;
    }

    /// <summary>Total match count; recomputed only when the text or query changed.</summary>
    public int CountMatches(TextBuffer buffer)
    {
        if (!HasQuery)
            return 0;
        if (_countVersion == buffer.Version && _countQueryStamp == _queryStamp)
            return _matchCount;

        int total = 0;
        for (int i = 0; i < buffer.LineCount; i++)
        {
            var span = buffer.GetLineSpan(i);
            int col = 0;
            while (col <= span.Length - _queryLength)
            {
                int idx = span[col..].IndexOf(Query, Comparison);
                if (idx < 0)
                    break;
                total++;
                col += idx + _queryLength;
            }
        }
        _matchCount = total;
        _countVersion = buffer.Version;
        _countQueryStamp = _queryStamp;
        return total;
    }
}
