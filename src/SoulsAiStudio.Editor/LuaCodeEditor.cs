// Parts derived from ImGuiColorTextEditNet (MIT) TextEditor.cs, Editor/TextEditorRenderer.cs and
// Input/Standard*Input.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Editor.Completion;
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

    internal enum FocusTarget { Text, Find, Replace, Goto }

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
    readonly LineInput _replaceField = new(256);
    readonly CompletionController _completion;

    int _highlightLine = -1;
    int _scrollCenterLine = -1;
    bool _focusRequested;
    bool _mouseSelecting;
    double _blinkStart;
    long _lastVersion = -1;
    Coordinates _lastCursor;
    bool _textWindowFocused;
    bool _lastEditorFocused;
    int _enterCooldown; // frames during which an Enter from the OTHER source (key vs typed CR) is ignored
    bool _lastEnterWasChar;
    int _lastTypedCount;

    FocusTarget _target = FocusTarget.Text;
    bool _findOpen;
    bool _caseSensitive;
    bool _wholeWord;
    bool _statusIsSwap; // the "Replaced N" status came from Swap
    Coordinates _findOrigin;
    int _findFieldSeenVersion = -1;
    bool _gotoOpen;
    bool _replaceOpen;
    int _replaceStatusCount = -1; // "Replaced N" shown until the text changes again
    long _replaceStatusVersion = -1;
    bool _focusTextNextFrame;

    // completion popup geometry from the last draw (for mouse hits before this frame's draw)
    bool _popupDrawn;
    Vector2 _popupMin, _popupMax;
    float _popupRowH;

    long _widthVersion = -1;
    float _maxLineWidth;

    public LuaCodeEditor()
    {
        _core.Clipboard = ImGuiClipboard.Instance;
        _diagnostics.Attach(_core.Buffer);
        _colorizer = new Colorizer(_core.Buffer);
        _view = new EditorView(_core, _diagnostics, _find);
        _completion = new CompletionController(_core)
        {
            LineStartState = line => line > 0 && line - 1 < _core.Buffer.LineCount ? _colorizer.EndState(line - 1) : LuaLexState.Normal,
        };
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
            _completion.Close();
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

    /// <summary>
    /// Replaces [start, end) (0-based line/column) with <paramref name="text"/> as one undo step, for edits made from
    /// outside the editor (e.g. the act weight table). The cursor stays on its line when <paramref name="keepCursor"/>.
    /// Raises <see cref="TextChanged"/>. Returns false when read-only or nothing changed.
    /// </summary>
    public bool ApplyEdit(int startLine, int startColumn, int endLine, int endColumn, string text, bool keepCursor = true)
    {
        _completion.Close();
        long v = Version;
        var keep = _core.Cursor;
        var after = keepCursor ? new SelectionState(keep, keep) : (SelectionState?)null;
        bool ok = _core.ReplaceRange(new Coordinates(startLine, startColumn), new Coordinates(endLine, endColumn), text, after: after);
        if (ok && keepCursor) _core.ScrollToCursorRequested = false;
        if (v != Version)
            TextChanged?.Invoke();
        _lastVersion = Version;
        return ok;
    }

    /// <summary>Selects [startColumn, endColumn) of <paramref name="line"/> (0-based), centers it and focuses the editor.</summary>
    public void SelectRange(int line, int startColumn, int endColumn)
    {
        line = Math.Clamp(line, 0, _core.Buffer.LineCount - 1);
        _core.Select(new Coordinates(line, startColumn), new Coordinates(line, endColumn));
        _scrollCenterLine = line;
        _focusRequested = true;
        _target = FocusTarget.Text;
    }

    /// <summary>Text of one line (0-based; allocates). Empty when out of range.</summary>
    public string GetLineText(int line) =>
        line >= 0 && line < _core.Buffer.LineCount ? new string(_core.Buffer.GetLineSpan(line)) : "";

    /// <summary>
    /// Replaces every occurrence of <paramref name="find"/> with <paramref name="replacement"/> (same matching as the
    /// find bar) as ONE undo step, for edits made from outside (Find in Scripts). Raises <see cref="TextChanged"/>.
    /// Returns the number of replacements.
    /// </summary>
    public int ReplaceAllText(string find, string replacement, bool matchCase, bool wholeWord) =>
        ExternalFindEdit(find, replacement, matchCase, wholeWord, swap: false);

    /// <summary>Exchanges every <paramref name="a"/> with <paramref name="b"/> and back (find bar Swap) as ONE undo step.</summary>
    public int SwapAllText(string a, string b, bool matchCase, bool wholeWord) =>
        ExternalFindEdit(a, b, matchCase, wholeWord, swap: true);

    int ExternalFindEdit(string find, string other, bool matchCase, bool wholeWord, bool swap)
    {
        if (_core.ReadOnly || string.IsNullOrEmpty(find))
            return 0;
        _completion.Close();
        var model = new FindModel();
        model.SetQuery(find, matchCase, wholeWord);
        long v = Version;
        int n = swap ? _core.SwapAll(model, other, out _) : _core.ReplaceAll(model, other);
        _core.ScrollToCursorRequested = false;
        if (v != Version)
            TextChanged?.Invoke();
        _lastVersion = Version;
        return n;
    }

    bool _findInFilesRequested;
    string _findInFilesSeed = "";

    void RequestFindInFiles()
    {
        _completion.Close();
        _findInFilesRequested = true;
        if (_core.HasSelection && _core.SelectionStart.Line == _core.SelectionEnd.Line)
            _findInFilesSeed = _core.GetSelectedText();
        else
        {
            var (s, e) = _core.WordRangeAt(_core.Cursor);
            _findInFilesSeed = s != e && EditorCore.IsWordChar(_core.Buffer.GetLineSpan(s.Line)[s.Column]) ? _core.Buffer.GetText(s, e) : "";
        }
    }

    /// <summary>
    /// True once after Ctrl+Shift+F was pressed in this editor; <paramref name="seed"/> = the selected text or the word
    /// at the cursor (may be empty). The host opens its "Find in Scripts" panel.
    /// </summary>
    public bool TryTakeFindInFilesRequest(out string seed)
    {
        seed = _findInFilesSeed;
        if (!_findInFilesRequested)
            return false;
        _findInFilesRequested = false;
        return true;
    }

    /// <summary>Number of carets (1 + secondary carets from Ctrl+D / Alt+Click / Ctrl+Alt+Up/Down).</summary>
    public int CaretCount => _core.CaretCount;

    public void Undo()
    {
        _completion.Close();
        long v = Version;
        _core.DoUndo();
        if (v != Version)
            TextChanged?.Invoke();
        _lastVersion = Version;
    }

    public void Redo()
    {
        _completion.Close();
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
    /// Extra completion items (other open scripts, game API seeds). The editor itself always offers Lua keywords,
    /// builtins and the identifiers of its own buffer. See <see cref="WorkspaceCompletionProvider"/>.
    /// </summary>
    public ICompletionProvider? CompletionProvider
    {
        get => _completion.Provider;
        set => _completion.Provider = value;
    }

    /// <summary>Enables the suggestion popup (automatic and Ctrl+Space). Default true.</summary>
    public bool AutoCompleteEnabled
    {
        get => _completion.Enabled;
        set
        {
            _completion.Enabled = value;
            if (!value)
                _completion.Close();
        }
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
            // Root+children: the editor keeps the keyboard when the host focuses its window (e.g. DSAS docking /
            // SetWindowFocus on the panel) rather than our child, which otherwise silently dropped typed chars.
            bool editorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || _focusRequested;
            _lastEditorFocused = editorFocused;
            float lineH = _metrics.LineHeight;
            var avail = ImGui.GetContentRegionAvail();
            bool textKeys = editorFocused && !ImGui.IsAnyItemActive();
            ProcessKeys(KeyInput, editorFocused, textKeys, (int)MathF.Max(1, avail.Y / lineH - 2));

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

            focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
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
        _lastTypedCount = n;
        bool enterCooling = _enterCooldown > 0;
        if (_enterCooldown > 0)
            _enterCooldown--;
        if (!editorFocused)
        {
            _completion.Close();
            return;
        }

        bool ctrl = k.Ctrl, shift = k.Shift, alt = k.Alt;
        var chars = _typed.AsSpan(0, n);

        // Ctrl+F / Ctrl+H / Ctrl+G switch the target; chars typed later in the same frame (fast typists, SendKeys)
        // continue into the newly opened field instead of being dropped.
        if (ctrl && !alt && shift && k.IsPressed(EditorKey.F))
            RequestFindInFiles();
        else if (ctrl && !alt && k.IsPressed(EditorKey.F))
            OpenFind(replace: false);
        else if (ctrl && !alt && k.IsPressed(EditorKey.H))
            OpenFind(replace: true);
        else if (ctrl && !alt && k.IsPressed(EditorKey.G))
            OpenGoto();
        if (k.IsPressed(EditorKey.F3))
            FindStep(!shift);
        if (k.IsPressed(EditorKey.Escape))
        {
            if (_target == FocusTarget.Text && _completion.IsOpen)
                _completion.Close(); // Escape closes the popup first, the bars on the next press
            else if (_target == FocusTarget.Text && _core.HasExtras)
                _core.CollapseToPrimary(); // then the secondary carets
            else if (_findOpen || _gotoOpen)
            {
                CloseBars();
                return;
            }
        }

        if ((_target == FocusTarget.Find || _target == FocusTarget.Replace) && !_findOpen || _target == FocusTarget.Goto && !_gotoOpen)
            _target = FocusTarget.Text;
        if (_target == FocusTarget.Replace && !_replaceOpen)
            _target = FocusTarget.Find;

        switch (_target)
        {
            case FocusTarget.Find:
                if (_replaceOpen && !ctrl && k.IsPressed(EditorKey.Tab))
                {
                    _target = FocusTarget.Replace; // Tab: find field -> replace field
                    break;
                }
                if (FeedField(_findField, k, chars, enterCooling))
                    FindStep(!shift);
                SyncFindQuery();
                break;

            case FocusTarget.Replace:
                if (!ctrl && k.IsPressed(EditorKey.Tab))
                {
                    _target = FocusTarget.Find; // Tab / Shift+Tab: back to the find field
                    break;
                }
                // Enter = replace and go to the next match; Ctrl+Enter = replace all
                if (FeedField(_replaceField, k, chars, enterCooling, allowCtrlEnter: true))
                {
                    if (ctrl)
                        ReplaceAll();
                    else
                        ReplaceOne();
                }
                break;

            case FocusTarget.Goto:
                if (FeedField(_gotoField, k, chars, enterCooling))
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
                    HandleTextKeys(k, chars, pageLines, enterCooling);
                else
                    _completion.Close();
                break;
        }
    }

    /// <summary>
    /// Edits a bar field. Returns true when Enter was pressed: either the Enter key or a typed CR/LF char
    /// (hosts that poll key state can miss a short Enter press, but WM_CHAR CR still arrives). One Enter per
    /// frame; the other source is then ignored for a few frames so it can't fire twice.
    /// </summary>
    bool FeedField(LineInput field, IKeyInput k, ReadOnlySpan<char> chars, bool enterCooling, bool allowCtrlEnter = false)
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

        // Printable chars are accepted even if Ctrl reads as held: WM_CHAR only yields printable chars for
        // keys typed without Ctrl (Ctrl+letter gives control codes), and polled modifier state can lag a frame.
        bool enterChar = false;
        foreach (var c in chars)
        {
            if (c == '\r' || c == '\n')
            {
                enterChar = true;
                break; // anything after Enter belongs to whatever has focus next
            }
            field.Insert(c); // other control chars are ignored by the field
        }

        bool enterKey = (!ctrl || allowCtrlEnter) && k.IsPressed(EditorKey.Enter);
        if (!enterChar && !enterKey)
            return false;
        // One physical press can show up as key AND typed CR, possibly a frame apart. While cooling down,
        // only accept Enter from the same source as the previous one (so repeated presses still work).
        bool fromChar = enterChar && !(enterKey && enterCooling && !_lastEnterWasChar);
        if (enterCooling && fromChar != _lastEnterWasChar)
            return false;
        _lastEnterWasChar = fromChar;
        _enterCooldown = 3;
        return true;
    }

    void HandleTextKeys(IKeyInput k, ReadOnlySpan<char> chars, int pageLines, bool enterCooling)
    {
        bool ctrl = k.Ctrl, shift = k.Shift, alt = k.Alt;
        bool ctrlSpace = ctrl && !alt && k.IsPressed(EditorKey.Space);

        if (ctrl && !alt)
        {
            if (k.IsPressed(EditorKey.Z)) { _completion.Close(); if (shift) _core.DoRedo(); else _core.DoUndo(); }
            else if (k.IsPressed(EditorKey.Y)) { _completion.Close(); _core.DoRedo(); }
            else if (k.IsPressed(EditorKey.A)) _core.SelectAll();
            else if (k.IsPressed(EditorKey.C)) _core.Copy();
            else if (k.IsPressed(EditorKey.X)) { _completion.Close(); _core.Cut(); }
            else if (k.IsPressed(EditorKey.V)) { _completion.Close(); _core.Paste(); }

            // VSCode-style line commands
            if (k.IsPressed(EditorKey.L)) { _completion.Close(); if (shift) _core.SelectAllOccurrences(); else _core.SelectLineDown(); }
            if (shift && k.IsPressed(EditorKey.K)) { _completion.Close(); _core.DeleteLines(); }
            if (k.IsPressed(EditorKey.D)) { _completion.Close(); _core.SelectNextOccurrence(); }
            if (k.IsPressed(EditorKey.Slash)) { _completion.Close(); _core.ToggleLineComment(); }
            if (k.IsPressed(EditorKey.BracketRight)) _core.IndentLines(true);
            if (k.IsPressed(EditorKey.BracketLeft)) _core.IndentLines(false);
            if (k.IsPressed(EditorKey.Enter)) { _completion.Close(); _core.InsertLine(above: shift); }
        }

        // Ctrl+Alt+Up/Down: add a caret above / below.
        if (alt && ctrl)
        {
            int dir = k.IsPressed(EditorKey.Up) ? -1 : k.IsPressed(EditorKey.Down) ? 1 : 0;
            if (dir != 0)
            {
                _completion.Close();
                _core.AddCaretVertical(dir);
            }
        }

        // Alt+Up/Down: move lines; Shift+Alt+Up/Down: copy lines.
        if (alt && !ctrl)
        {
            int dir = k.IsPressed(EditorKey.Up) ? -1 : k.IsPressed(EditorKey.Down) ? 1 : 0;
            if (dir != 0)
            {
                _completion.Close();
                if (shift) _core.CopyLines(dir); else _core.MoveLines(dir);
            }
        }

        if (!alt)
        {
            // While the popup is open it owns Up/Down/PageUp/PageDown and accepts with Enter/Tab.
            // With the popup closed these keys behave exactly as before (never stolen).
            bool popupKeys = _completion.IsOpen && !ctrl;
            if (popupKeys && k.IsPressed(EditorKey.Up)) _completion.Move(-1);
            else if (k.IsPressed(EditorKey.Up)) _core.MoveUp(1, shift);
            if (popupKeys && k.IsPressed(EditorKey.Down)) _completion.Move(1);
            else if (k.IsPressed(EditorKey.Down)) _core.MoveDown(1, shift);
            if (popupKeys && k.IsPressed(EditorKey.PageUp)) _completion.Move(-CompletionController.MaxVisible);
            else if (k.IsPressed(EditorKey.PageUp)) _core.MoveUp(pageLines, shift);
            if (popupKeys && k.IsPressed(EditorKey.PageDown)) _completion.Move(CompletionController.MaxVisible);
            else if (k.IsPressed(EditorKey.PageDown)) _core.MoveDown(pageLines, shift);

            if (k.IsPressed(EditorKey.Left)) _core.MoveLeft(shift, ctrl);
            if (k.IsPressed(EditorKey.Right)) _core.MoveRight(shift, ctrl);
            if (k.IsPressed(EditorKey.Home)) { if (ctrl) _core.MoveToStartOfFile(shift); else _core.MoveHome(shift); }
            if (k.IsPressed(EditorKey.End)) { if (ctrl) _core.MoveToEndOfFile(shift); else _core.MoveEnd(shift); }
            if (k.IsPressed(EditorKey.Backspace)) _core.Backspace(ctrl);
            if (k.IsPressed(EditorKey.Delete)) _core.Delete(ctrl);

            bool enter = !ctrl && !(enterCooling && _lastEnterWasChar) && k.IsPressed(EditorKey.Enter);
            bool tab = !ctrl && k.IsPressed(EditorKey.Tab);
            if (popupKeys && _completion.IsOpen && (enter || tab && !shift))
                _completion.Accept();
            else
            {
                if (enter) _core.TypeChar('\n');
                if (tab) _core.Tab(shift);
            }
        }

        // Printable chars only come from keys typed without Ctrl (see FeedField), so no Ctrl gating here.
        // Ctrl+Space also arrives as a typed ' ': drop it.
        bool typed = false;
        foreach (var c in chars)
        {
            if (c < 32 || c == 127 || ctrlSpace && c == ' ')
                continue;
            _core.TypeChar(c);
            typed = true;
        }

        if (_core.HasExtras)
            _completion.Close(); // suggestions work with a single caret only
        else if (ctrlSpace)
            _completion.OpenExplicit();
        else if (typed)
            _completion.OnTyped();
        else if (_completion.IsOpen)
            _completion.Update(); // cursor moved / Backspace: refilter or close
    }

    void OpenFind(bool replace)
    {
        _completion.Close();
        bool wasInBar = _findOpen && (_target == FocusTarget.Find || _target == FocusTarget.Replace);
        _findOpen = true;
        _gotoOpen = false;
        if (replace)
            _replaceOpen = true;
        else if (!wasInBar)
            _replaceOpen = false; // Ctrl+F from the code shows the plain find bar
        _target = FocusTarget.Find;
        _findOrigin = _core.SelectionStart;
        if (_core.HasSelection && _core.SelectionStart.Line == _core.SelectionEnd.Line)
            _findField.SetText(_core.Buffer.GetLineSpan(_core.SelectionStart.Line)[_core.SelectionStart.Column.._core.SelectionEnd.Column]);
        else
            _findField.SelectAll();
    }

    void OpenGoto()
    {
        _completion.Close();
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
        _find.SetQuery(_findField.Text, _caseSensitive, _wholeWord);
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

    /// <summary>Replaces the current match (if the selection is one) and selects the next match.</summary>
    void ReplaceOne()
    {
        SyncFindQuery();
        if (!_find.HasQuery || _core.ReadOnly)
            return;
        if (_find.IsMatchAt(_core.Buffer, _core.SelectionStart, _core.SelectionEnd))
            _core.ReplaceSelection(new string(_replaceField.Text));
        // search from the end of the replacement, so the inserted text is never matched again
        if (_find.FindNext(_core.Buffer, _core.SelectionEnd, out var s, out var e))
            _core.Select(s, e);
    }

    /// <summary>Replaces every match as one undo step. Returns the number of replacements.</summary>
    int ReplaceAll()
    {
        SyncFindQuery();
        if (!_find.HasQuery || _core.ReadOnly)
            return 0;
        int n = _core.ReplaceAll(_find, _replaceField.Text);
        _replaceStatusCount = n;
        _replaceStatusVersion = _core.Buffer.Version;
        _statusIsSwap = false;
        return n;
    }

    /// <summary>
    /// Exchanges the Find text and the Replace text everywhere (e.g. 3001 ↔ 3012) as one undo step, with the same
    /// Match case / Whole word options. Returns the number of exchanged occurrences.
    /// </summary>
    internal int SwapAll()
    {
        SyncFindQuery();
        if (!_find.HasQuery || _replaceField.Text.IsEmpty || _core.ReadOnly)
            return 0;
        int n = _core.SwapAll(_find, _replaceField.Text, out _);
        _replaceStatusCount = n;
        _replaceStatusVersion = _core.Buffer.Version;
        _statusIsSwap = true;
        return n;
    }

    /// <summary>Find options, as the find bar checkboxes set them (also for tests).</summary>
    internal void SetFindOptions(bool matchCase, bool wholeWord)
    {
        _caseSensitive = matchCase;
        _wholeWord = wholeWord;
        _find.SetQuery(_findField.Text, _caseSensitive, _wholeWord);
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
            Focused = (textFocused || _lastEditorFocused) && _target == FocusTarget.Text,
            HighlightLine = _highlightLine,
            ShowFindMatches = _findOpen && _find.HasQuery,
            CurrentMatchStart = _core.SelectionStart,
            CurrentMatchEnd = _core.SelectionEnd,
        };

        if (!HandlePopupMouse())
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
        if (_completion.IsOpen)
            _completion.Update(); // picks up the background buffer scan
        DrawCompletionPopup(in f);

        // content size for scrollbars
        if (_widthVersion != Version)
        {
            _widthVersion = Version;
            _maxLineWidth = ComputeMaxLineWidth(m);
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(_view.TextStartX(m) + _maxLineWidth + 4 * lineH, _core.Buffer.LineCount * lineH + visible.Y * 0.5f));

        if (!DrawDiagnosticTooltip(in f))
            DrawNumberHoverTooltip(in f);
    }

    /// <summary>
    /// Hover tooltip for an integer literal: (number, text of its line) → text to show, or null for none. E.g. the
    /// app shows the label of a SpEffect id. Called only while the mouse rests on a number.
    /// </summary>
    public Func<long, string, string?>? NumberHoverText { get; set; }

    void DrawNumberHoverTooltip(in FrameParams f)
    {
        if (NumberHoverText == null || !ImGui.IsWindowHovered() || ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;
        var mouse = ImGui.GetMousePos();
        if (mouse.X < f.WindowPos.X + _view.GutterWidth(_metrics))
            return;
        var pos = _view.ScreenToCoordinates(mouse, in f, _metrics);
        if (pos.Line < 0 || pos.Line >= _core.Buffer.LineCount)
            return;
        float lineTop = f.ContentOrigin.Y + pos.Line * _metrics.LineHeight;
        if (mouse.Y < lineTop || mouse.Y > lineTop + _metrics.LineHeight)
            return;
        var line = _core.Buffer.GetLineSpan(pos.Line);
        int c = Math.Min(pos.Column, line.Length - 1);
        if (c < 0)
            return;
        // ScreenToCoordinates rounds to the nearest caret position: the char under the mouse is at c or c - 1.
        if (!char.IsAsciiDigit(line[c]) && c > 0 && char.IsAsciiDigit(line[c - 1]))
            c--;
        if (!char.IsAsciiDigit(line[c]))
            return;
        int a = c, b = c + 1;
        while (a > 0 && char.IsAsciiDigit(line[a - 1])) a--;
        while (b < line.Length && char.IsAsciiDigit(line[b])) b++;
        // Part of a name (f2_local6), a decimal (1.5) or a hex literal: not an id.
        if (a > 0 && (char.IsAsciiLetter(line[a - 1]) || line[a - 1] is '_' or '.'))
            return;
        if (b < line.Length && (char.IsAsciiLetter(line[b]) || line[b] is '_' or '.'))
            return;
        if (b - a > 18 || !long.TryParse(line[a..b], out long n))
            return;
        string? text = NumberHoverText(n, line.ToString());
        if (string.IsNullOrEmpty(text))
            return;
        ImGui.BeginTooltip();
        _sink.TextUnformatted(text);
        ImGui.EndTooltip();
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

    /// <returns>True when a diagnostic tooltip was shown.</returns>
    bool DrawDiagnosticTooltip(in FrameParams f)
    {
        if (_diagnostics.Count == 0 || !ImGui.IsWindowHovered())
            return false;
        var mouse = ImGui.GetMousePos();
        var pos = _view.ScreenToCoordinates(mouse, in f, _metrics);
        float lineTop = f.ContentOrigin.Y + pos.Line * _metrics.LineHeight;
        if (mouse.Y < lineTop || mouse.Y > lineTop + _metrics.LineHeight)
            return false;

        bool inGutter = mouse.X < f.WindowPos.X + _view.GutterWidth(_metrics);
        int d = _view.DiagnosticAt(pos, anyOnLine: inGutter);
        if (d < 0)
            return false;

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
        return true;
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
            _completion.Close();
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
            else if (KeyInput.Alt && !KeyInput.Shift && !KeyInput.Ctrl)
            {
                _core.AddCaret(pos); // Alt+Click: add a caret
                return;
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
        // row 1: [>] Find [field] < > Aa  "3 of 17"  x
        if (ImGui.Button(_replaceOpen ? "v##togglereplace" : ">##togglereplace"))
        {
            _replaceOpen = !_replaceOpen;
            if (!_replaceOpen && _target == FocusTarget.Replace)
                _target = FocusTarget.Find;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Toggle Replace (Ctrl+H)");
        ImGui.SameLine();
        float labelX = ImGui.GetCursorPosX();
        float labelW = ImGui.CalcTextSize("Replace").X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Find");
        ImGui.SameLine(labelX + labelW);
        float w = MathF.Min(260, MathF.Max(80, ImGui.GetContentRegionAvail().X - 300));
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
            _find.SetQuery(_findField.Text, _caseSensitive, _wholeWord);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Match case");
        ImGui.SameLine();
        if (ImGui.Checkbox("W", ref _wholeWord))
            _find.SetQuery(_findField.Text, _caseSensitive, _wholeWord);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Match whole word: 3001 does not match inside 30012 or a3001");
        ImGui.SameLine();

        Span<char> label = stackalloc char[48];
        int len = FormatMatchStatus(label);
        _sink.TextUnformatted(label[..len]);
        ImGui.SameLine();
        if (ImGui.Button("x##closefind"))
            CloseBars();

        if (!_replaceOpen)
            return;

        // row 2:      Replace [field] [Replace] [Replace All]
        ImGui.SetCursorPosX(labelX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Replace");
        ImGui.SameLine(labelX + labelW);
        if (DrawField("##replacefield", _replaceField, w, _target == FocusTarget.Replace))
            _target = FocusTarget.Replace;
        ImGui.SameLine();
        bool canReplace = _find.HasQuery && !_core.ReadOnly;
        if (!canReplace)
            ImGui.BeginDisabled();
        if (ImGui.Button("Replace##one"))
            ReplaceOne();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Replace this match and go to the next (Enter in the replace field)");
        ImGui.SameLine();
        if (ImGui.Button("Replace All##all"))
            ReplaceAll();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Replace every match, one undo step (Ctrl+Enter in the replace field)");
        if (!canReplace)
            ImGui.EndDisabled();
        ImGui.SameLine();
        bool canSwap = canReplace && !_replaceField.Text.IsEmpty;
        if (!canSwap)
            ImGui.BeginDisabled();
        if (ImGui.Button("Swap##swap"))
            SwapAll();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Exchange the two texts everywhere at once: every Find becomes Replace and every Replace becomes Find\n" +
                             "(e.g. 3001 <-> 3012), one undo step. Tip: turn on W (whole word) for anim ids.");
        if (!canSwap)
            ImGui.EndDisabled();
    }

    /// <summary>"3 of 17", "- of 17" (selection not on a match), "No results", "Replaced 5"; empty without a query. No allocation.</summary>
    internal int FormatMatchStatus(Span<char> label)
    {
        if (_replaceStatusCount >= 0 && _replaceStatusVersion == _core.Buffer.Version)
            return Concat(label, _statusIsSwap ? "Swapped " : "Replaced ", _replaceStatusCount, _replaceStatusCount == 1 ? " match" : " matches");
        if (!_find.HasQuery)
            return 0;
        int count = _find.CountMatches(_core.Buffer);
        if (count == 0)
        {
            "No results".AsSpan().CopyTo(label);
            return 10;
        }
        int index = _find.MatchIndex(_core.Buffer, _core.SelectionStart, _core.SelectionEnd);
        int len;
        if (index > 0)
            index.TryFormat(label, out len);
        else
        {
            label[0] = '-';
            len = 1;
        }
        " of ".AsSpan().CopyTo(label[len..]);
        len += 4;
        count.TryFormat(label[len..], out int w);
        return len + w;
    }

    static int Concat(Span<char> dst, string a, int n, string b)
    {
        a.AsSpan().CopyTo(dst);
        n.TryFormat(dst[a.Length..], out int w);
        b.AsSpan().CopyTo(dst[(a.Length + w)..]);
        return a.Length + w + b.Length;
    }

    // ================================================================= completion popup

    static LuaTokenKind KindColor(CompletionKind k) => k switch
    {
        CompletionKind.Keyword => LuaTokenKind.Keyword,
        CompletionKind.Constant => LuaTokenKind.Constant,
        CompletionKind.Function or CompletionKind.Method or CompletionKind.Builtin => LuaTokenKind.Function,
        _ => LuaTokenKind.Identifier,
    };

    float TextWidth(ReadOnlySpan<char> s)
    {
        float w = 0;
        foreach (var c in s)
            w += _metrics.Advance(c);
        return w;
    }

    /// <summary>Draws the suggestion list under (or above) the typed word. Only the visible rows are measured/drawn.</summary>
    void DrawCompletionPopup(in FrameParams f)
    {
        _popupDrawn = false;
        var items = _completion.Items;
        if (!_completion.IsOpen || items.Count == 0)
            return;

        var m = _metrics;
        var pal = _view.Palette;
        float lineH = m.LineHeight;
        float rowH = lineH + 2;
        const float pad = 4;
        int top = _completion.ScrollTop;
        int rows = Math.Min(CompletionController.MaxVisible, items.Count - top);

        float labelW = 0, kindW = 0;
        for (int i = top; i < top + rows; i++)
        {
            labelW = MathF.Max(labelW, TextWidth(items[i].Label));
            kindW = MathF.Max(kindW, TextWidth(items[i].KindLabel));
        }
        float width = MathF.Max(220, labelW + kindW + 4 * pad + 24);
        float height = rows * rowH + 2 * pad;

        var ws = _completion.WordStart;
        float x = f.ContentOrigin.X + _view.TextStartX(m) + _view.ColumnToX(_core.Buffer.GetLineSpan(ws.Line), ws.Column, m) - pad;
        float lineTop = f.ContentOrigin.Y + ws.Line * lineH;
        float y = lineTop + lineH;
        float winBottom = f.WindowPos.Y + f.WindowSize.Y;
        if (y + height > winBottom && lineTop - height >= f.WindowPos.Y)
            y = lineTop - height; // flip above when there is no room below
        float winRight = f.WindowPos.X + f.WindowSize.X;
        if (x + width > winRight)
            x = MathF.Max(f.WindowPos.X, winRight - width);

        var min = new Vector2(x, y);
        var max = new Vector2(x + width, y + height);
        _popupMin = min;
        _popupMax = max;
        _popupRowH = rowH;
        _popupDrawn = true;

        _sink.Begin(ImGui.GetForegroundDrawList());
        _sink.Rect(min, max, pal.PopupBackground);
        _sink.Line(min, new Vector2(max.X, min.Y), pal.PopupBorder, 1f);
        _sink.Line(new Vector2(max.X, min.Y), max, pal.PopupBorder, 1f);
        _sink.Line(max, new Vector2(min.X, max.Y), pal.PopupBorder, 1f);
        _sink.Line(new Vector2(min.X, max.Y), min, pal.PopupBorder, 1f);
        _sink.PushClip(min, max);

        var mouse = ImGui.GetMousePos();
        bool mouseIn = mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y + pad && mouse.Y < max.Y - pad;
        int hoverRow = mouseIn ? (int)((mouse.Y - min.Y - pad) / rowH) : -1;
        float textDy = (rowH - ImGui.GetFontSize()) * 0.5f;
        for (int r = 0; r < rows; r++)
        {
            int i = top + r;
            var item = items[i];
            float ry = min.Y + pad + r * rowH;
            if (i == _completion.Selected)
                _sink.Rect(new Vector2(min.X + 1, ry), new Vector2(max.X - 1, ry + rowH), pal.PopupSelected);
            else if (r == hoverRow)
                _sink.Rect(new Vector2(min.X + 1, ry), new Vector2(max.X - 1, ry + rowH), pal.PopupHover);
            _sink.Text(new Vector2(min.X + 2 * pad, ry + textDy), pal.Tokens[(int)KindColor(item.Kind)], item.Label);
            float kw = TextWidth(item.KindLabel);
            _sink.Text(new Vector2(max.X - 2 * pad - kw - 6, ry + textDy), pal.PopupDim, item.KindLabel);
        }

        // scroll indicator
        if (items.Count > CompletionController.MaxVisible)
        {
            float trackH = height - 2 * pad;
            float thumbH = MathF.Max(8, trackH * CompletionController.MaxVisible / items.Count);
            float thumbY = min.Y + pad + (trackH - thumbH) * top / Math.Max(1, items.Count - CompletionController.MaxVisible);
            _sink.Rect(new Vector2(max.X - 5, thumbY), new Vector2(max.X - 2, thumbY + thumbH), pal.PopupBorder);
        }
        _sink.PopClip();
    }

    /// <summary>Mouse on the popup (last frame's geometry): click accepts a row, wheel scrolls. Returns true if the mouse was handled.</summary>
    bool HandlePopupMouse()
    {
        if (!_popupDrawn || !_completion.IsOpen)
            return false;
        var mouse = ImGui.GetMousePos();
        if (mouse.X < _popupMin.X || mouse.X >= _popupMax.X || mouse.Y < _popupMin.Y || mouse.Y >= _popupMax.Y)
            return false;
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0)
            _completion.Scroll(wheel > 0 ? -3 : 3);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            int row = (int)((mouse.Y - _popupMin.Y - 4) / _popupRowH);
            if (row >= 0 && row < CompletionController.MaxVisible)
            {
                _completion.Select(_completion.ScrollTop + row);
                _completion.Accept();
            }
            _target = FocusTarget.Text;
            return true;
        }
        return wheel != 0 || ImGui.IsMouseDown(ImGuiMouseButton.Left);
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

    /// <summary>One-line input-routing state for host diagnostics (allocates; call only when debugging).</summary>
    public string DebugState =>
        $"target={_target} find={_findOpen} replace={_replaceOpen} goto={_gotoOpen} popup={_completion.IsOpen} findText='{new string(_findField.Text)}' gotoText='{new string(_gotoField.Text)}' " +
        $"editorFocused={_lastEditorFocused} textWindowFocused={_textWindowFocused} typedLastFrame={_lastTypedCount} enterCooldown={_enterCooldown}";

    // ================================================================= test hooks

    internal EditorCore Core => _core;
    internal DiagnosticStore Diagnostics => _diagnostics;
    internal FindModel Find => _find;
    internal FocusTarget Target => _target;
    internal bool FindOpen => _findOpen;
    internal bool GotoOpen => _gotoOpen;
    internal LineInput FindField => _findField;
    internal LineInput GotoField => _gotoField;
    internal LineInput ReplaceField => _replaceField;
    internal bool ReplaceOpen => _replaceOpen;
    internal CompletionController Completion => _completion;

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
