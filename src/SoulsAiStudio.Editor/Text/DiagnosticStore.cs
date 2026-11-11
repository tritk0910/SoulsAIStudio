namespace SoulsAiStudio.Editor.Text;

internal readonly struct Diagnostic
{
    public readonly int Line;
    public readonly int StartCol;
    public readonly int EndCol;
    public readonly bool IsWarning;
    public readonly string Message;

    public Diagnostic(int line, int startCol, int endCol, bool isWarning, string message)
    {
        Line = line;
        StartCol = startCol;
        EndCol = endCol;
        IsWarning = isWarning;
        Message = message;
    }

    public Diagnostic WithLine(int line) => new(line, StartCol, EndCol, IsWarning, Message);
}

/// <summary>
/// Diagnostics sorted by line. Line numbers follow line inserts/removes until the host sends a fresh set
/// (columns are not adjusted for in-line edits).
/// </summary>
internal sealed class DiagnosticStore
{
    readonly List<Diagnostic> _items = new();

    public int Count => _items.Count;
    public Diagnostic this[int i] => _items[i];

    public void Attach(TextBuffer buffer)
    {
        buffer.LinesInserted += OnLinesInserted;
        buffer.LinesRemoved += OnLinesRemoved;
        buffer.Reset += _items.Clear;
    }

    public void Set(IReadOnlyList<(int Line, int StartCol, int EndCol, bool IsWarning, string Message)>? diagnostics)
    {
        _items.Clear();
        if (diagnostics == null)
            return;
        foreach (var d in diagnostics)
        {
            if (d.Line < 0)
                continue;
            int s = Math.Max(0, d.StartCol);
            int e = Math.Max(s, d.EndCol);
            _items.Add(new Diagnostic(d.Line, s, e, d.IsWarning, d.Message ?? ""));
        }
        _items.Sort(static (a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.StartCol.CompareTo(b.StartCol));
    }

    /// <summary>Index of the first diagnostic with Line &gt;= line (Count if none).</summary>
    public int FirstIndexAtOrAfter(int line)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_items[mid].Line < line)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    void OnLinesInserted(int at, int count)
    {
        for (int i = FirstIndexAtOrAfter(at); i < _items.Count; i++)
            _items[i] = _items[i].WithLine(_items[i].Line + count);
    }

    void OnLinesRemoved(int at, int count)
    {
        int first = FirstIndexAtOrAfter(at);
        int end = FirstIndexAtOrAfter(at + count);
        if (end > first)
            _items.RemoveRange(first, end - first);
        for (int i = first; i < _items.Count; i++)
            _items[i] = _items[i].WithLine(_items[i].Line - count);
    }
}
