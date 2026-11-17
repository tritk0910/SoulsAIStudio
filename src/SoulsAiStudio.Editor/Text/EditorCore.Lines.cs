namespace SoulsAiStudio.Editor.Text;

/// <summary>
/// VSCode-style line commands: move / copy lines (Alt+Up/Down, Shift+Alt+Up/Down), delete lines (Ctrl+Shift+K),
/// select line (Ctrl+L), insert line below/above (Ctrl+Enter / Ctrl+Shift+Enter), toggle <c>--</c> comment (Ctrl+/),
/// select word / next occurrence (Ctrl+D). Each command is one undo step.
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
    /// Ctrl+/: comments the selected lines with <c>-- </c> at their common indentation, or uncomments them when every
    /// non-blank one already starts with <c>--</c>. Blank lines are left alone.
    /// </summary>
    public bool ToggleLineComment()
    {
        if (ReadOnly)
            return false;
        var (first, last) = SelectedLines();
        int minIndent = int.MaxValue;
        bool allCommented = true, any = false;
        for (int i = first; i <= last; i++)
        {
            var l = Buffer.GetLineSpan(i);
            int ind = IndentOf(l);
            if (ind == l.Length)
                continue;
            any = true;
            minIndent = Math.Min(minIndent, ind);
            if (!l[ind..].StartsWith("--"))
                allCommented = false;
        }
        if (!any)
            return false;

        int n = last - first + 1;
        var cols = new int[n];
        var deltas = new int[n];
        var sb = new System.Text.StringBuilder();
        for (int i = first; i <= last; i++)
        {
            if (i > first)
                sb.Append('\n');
            var l = Buffer.GetLineSpan(i);
            int ind = IndentOf(l);
            if (ind == l.Length)
            {
                sb.Append(l);
                continue;
            }
            if (allCommented)
            {
                int remove = 2 + (ind + 2 < l.Length && l[ind + 2] == ' ' ? 1 : 0);
                sb.Append(l[..ind]).Append(l[(ind + remove)..]);
                cols[i - first] = ind;
                deltas[i - first] = -remove;
            }
            else
            {
                sb.Append(l[..minIndent]).Append("-- ").Append(l[minIndent..]);
                cols[i - first] = minIndent;
                deltas[i - first] = 3;
            }
        }
        Coordinates Adjust(Coordinates p)
        {
            if (p.Line < first || p.Line > last)
                return p;
            int k = p.Line - first;
            if (deltas[k] == 0 || p.Column < cols[k])
                return p;
            return new Coordinates(p.Line, Math.Max(cols[k], p.Column + deltas[k]));
        }
        return ReplaceRange(new Coordinates(first, 0), new Coordinates(last, Buffer.GetLineLength(last)), sb.ToString(),
            after: new SelectionState(Adjust(_anchor), Adjust(_cursor)));
    }

    /// <summary>
    /// Ctrl+D: with no selection, selects the word at the cursor; with a single-line selection, selects the next
    /// occurrence of the same text (case-sensitive, wraps). The editor has a single selection, so it moves rather than adds.
    /// </summary>
    public bool SelectNextOccurrence()
    {
        if (!HasSelection)
        {
            var (s, e) = WordRangeAt(_cursor);
            if (s == e)
                return false;
            Select(s, e);
            return true;
        }
        if (SelectionStart.Line != SelectionEnd.Line)
            return false;
        string q = GetSelectedText();
        int lines = Buffer.LineCount;
        var from = SelectionEnd;
        for (int k = 0; k <= lines; k++)
        {
            int line = (from.Line + k) % lines;
            var span = Buffer.GetLineSpan(line);
            int startCol = k == 0 ? from.Column : 0;
            if (startCol > span.Length)
                continue;
            int idx = span[startCol..].IndexOf(q.AsSpan(), StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var s = new Coordinates(line, startCol + idx);
            Select(s, new Coordinates(line, s.Column + q.Length));
            return true;
        }
        return false;
    }
}
