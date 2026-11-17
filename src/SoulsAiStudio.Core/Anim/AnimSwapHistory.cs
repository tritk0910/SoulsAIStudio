using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.Core.Anim;

/// <summary>Where a script's text stands relative to one recorded Lua id swap (judged from the two ids' counts).</summary>
public enum SwapTextState
{
    /// <summary>The counts are those from before the swap.</summary>
    Before,
    /// <summary>The counts are those from after the swap.</summary>
    After,
    /// <summary>Both ids had the same count, so before and after can't be told apart.</summary>
    Ambiguous,
    /// <summary>Neither: the ids were edited by hand since.</summary>
    Unknown,
}

/// <summary>What to do to a script when its swap is undone or redone.</summary>
public enum SwapLuaAction
{
    /// <summary>Apply the id swap again (a swap is its own inverse).</summary>
    Apply,
    /// <summary>Apply it, but the text no longer looks like the expected state (ids edited by hand since): tell the user.</summary>
    ApplyUnsure,
    /// <summary>Already in the wanted state (e.g. the user undid the Lua edit with Ctrl+Z in the tab): leave it.</summary>
    Skip,
}

/// <summary>The Lua part of a swap in one script: ids as written in that script and how often each occurred before.</summary>
/// <param name="CountA">Occurrences of <paramref name="LuaA"/> before the swap (anim literals, see <see cref="AnimSlots.FindLuaAnimLiterals"/>).</param>
public sealed record SwapScriptChange(ProjectScript Script, int LuaA, int LuaB, int CountA, int CountB)
{
    public string Key => Script.Key;

    /// <summary>Records the swap of <paramref name="la"/> / <paramref name="lb"/> about to be applied to <paramref name="textBefore"/>.</summary>
    public static SwapScriptChange Measure(ProjectScript script, string textBefore, int la, int lb)
    {
        var (ca, cb) = Count(textBefore, la, lb);
        return new SwapScriptChange(script, la, lb, ca, cb);
    }

    public SwapTextState Classify(string text)
    {
        var (ca, cb) = Count(text, LuaA, LuaB);
        bool before = ca == CountA && cb == CountB, after = ca == CountB && cb == CountA;
        return before && after ? SwapTextState.Ambiguous : before ? SwapTextState.Before : after ? SwapTextState.After : SwapTextState.Unknown;
    }

    /// <summary>What to do to <paramref name="text"/> to bring it back before the swap (undo) or after it again (redo).</summary>
    public SwapLuaAction Decide(string text, bool undo) => Classify(text) switch
    {
        SwapTextState.Before => undo ? SwapLuaAction.Skip : SwapLuaAction.Apply,
        SwapTextState.After => undo ? SwapLuaAction.Apply : SwapLuaAction.Skip,
        SwapTextState.Ambiguous => SwapLuaAction.Apply,
        _ => SwapLuaAction.ApplyUnsure,
    };

    private static (int A, int B) Count(string text, int la, int lb)
    {
        int ca = 0, cb = 0;
        foreach (var l in AnimSlots.FindLuaAnimLiterals(text))
            if (l.Value == la) ca++;
            else if (l.Value == lb) cb++;
        return (ca, cb);
    }
}

/// <summary>One animation swap (TAE/HKX + the Lua scripts it changed) as a single undoable operation.</summary>
public sealed class AnimSwapRecord
{
    public AnimSwapRecord(int a, int b, DateTime time)
    {
        A = a; B = b; Time = time;
    }

    public int A { get; }
    public int B { get; }
    public DateTime Time { get; }
    public string Description => $"{AnimSlots.IdName(A)} <-> {AnimSlots.IdName(B)}";

    private readonly List<SwapScriptChange> _scripts = new();
    /// <summary>Scripts whose ids were swapped (added as each one is applied: scripts that had to be opened come later).</summary>
    public IReadOnlyList<SwapScriptChange> Scripts => _scripts;
    /// <summary>Scripts still being opened for the initial Lua swap.</summary>
    public int Pending { get; set; }
    /// <summary>True after the swap was undone (DSAS Undo [Global] or "Undo last swap"), false again after a redo.</summary>
    public bool IsUndone { get; set; }

    public void AddScript(SwapScriptChange change)
    {
        _scripts.RemoveAll(s => s.Key == change.Key);
        _scripts.Add(change);
    }
}

/// <summary>Recent swaps, newest first, capped.</summary>
public sealed class AnimSwapHistory
{
    private readonly List<AnimSwapRecord> _items = new();
    public AnimSwapHistory(int capacity = 10) => Capacity = Math.Max(1, capacity);

    public int Capacity { get; }
    public IReadOnlyList<AnimSwapRecord> Recent => _items;

    public void Add(AnimSwapRecord record)
    {
        _items.Insert(0, record);
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
    }

    /// <summary>Newest swap that is not undone (the one "Undo last swap" acts on), or null.</summary>
    public AnimSwapRecord? LastApplied => _items.FirstOrDefault(r => !r.IsUndone);

    public void Clear() => _items.Clear();
}
