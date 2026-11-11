// Derived from ImGuiColorTextEditNet (MIT) Editor/TextEditorModify.cs, TextEditorMovement.cs and
// TextEditorSelection.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md. Rewritten around an
// anchor/cursor selection and a single ReplaceRange primitive; no ImGui dependency.
using SoulsAiStudio.Editor.Input;

namespace SoulsAiStudio.Editor.Text;

/// <summary>Headless editor model: text, selection, edits, undo/redo. Unit-testable without ImGui.</summary>
internal sealed class EditorCore
{
    public readonly TextBuffer Buffer = new();
    public readonly UndoStack Undo = new();

    Coordinates _anchor;
    Coordinates _cursor;
    int _preferredColumn = -1;

    public bool ReadOnly { get; set; }
    public int TabSize { get; set; } = 4;
    public IClipboard Clipboard { get; set; } = new MemoryClipboard();

    /// <summary>Set when an action moved the cursor and the view should scroll it into view.</summary>
    public bool ScrollToCursorRequested { get; set; }

    public Coordinates Cursor => _cursor;
    public Coordinates Anchor => _anchor;
    public Coordinates SelectionStart => Coordinates.Min(_anchor, _cursor);
    public Coordinates SelectionEnd => Coordinates.Max(_anchor, _cursor);
    public bool HasSelection => _anchor != _cursor;

    // ---------------------------------------------------------------- text

    public void SetText(string? text)
    {
        Buffer.SetText(text);
        Undo.Clear();
        _anchor = _cursor = default;
        _preferredColumn = -1;
    }

    public string GetSelectedText() => Buffer.GetText(SelectionStart, SelectionEnd);

    // ---------------------------------------------------------------- selection

    public void SetCursor(Coordinates pos, bool extendSelection = false)
    {
        _cursor = Buffer.Sanitize(pos);
        if (!extendSelection)
            _anchor = _cursor;
        _preferredColumn = -1;
        ScrollToCursorRequested = true;
    }

    public void Select(Coordinates anchor, Coordinates cursor)
    {
        _anchor = Buffer.Sanitize(anchor);
        _cursor = Buffer.Sanitize(cursor);
        _preferredColumn = -1;
        ScrollToCursorRequested = true;
    }

    public void SelectAll() => Select(default, Buffer.EndOfText);

    public void SelectLine(int line)
    {
        line = Math.Clamp(line, 0, Buffer.LineCount - 1);
        Select(new Coordinates(line, 0), new Coordinates(line, Buffer.GetLineLength(line)));
    }

    public void SelectWordAt(Coordinates pos)
    {
        pos = Buffer.Sanitize(pos);
        var (s, e) = WordRangeAt(pos);
        Select(s, e);
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c > 127 && !char.IsWhiteSpace(c);

    public (Coordinates Start, Coordinates End) WordRangeAt(Coordinates pos)
    {
        var line = Buffer.GetLineSpan(pos.Line);
        int col = pos.Column;
        if (line.Length == 0)
            return (pos, pos);
        if (col >= line.Length)
            col = line.Length - 1;

        char c = line[col];
        Func<char, bool> same = IsWordChar(c) ? IsWordChar : char.IsWhiteSpace(c) ? char.IsWhiteSpace : ch => !IsWordChar(ch) && !char.IsWhiteSpace(ch);
        int s = col, e = col;
        while (s > 0 && same(line[s - 1]))
            s--;
        while (e < line.Length && same(line[e]))
            e++;
        return (new Coordinates(pos.Line, s), new Coordinates(pos.Line, e));
    }

    // ---------------------------------------------------------------- movement

    void MoveTo(Coordinates pos, bool select, bool keepPreferredColumn = false)
    {
        _cursor = Buffer.Sanitize(pos);
        if (!select)
            _anchor = _cursor;
        if (!keepPreferredColumn)
            _preferredColumn = -1;
        ScrollToCursorRequested = true;
    }

    public void MoveLeft(bool select, bool word)
    {
        if (!select && HasSelection && !word)
        {
            MoveTo(SelectionStart, false);
            return;
        }
        MoveTo(word ? PrevWordBoundary(_cursor) : PrevChar(_cursor), select);
    }

    public void MoveRight(bool select, bool word)
    {
        if (!select && HasSelection && !word)
        {
            MoveTo(SelectionEnd, false);
            return;
        }
        MoveTo(word ? NextWordBoundary(_cursor) : NextChar(_cursor), select);
    }

    public void MoveUp(int lines, bool select) => MoveVertical(-lines, select);

    public void MoveDown(int lines, bool select) => MoveVertical(lines, select);

    void MoveVertical(int delta, bool select)
    {
        if (_preferredColumn < 0)
            _preferredColumn = _cursor.Column;
        int line = Math.Clamp(_cursor.Line + delta, 0, Buffer.LineCount - 1);
        Coordinates target;
        if (delta < 0 && _cursor.Line == 0)
            target = new Coordinates(0, 0);
        else if (delta > 0 && _cursor.Line == Buffer.LineCount - 1)
            target = new Coordinates(line, Buffer.GetLineLength(line));
        else
            target = new Coordinates(line, Math.Min(_preferredColumn, Buffer.GetLineLength(line)));
        MoveTo(target, select, keepPreferredColumn: true);
    }

    /// <summary>Smart home: first non-blank char, or column 0 if already there.</summary>
    public void MoveHome(bool select)
    {
        var line = Buffer.GetLineSpan(_cursor.Line);
        int indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            indent++;
        int col = _cursor.Column == indent ? 0 : indent;
        MoveTo(new Coordinates(_cursor.Line, col), select);
    }

    public void MoveEnd(bool select) => MoveTo(new Coordinates(_cursor.Line, Buffer.GetLineLength(_cursor.Line)), select);

    public void MoveToStartOfFile(bool select) => MoveTo(default, select);

    public void MoveToEndOfFile(bool select) => MoveTo(Buffer.EndOfText, select);

    Coordinates PrevChar(Coordinates p)
    {
        if (p.Column > 0)
        {
            var line = Buffer.GetLineSpan(p.Line);
            int col = p.Column - 1;
            if (col > 0 && char.IsLowSurrogate(line[col]) && char.IsHighSurrogate(line[col - 1]))
                col--;
            return new Coordinates(p.Line, col);
        }
        return p.Line > 0 ? new Coordinates(p.Line - 1, Buffer.GetLineLength(p.Line - 1)) : p;
    }

    Coordinates NextChar(Coordinates p)
    {
        var line = Buffer.GetLineSpan(p.Line);
        if (p.Column < line.Length)
        {
            int col = p.Column + 1;
            if (col < line.Length && char.IsLowSurrogate(line[col]) && char.IsHighSurrogate(line[col - 1]))
                col++;
            return new Coordinates(p.Line, col);
        }
        return p.Line < Buffer.LineCount - 1 ? new Coordinates(p.Line + 1, 0) : p;
    }

    static int CharClass(char c) => char.IsWhiteSpace(c) ? 0 : IsWordChar(c) ? 1 : 2;

    Coordinates PrevWordBoundary(Coordinates p)
    {
        if (p.Column == 0)
            return PrevChar(p);
        var line = Buffer.GetLineSpan(p.Line);
        int col = p.Column;
        while (col > 0 && char.IsWhiteSpace(line[col - 1]))
            col--;
        if (col > 0)
        {
            int cls = CharClass(line[col - 1]);
            while (col > 0 && CharClass(line[col - 1]) == cls)
                col--;
        }
        return new Coordinates(p.Line, col);
    }

    Coordinates NextWordBoundary(Coordinates p)
    {
        var line = Buffer.GetLineSpan(p.Line);
        if (p.Column >= line.Length)
            return NextChar(p);
        int col = p.Column;
        int cls = CharClass(line[col]);
        while (col < line.Length && CharClass(line[col]) == cls)
            col++;
        while (col < line.Length && char.IsWhiteSpace(line[col]))
            col++;
        return new Coordinates(p.Line, col);
    }

    // ---------------------------------------------------------------- edits

    /// <summary>Replaces [a, b) with <paramref name="text"/> as one undo step. Cursor goes to the end of the inserted text.</summary>
    public bool ReplaceRange(Coordinates a, Coordinates b, string text, bool isTyping = false, SelectionState? after = null)
    {
        if (ReadOnly)
            return false;
        a = Buffer.Sanitize(a);
        b = Buffer.Sanitize(b);
        if (b < a)
            (a, b) = (b, a);
        if (text.Contains('\r'))
            text = text.Replace("\r", "");
        if (a == b && text.Length == 0)
            return false;

        var rec = new UndoRecord
        {
            Start = a,
            RemovedEnd = b,
            Removed = a == b ? "" : Buffer.GetText(a, b),
            Added = text,
            Before = new SelectionState(_anchor, _cursor),
            IsTyping = isTyping,
        };

        Buffer.Delete(a, b);
        rec.AddedEnd = Buffer.Insert(a, text);
        rec.After = after ?? new SelectionState(rec.AddedEnd, rec.AddedEnd);
        _anchor = Buffer.Sanitize(rec.After.Anchor);
        _cursor = Buffer.Sanitize(rec.After.Cursor);
        _preferredColumn = -1;
        ScrollToCursorRequested = true;
        Undo.Push(rec);
        return true;
    }

    public bool ReplaceSelection(string text, bool isTyping = false) => ReplaceRange(SelectionStart, SelectionEnd, text, isTyping);

    /// <summary>Types one char (or a newline with auto-indent).</summary>
    public bool TypeChar(char c)
    {
        if (ReadOnly)
            return false;
        if (c == '\r')
            c = '\n';
        if (c == '\n')
        {
            var line = Buffer.GetLineSpan(SelectionStart.Line);
            int indent = 0;
            while (indent < line.Length && indent < SelectionStart.Column && (line[indent] == ' ' || line[indent] == '\t'))
                indent++;
            return ReplaceSelection(indent == 0 ? "\n" : "\n" + new string(line[..indent]));
        }

        bool typing = !HasSelection;
        return ReplaceSelection(c.ToString(), typing);
    }

    public bool Backspace(bool word)
    {
        if (ReadOnly)
            return false;
        if (HasSelection)
            return ReplaceSelection("");
        var from = word ? PrevWordBoundary(_cursor) : PrevChar(_cursor);
        return from != _cursor && ReplaceRange(from, _cursor, "");
    }

    public bool Delete(bool word)
    {
        if (ReadOnly)
            return false;
        if (HasSelection)
            return ReplaceSelection("");
        var to = word ? NextWordBoundary(_cursor) : NextChar(_cursor);
        return to != _cursor && ReplaceRange(_cursor, to, "");
    }

    /// <summary>Tab: indent selected lines, or insert spaces to the next tab stop.</summary>
    public bool Tab(bool shift)
    {
        if (ReadOnly)
            return false;
        bool multiLine = HasSelection && SelectionStart.Line != SelectionEnd.Line;
        if (shift || multiLine)
            return IndentLines(!shift);

        int spaces = TabSize - _cursor.Column % TabSize;
        return ReplaceSelection(new string(' ', spaces));
    }

    /// <summary>Indents/unindents every line touched by the selection, as one undo step.</summary>
    public bool IndentLines(bool indent)
    {
        if (ReadOnly)
            return false;
        int first = SelectionStart.Line;
        int last = SelectionEnd.Line;
        if (last > first && SelectionEnd.Column == 0)
            last--;

        var sb = new System.Text.StringBuilder();
        int anchorDelta = 0, cursorDelta = 0;
        bool changed = false;
        for (int i = first; i <= last; i++)
        {
            if (i > first)
                sb.Append('\n');
            var line = Buffer.GetLineSpan(i);
            int delta;
            if (indent)
            {
                delta = line.Length == 0 ? 0 : TabSize;
                sb.Append(' ', delta);
                sb.Append(line);
            }
            else
            {
                int n = 0;
                if (line.Length > 0 && line[0] == '\t')
                    n = 1;
                else
                    while (n < TabSize && n < line.Length && line[n] == ' ')
                        n++;
                delta = -n;
                sb.Append(line[n..]);
            }
            if (delta != 0)
                changed = true;
            if (i == _anchor.Line)
                anchorDelta = delta;
            if (i == _cursor.Line)
                cursorDelta = delta;
        }
        if (!changed)
            return false;

        var newAnchor = new Coordinates(_anchor.Line, Math.Max(0, _anchor.Column + (_anchor.Line >= first && _anchor.Line <= last ? anchorDelta : 0)));
        var newCursor = new Coordinates(_cursor.Line, Math.Max(0, _cursor.Column + (_cursor.Line >= first && _cursor.Line <= last ? cursorDelta : 0)));
        if (_anchor.Column == 0 && _anchor.Line != _cursor.Line)
            newAnchor = new Coordinates(_anchor.Line, 0);
        if (_cursor.Column == 0 && _anchor.Line != _cursor.Line)
            newCursor = new Coordinates(_cursor.Line, 0);

        return ReplaceRange(new Coordinates(first, 0), new Coordinates(last, Buffer.GetLineLength(last)), sb.ToString(),
            after: new SelectionState(newAnchor, newCursor));
    }

    public void Copy()
    {
        if (HasSelection)
            Clipboard.SetText(ToClipboard(GetSelectedText()));
        else
            Clipboard.SetText(new string(Buffer.GetLineSpan(_cursor.Line)) + Environment.NewLine);
    }

    public bool Cut()
    {
        if (ReadOnly)
        {
            Copy();
            return false;
        }
        if (!HasSelection)
        {
            // VSCode-like: cut the whole line.
            int line = _cursor.Line;
            Clipboard.SetText(new string(Buffer.GetLineSpan(line)) + Environment.NewLine);
            var start = new Coordinates(line, 0);
            var end = line < Buffer.LineCount - 1 ? new Coordinates(line + 1, 0) : new Coordinates(line, Buffer.GetLineLength(line));
            if (line == Buffer.LineCount - 1 && line > 0)
                start = new Coordinates(line - 1, Buffer.GetLineLength(line - 1));
            return ReplaceRange(start, end, "");
        }
        Clipboard.SetText(ToClipboard(GetSelectedText()));
        return ReplaceSelection("");
    }

    public bool Paste()
    {
        if (ReadOnly)
            return false;
        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
            return false;
        return ReplaceSelection(text.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    static string ToClipboard(string text) => Environment.NewLine == "\n" ? text : text.Replace("\n", Environment.NewLine);

    public bool DoUndo()
    {
        if (ReadOnly)
            return false;
        var rec = Undo.PopUndo();
        if (rec == null)
            return false;
        rec.Undo(Buffer);
        _anchor = Buffer.Sanitize(rec.Before.Anchor);
        _cursor = Buffer.Sanitize(rec.Before.Cursor);
        _preferredColumn = -1;
        ScrollToCursorRequested = true;
        return true;
    }

    public bool DoRedo()
    {
        if (ReadOnly)
            return false;
        var rec = Undo.PopRedo();
        if (rec == null)
            return false;
        rec.Redo(Buffer);
        _anchor = Buffer.Sanitize(rec.After.Anchor);
        _cursor = Buffer.Sanitize(rec.After.Cursor);
        _preferredColumn = -1;
        ScrollToCursorRequested = true;
        return true;
    }
}
