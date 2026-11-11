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

    /// <summary>Single-char typing run that may absorb the next typed char.</summary>
    public bool IsTyping;

    public void Undo(TextBuffer buffer)
    {
        buffer.Delete(Start, AddedEnd);
        buffer.Insert(Start, Removed);
    }

    public void Redo(TextBuffer buffer)
    {
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
        if (!prev.IsTyping || prev.Removed.Length != 0 || cur.Removed.Length != 0)
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

    public UndoRecord? PopUndo() => CanUndo ? _records[--_index] : null;

    public UndoRecord? PopRedo() => CanRedo ? _records[_index++] : null;
}
