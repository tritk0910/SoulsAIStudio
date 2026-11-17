namespace SoulsAiStudio.Editor.Text;

/// <summary>A secondary caret / selection (the primary one lives in <see cref="EditorCore"/>'s anchor/cursor fields).</summary>
internal struct Caret
{
    public Coordinates Anchor;
    public Coordinates Cursor;
    public int PreferredColumn;

    public Caret(Coordinates anchor, Coordinates cursor, int preferredColumn = -1)
    {
        Anchor = anchor;
        Cursor = cursor;
        PreferredColumn = preferredColumn;
    }

    public readonly Coordinates Start => Coordinates.Min(Anchor, Cursor);
    public readonly Coordinates End => Coordinates.Max(Anchor, Cursor);
    public readonly bool IsEmpty => Anchor == Cursor;
}

/// <summary>
/// VSCode-like multiple cursors. The primary selection is the classic anchor/cursor pair (find bar, completion,
/// scrolling, status line and line commands use it); secondary carets are kept in <see cref="_extras"/>.
/// <list type="bullet">
/// <item>Typing, Backspace/Delete (incl. word), Enter, Tab (no multi-line selection), paste, cut/copy and caret movement
/// apply to every caret; each multi-caret edit is ONE undo step that also restores all carets.</item>
/// <item>Overlapping selections (or carets at the same place) are merged after every change.</item>
/// <item>Line commands (move/copy/delete lines, comment, indent, Ctrl+Enter, Ctrl+L) work on the primary selection only
/// and drop the secondary carets (they can be brought back with undo).</item>
/// <item><see cref="EditorCore.Select"/> / <see cref="EditorCore.SetCursor"/> (clicks, find, go to line) drop them too.</item>
/// </list>
/// </summary>
internal sealed partial class EditorCore
{
    readonly List<Caret> _extras = new();

    public bool HasExtras => _extras.Count > 0;

    /// <summary>Number of secondary carets (total carets = this + 1).</summary>
    public int ExtraCount => _extras.Count;

    /// <summary>Secondary caret <paramref name="index"/>. They are kept sorted by position and never overlap.</summary>
    public Caret GetExtra(int index) => _extras[index];

    /// <summary>Index of the first secondary caret whose selection ends on or after <paramref name="line"/> (binary search).</summary>
    public int FirstExtraEndingAtOrAfter(int line)
    {
        int lo = 0, hi = _extras.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_extras[mid].End.Line < line)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    public int CaretCount => _extras.Count + 1;

    /// <summary>Every selection (primary included) sorted by position (allocates; for tests and commands).</summary>
    public List<(Coordinates Start, Coordinates End, bool Primary)> AllSelections()
    {
        var list = SortedCarets(out int primary);
        var result = new List<(Coordinates, Coordinates, bool)>(list.Count);
        for (int i = 0; i < list.Count; i++)
            result.Add((list[i].Start, list[i].End, i == primary));
        return result;
    }

    /// <summary>Escape: keeps only the primary caret. Returns true if there were others.</summary>
    public bool CollapseToPrimary()
    {
        if (_extras.Count == 0)
            return false;
        _extras.Clear();
        ScrollToCursorRequested = true;
        return true;
    }

    /// <summary>Alt+Click: adds a caret at <paramref name="pos"/> (it becomes the primary one).</summary>
    public void AddCaret(Coordinates pos)
    {
        pos = Buffer.Sanitize(pos);
        _extras.Add(new Caret(_anchor, _cursor, _preferredColumn));
        _anchor = _cursor = pos;
        _preferredColumn = -1;
        Normalize();
        ScrollToCursorRequested = true;
    }

    /// <summary>Ctrl+Alt+Up / Down: adds a caret on the line above the topmost caret / below the bottommost one.</summary>
    public bool AddCaretVertical(int direction)
    {
        if (direction == 0)
            return false;
        var edge = new Caret(_anchor, _cursor, _preferredColumn);
        foreach (var c in _extras)
            if (direction < 0 ? c.Cursor.Line < edge.Cursor.Line : c.Cursor.Line > edge.Cursor.Line)
                edge = c;
        int line = edge.Cursor.Line + (direction < 0 ? -1 : 1);
        if (line < 0 || line >= Buffer.LineCount)
            return false;
        int col = edge.PreferredColumn >= 0 ? edge.PreferredColumn : edge.Cursor.Column;
        _extras.Add(new Caret(_anchor, _cursor, _preferredColumn));
        _anchor = _cursor = new Coordinates(line, Math.Min(col, Buffer.GetLineLength(line)));
        _preferredColumn = col;
        Normalize();
        ScrollToCursorRequested = true;
        return true;
    }

    SelectionState[]? ExtrasSnapshot()
    {
        if (_extras.Count == 0)
            return null;
        var a = new SelectionState[_extras.Count];
        for (int i = 0; i < a.Length; i++)
            a[i] = new SelectionState(_extras[i].Anchor, _extras[i].Cursor);
        return a;
    }

    void RestoreExtras(SelectionState[]? extras)
    {
        _extras.Clear();
        if (extras == null)
            return;
        foreach (var e in extras)
            _extras.Add(new Caret(Buffer.Sanitize(e.Anchor), Buffer.Sanitize(e.Cursor)));
        Normalize();
    }

    /// <summary>Runs a single-caret movement on every caret (by swapping each into the primary fields), then merges.</summary>
    void ForEachCaret(Action move)
    {
        if (_extras.Count == 0)
        {
            move();
            return;
        }
        var pa = _anchor;
        var pc = _cursor;
        int pp = _preferredColumn;
        for (int i = 0; i < _extras.Count; i++)
        {
            var c = _extras[i];
            _anchor = c.Anchor;
            _cursor = c.Cursor;
            _preferredColumn = c.PreferredColumn;
            move();
            _extras[i] = new Caret(_anchor, _cursor, _preferredColumn);
        }
        _anchor = pa;
        _cursor = pc;
        _preferredColumn = pp;
        move(); // primary last, so the scroll request follows it
        Normalize();
    }

    /// <summary>All carets sorted by start; <paramref name="primary"/> = index of the primary one.</summary>
    List<Caret> SortedCarets(out int primary)
    {
        var list = new List<Caret>(_extras.Count + 1) { new(_anchor, _cursor, _preferredColumn) };
        list.AddRange(_extras);
        var order = new int[list.Count];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            int c = list[x].Start.CompareTo(list[y].Start);
            return c != 0 ? c : list[x].End.CompareTo(list[y].End);
        });
        var sorted = new List<Caret>(list.Count);
        primary = 0;
        for (int i = 0; i < order.Length; i++)
        {
            if (order[i] == 0)
                primary = i;
            sorted.Add(list[order[i]]);
        }
        return sorted;
    }

    /// <summary>Merges overlapping selections and carets at the same place. Keeps the primary one primary.</summary>
    void Normalize()
    {
        if (_extras.Count == 0)
            return;
        var sorted = SortedCarets(out int primary);
        var merged = new List<Caret>(sorted.Count);
        int mergedPrimary = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            var c = sorted[i];
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                bool overlap = c.Start < prev.End || c.Start == prev.End && (c.IsEmpty || prev.IsEmpty);
                if (overlap)
                {
                    var start = prev.Start;
                    var end = Coordinates.Max(prev.End, c.End);
                    // keep the direction of the primary one, else of the later one
                    bool prevIsPrimary = mergedPrimary == merged.Count - 1;
                    var dir = prevIsPrimary && i != primary ? prev : c;
                    bool backwards = dir.Cursor < dir.Anchor;
                    merged[^1] = backwards ? new Caret(end, start, dir.PreferredColumn) : new Caret(start, end, dir.PreferredColumn);
                    if (i == primary)
                        mergedPrimary = merged.Count - 1;
                    continue;
                }
            }
            if (i == primary)
                mergedPrimary = merged.Count;
            merged.Add(c);
        }
        _extras.Clear();
        for (int i = 0; i < merged.Count; i++)
        {
            if (i == mergedPrimary)
            {
                _anchor = merged[i].Anchor;
                _cursor = merged[i].Cursor;
                _preferredColumn = merged[i].PreferredColumn;
            }
            else
                _extras.Add(merged[i]);
        }
    }

    // ---------------------------------------------------------------- multi-caret edits

    /// <summary>
    /// The edit for one caret: replace [a, b) with <paramref name="text"/>. Return false for "no change" (the caret stays).
    /// Ranges are in the coordinates of the text BEFORE any of this command's edits.
    /// </summary>
    delegate bool CaretEdit(int index, Caret caret, out Coordinates a, out Coordinates b, out string text);

    /// <summary>
    /// Applies one edit per caret as ONE undo step (sub-edits applied bottom-up so earlier coordinates stay valid).
    /// Each caret ends up collapsed at the end of its inserted text. Returns false if nothing changed.
    /// </summary>
    bool MultiEdit(CaretEdit edit)
    {
        if (ReadOnly)
            return false;
        var carets = SortedCarets(out int primary);
        int n = carets.Count;
        var A = new Coordinates[n];
        var B = new Coordinates[n];
        var T = new string[n];
        for (int i = 0; i < n; i++)
        {
            if (edit(i, carets[i], out var a, out var b, out var t))
            {
                a = Buffer.Sanitize(a);
                b = Buffer.Sanitize(b);
                if (b < a)
                    (a, b) = (b, a);
                A[i] = a;
                B[i] = b;
                T[i] = t.Contains('\r') ? t.Replace("\r", "") : t;
            }
            else
            {
                A[i] = B[i] = carets[i].Cursor;
                T[i] = "";
            }
        }
        // ranges must not overlap: clip each one to start where the previous one ends
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && A[i] < B[i - 1])
            {
                A[i] = B[i - 1];
                if (B[i] < A[i])
                    B[i] = A[i];
            }
            if (A[i] != B[i] || T[i].Length > 0)
                any = true;
        }
        if (!any)
            return false;

        // where each edit ends once all edits are applied (forward pass over the original coordinates)
        var newEnd = new Coordinates[n];
        for (int i = 0; i < n; i++)
        {
            Coordinates na;
            if (i == 0)
                na = A[i];
            else if (A[i].Line == B[i - 1].Line)
                na = new Coordinates(newEnd[i - 1].Line, newEnd[i - 1].Column + A[i].Column - B[i - 1].Column);
            else
                na = new Coordinates(A[i].Line + newEnd[i - 1].Line - B[i - 1].Line, A[i].Column);
            newEnd[i] = TextBuffer.EndAfter(na, T[i]);
        }

        var before = new SelectionState(_anchor, _cursor);
        var beforeExtras = ExtrasSnapshot();
        var parts = new List<UndoRecord>(n);
        for (int i = n - 1; i >= 0; i--)
        {
            if (A[i] == B[i] && T[i].Length == 0)
                continue;
            var p = new UndoRecord
            {
                Start = A[i],
                RemovedEnd = B[i],
                Removed = A[i] == B[i] ? "" : Buffer.GetText(A[i], B[i]),
                Added = T[i],
            };
            Buffer.Delete(A[i], B[i]);
            p.AddedEnd = Buffer.Insert(A[i], T[i]);
            parts.Add(p);
        }

        _extras.Clear();
        for (int i = 0; i < n; i++)
        {
            var c = Buffer.Sanitize(newEnd[i]);
            if (i == primary)
                _anchor = _cursor = c;
            else
                _extras.Add(new Caret(c, c));
        }
        _preferredColumn = -1;
        Normalize();
        Undo.Push(new UndoRecord
        {
            Parts = parts.ToArray(),
            Before = before,
            BeforeExtras = beforeExtras,
            After = new SelectionState(_anchor, _cursor),
            AfterExtras = ExtrasSnapshot(),
        });
        ScrollToCursorRequested = true;
        return true;
    }

    bool MultiTypeChar(char c) => MultiEdit((int _, Caret k, out Coordinates a, out Coordinates b, out string t) =>
    {
        a = k.Start;
        b = k.End;
        if (c != '\n')
        {
            t = c.ToString();
            return true;
        }
        var line = Buffer.GetLineSpan(a.Line);
        int indent = 0;
        while (indent < line.Length && indent < a.Column && (line[indent] == ' ' || line[indent] == '\t'))
            indent++;
        t = indent == 0 ? "\n" : "\n" + new string(line[..indent]);
        return true;
    });

    bool MultiBackspace(bool word) => MultiEdit((int _, Caret k, out Coordinates a, out Coordinates b, out string t) =>
    {
        t = "";
        b = k.End;
        a = k.IsEmpty ? (word ? PrevWordBoundary(k.Cursor) : PrevChar(k.Cursor)) : k.Start;
        return a != b;
    });

    bool MultiDelete(bool word) => MultiEdit((int _, Caret k, out Coordinates a, out Coordinates b, out string t) =>
    {
        t = "";
        a = k.Start;
        b = k.IsEmpty ? (word ? NextWordBoundary(k.Cursor) : NextChar(k.Cursor)) : k.End;
        return a != b;
    });

    bool MultiTab() => MultiEdit((int _, Caret k, out Coordinates a, out Coordinates b, out string t) =>
    {
        a = k.Start;
        b = k.End;
        t = new string(' ', TabSize - k.Cursor.Column % TabSize);
        return true;
    });

    /// <summary>
    /// Paste at every caret: one clipboard line per caret when the clipboard has exactly as many lines as there are
    /// carets (a trailing line break is ignored), else the whole text at each.
    /// </summary>
    bool MultiPaste(string text)
    {
        int n = CaretCount;
        string[]? lines = null;
        var split = text.Split('\n');
        if (split.Length == n)
            lines = split;
        else if (split.Length == n + 1 && split[^1].Length == 0)
            lines = split[..n];
        return MultiEdit((int i, Caret k, out Coordinates a, out Coordinates b, out string t) =>
        {
            a = k.Start;
            b = k.End;
            t = lines != null ? lines[i] : text;
            return true;
        });
    }

    /// <summary>Copy with several carets: the selected texts one per line, or (no selection at all) every caret's line.</summary>
    string MultiCopyText()
    {
        var carets = SortedCarets(out _);
        var sb = new System.Text.StringBuilder();
        if (carets.TrueForAll(c => c.IsEmpty))
        {
            int last = -1;
            foreach (var c in carets)
            {
                if (c.Cursor.Line == last)
                    continue;
                last = c.Cursor.Line;
                sb.Append(Buffer.GetLineSpan(last)).Append('\n');
            }
            return sb.ToString();
        }
        for (int i = 0; i < carets.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(Buffer.GetText(carets[i].Start, carets[i].End));
        }
        return sb.ToString();
    }

    bool MultiCut()
    {
        Clipboard.SetText(ToClipboard(MultiCopyText()));
        bool lines = SortedCarets(out _).TrueForAll(c => c.IsEmpty);
        return MultiEdit((int _, Caret k, out Coordinates a, out Coordinates b, out string t) =>
        {
            t = "";
            if (!lines)
            {
                a = k.Start;
                b = k.End;
                return a != b;
            }
            int line = k.Cursor.Line;
            a = new Coordinates(line, 0);
            b = line < Buffer.LineCount - 1 ? new Coordinates(line + 1, 0) : new Coordinates(line, Buffer.GetLineLength(line));
            return a != b;
        });
    }

    // ---------------------------------------------------------------- occurrences (Ctrl+D, Ctrl+Shift+L)

    /// <summary>Start columns of every non-overlapping occurrence of <paramref name="q"/> (ordinal), in text order.</summary>
    List<Coordinates> Occurrences(string q)
    {
        var result = new List<Coordinates>();
        if (q.Length == 0)
            return result;
        for (int line = 0; line < Buffer.LineCount; line++)
        {
            var span = Buffer.GetLineSpan(line);
            int col = 0;
            while (col <= span.Length - q.Length)
            {
                int idx = span[col..].IndexOf(q.AsSpan(), StringComparison.Ordinal);
                if (idx < 0)
                    break;
                result.Add(new Coordinates(line, col + idx));
                col += idx + q.Length;
            }
        }
        return result;
    }

    bool IsSelected(Coordinates start, Coordinates end)
    {
        if (SelectionStart == start && SelectionEnd == end)
            return true;
        foreach (var c in _extras)
            if (c.Start == start && c.End == end)
                return true;
        return false;
    }

    /// <summary>
    /// Ctrl+Shift+L: selects every occurrence of the primary selection's text (or of the word at the cursor).
    /// The occurrence at the primary selection stays primary.
    /// </summary>
    public bool SelectAllOccurrences()
    {
        if (!HasSelection)
        {
            var (ws, we) = WordRangeAt(_cursor);
            if (ws == we)
                return false;
            Select(ws, we);
        }
        if (SelectionStart.Line != SelectionEnd.Line)
            return false;
        string q = GetSelectedText();
        var occ = Occurrences(q);
        if (occ.Count == 0)
            return false;
        var primaryStart = SelectionStart;
        int primary = occ.FindIndex(p => p >= primaryStart);
        if (primary < 0)
            primary = occ.Count - 1;
        _extras.Clear();
        for (int i = 0; i < occ.Count; i++)
        {
            var s = occ[i];
            var e = new Coordinates(s.Line, s.Column + q.Length);
            if (i == primary)
            {
                _anchor = s;
                _cursor = e;
            }
            else
                _extras.Add(new Caret(s, e));
        }
        _preferredColumn = -1;
        Normalize();
        ScrollToCursorRequested = true;
        return true;
    }
}
