using SoulsAiStudio.Editor.Syntax;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Completion;

/// <summary>
/// Autocomplete popup state and trigger rules, ImGui-free (driven by <see cref="LuaCodeEditor"/>, unit-tested).
/// <list type="bullet">
/// <item>Opens automatically after typing 2+ identifier chars, after <c>:</c> / <c>.</c>, or explicitly (Ctrl+Space).</item>
/// <item>Refilters while typing; closes when the cursor leaves the word, on a non-word char, or when nothing matches.</item>
/// <item>Candidates: Lua keywords/builtins + this buffer's identifiers (scanned off the UI thread) + host provider.</item>
/// </list>
/// With the popup closed it does no work per frame.
/// </summary>
internal sealed class CompletionController
{
    public const int MaxVisible = 10;
    const int MinAutoChars = 2;
    /// <summary>The buffer is rescanned at most this often while typing (the scan itself runs on a worker thread).</summary>
    static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(500);

    public enum Context { Global, Method, Field }

    readonly EditorCore _core;
    readonly CompletionFilter _filter = new();
    byte[] _kinds = new byte[256];

    // buffer index (worker thread)
    CompletionSet _bufferSet = CompletionSet.Empty;
    Task<CompletionSet>? _scan;
    long _scanVersion = -1;
    DateTime _scanStarted = DateTime.MinValue;

    // merged candidates cache
    CompletionSet? _mergedFor1, _mergedFor2;
    CompletionSet _merged = CompletionSet.Empty;

    // popup
    bool _explicit;
    long _filteredVersion = -1;
    Coordinates _filteredCursor;
    string? _qualifier;

    public CompletionController(EditorCore core) => _core = core;

    public ICompletionProvider? Provider { get; set; }
    public bool Enabled { get; set; } = true;

    public bool IsOpen { get; private set; }
    public Context Kind { get; private set; }
    public Coordinates WordStart { get; private set; }
    public int Selected { get; private set; }
    public int ScrollTop { get; private set; }
    public IReadOnlyList<CompletionItem> Items => _filter.Results;

    /// <summary>Lexer state at the start of a line (from the colorizer); used to tell code from strings/comments.</summary>
    public Func<int, LuaLexState>? LineStartState { get; set; }

    // ------------------------------------------------------------------ triggers

    /// <summary>Called after printable chars were typed this frame.</summary>
    public void OnTyped()
    {
        if (!Enabled || _core.ReadOnly)
            return;
        if (IsOpen)
        {
            Update();
            if (IsOpen)
                return;
        }
        // e.g. 'ai:' after a word closed the global popup → reopen for methods
        TryOpen(explicitRequest: false);
    }

    /// <summary>Ctrl+Space.</summary>
    public void OpenExplicit()
    {
        if (!Enabled || _core.ReadOnly)
            return;
        TryOpen(explicitRequest: true);
    }

    public void Close()
    {
        IsOpen = false;
        _explicit = false;
    }

    void TryOpen(bool explicitRequest)
    {
        var cursor = _core.Cursor;
        if (_core.HasSelection)
            return;
        var line = _core.Buffer.GetLineSpan(cursor.Line);
        int start = cursor.Column;
        while (start > 0 && EditorCore.IsWordChar(line[start - 1]))
            start--;
        int wordLen = cursor.Column - start;
        if (wordLen > 0 && char.IsDigit(line[start]))
            return; // number
        if (!IsCode(cursor.Line, line, cursor.Column))
            return;

        var (ctx, qualifier) = ContextAt(line, start);
        if (!explicitRequest && ctx == Context.Global && wordLen < MinAutoChars)
            return;

        IsOpen = true;
        _explicit = explicitRequest;
        Kind = ctx;
        _qualifier = qualifier;
        WordStart = new Coordinates(cursor.Line, start);
        RequestBufferScan();
        _filteredVersion = -1;
        _filteredCursor = new Coordinates(-1, -1);
        Selected = 0;
        ScrollTop = 0;
        Refilter();
    }

    /// <summary>Global / after ':' / after '.' (with the table name before the dot, for library members).</summary>
    static (Context, string?) ContextAt(ReadOnlySpan<char> line, int wordStart)
    {
        int p = wordStart - 1;
        while (p >= 0 && (line[p] == ' ' || line[p] == '\t'))
            p--;
        if (p < 0)
            return (Context.Global, null);
        char c = line[p];
        if (c == ':' && !(p > 0 && line[p - 1] == ':'))
            return (Context.Method, null);
        if (c == '.' && !(p > 0 && (line[p - 1] == '.' || char.IsDigit(line[p - 1]) && !IsAfterIdentifier(line, p - 1))))
        {
            int qe = p;
            while (qe > 0 && (line[qe - 1] == ' ' || line[qe - 1] == '\t'))
                qe--;
            int qs = qe;
            while (qs > 0 && EditorCore.IsWordChar(line[qs - 1]))
                qs--;
            return (Context.Field, qe > qs ? new string(line[qs..qe]) : null);
        }
        return (Context.Global, null);
    }

    /// <summary>True when the digit at <paramref name="i"/> belongs to an identifier (e.g. <c>arg1.</c>), not a number.</summary>
    static bool IsAfterIdentifier(ReadOnlySpan<char> line, int i)
    {
        while (i > 0 && EditorCore.IsWordChar(line[i - 1]))
            i--;
        return !char.IsDigit(line[i]);
    }

    bool IsCode(int lineIndex, ReadOnlySpan<char> line, int column)
    {
        if (column == 0 || line.Length == 0)
            return true;
        if (_kinds.Length < line.Length)
            _kinds = new byte[Math.Max(line.Length, _kinds.Length * 2)];
        var state = LineStartState?.Invoke(lineIndex) ?? LuaLexState.Normal;
        LuaLexer.ColorizeLine(line, _kinds, state);
        var k = (LuaTokenKind)_kinds[column - 1];
        return k != LuaTokenKind.String && k != LuaTokenKind.Comment && k != LuaTokenKind.Number;
    }

    // ------------------------------------------------------------------ per frame (only while open)

    /// <summary>Re-validates the popup against the cursor and refilters if the word changed. Cheap when nothing changed.</summary>
    public void Update()
    {
        if (!IsOpen)
            return;
        PollScan();
        var cursor = _core.Cursor;
        if (_core.HasSelection || cursor.Line != WordStart.Line || cursor.Column < WordStart.Column)
        {
            Close();
            return;
        }
        if (_filteredVersion == _core.Buffer.Version && _filteredCursor == cursor)
            return;

        var line = _core.Buffer.GetLineSpan(cursor.Line);
        if (WordStart.Column > line.Length || cursor.Column > line.Length)
        {
            Close();
            return;
        }
        for (int i = WordStart.Column; i < cursor.Column; i++)
            if (!EditorCore.IsWordChar(line[i]))
            {
                Close();
                return;
            }
        // the ':' / '.' that opened a member popup was deleted
        if (Kind != Context.Global && ContextAt(line, WordStart.Column).Item1 != Kind)
        {
            Close();
            return;
        }
        if (Kind == Context.Global && !_explicit && cursor.Column - WordStart.Column == 0)
        {
            Close();
            return;
        }
        Refilter();
    }

    void Refilter()
    {
        // keep the highlighted item when only the candidates changed (background scan finished), not the word
        string? keep = _filteredCursor == _core.Cursor && Selected < Items.Count ? Items[Selected].Label : null;
        var cursor = _core.Cursor;
        var prefix = _core.Buffer.GetLineSpan(cursor.Line)[WordStart.Column..cursor.Column];
        var merged = Merged();

        switch (Kind)
        {
            case Context.Method:
                _filter.Run(prefix, merged.Methods, presorted: true);
                break;
            case Context.Field:
                if (_qualifier != null && LuaBuiltins.LibraryMembers.TryGetValue(_qualifier, out var lib))
                    _filter.Run(prefix, lib, presorted: true);
                else
                    _filter.Run(prefix, merged.Fields, presorted: true);
                break;
            default:
                _filter.Run(prefix, merged.Globals, presorted: true);
                break;
        }

        _filteredVersion = _core.Buffer.Version;
        _filteredCursor = cursor;
        Selected = 0;
        ScrollTop = 0;

        var items = _filter.Results;
        if (keep != null)
            for (int i = 0; i < items.Count; i++)
                if (ReferenceEquals(items[i].Label, keep) || items[i].Label == keep)
                {
                    Select(i);
                    break;
                }
        // Nothing to offer, or the only suggestion is exactly what is already typed (so Enter keeps making a newline).
        if (items.Count == 0 || items.Count == 1 && prefix.SequenceEqual(items[0].Label))
            Close();
    }

    CompletionSet Merged()
    {
        var provided = Provider?.Current ?? CompletionSet.Empty;
        if (!ReferenceEquals(provided, _mergedFor1) || !ReferenceEquals(_bufferSet, _mergedFor2))
        {
            _merged = CompletionSet.Merge(new[] { LuaBuiltins.Set, _bufferSet, provided });
            _mergedFor1 = provided;
            _mergedFor2 = _bufferSet;
        }
        return _merged;
    }

    // ------------------------------------------------------------------ buffer index

    void RequestBufferScan()
    {
        if (_scan != null || _scanVersion == _core.Buffer.Version)
            return;
        if (_scanVersion >= 0 && DateTime.UtcNow - _scanStarted < RescanInterval)
            return;
        _scanVersion = _core.Buffer.Version;
        _scanStarted = DateTime.UtcNow;
        string snapshot = _core.Buffer.GetText();
        var c = _core.Cursor;
        _scan = Task.Run(() => LuaCompletionScanner.Scan(snapshot, c.Line, c.Column));
    }

    void PollScan()
    {
        if (_scan is not { IsCompleted: true } t)
            return;
        _scan = null;
        if (t.IsCompletedSuccessfully)
        {
            _bufferSet = t.Result;
            _filteredVersion = -1; // refilter with the new names
        }
    }

    /// <summary>Test hook: scan the buffer synchronously now.</summary>
    internal void ScanBufferNow()
    {
        var c = _core.Cursor;
        _bufferSet = LuaCompletionScanner.Scan(_core.Buffer.GetText(), c.Line, c.Column);
        _scanVersion = _core.Buffer.Version;
        _scanStarted = DateTime.UtcNow;
        _scan = null;
        _filteredVersion = -1;
    }

    // ------------------------------------------------------------------ navigation / accept

    public void Move(int delta)
    {
        int n = Items.Count;
        if (n == 0)
            return;
        int s = Selected + delta;
        if (Math.Abs(delta) == 1)
            s = (s % n + n) % n; // arrows wrap
        else
            s = Math.Clamp(s, 0, n - 1);
        Select(s);
    }

    public void Select(int index)
    {
        if (Items.Count == 0)
            return;
        Selected = Math.Clamp(index, 0, Items.Count - 1);
        if (Selected < ScrollTop)
            ScrollTop = Selected;
        else if (Selected >= ScrollTop + MaxVisible)
            ScrollTop = Selected - MaxVisible + 1;
    }

    public void Scroll(int delta) =>
        ScrollTop = Math.Clamp(ScrollTop + delta, 0, Math.Max(0, Items.Count - MaxVisible));

    /// <summary>Replaces the typed word with the selected item (one undo step) and closes.</summary>
    public bool Accept()
    {
        if (!IsOpen || Items.Count == 0)
        {
            Close();
            return false;
        }
        var item = Items[Selected];
        var cursor = _core.Cursor;
        Close();
        return _core.ReplaceRange(WordStart, cursor, item.Label);
    }
}
