// Parts derived from ImGuiColorTextEditNet (MIT) TextEditor.cs, Editor/TextEditorRenderer.cs and
// Input/Standard*Input.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Rendering;
using SoulsAiStudio.Editor.Syntax;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor;

/// <summary>
/// Lua code editor widget for ImGui.NET 1.87 (DSAS). Call <see cref="Render"/> once per frame inside an
/// ImGui window. Only visible lines are drawn; the steady-state frame does not allocate.
/// <para>All text entry (code, find field, go-to field) goes through <see cref="IKeyInput"/>; the widget
/// never uses native ImGui.InputText, so typed chars have exactly one source.</para>
/// </summary>
public sealed class LuaCodeEditor
{
    /// <summary>Lines (re)colorized per frame at most. A 3000-line file colors fully in the first frame.</summary>
    const int ColorizeBudgetPerFrame = 8000;

    internal enum FocusTarget { Text, Find, Goto }

    readonly EditorCore _core = new();
    readonly DiagnosticStore _diagnostics = new();
    readonly FindModel _find = new();
    readonly Colorizer _colorizer;
    readonly EditorView _view;
    readonly ImGuiDrawSink _sink = new();
    readonly ImGuiGlyphMetrics _metrics = new();

    readonly char[] _typed = new char[256];
    readonly LineInput _findField = new(256);
    readonly LineInput _gotoField = new(9, digitsOnly: true);

    int _highlightLine = -1;
    int _scrollCenterLine = -1;
    bool _focusRequested;
    bool _mouseSelecting;
    double _blinkStart;
    long _lastVersion = -1;
    Coordinates _lastCursor;
    bool _textWindowFocused;

    FocusTarget _target = FocusTarget.Text;
    bool _findOpen;
    bool _caseSensitive;
    Coordinates _findOrigin;
    int _findFieldSeenVersion = -1;
    bool _gotoOpen;
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
        _target = FocusTarget.Text;
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

    /// <summary>Key and typed-char source; see <see cref="IKeyInput"/> for what DSAS must feed. Default reads ImGui io.</summary>
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
            bool editorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || _focusRequested;
            float lineH = _metrics.LineHeight;
            var avail = ImGui.GetContentRegionAvail();
            ProcessKeys(KeyInput, editorFocused, _textWindowFocused, (int)MathF.Max(1, avail.Y / lineH - 2));

            if (_findOpen)
                DrawFindBar();
            if (_gotoOpen)
                DrawGotoBar();

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.BeginChild("##text", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNavInputs))
                DrawTextArea();
            else
                _textWindowFocused = false;
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

    // ================================================================= keyboard (ImGui-free, unit-tested)

    /// <summary>
    /// Reads this frame's typed chars once and routes keys + chars to exactly one target:
    /// the find field, the go-to field, or the text area.
    /// </summary>
    internal void ProcessKeys(IKeyInput k, bool editorFocused, bool textFocused, int pageLines)
    {
        int n = k.ReadTypedChars(_typed); // always drain, so stale chars never leak into the next focus
        if (!editorFocused)
            return;

        bool ctrl = k.Ctrl, shift = k.Shift, alt = k.Alt;
        var chars = _typed.AsSpan(0, n);

        if (ctrl && !alt && k.IsPressed(EditorKey.F))
        {
            OpenFind();
            return;
        }
        if (ctrl && !alt && k.IsPressed(EditorKey.G))
        {
            OpenGoto();
            return;
        }
        if (k.IsPressed(EditorKey.F3))
            FindStep(!shift);
        if (k.IsPressed(EditorKey.Escape) && (_findOpen || _gotoOpen))
        {
            CloseBars();
            return;
        }

        if (_target == FocusTarget.Find && !_findOpen || _target == FocusTarget.Goto && !_gotoOpen)
            _target = FocusTarget.Text;

        switch (_target)
        {
            case FocusTarget.Find:
                if (FeedField(_findField, k, chars))
                    FindStep(!shift);
                SyncFindQuery();
                break;

            case FocusTarget.Goto:
                if (FeedField(_gotoField, k, chars))
                {
                    int value = _gotoField.ParseInt();
                    _gotoOpen = false;
                    _target = FocusTarget.Text;
                    if (value > 0)
                        GoToLine(value - 1);
                    else
                        _focusTextNextFrame = true;
                }
                break;

            default:
                if (textFocused)
                    HandleTextKeys(k, chars, pageLines);
                break;
        }
    }

    /// <summary>Edits a bar field. Returns true when Enter was pressed.</summary>
    bool FeedField(LineInput field, IKeyInput k, ReadOnlySpan<char> chars)
    {
        bool ctrl = k.Ctrl, alt = k.Alt;
        if (ctrl && !alt)
        {
            if (k.IsPressed(EditorKey.A))
                field.SelectAll();
            else if (k.IsPressed(EditorKey.C))
                Clipboard.SetText(new string(field.Text));
            else if (k.IsPressed(EditorKey.V))
            {
                var clip = Clipboard.GetText();
                if (!string.IsNullOrEmpty(clip))
                    field.Insert(clip);
            }
        }
        if (k.IsPressed(EditorKey.Backspace)) field.Backspace();
        if (k.IsPressed(EditorKey.Delete)) field.Delete();
        if (k.IsPressed(EditorKey.Left)) field.MoveLeft();
        if (k.IsPressed(EditorKey.Right)) field.MoveRight();
        if (k.IsPressed(EditorKey.Home)) field.Home();
        if (k.IsPressed(EditorKey.End)) field.End();

        if (!ctrl || alt)
            foreach (var c in chars)
                field.Insert(c); // control chars ('\r', '\b', ...) are ignored by the field

        return !ctrl && k.IsPressed(EditorKey.Enter);
    }

    void HandleTextKeys(IKeyInput k, ReadOnlySpan<char> chars, int pageLines)
    {
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

        if (!ctrl || alt) // Ctrl+Alt = AltGr on European layouts
        {
            foreach (var c in chars)
                if (c >= 32 && c != 127)
                    _core.TypeChar(c);
        }
    }

    void OpenFind()
    {
        _findOpen = true;
        _gotoOpen = false;
        _target = FocusTarget.Find;
        _findOrigin = _core.SelectionStart;
        if (_core.HasSelection && _core.SelectionStart.Line == _core.SelectionEnd.Line)
            _findField.SetText(_core.Buffer.GetLineSpan(_core.SelectionStart.Line)[_core.SelectionStart.Column.._core.SelectionEnd.Column]);
        else
            _findField.SelectAll();
    }

    void OpenGoto()
    {
        _gotoOpen = true;
        _findOpen = false;
        _target = FocusTarget.Goto;
        _gotoField.Clear();
    }

    void CloseBars()
    {
        _findOpen = false;
        _gotoOpen = false;
        _target = FocusTarget.Text;
        _focusTextNextFrame = true;
    }

    /// <summary>Pushes the find field into the search model; on change, selects the first match from where Find was opened.</summary>
    void SyncFindQuery()
    {
        if (_findField.Version == _findFieldSeenVersion)
            return;
        _findFieldSeenVersion = _findField.Version;
        _find.SetQuery(_findField.Text, _caseSensitive);
        if (_find.HasQuery && _find.FindNext(_core.Buffer, _findOrigin, out var s, out var e))
            _core.Select(s, e);
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
        _textWindowFocused = textFocused;

        var winPos = ImGui.GetWindowPos();
        var visible = ImGui.GetWindowContentRegionMax() - ImGui.GetWindowContentRegionMin();
        var origin = ImGui.GetCursorScreenPos();

        var f = new FrameParams
        {
            ContentOrigin = origin,
            WindowPos = winPos,
            WindowSize = visible,
            Focused = textFocused && _target == FocusTarget.Text,
            HighlightLine = _highlightLine,
            ShowFindMatches = _findOpen && _find.HasQuery,
            CurrentMatchStart = _core.SelectionStart,
            CurrentMatchEnd = _core.SelectionEnd,
        };

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

    void HandleMouse(in FrameParams f)
    {
        bool hovered = ImGui.IsWindowHovered();
        var mouse = ImGui.GetMousePos();
        float gutterRight = f.WindowPos.X + _view.GutterWidth(_metrics);

        if (hovered && mouse.X >= gutterRight)
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _target = FocusTarget.Text;
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

    // ================================================================= bars (drawn with our own field, not ImGui.InputText)

    /// <summary>Draws a single-line field; returns true when clicked (caller makes it the key target).</summary>
    bool DrawField(string id, LineInput field, float width, bool active)
    {
        var pal = _view.Palette;
        float h = ImGui.GetFrameHeight();
        ImGui.InvisibleButton(id, new Vector2(width, h));
        bool clicked = ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        const float pad = 4f;
        var text = field.Text;
        float caretX = 0;
        for (int i = 0; i < field.Cursor; i++)
            caretX += _metrics.Advance(text[i]);
        float textW = caretX;
        for (int i = field.Cursor; i < text.Length; i++)
            textW += _metrics.Advance(text[i]);
        float scroll = MathF.Max(0, caretX - (width - 2 * pad - 2));

        if (clicked)
        {
            float local = ImGui.GetMousePos().X - (min.X + pad) + scroll;
            float x = 0;
            int idx = 0;
            while (idx < text.Length && x + _metrics.Advance(text[idx]) * 0.5f < local)
                x += _metrics.Advance(text[idx++]);
            field.SetCursor(idx);
        }

        _sink.Begin(ImGui.GetWindowDrawList());
        _sink.Rect(min, max, pal.FieldBackground);
        uint border = active ? pal.FieldBorderActive : pal.FieldBorder;
        _sink.Line(min, new Vector2(max.X, min.Y), border, 1f);
        _sink.Line(new Vector2(max.X, min.Y), max, border, 1f);
        _sink.Line(max, new Vector2(min.X, max.Y), border, 1f);
        _sink.Line(new Vector2(min.X, max.Y), min, border, 1f);

        _sink.PushClip(min + new Vector2(pad, 0), max - new Vector2(pad, 0));
        float ty = min.Y + (h - ImGui.GetFontSize()) * 0.5f;
        float tx = min.X + pad - scroll;
        if (field.AllSelected)
            _sink.Rect(new Vector2(tx, ty), new Vector2(tx + textW, ty + ImGui.GetFontSize()), pal.Selection);
        _sink.Text(new Vector2(tx, ty), pal.Tokens[0], text);
        if (active && (ImGui.GetTime() - _blinkStart) % 1.0 < 0.6)
            _sink.Rect(new Vector2(tx + caretX, ty), new Vector2(tx + caretX + 1.5f, ty + ImGui.GetFontSize()), pal.Cursor);
        _sink.PopClip();
        return clicked;
    }

    void DrawFindBar()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Find");
        ImGui.SameLine();
        float w = MathF.Min(260, MathF.Max(80, ImGui.GetContentRegionAvail().X - 260));
        if (DrawField("##findfield", _findField, w, _target == FocusTarget.Find))
            _target = FocusTarget.Find;

        ImGui.SameLine();
        if (ImGui.Button("<"))
            FindStep(false);
        ImGui.SameLine();
        if (ImGui.Button(">"))
            FindStep(true);
        ImGui.SameLine();
        if (ImGui.Checkbox("Aa", ref _caseSensitive))
            _find.SetQuery(_findField.Text, _caseSensitive);
        ImGui.SameLine();

        Span<char> label = stackalloc char[32];
        int count = _find.CountMatches(_core.Buffer);
        int len;
        if (!_find.HasQuery)
            len = 0;
        else if (count == 0)
        {
            "No results".AsSpan().CopyTo(label);
            len = 10;
        }
        else
        {
            count.TryFormat(label, out len);
            var suffix = count == 1 ? " match" : " matches";
            suffix.AsSpan().CopyTo(label[len..]);
            len += suffix.Length;
        }
        _sink.TextUnformatted(label[..len]);
        ImGui.SameLine();
        if (ImGui.Button("x##closefind"))
            CloseBars();
    }

    void DrawGotoBar()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Go to line");
        ImGui.SameLine();
        if (DrawField("##gotofield", _gotoField, 90, _target == FocusTarget.Goto))
            _target = FocusTarget.Goto;
        ImGui.SameLine();
        Span<char> label = stackalloc char[24];
        "(1-".AsSpan().CopyTo(label);
        _core.Buffer.LineCount.TryFormat(label[3..], out int w);
        label[3 + w] = ')';
        _sink.TextUnformatted(label[..(4 + w)]);
        ImGui.SameLine();
        if (ImGui.Button("x##closegoto"))
            CloseBars();
    }

    // ================================================================= test hooks

    internal EditorCore Core => _core;
    internal DiagnosticStore Diagnostics => _diagnostics;
    internal FindModel Find => _find;
    internal FocusTarget Target => _target;
    internal bool FindOpen => _findOpen;
    internal bool GotoOpen => _gotoOpen;
    internal LineInput FindField => _findField;
    internal LineInput GotoField => _gotoField;

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
