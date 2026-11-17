using SoulsAiStudio.Core.Anim;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.Core.Tests.Anim;

public class AnimSwapHistoryTests
{
    private const string Src =
        "Goal.Act01 = function(ai, goal, _)\n" +
        "    goal:AddSubGoal(GOAL_COMMON_ComboAttack, 10, 3001, TARGET_ENE_0, 9999, 0, 0)\n" +
        "    goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0, 9999, 0, 0)\n" +
        "    goal:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3012, TARGET_ENE_0, 9999, 0, 0)\n" +
        "end\n";

    private static readonly ProjectScript Script = new() { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_battle.lua" };

    private static string Swap(string text, int a, int b) =>
        AnimSlots.SwapLuaAnimIds(text, a, b, out _) is { } e ? TextEdits.Apply(text, e) : text;

    [Fact]
    public void UndoAndRedo_ApplyTheSwapOnlyWhenNeeded()
    {
        var change = SwapScriptChange.Measure(Script, Src, 3001, 3012);
        Assert.Equal((2, 1), (change.CountA, change.CountB));
        string swapped = Swap(Src, 3001, 3012);

        Assert.Equal(SwapTextState.After, change.Classify(swapped));
        Assert.Equal(SwapLuaAction.Apply, change.Decide(swapped, undo: true));
        Assert.Equal(SwapLuaAction.Skip, change.Decide(swapped, undo: false));

        // The user already reverted it with Ctrl+Z in the tab: undoing the swap must not swap it back again.
        Assert.Equal(SwapTextState.Before, change.Classify(Src));
        Assert.Equal(SwapLuaAction.Skip, change.Decide(Src, undo: true));
        Assert.Equal(SwapLuaAction.Apply, change.Decide(Src, undo: false));

        // Undo = the same swap applied to the current text, even after unrelated edits elsewhere.
        string edited = swapped.Replace("Goal.Act01", "Goal.Act02");
        Assert.Equal(Src.Replace("Goal.Act01", "Goal.Act02"), Swap(edited, 3001, 3012));
    }

    [Fact]
    public void EqualCounts_AreAmbiguous_AndHandEditedIdsAreUnsure()
    {
        string src = Src.Replace("ComboRepeat, 10, 3001", "ComboRepeat, 10, 3005");
        var change = SwapScriptChange.Measure(Script, src, 3001, 3012);
        Assert.Equal(SwapTextState.Ambiguous, change.Classify(Swap(src, 3001, 3012)));
        Assert.Equal(SwapLuaAction.Apply, change.Decide(src, undo: true));

        var c2 = SwapScriptChange.Measure(Script, Src, 3001, 3012);
        string handEdited = Swap(Src, 3001, 3012).Replace("ComboFinal, 10, 3001", "ComboFinal, 10, 3999");
        Assert.Equal(SwapTextState.Unknown, c2.Classify(handEdited));
        Assert.Equal(SwapLuaAction.ApplyUnsure, c2.Decide(handEdited, undo: true));
    }

    [Fact]
    public void History_NewestFirst_Capped_LastAppliedSkipsUndone()
    {
        var h = new AnimSwapHistory(capacity: 3);
        var recs = Enumerable.Range(0, 5).Select(i => new AnimSwapRecord(3000 + i, 3100 + i, DateTime.UtcNow)).ToList();
        foreach (var r in recs) h.Add(r);
        Assert.Equal([recs[4], recs[3], recs[2]], h.Recent);
        Assert.Same(recs[4], h.LastApplied);
        recs[4].IsUndone = true;
        Assert.Same(recs[3], h.LastApplied);
        Assert.Equal("a000_003003 <-> a000_003103", recs[3].Description);

        recs[3].AddScript(SwapScriptChange.Measure(Script, Src, 3001, 3012));
        recs[3].AddScript(SwapScriptChange.Measure(Script, Src, 3001, 3012)); // same script again: replaced, not duplicated
        Assert.Single(recs[3].Scripts);
    }
}
