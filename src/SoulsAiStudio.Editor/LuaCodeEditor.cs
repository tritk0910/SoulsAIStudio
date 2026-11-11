// Parts derived from ImGuiColorTextEditNet (MIT) TextEditor.cs, Editor/TextEditorRenderer.cs and
// Input/Standard*Input.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
using System.Numerics;
using System.Text.Unicode;
using ImGuiNET;
using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Rendering;
using SoulsAiStudio.Editor.Syntax;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor;

/// <summary>
/// Lua code editor widget for ImGui.NET 1.87 (DSAS). Call <see cref="Render"/> once per frame inside an
/// ImGui window. Only visible lines are drawn; the steady-state frame does not allocate.
/// </summary>
public sealed class LuaCodeEditor
{
    /// <summary>Lines (re)colorized per frame at most. A 3000-line file colors fully in the first frame.</summary>
    const int ColorizeBudgetPerFrame = 8000;

    readonly EditorCore _core = new();
    readonly DiagnosticStore _diagnostics = new();
    readonly FindModel _find = new();
    readonly Colorizer _colorizer;
    readonly EditorView _view;
    readonly ImGuiDrawSink _sink = new();
    readonly ImGuiGlyphMetrics _metrics = new();

    readonly char[] _typed = new char[64];
    readonly char[] _decoded = new char[256];
    readonly byte[] _findBuf = new byte[256];
    readonly byte[] _gotoBuf = new byte[16];

    int _highlightLine = -1;
    int _scrollCenterLine = -1;
    bool _focusRequested;
    bool _mouseSelecting;
    double _blinkStart;
    long _lastVersion = -1;
    Coordinates _lastCursor;

    bool _findOpen;
    bool _focusFindInput;
    bool _caseSensitive;
    Coordinates _findOrigin;
    bool _gotoOpen;
    bool _focusGotoInput;
    bool _focusTextNextFrame;

    long _widthVersion = -1;
    float _maxLineWidth;

    public LuaCodeEditor()
    {
        _core.Clipboard = ImGuiClipboard.Instance;
        _diagnostics.Attach(_core.Buffer);
        _colorizer = new Colorizer(_core.Buffer);
        _view = new EditorView(_core, _diagnostics, _find);
    }

    // ================================================================= public API

    /// <summary>Whole text (uses the line ending detected when the text was set). Setting resets undo and marks clean.
    /// Does not raise <see cref="TextChanged"/>. Getting allocates — don't call it every frame.</summary>
    public string Text
    {
        get => _core.Buffer.GetText();
        set
        {
            _core.SetText(value);
            _mouseSelecting = false;
            _scrollCenterLine = 0;
            _lastVersion = _core.Buffer.Version;
        }
    }

    /// <summary>True when the text differs from the last <see cref="MarkClean"/> / <see cref="Text"/> set (undo back to it = clean).</summary>
    public bool IsDirty => _core.Undo.IsDirty;

    public void MarkClean() => _core.Undo.MarkClean();

    public bool ReadOnly
    {
        get => _core.ReadOnly;
        set => _core.ReadOnly = value;
    }

    /// <summary>Raised at the end of <see cref="Render"/> (or after <see cref="Undo"/>/<see cref="Redo"/>) when the text changed.</summary>
    public event Action? TextChanged;

    /// <summary>0-based cursor position; Column is a UTF-16 index in the line.</summary>
    public (int Line, int Column) Cursor => (_core.Cursor.Line, _core.Cursor.Column);

    /// <summary>Incremented on every text change (cheap change detection for the host).</summary>
    public long Version => _core.Buffer.Version;

    public int LineCount => _core.Buffer.LineCount;

    /// <summary>Moves the cursor to the start of <paramref name="line"/> (0-based), centers it and focuses the editor.
    /// <paramref name="select"/> selects the whole line.</summary>
    public void GoToLine(int line, bool select = false)
    {
        line = Math.Clamp(line, 0, _core.Buffer.LineCount - 1);
        if (select)
            _core.SelectLine(line);
        else
            _core.SetCursor(new Coordinates(line, 0));
        _scrollCenterLine = line;
        _focusRequested = true;
    }

    /// <summary>Replaces all diagnostics. Line/columns are 0-based; IsWarning=false draws as error (red).</summary>
    public void SetDiagnostics(IReadOnlyList<(int Line, int StartCol, int EndCol, bool IsWarning, string Message)> d) =>
        _diagnostics.Set(d);

    /// <summary>Highlights one line (e.g. current goal header); null clears.</summary>
    public void SetHighlightLine(int? line) => _highlightLine = line ?? -1;

    public void Undo()
    {
        long v = Version;
        _core.DoUndo();
        if (v != Version)
            TextChanged?.Invoke();
        _lastVersion = Version;
    }

    public void Redo()
    {
        long v = Version;
        _core.DoRedo();
        if (v != Version)
            TextChanged?.Invoke();
        _lastVersion = Version;
    }

    /// <summary>Key source; see <see cref="IKeyInput"/> for what DSAS must feed. Default reads ImGui io.</summary>
    public IKeyInput KeyInput { get; set; } = ImGuiKeyInput.Instance;

    /// <summary>Clipboard; default goes through ImGui.</summary>
    public IClipboard Clipboard
    {
        get => _core.Clipboard;
        set => _core.Clipboard = value ?? ImGuiClipboard.Instance;
    }

    public EditorPalette Palette
    {
        get => _view.Palette;
        set => _view.Palette = value ?? EditorPalette.Dark();
    }

    public int TabSize
    {
        get => _core.TabSize;
        set => _core.TabSize = Math.Clamp(value, 1, 16);
    }

    /// <summary>
    /// Draws inside the current ImGui window/child. Returns true if the editor (text area or its find /
    /// go-to bar) has keyboard focus this frame — the host should then suppress its own shortcuts.
    /// </summary>
    public bool Render(string id, Vector2 size)
    {
        _metrics.Update();
        bool focused = false;

        ImGui.PushID(id);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, _view.Palette.Background);
        if (ImGui.BeginChild("##outer", size, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove))
        {
            bool outerFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || _focusRequested;
            if (outerFocused)
                HandleBarKeys();

            if (_findOpen)
                DrawFindBar();
            if (_gotoOpen)
                DrawGotoBar();

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.BeginChild("##text", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNavInputs))
                DrawTextArea();
            ImGui.EndChild();
            ImGui.PopStyleVar();

            focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopID();

        if (focused)
        {
            var io = ImGui.GetIO();
            io.WantCaptureKeyboard = true;
            io.WantTextInput = true;
        }

        if (_lastVersion != Version)
        {
            _lastVersion = Version;
            TextChanged?.Invoke();
        }
        return focused;
    }

    // ================================================================= text area

    void DrawTextArea()
    {
        var m = _metrics;
        float lineH = m.LineHeight;

        if (_focusRequested || _focusTextNextFrame)
        {
            ImGui.SetWindowFocus();
            _focusRequested = false;
            _focusTextNextFrame = false;
        }
        bool textFocused = ImGui.IsWindowFocused();

        var winPos = ImGui.GetWindowPos();
        var visible = ImGui.GetWindowContentRegionMax() - ImGui.GetWindowContentRegionMin();
        var origin = ImGui.GetCursorScreenPos();

        var f = new FrameParams
        {
            ContentOrigin = origin,
            WindowPos = winPos,
            WindowSize = visible,
            Focused = textFocused,
            HighlightLine = _highlightLine,
            ShowFindMatches = _findOpen && _find.HasQuery,
            CurrentMatchStart = _core.SelectionStart,
            CurrentMatchEnd = _core.SelectionEnd,
        };

        if (textFocused)
            HandleKeyboard((int)MathF.Max(1, visible.Y / lineH - 1));
        HandleMouse(in f);

        _colorizer.Run(ColorizeBudgetPerFrame);

        // scrolling (applied by ImGui next frame)
        if (_scrollCenterLine >= 0)
        {
            ImGui.SetScrollY(MathF.Max(0, _scrollCenterLine * lineH - visible.Y * 0.5f + lineH * 0.5f));
            _scrollCenterLine = -1;
            _core.ScrollToCursorRequested = false;
        }
        else if (_core.ScrollToCursorRequested)
        {
            EnsureCursorVisible(visible);
            _core.ScrollToCursorRequested = false;
        }

        // cursor blink: restart on any change
        double now = ImGui.GetTime();
        if (_core.Cursor != _lastCursor || Version != _lastVersion)
        {
            _blinkStart = now;
            _lastCursor = _core.Cursor;
        }
        f.CursorVisible = (now - _blinkStart) % 1.0 < 0.6;

        _sink.Begin(ImGui.GetWindowDrawList());
        _view.Draw(_sink, m, in f);

        // content size for scrollbars
        if (_widthVersion != Version)
        {
            _widthVersion = Version;
            _maxLineWidth = ComputeMaxLineWidth(m);
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(_view.TextStartX(m) + _maxLineWidth + 4 * lineH, _core.Buffer.LineCount * lineH + visible.Y * 0.5f));

        DrawDiagnosticTooltip(in f);
    }

    float ComputeMaxLineWidth(IGlyphMetrics m)
    {
        int longest = 0, longestIndex = 0;
        for (int i = 0; i < _core.Buffer.LineCount; i++)
        {
            int len = _core.Buffer.GetLineLength(i);
            if (len > longest)
            {
                longest = len;
                longestIndex = i;
            }
        }
        // monospace assumption for the estimate, measured on the longest line
        return _view.ColumnToX(_core.Buffer.GetLineSpan(longestIndex), longest, m) + m.Advance('M') * 2;
    }

    void EnsureCursorVisible(Vector2 visible)
    {
        var m = _metrics;
        float lineH = m.LineHeight;
        var c = _core.Cursor;

        float scrollY = ImGui.GetScrollY();
        float y = c.Line * lineH;
        if (y < scrollY)
            ImGui.SetScrollY(y);
        else if (y + lineH > scrollY + visible.Y)
            ImGui.SetScrollY(y + lineH - visible.Y);

        float gutter = _view.TextStartX(m);
        float x = gutter + _view.ColumnToX(_core.Buffer.GetLineSpan(c.Line), c.Column, m);
        float scrollX = ImGui.GetScrollX();
        float margin = m.Advance('M') * 4;
        if (x < scrollX + gutter + margin)
            ImGui.SetScrollX(MathF.Max(0, x - gutter - margin));
        else if (x > scrollX + visible.X - margin)
            ImGui.SetScrollX(x - visible.X + margin);
    }

    void DrawDiagnosticTooltip(in FrameParams f)
    {
        if (_diagnostics.Count == 0 || !ImGui.IsWindowHovered())
            return;
        var mouse = ImGui.GetMousePos();
        var pos = _view.ScreenToCoordinates(mouse, in f, _metrics);
        float lineTop = f.ContentOrigin.Y + pos.Line * _metrics.LineHeight;
        if (mouse.Y < lineTop || mouse.Y > lineTop + _metrics.LineHeight)
            return;

        bool inGutter = mouse.X < f.WindowPos.X + _view.GutterWidth(_metrics);
        int d = _view.DiagnosticAt(pos, anyOnLine: inGutter);
        if (d < 0)
            return;

        ImGui.BeginTooltip();
        if (inGutter)
        {
            for (int i = d; i < _diagnostics.Count && _diagnostics[i].Line == pos.Line; i++)
                TooltipLine(_diagnostics[i]);
        }
        else
        {
            TooltipLine(_diagnostics[d]);
        }
        ImGui.EndTooltip();
    }

    void TooltipLine(Diagnostic diag)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, diag.IsWarning ? _view.Palette.Warning : _view.Palette.Error);
        _sink.TextUnformatted(diag.IsWarning ? "Warning: " : "Error: ");
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        _sink.TextUnformatted(diag.Message);
    }

    // ================================================================= input

    void HandleKeyboard(int pageLines)
    {
        var k = KeyInput;
        bool ctrl = k.Ctrl, shift = k.Shift, alt = k.Alt;

        if (ctrl && !alt)
        {
            if (k.IsPressed(EditorKey.Z)) { if (shift) _core.DoRedo(); else _core.DoUndo(); }
            else if (k.IsPressed(EditorKey.Y)) _core.DoRedo();
            else if (k.IsPressed(EditorKey.A)) _core.SelectAll();
            else if (k.IsPressed(EditorKey.C)) _core.Copy();
            else if (k.IsPressed(EditorKey.X)) _core.Cut();
            else if (k.IsPressed(EditorKey.V)) _core.Paste();
        }

        if (!alt)
        {
            if (k.IsPressed(EditorKey.Left)) _core.MoveLeft(shift, ctrl);
            if (k.IsPressed(EditorKey.Right)) _core.MoveRight(shift, ctrl);
            if (k.IsPressed(EditorKey.Up)) _core.MoveUp(1, shift);
            if (k.IsPressed(EditorKey.Down)) _core.MoveDown(1, shift);
            if (k.IsPressed(EditorKey.PageUp)) _core.MoveUp(pageLines, shift);
            if (k.IsPressed(EditorKey.PageDown)) _core.MoveDown(pageLines, shift);
            if (k.IsPressed(EditorKey.Home)) { if (ctrl) _core.MoveToStartOfFile(shift); else _core.MoveHome(shift); }
            if (k.IsPressed(EditorKey.End)) { if (ctrl) _core.MoveToEndOfFile(shift); else _core.MoveEnd(shift); }
            if (k.IsPressed(EditorKey.Backspace)) _core.Backspace(ctrl);
            if (k.IsPressed(EditorKey.Delete)) _core.Delete(ctrl);
            if (!ctrl && k.IsPressed(EditorKey.Enter)) _core.TypeChar('\n');
            if (!ctrl && k.IsPressed(EditorKey.Tab)) _core.Tab(shift);
        }

        int n = k.ReadTypedChars(_typed);
        if (!ctrl || alt) // Ctrl+Alt = AltGr on European layouts
        {
            for (int i = 0; i < n; i++)
            {
                char c = _typed[i];
                if (c >= 32 && c != 127)
                    _core.TypeChar(c);
            }
        }
    }

    void HandleMouse(in FrameParams f)
    {
        bool hovered = ImGui.IsWindowHovered();
        var mouse = ImGui.GetMousePos();
        float gutterRight = f.WindowPos.X + _view.GutterWidth(_metrics);

        if (hovered && mouse.X >= gutterRight)
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var pos = _view.ScreenToCoordinates(mouse, in f, _metrics);
            if (mouse.X < gutterRight)
            {
                int line = pos.Line;
                var end = line + 1 < _core.Buffer.LineCount ? new Coordinates(line + 1, 0) : new Coordinates(line, _core.Buffer.GetLineLength(line));
                _core.Select(new Coordinates(line, 0), end);
            }
            else if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _core.SelectWordAt(pos);
            }
            else
            {
                _core.SetCursor(pos, extendSelection: KeyInput.Shift);
            }
            _mouseSelecting = true;
        }

        if (_mouseSelecting)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _mouseSelecting = false;
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                _core.SetCursor(_view.ScreenToCoordinates(mouse, in f, _metrics), extendSelection: true);
        }
    }

    /// <summary>Ctrl+F / Ctrl+G / F3 / Escape — active while the editor or one of its bars is focused.</summary>
    void HandleBarKeys()
    {
        var k = KeyInput;
        bool ctrl = k.Ctrl;
        if (ctrl && !k.Alt && k.IsPressed(EditorKey.F))
        {
            OpenFind();
        }
        else if (ctrl && !k.Alt && k.IsPressed(EditorKey.G))
        {
            _gotoOpen = true;
            _findOpen = false;
            _focusGotoInput = true;
            _gotoBuf.AsSpan().Clear();
        }
        else if (k.IsPressed(EditorKey.F3))
        {
            FindStep(!k.Shift);
        }
        else if (k.IsPressed(EditorKey.Escape) && (_findOpen || _gotoOpen))
        {
            _findOpen = false;
            _gotoOpen = false;
            _focusTextNextFrame = true;
        }
    }

    void OpenFind()
    {
        _findOpen = true;
        _gotoOpen = false;
        _focusFindInput = true;
        _findOrigin = _core.SelectionStart;
        if (_core.HasSelection && _core.SelectionStart.Line == _core.SelectionEnd.Line)
        {
            var span = _core.Buffer.GetLineSpan(_core.SelectionStart.Line)[_core.SelectionStart.Column.._core.SelectionEnd.Column];
            _findBuf.AsSpan().Clear();
            Utf8.FromUtf16(span, _findBuf.AsSpan(0, _findBuf.Length - 1), out _, out _, replaceInvalidSequences: true);
        }
    }

    void FindStep(bool forward)
    {
        if (!_find.HasQuery)
            return;
        bool ok = forward
            ? _find.FindNext(_core.Buffer, _core.SelectionEnd, out var s, out var e)
            : _find.FindPrev(_core.Buffer, _core.SelectionStart, out s, out e);
        if (ok)
            _core.Select(s, e);
    }

    void DrawFindBar()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Find");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(260, MathF.Max(80, ImGui.GetContentRegionAvail().X - 260)));
        if (_focusFindInput)
        {
            ImGui.SetKeyboardFocusHere();
            _focusFindInput = false;
        }
        bool enter = ImGui.InputText("##find", _findBuf, (uint)_findBuf.Length, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        // sync query from the UTF-8 buffer (no allocation)
        int len = Array.IndexOf(_findBuf, (byte)0);
        if (len < 0)
            len = _findBuf.Length;
        Utf8.ToUtf16(_findBuf.AsSpan(0, len), _decoded, out _, out int chars, replaceInvalidSequences: true);
        var before = _find.Query.Length;
        bool wasEqual = _find.Query.SequenceEqual(_decoded.AsSpan(0, chars));
        _find.SetQuery(_decoded.AsSpan(0, chars), _caseSensitive);
        if ((!wasEqual || before != chars) && _find.HasQuery)
        {
            // incremental: jump to the first match from where Find was opened
            if (_find.FindNext(_core.Buffer, _findOrigin, out var s, out var e))
                _core.Select(s, e);
        }

        if (enter)
        {
            FindStep(!KeyInput.Shift);
            _focusFindInput = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("<"))
            FindStep(false);
        ImGui.SameLine();
        if (ImGui.Button(">"))
            FindStep(true);
        ImGui.SameLine();
        if (ImGui.Checkbox("Aa", ref _caseSensitive))
            _find.SetQuery(_find.Query, _caseSensitive);
        ImGui.SameLine();

        Span<char> label = stackalloc char[32];
        int count = _find.CountMatches(_core.Buffer);
        int w;
        if (!_find.HasQuery)
            w = 0;
        else if (count == 0)
        {
            "No results".AsSpan().CopyTo(label);
            w = 10;
        }
        else
        {
            count.TryFormat(label, out w);
            var suffix = count == 1 ? " match" : " matches";
            suffix.AsSpan().CopyTo(label[w..]);
            w += suffix.Length;
        }
        _sink.TextUnformatted(label[..w]);
        ImGui.SameLine();
        if (ImGui.Button("x##closefind"))
        {
            _findOpen = false;
            _focusTextNextFrame = true;
        }
    }

    void DrawGotoBar()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Go to line");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        if (_focusGotoInput)
        {
            ImGui.SetKeyboardFocusHere();
            _focusGotoInput = false;
        }
        bool enter = ImGui.InputText("##goto", _gotoBuf, (uint)_gotoBuf.Length, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        Span<char> label = stackalloc char[24];
        "(1-".AsSpan().CopyTo(label);
        _core.Buffer.LineCount.TryFormat(label[3..], out int w);
        label[3 + w] = ')';
        _sink.TextUnformatted(label[..(4 + w)]);
        ImGui.SameLine();
        if (ImGui.Button("x##closegoto"))
        {
            _gotoOpen = false;
            _focusTextNextFrame = true;
        }

        if (enter)
        {
            int value = 0;
            foreach (var b in _gotoBuf)
            {
                if (b == 0)
                    break;
                if (b >= (byte)'0' && b <= (byte)'9' && value < 10_000_000)
                    value = value * 10 + (b - '0');
            }
            _gotoOpen = false;
            if (value > 0)
                GoToLine(value - 1);
            else
                _focusTextNextFrame = true;
        }
    }

    // ================================================================= test hooks

    internal EditorCore Core => _core;
    internal DiagnosticStore Diagnostics => _diagnostics;
    internal FindModel Find => _find;

    internal bool ColorizeAll() => _colorizer.Run(int.MaxValue);

    /// <summary>The managed per-frame work of the text area without ImGui (for perf / allocation tests).</summary>
    internal void DrawHeadless(IDrawSink sink, IGlyphMetrics metrics, float scrollY, Vector2 size, bool showFind = false)
    {
        _colorizer.Run(ColorizeBudgetPerFrame);
        var f = new FrameParams
        {
            ContentOrigin = new Vector2(0, -scrollY),
            WindowPos = Vector2.Zero,
            WindowSize = size,
            Focused = true,
            CursorVisible = true,
            HighlightLine = _highlightLine,
            ShowFindMatches = showFind,
            CurrentMatchStart = _core.SelectionStart,
            CurrentMatchEnd = _core.SelectionEnd,
        };
        _view.Draw(sink, metrics, in f);
    }
}
