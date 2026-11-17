namespace SoulsAiStudio.Editor.Text;

/// <summary>
/// Plain-text search over a <see cref="TextBuffer"/>. Single-line queries only. No allocations when searching.
/// Options: match case, match whole word (the match must not touch a letter, digit or <c>_</c> on either side,
/// so <c>3001</c> does not match inside <c>30012</c> or <c>a3001</c>).
/// </summary>
internal sealed class FindModel
{
    char[] _query = new char[64];
    int _queryLength;
    long _countVersion = -1;
    int _countQueryStamp = -1;
    int _queryStamp;
    int _matchCount;

    public bool CaseSensitive { get; private set; }
    public bool WholeWord { get; private set; }

    public ReadOnlySpan<char> Query => _query.AsSpan(0, _queryLength);

    public bool HasQuery => _queryLength > 0;

    StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public void SetQuery(ReadOnlySpan<char> query, bool caseSensitive, bool wholeWord = false)
    {
        int nl = query.IndexOfAny('\r', '\n');
        if (nl >= 0)
            query = query[..nl];
        if (caseSensitive == CaseSensitive && wholeWord == WholeWord && query.SequenceEqual(Query))
            return;
        if (_query.Length < query.Length)
            _query = new char[Math.Max(query.Length, _query.Length * 2)];
        query.CopyTo(_query);
        _queryLength = query.Length;
        CaseSensitive = caseSensitive;
        WholeWord = wholeWord;
        _queryStamp++;
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>True when [start, start+length) of <paramref name="line"/> is not glued to a word character on either side.</summary>
    static bool IsWholeWord(ReadOnlySpan<char> line, int start, int length) =>
        (start == 0 || !IsWordChar(line[start - 1])) && (start + length >= line.Length || !IsWordChar(line[start + length]));

    /// <summary>First match of <paramref name="q"/> in <paramref name="line"/> starting at/after <paramref name="from"/>, or -1.</summary>
    static int IndexIn(ReadOnlySpan<char> line, int from, ReadOnlySpan<char> q, StringComparison cmp, bool wholeWord)
    {
        while (from <= line.Length - q.Length)
        {
            int idx = line[from..].IndexOf(q, cmp);
            if (idx < 0)
                return -1;
            idx += from;
            if (!wholeWord || IsWholeWord(line, idx, q.Length))
                return idx;
            from = idx + 1;
        }
        return -1;
    }

    /// <summary>Last match of <paramref name="q"/> fully inside <paramref name="line"/> (whole-word checked against the full line), or -1.</summary>
    static int LastIndexIn(ReadOnlySpan<char> fullLine, int limit, ReadOnlySpan<char> q, StringComparison cmp, bool wholeWord)
    {
        var span = fullLine[..limit];
        while (true)
        {
            int idx = span.LastIndexOf(q, cmp);
            if (idx < 0)
                return -1;
            if (!wholeWord || IsWholeWord(fullLine, idx, q.Length))
                return idx;
            if (idx + q.Length - 1 <= 0)
                return -1;
            span = span[..(idx + q.Length - 1)];
        }
    }

    int IndexIn(ReadOnlySpan<char> line, int from) => IndexIn(line, from, Query, Comparison, WholeWord);

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
            int idx;
            if (k == lines)
            {
                // wrapped back to the first line: only matches that start before 'from'
                idx = IndexIn(span, 0);
                if (idx >= from.Column)
                    idx = -1;
            }
            else
            {
                if (startCol > span.Length)
                    continue;
                idx = IndexIn(span, startCol);
            }
            if (idx >= 0)
            {
                start = new Coordinates(line, idx);
                end = new Coordinates(line, idx + _queryLength);
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
            var full = buffer.GetLineSpan(line);
            int limit = k == 0 ? before.Column : full.Length;
            int idx = LastIndexIn(full, limit, Query, Comparison, WholeWord);
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
        while (n < starts.Length)
        {
            int idx = IndexIn(line, col);
            if (idx < 0)
                break;
            starts[n++] = idx;
            col = idx + Math.Max(1, _queryLength);
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
        while (true)
        {
            int idx = IndexIn(line, col);
            if (idx < 0 || idx >= beforeColumn)
                break;
            n++;
            col = idx + _queryLength;
        }
        return n;
    }

    /// <summary>True when [start, end) is exactly one occurrence of the query (same line, same length, equal text, word rule).</summary>
    public bool IsMatchAt(TextBuffer buffer, Coordinates start, Coordinates end)
    {
        if (!HasQuery || start.Line != end.Line || end.Column - start.Column != _queryLength)
            return false;
        if (start.Line < 0 || start.Line >= buffer.LineCount)
            return false;
        var line = buffer.GetLineSpan(start.Line);
        if (start.Column < 0 || end.Column > line.Length)
            return false;
        return line[start.Column..end.Column].Equals(Query, Comparison)
               && (!WholeWord || IsWholeWord(line, start.Column, _queryLength));
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
    public int BuildReplaceAll(TextBuffer buffer, ReadOnlySpan<char> replacement, out int firstLine, out int lastLine, out string text) =>
        BuildEdit(buffer, replacement, swap: false, out firstLine, out lastLine, out text, out _);

    /// <summary>
    /// Text of lines [first, last] with every match of the query and of <paramref name="other"/> exchanged (A → B and
    /// B → A at the same time, so nothing is replaced twice), for a single-step Swap. Same case/whole-word options.
    /// Returns the total number of exchanged occurrences; <paramref name="otherCount"/> = how many of them were B.
    /// </summary>
    public int BuildSwapAll(TextBuffer buffer, ReadOnlySpan<char> other, out int firstLine, out int lastLine, out string text, out int otherCount) =>
        BuildEdit(buffer, other, swap: true, out firstLine, out lastLine, out text, out otherCount);

    int BuildEdit(TextBuffer buffer, ReadOnlySpan<char> other, bool swap, out int firstLine, out int lastLine, out string text, out int otherCount)
    {
        firstLine = lastLine = -1;
        text = "";
        otherCount = 0;
        if (!HasQuery || swap && (other.IsEmpty || other.Equals(Query, Comparison)))
            return 0;
        var q = Query;
        var cmp = Comparison;
        bool ww = WholeWord;
        int lines = buffer.LineCount;
        for (int i = 0; i < lines && firstLine < 0; i++)
            if (Next(buffer.GetLineSpan(i), 0, q, other, swap, cmp, ww, out _) >= 0)
                firstLine = i;
        if (firstLine < 0)
            return 0;
        for (int i = lines - 1; i >= firstLine && lastLine < 0; i--)
            if (Next(buffer.GetLineSpan(i), 0, q, other, swap, cmp, ww, out _) >= 0)
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
            while (true)
            {
                int idx = Next(span, col, q, other, swap, cmp, ww, out bool isB);
                if (idx < 0)
                    break;
                sb.Append(span[col..idx]);
                sb.Append(isB ? q : other); // replace: A -> other; swap: A -> B, B -> A
                count++;
                if (isB)
                    otherCount++;
                col = idx + (isB ? other.Length : q.Length);
            }
            sb.Append(span[col..]);
        }
        text = sb.ToString();
        return count;
    }

    /// <summary>Next occurrence of A or, when swapping, B: earliest start, the longer one on a tie.</summary>
    static int Next(ReadOnlySpan<char> line, int from, ReadOnlySpan<char> a, ReadOnlySpan<char> b, bool swap,
        StringComparison cmp, bool wholeWord, out bool isB)
    {
        int ia = IndexIn(line, from, a, cmp, wholeWord);
        int ib = swap ? IndexIn(line, from, b, cmp, wholeWord) : -1;
        isB = ib >= 0 && (ia < 0 || ib < ia || ib == ia && b.Length > a.Length);
        return isB ? ib : ia;
    }
}
