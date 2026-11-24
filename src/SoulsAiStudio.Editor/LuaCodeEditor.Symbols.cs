using System.Numerics;
using System.Text;
using ImGuiNET;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor;

/// <summary>A variable the host resolved at some position: its name, where it is declared and every place it is used.</summary>
/// <param name="Tag">Host data to find the variable again when validating a rename.</param>
public sealed record EditorSymbol(string Name, EditorSpan Declaration, IReadOnlyList<EditorSpan> Occurrences, object? Tag = null);

/// <summary>A word in the text: 0-based line, UTF-16 column, length.</summary>
public readonly record struct EditorSpan(int Line, int Column, int Length);

// F2 rename and Ctrl+Click go-to-declaration. The editor knows nothing about Lua scopes: the host plugs in
// <see cref="SymbolProvider"/> (and <see cref="RenameValidator"/>).
public sealed partial class LuaCodeEditor
{
    /// <summary>(line, column) → the local variable named there, or null (keywords, globals, fields, comments…).</summary>
    public Func<int, int, EditorSymbol?>? SymbolProvider { get; set; }

    /// <summary>(symbol, new name) → why the rename isn't allowed (shown in the bar), or null when it is.</summary>
    public Func<EditorSymbol, string, string?>? RenameValidator { get; set; }

    /// <summary>Shift+Alt+F: the host should format the document (the editor has no formatter).</summary>
    public event Action? FormatRequested;

    // Hover lookups run every frame while Ctrl is held: cache the last answer per (text version, position).
    long _defKeyVersion = -1;
    int _defKeyLine = -1, _defKeyColumn = -1;
    EditorSpan? _defKeyResult;

    /// <summary>The declaration of the variable at <paramref name="pos"/>, or null when there is none or <paramref name="pos"/> is the declaration itself.</summary>
    EditorSpan? DefinitionOf(Coordinates pos)
    {
        if (SymbolProvider == null) return null;
        if (_defKeyVersion == Version && _defKeyLine == pos.Line && _defKeyColumn == pos.Column) return _defKeyResult;
        var sym = SymbolProvider(pos.Line, pos.Column);
        EditorSpan? result = null;
        _defKeyUse = null;
        if (sym != null)
        {
            var d = sym.Declaration;
            bool onDeclaration = pos.Line == d.Line && pos.Column >= d.Column && pos.Column <= d.Column + d.Length;
            if (!onDeclaration)
            {
                result = d;
                foreach (var o in sym.Occurrences)
                    if (o.Line == pos.Line && pos.Column >= o.Column && pos.Column <= o.Column + o.Length) { _defKeyUse = o; break; }
            }
        }
        _defKeyVersion = Version; _defKeyLine = pos.Line; _defKeyColumn = pos.Column; _defKeyResult = result;
        return result;
    }

    EditorSpan? _defKeyUse;      // the use under the pointer for the cached lookup
    EditorSpan? _linkUse;        // use to emphasize this frame (Ctrl held over a variable)
    EditorSpan? _linkDecl;       // its declaration

    // ---- word under the caret (VS Code "occurrence highlight")
    readonly List<EditorSpan> _wordSpans = new();
    long _wordVersion = -1;
    Coordinates _wordCursor;
    int _wordFirstLine = -1;

    void DrawCaretWordHighlight(Vector2 origin, Vector2 visible)
    {
        if (_core.HasSelection || _core.Cursor.Line >= _core.Buffer.LineCount) return;
        var m = _metrics;
        float lineH = m.LineHeight;
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / lineH));
        int last = Math.Min(_core.Buffer.LineCount - 1, first + (int)(visible.Y / lineH) + 1);
        var cur = _core.Cursor;

        if (_wordVersion != Version || _wordCursor != cur || _wordFirstLine != first)
        {
            _wordVersion = Version; _wordCursor = cur; _wordFirstLine = first;
            _wordSpans.Clear();
            var line = _core.Buffer.GetLineSpan(cur.Line);
            int s = Math.Min(cur.Column, line.Length), e = s;
            while (s > 0 && EditorCore.IsWordChar(line[s - 1])) s--;
            while (e < line.Length && EditorCore.IsWordChar(line[e])) e++;
            if (e > s && !char.IsDigit(line[s]))
            {
                string word = new string(line[s..e]);
                var sym = SymbolProvider?.Invoke(cur.Line, s);
                if (sym != null)
                {
                    _wordSpans.AddRange(sym.Occurrences);
                }
                else
                {
                    for (int l = first; l <= last; l++)
                    {
                        var text = _core.Buffer.GetLineSpan(l);
                        int from = 0;
                        while (from <= text.Length - word.Length)
                        {
                            int i = text[from..].IndexOf(word.AsSpan(), StringComparison.Ordinal);
                            if (i < 0) break;
                            i += from;
                            bool okL = i == 0 || !EditorCore.IsWordChar(text[i - 1]);
                            bool okR = i + word.Length >= text.Length || !EditorCore.IsWordChar(text[i + word.Length]);
                            if (okL && okR) _wordSpans.Add(new EditorSpan(l, i, word.Length));
                            from = i + word.Length;
                        }
                    }
                }
            }
        }
        if (_wordSpans.Count < 2) return; // nothing else to relate to

        float textX0 = origin.X + _view.TextStartX(m);
        var dl = ImGui.GetWindowDrawList();
        uint fill = ImGui.GetColorU32(new Vector4(0.6f, 0.65f, 0.75f, 0.22f));
        foreach (var w in _wordSpans)
        {
            if (w.Line < first || w.Line > last || w.Line >= _core.Buffer.LineCount) continue;
            var chars = _core.Buffer.GetLineSpan(w.Line);
            if (w.Column + w.Length > chars.Length) continue;
            float x1 = textX0 + _view.ColumnToX(chars, w.Column, m);
            float x2 = textX0 + _view.ColumnToX(chars, w.Column + w.Length, m);
            float y = origin.Y + w.Line * lineH;
            dl.AddRectFilled(new Vector2(x1, y), new Vector2(x2, y + lineH), fill, 2f);
        }
    }

    /// <summary>Underlines the variable under the pointer (Ctrl held) and boxes the declaration it would jump to.</summary>
    void DrawLinkHighlight(Vector2 origin)
    {
        if (_linkUse is not { } use || _linkDecl is not { } decl) return;
        var m = _metrics;
        float lineH = m.LineHeight;
        float textX0 = origin.X + _view.TextStartX(m);
        var dl = ImGui.GetWindowDrawList();
        var pal = _view.Palette;

        void Box(EditorSpan s, bool underline)
        {
            if (s.Line < 0 || s.Line >= _core.Buffer.LineCount) return;
            var chars = _core.Buffer.GetLineSpan(s.Line);
            float x1 = textX0 + _view.ColumnToX(chars, s.Column, m);
            float x2 = textX0 + _view.ColumnToX(chars, s.Column + s.Length, m);
            float y = origin.Y + s.Line * lineH;
            uint accent = ImGui.GetColorU32(new Vector4(0.45f, 0.75f, 1f, 1f));
            if (underline)
            {
                dl.AddRectFilled(new Vector2(x1, y), new Vector2(x2, y + lineH), ImGui.GetColorU32(new Vector4(0.45f, 0.75f, 1f, 0.22f)));
                dl.AddLine(new Vector2(x1, y + lineH - 2f), new Vector2(x2, y + lineH - 2f), accent, 1.5f);
            }
            else
            {
                dl.AddRect(new Vector2(x1 - 1, y), new Vector2(x2 + 1, y + lineH), accent, 2f, ImDrawFlags.None, 1.5f);
            }
        }
        Box(use, true);
        Box(decl, false);
    }

    /// <summary>Moves to the declaration and selects its name (scrolled to the middle of the view).</summary>
    void GoToDeclaration(EditorSpan decl)
    {
        _completion.Close();
        _mouseSelecting = false;
        SelectRange(decl.Line, decl.Column, decl.Column + decl.Length);
    }

    /// <summary>Jumps to the declaration of the variable under the cursor (same as Ctrl+Click). False when it isn't a local.</summary>
    public bool GoToDefinitionAtCursor()
    {
        var def = DefinitionOf(_core.Cursor);
        if (def == null) return false;
        GoToDeclaration(def.Value);
        return true;
    }

    // ------------------------------------------------------------------------------------------ rename (F2)

    void OpenRename()
    {
        if (ReadOnly) return;
        _completion.Close();
        _findOpen = false;
        _gotoOpen = false;
        _renameOpen = true;
        _target = FocusTarget.Rename;
        _renameError = "";
        _renameSymbol = SymbolProvider?.Invoke(_core.Cursor.Line, _core.Cursor.Column);
        if (_renameSymbol == null)
        {
            _renameField.Clear();
            _renameError = "Put the cursor on a local variable or parameter (globals and fields can't be renamed).";
        }
        else
        {
            _renameField.SetText(_renameSymbol.Name);
            _renameField.SelectAll();
        }
    }

    void ApplyRename()
    {
        var sym = _renameSymbol;
        if (sym == null) { CloseBars(); return; }
        string name = new string(_renameField.Text).Trim();
        if (name == sym.Name) { CloseBars(); return; }
        string? error = RenameValidator?.Invoke(sym, name);
        if (error != null) { _renameError = error; return; }

        var lines = sym.Occurrences.GroupBy(o => o.Line).OrderBy(g => g.Key).ToList();
        int first = lines[0].Key, last = lines[^1].Key;
        var text = new StringBuilder();
        for (int l = first; l <= last; l++)
        {
            string line = GetLineText(l);
            var group = lines.FirstOrDefault(g => g.Key == l);
            if (group != null)
                foreach (var o in group.OrderByDescending(o => o.Column))
                {
                    if (o.Column < 0 || o.Column + o.Length > line.Length || string.CompareOrdinal(line, o.Column, sym.Name, 0, o.Length) != 0)
                    {
                        _renameError = "The text changed since F2 was pressed; press F2 again.";
                        return;
                    }
                    line = string.Concat(line.AsSpan(0, o.Column), name, line.AsSpan(o.Column + o.Length));
                }
            if (l > first) text.Append('\n');
            text.Append(line);
        }
        // one undo step for every use of the variable
        ApplyEdit(first, 0, last, GetLineText(last).Length, text.ToString(), keepCursor: true);
        CloseBars();
    }

    void DrawRenameBar()
    {
        ImGui.AlignTextToFramePadding();
        if (_renameSymbol == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), _renameError);
        }
        else
        {
            ImGui.TextUnformatted($"Rename '{_renameSymbol.Name}' ({_renameSymbol.Occurrences.Count} uses) to");
            ImGui.SameLine();
            if (DrawField("##renamefield", _renameField, 220, _target == FocusTarget.Rename))
                _target = FocusTarget.Rename;
            if (_renameError.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), _renameError);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("x##closerename"))
            CloseBars();
    }

    // test hooks
    internal bool RenameOpen => _renameOpen;
    internal string RenameError => _renameError;
    internal LineInput RenameField => _renameField;
}
