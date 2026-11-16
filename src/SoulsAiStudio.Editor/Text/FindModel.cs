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

    int[] _matchesBeforeLine = new int[64]; // cumulative match count before each line (valid with _matchCount)

    /// <summary>Total match count; recomputed only when the text or query changed.</summary>
    public int CountMatches(TextBuffer buffer)
    {
        if (!HasQuery)
            return 0;
        if (_countVersion == buffer.Version && _countQueryStamp == _queryStamp)
            return _matchCount;

        int lines = buffer.LineCount;
        if (_matchesBeforeLine.Length < lines + 1)
            _matchesBeforeLine = new int[Math.Max(lines + 1, _matchesBeforeLine.Length * 2)];
        int total = 0;
        for (int i = 0; i < lines; i++)
        {
            _matchesBeforeLine[i] = total;
            total += CountInLine(buffer.GetLineSpan(i), int.MaxValue);
        }
        _matchesBeforeLine[lines] = total;
        _matchCount = total;
        _countVersion = buffer.Version;
        _countQueryStamp = _queryStamp;
        return total;
    }

    /// <summary>Non-overlapping matches in <paramref name="line"/> that start before <paramref name="beforeColumn"/>.</summary>
    int CountInLine(ReadOnlySpan<char> line, int beforeColumn)
    {
        int n = 0, col = 0;
        while (col <= line.Length - _queryLength)
        {
            int idx = line[col..].IndexOf(Query, Comparison);
            if (idx < 0 || col + idx >= beforeColumn)
                break;
            n++;
            col += idx + _queryLength;
        }
        return n;
    }

    /// <summary>True when [start, end) is exactly one occurrence of the query (same line, same length, equal text).</summary>
    public bool IsMatchAt(TextBuffer buffer, Coordinates start, Coordinates end)
    {
        if (!HasQuery || start.Line != end.Line || end.Column - start.Column != _queryLength)
            return false;
        if (start.Line < 0 || start.Line >= buffer.LineCount)
            return false;
        var line = buffer.GetLineSpan(start.Line);
        if (start.Column < 0 || end.Column > line.Length)
            return false;
        return line[start.Column..end.Column].Equals(Query, Comparison);
    }

    /// <summary>
    /// 1-based index of the match [start, end) among all matches (in the same non-overlapping order as
    /// <see cref="CountMatches"/>), or 0 when the range is not one of them. O(length of that line) after the count is cached.
    /// </summary>
    public int MatchIndex(TextBuffer buffer, Coordinates start, Coordinates end)
    {
        if (!IsMatchAt(buffer, start, end))
            return 0;
        if (CountMatches(buffer) == 0)
            return 0;
        var line = buffer.GetLineSpan(start.Line);
        int before = CountInLine(line, start.Column);
        // the match must be one the enumeration actually lands on (overlapping queries like "aa" in "aaa")
        if (CountInLine(line, start.Column + 1) != before + 1)
            return 0;
        return _matchesBeforeLine[start.Line] + before + 1;
    }

    /// <summary>
    /// Text of lines [first, last] with every match replaced, for a single-step Replace All. Returns the number of
    /// replacements (0 = nothing to do, outputs undefined). Line count never changes (single-line query/replacement).
    /// </summary>
    public int BuildReplaceAll(TextBuffer buffer, ReadOnlySpan<char> replacement, out int firstLine, out int lastLine, out string text)
    {
        firstLine = lastLine = -1;
        text = "";
        if (!HasQuery)
            return 0;
        int lines = buffer.LineCount;
        for (int i = 0; i < lines && firstLine < 0; i++)
            if (buffer.GetLineSpan(i).IndexOf(Query, Comparison) >= 0)
                firstLine = i;
        if (firstLine < 0)
            return 0;
        for (int i = lines - 1; i >= firstLine && lastLine < 0; i--)
            if (buffer.GetLineSpan(i).IndexOf(Query, Comparison) >= 0)
                lastLine = i;

        int size = 0;
        for (int i = firstLine; i <= lastLine; i++)
            size += buffer.GetLineLength(i) + 1;
        var sb = new System.Text.StringBuilder(size + size / 8);
        int count = 0;
        for (int i = firstLine; i <= lastLine; i++)
        {
            if (i > firstLine)
                sb.Append('\n');
            var span = buffer.GetLineSpan(i);
            int col = 0;
            while (col <= span.Length - _queryLength)
            {
                int idx = span[col..].IndexOf(Query, Comparison);
                if (idx < 0)
                    break;
                sb.Append(span.Slice(col, idx));
                sb.Append(replacement);
                count++;
                col += idx + _queryLength;
            }
            sb.Append(span[col..]);
        }
        text = sb.ToString();
        return count;
    }
}
