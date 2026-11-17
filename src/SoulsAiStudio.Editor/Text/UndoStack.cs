// Derived from ImGuiColorTextEditNet (MIT) Editor/TextEditorUndoStack.cs + Operations/UndoRecord.cs
// — see THIRD_PARTY_ImGuiColorTextEditNet.md.
namespace SoulsAiStudio.Editor.Text;

internal struct SelectionState
{
    public Coordinates Anchor;
    public Coordinates Cursor;

    public SelectionState(Coordinates anchor, Coordinates cursor)
    {
        Anchor = anchor;
        Cursor = cursor;
    }
}

/// <summary>One undoable edit: text [Start, RemovedEnd) was replaced by Added (ending at AddedEnd).</summary>
internal sealed class UndoRecord
{
    public Coordinates Start;
    public Coordinates RemovedEnd;
    public string Removed = "";
    public Coordinates AddedEnd;
    public string Added = "";
    public SelectionState Before;
    public SelectionState After;

    /// <summary>Secondary carets/selections before / after the edit (null = none). See EditorCore.MultiCursor.</summary>
    public SelectionState[]? BeforeExtras;
    public SelectionState[]? AfterExtras;

    /// <summary>
    /// Multi-caret edit: sub-edits in the order they were applied (bottom of the text first). When set, the record's own
    /// range fields are unused; undo applies the parts' undo in reverse order.
    /// </summary>
    public UndoRecord[]? Parts;

    /// <summary>Single-char typing run that may absorb the next typed char.</summary>
    public bool IsTyping;

    public void Undo(TextBuffer buffer)
    {
        if (Parts != null)
        {
            for (int i = Parts.Length - 1; i >= 0; i--)
                Parts[i].Undo(buffer);
            return;
        }
        buffer.Delete(Start, AddedEnd);
        buffer.Insert(Start, Removed);
    }

    public void Redo(TextBuffer buffer)
    {
        if (Parts != null)
        {
            foreach (var p in Parts)
                p.Redo(buffer);
            return;
        }
        buffer.Delete(Start, RemovedEnd);
        buffer.Insert(Start, Added);
    }

    public override string ToString() => $"[{Start}-{RemovedEnd}) -\"{Removed}\" +\"{Added}\"";
}

internal sealed class UndoStack
{
    readonly List<UndoRecord> _records = new();
    int _index;      // number of applied records
    int _cleanIndex; // value of _index at the last save point; -1 = unreachable

    public int Count => _records.Count;
    public int Index => _index;
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _records.Count;
    public bool IsDirty => _index != _cleanIndex;

    public void Clear()
    {
        _records.Clear();
        _index = 0;
        _cleanIndex = 0;
    }

    public void MarkClean() => _cleanIndex = _index;

    public void Push(UndoRecord record)
    {
        if (_index < _records.Count)
        {
            if (_cleanIndex > _index)
                _cleanIndex = -1;
            _records.RemoveRange(_index, _records.Count - _index);
        }

        if (record.IsTyping && _index > 0 && _index != _cleanIndex && TryMerge(_records[_index - 1], record))
            return;

        _records.Add(record);
        _index++;
    }

    static bool TryMerge(UndoRecord prev, UndoRecord cur)
    {
        if (prev.Parts != null && cur.Parts != null)
            return TryMergeMulti(prev, cur);
        if (!prev.IsTyping || prev.Parts != null || cur.Parts != null || prev.AfterExtras != null || cur.BeforeExtras != null || prev.Removed.Length != 0 || cur.Removed.Length != 0)
            return false;
        if (prev.AddedEnd != cur.Start || prev.Added.Length == 0 || cur.Added.Length != 1)
            return false;
        // Word granularity: a space typed after a non-space starts a new step.
        char last = prev.Added[^1];
        char c = cur.Added[0];
        if (c == ' ' && last != ' ')
            return false;
        if (prev.Added.Length >= 256)
            return false;

        prev.Added += cur.Added;
        prev.AddedEnd = cur.AddedEnd;
        prev.After = cur.After;
        return true;
    }

    /// <summary>
    /// Multi-caret typing: merges when the same carets typed one more char each and nothing else happened in between
    /// (the carets are exactly where the previous step left them). Each part keeps its original start: undo removes the
    /// parts top-down, so every part's recorded start is valid again when its own text is removed (see UndoRecord.Undo).
    /// </summary>
    static bool TryMergeMulti(UndoRecord prev, UndoRecord cur)
    {
        if (!prev.IsTyping || !cur.IsTyping || prev.Parts!.Length != cur.Parts!.Length)
            return false;
        if (prev.After.Anchor != cur.Before.Anchor || prev.After.Cursor != cur.Before.Cursor || !SameCarets(prev.AfterExtras, cur.BeforeExtras))
            return false;
        for (int i = 0; i < prev.Parts.Length; i++)
        {
            var a = prev.Parts[i];
            var b = cur.Parts[i];
            if (a.Start.Line != b.Start.Line || a.Added.Length >= 256)
                return false;
            if (b.Added[0] == ' ' && a.Added[^1] != ' ')
                return false; // word granularity, as for one caret
        }
        for (int i = 0; i < prev.Parts.Length; i++)
        {
            var a = prev.Parts[i];
            a.Added += cur.Parts[i].Added;
            a.AddedEnd = new Coordinates(a.Start.Line, a.Start.Column + a.Added.Length);
        }
        prev.After = cur.After;
        prev.AfterExtras = cur.AfterExtras;
        return true;
    }

    static bool SameCarets(SelectionState[]? x, SelectionState[]? y)
    {
        if (x == null || y == null)
            return x == y;
        if (x.Length != y.Length)
            return false;
        for (int i = 0; i < x.Length; i++)
            if (x[i].Anchor != y[i].Anchor || x[i].Cursor != y[i].Cursor)
                return false;
        return true;
    }

    public UndoRecord? PopUndo() => CanUndo ? _records[--_index] : null;

    public UndoRecord? PopRedo() => CanRedo ? _records[_index++] : null;
}
