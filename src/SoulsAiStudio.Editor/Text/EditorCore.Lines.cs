namespace SoulsAiStudio.Editor.Text;

/// <summary>
/// VSCode-style line commands: move / copy lines (Alt+Up/Down, Shift+Alt+Up/Down), delete lines (Ctrl+Shift+K),
/// select line (Ctrl+L), insert line below/above (Ctrl+Enter / Ctrl+Shift+Enter), toggle <c>--</c> comment (Ctrl+/),
/// select word / add next occurrence (Ctrl+D). Each command is one undo step. Line commands act on the primary
/// selection only and drop secondary carets (see EditorCore.MultiCursor), except the comment toggle, which covers every caret.
/// </summary>
internal sealed partial class EditorCore
{
    /// <summary>Lines touched by the selection (a selection ending at column 0 of a line does not include that line).</summary>
    (int First, int Last) SelectedLines()
    {
        int first = SelectionStart.Line, last = SelectionEnd.Line;
        if (last > first && SelectionEnd.Column == 0)
            last--;
        return (first, last);
    }

    string LinesText(int first, int last)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = first; i <= last; i++)
        {
            if (i > first)
                sb.Append('\n');
            sb.Append(Buffer.GetLineSpan(i));
        }
        return sb.ToString();
    }

    static Coordinates Shift(Coordinates p, int lines) => new(p.Line + lines, p.Column);

    /// <summary>Alt+Up / Alt+Down: moves the selected lines one line up/down, keeping the selection on them.</summary>
    public bool MoveLines(int direction)
    {
        if (ReadOnly || direction == 0)
            return false;
        var (first, last) = SelectedLines();
        if (direction < 0 ? first == 0 : last >= Buffer.LineCount - 1)
            return false;
        string block = LinesText(first, last);
        int lo = direction < 0 ? first - 1 : first, hi = direction < 0 ? last : last + 1;
        string text = direction < 0
            ? block + "\n" + LinesText(first - 1, first - 1)
            : LinesText(last + 1, last + 1) + "\n" + block;
        int d = direction < 0 ? -1 : 1;
        return ReplaceRange(new Coordinates(lo, 0), new Coordinates(hi, Buffer.GetLineLength(hi)), text,
            after: new SelectionState(Shift(_anchor, d), Shift(_cursor, d)));
    }

    /// <summary>Shift+Alt+Up / Down: duplicates the selected lines above/below; the selection goes to the new copy (down) or stays (up).</summary>
    public bool CopyLines(int direction)
    {
        if (ReadOnly || direction == 0)
            return false;
        var (first, last) = SelectedLines();
        string block = LinesText(first, last);
        int count = last - first + 1;
        if (direction > 0)
        {
            var end = new Coordinates(last, Buffer.GetLineLength(last));
            return ReplaceRange(end, end, "\n" + block, after: new SelectionState(Shift(_anchor, count), Shift(_cursor, count)));
        }
        var start = new Coordinates(first, 0);
        return ReplaceRange(start, start, block + "\n", after: new SelectionState(_anchor, _cursor));
    }

    /// <summary>Ctrl+Shift+K: deletes the selected lines (the cursor stays on the same line number, same column if possible).</summary>
    public bool DeleteLines()
    {
        if (ReadOnly)
            return false;
        var (first, last) = SelectedLines();
        int col = _cursor.Column;
        Coordinates a, b;
        Coordinates after;
        if (last < Buffer.LineCount - 1)
        {
            a = new Coordinates(first, 0);
            b = new Coordinates(last + 1, 0);
            after = new Coordinates(first, Math.Min(col, Buffer.GetLineLength(last + 1)));
        }
        else if (first > 0)
        {
            a = new Coordinates(first - 1, Buffer.GetLineLength(first - 1));
            b = new Coordinates(last, Buffer.GetLineLength(last));
            after = new Coordinates(first - 1, Math.Min(col, Buffer.GetLineLength(first - 1)));
        }
        else
        {
            a = default;
            b = new Coordinates(last, Buffer.GetLineLength(last));
            after = default;
        }
        if (a == b)
            return false;
        return ReplaceRange(a, b, "", after: new SelectionState(after, after));
    }

    /// <summary>Ctrl+L: selects the current line (with its line break); pressed again, extends to the next line.</summary>
    public void SelectLineDown()
    {
        bool wholeLines = HasSelection && SelectionStart.Column == 0 && SelectionEnd.Column == 0;
        var start = new Coordinates(SelectionStart.Line, 0);
        int endLine = (wholeLines ? SelectionEnd.Line : SelectionEnd.Line) + 1;
        var end = endLine >= Buffer.LineCount ? Buffer.EndOfText : new Coordinates(endLine, 0);
        Select(start, end);
    }

    static int IndentOf(ReadOnlySpan<char> line)
    {
        int n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t'))
            n++;
        return n;
    }

    /// <summary>Ctrl+Enter (below) / Ctrl+Shift+Enter (above): new line with the current line's indentation, cursor on it.</summary>
    public bool InsertLine(bool above)
    {
        if (ReadOnly)
            return false;
        int line = _cursor.Line;
        var span = Buffer.GetLineSpan(line);
        string indent = new(span[..IndentOf(span)]);
        if (above)
        {
            var at = new Coordinates(line, 0);
            var c = new Coordinates(line, indent.Length);
            return ReplaceRange(at, at, indent + "\n", after: new SelectionState(c, c));
        }
        var end = new Coordinates(line, span.Length);
        var nc = new Coordinates(line + 1, indent.Length);
        return ReplaceRange(end, end, "\n" + indent, after: new SelectionState(nc, nc));
    }

    /// <summary>
    /// Ctrl+/: comments the lines of every caret / selection (multi-cursor from Ctrl+D, Alt+Click... included) with
    /// <c>-- </c> at the common indentation of each selection, or uncomments them when every non-blank one already
    /// starts with <c>--</c>. Blank lines are left alone. One undo step; all carets are kept.
    /// </summary>
    public bool ToggleLineComment()
    {
        if (ReadOnly)
            return false;
        // Line -> indentation to comment it at (the smallest indent of the selection it belongs to).
        var commentAt = new SortedDictionary<int, int>();
        var carets = SortedCarets(out _);
        foreach (var c in carets)
        {
            int first = c.Start.Line, last = c.End.Line;
            if (last > first && c.End.Column == 0)
                last--;
            int minIndent = int.MaxValue;
            for (int i = first; i <= last; i++)
            {
                var l = Buffer.GetLineSpan(i);
                int ind = IndentOf(l);
                if (ind < l.Length)
                    minIndent = Math.Min(minIndent, ind);
            }
            if (minIndent == int.MaxValue)
                continue;
            for (int i = first; i <= last; i++)
                commentAt[i] = commentAt.TryGetValue(i, out int prev) ? Math.Min(prev, minIndent) : minIndent;
        }
        bool allCommented = true, any = false;
        foreach (var (i, _) in commentAt)
        {
            var l = Buffer.GetLineSpan(i);
            int ind = IndentOf(l);
            if (ind == l.Length)
                continue;
            any = true;
            if (!l[ind..].StartsWith("--"))
                allCommented = false;
        }
        if (!any)
            return false;

        int firstLine = commentAt.Keys.First(), lastLine = commentAt.Keys.Last();
        int n = lastLine - firstLine + 1;
        var cols = new int[n];
        var deltas = new int[n];
        var sb = new System.Text.StringBuilder();
        for (int i = firstLine; i <= lastLine; i++)
        {
            if (i > firstLine)
                sb.Append('\n');
            var l = Buffer.GetLineSpan(i);
            int ind = IndentOf(l);
            if (!commentAt.TryGetValue(i, out int at) || ind == l.Length)
            {
                sb.Append(l);
                continue;
            }
            if (allCommented)
            {
                int remove = 2 + (ind + 2 < l.Length && l[ind + 2] == ' ' ? 1 : 0);
                sb.Append(l[..ind]).Append(l[(ind + remove)..]);
                cols[i - firstLine] = ind;
                deltas[i - firstLine] = -remove;
            }
            else
            {
                sb.Append(l[..at]).Append("-- ").Append(l[at..]);
                cols[i - firstLine] = at;
                deltas[i - firstLine] = 3;
            }
        }
        Coordinates Adjust(Coordinates p)
        {
            if (p.Line < firstLine || p.Line > lastLine)
                return p;
            int k = p.Line - firstLine;
            if (deltas[k] == 0 || p.Column < cols[k])
                return p;
            return new Coordinates(p.Line, Math.Max(cols[k], p.Column + deltas[k]));
        }
        SelectionState[]? extras = null;
        if (_extras.Count > 0)
        {
            extras = new SelectionState[_extras.Count];
            for (int i = 0; i < extras.Length; i++)
                extras[i] = new SelectionState(Adjust(_extras[i].Anchor), Adjust(_extras[i].Cursor));
        }
        return ReplaceRange(new Coordinates(firstLine, 0), new Coordinates(lastLine, Buffer.GetLineLength(lastLine)), sb.ToString(),
            after: new SelectionState(Adjust(_anchor), Adjust(_cursor)), afterExtras: extras);
    }

    /// <summary>
    /// Ctrl+D: with no selection, selects the word at the cursor; with a single-line selection, ADDS the next
    /// occurrence of the same text (case-sensitive, wraps, skips occurrences already selected) as a new selection that
    /// becomes the primary one. Returns false when every occurrence is already selected.
    /// </summary>
    public bool SelectNextOccurrence()
    {
        if (!HasSelection)
        {
            var (ws, we) = WordNearCursor(_cursor);
            if (ws == we)
                return false;
            var keep = _extras.ToArray();
            Select(ws, we);
            _extras.AddRange(keep);
            Normalize();
            return true;
        }
        if (SelectionStart.Line != SelectionEnd.Line)
            return false;
        string q = GetSelectedText();
        var occ = Occurrences(q);
        if (occ.Count == 0)
            return false;
        var from = SelectionEnd;
        int first = occ.FindIndex(p => p >= from);
        if (first < 0)
            first = 0;
        for (int k = 0; k < occ.Count; k++)
        {
            var s = occ[(first + k) % occ.Count];
            var e = new Coordinates(s.Line, s.Column + q.Length);
            if (IsSelected(s, e))
                continue;
            _extras.Add(new Caret(_anchor, _cursor, _preferredColumn));
            _anchor = s;
            _cursor = e;
            _preferredColumn = -1;
            Normalize();
            ScrollToCursorRequested = true;
            return true;
        }
        return false;
    }
}
