using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>Find bar: Match whole word, and Swap (exchange the Find and Replace texts everywhere, one undo step).</summary>
public class FindOptionsTests
{
    const string Code =
        "goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0)\n" +
        "goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3012, TARGET_ENE_0)\n" +
        "local x = 30012 + a3001 + 3001\n" +
        "SetCoolTime(ai, goal, 3012, 5, act[1], 1)";

    static TextBuffer Buf(string text)
    {
        var b = new TextBuffer();
        b.SetText(text);
        return b;
    }

    [Fact]
    public void WholeWord_SkipsMatchesInsideWords()
    {
        var b = Buf(Code);
        var f = new FindModel();
        f.SetQuery("3001", caseSensitive: false);
        Assert.Equal(4, f.CountMatches(b));         // 3001, 3001 in 30012, a3001, 3001
        f.SetQuery("3001", caseSensitive: false, wholeWord: true);
        Assert.Equal(2, f.CountMatches(b));
        Assert.True(f.FindNext(b, (1, 0), out var s, out _));
        Assert.Equal(new Coordinates(2, 26), s);    // skips 30012 and a3001
        Assert.True(f.FindPrev(b, (2, 26), out s, out _));
        Assert.Equal(new Coordinates(0, 45), s);
        Assert.Equal(0, f.MatchIndex(b, (2, 10), (2, 14)));  // "3001" inside 30012 is not a match
        Assert.Equal(2, f.MatchIndex(b, (2, 26), (2, 30)));
        Span<int> starts = stackalloc int[8];
        Assert.Equal(1, f.MatchesInLine(b.GetLineSpan(2), starts));
    }

    [Fact]
    public void WholeWord_Wraps_AndCaseOption()
    {
        var b = Buf("Goal goal\nGoalX");
        var f = new FindModel();
        f.SetQuery("goal", caseSensitive: true, wholeWord: true);
        Assert.Equal(1, f.CountMatches(b));
        Assert.True(f.FindNext(b, (1, 0), out var s, out _)); // wraps to line 0
        Assert.Equal(new Coordinates(0, 5), s);
        f.SetQuery("goal", caseSensitive: false, wholeWord: true);
        Assert.Equal(2, f.CountMatches(b));
    }

    [Fact]
    public void Swap_ExchangesBothWays_OneUndoStep()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = Code;
        var f = new FindModel();
        f.SetQuery("3001", caseSensitive: true, wholeWord: true);
        int undoBefore = e.Core.Undo.Index;
        int n = e.Core.SwapAll(f, "3012", out int other);
        Assert.Equal(4, n);
        Assert.Equal(2, other);
        Assert.Equal(
            "goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3012, TARGET_ENE_0)\n" +
            "goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0)\n" +
            "local x = 30012 + a3001 + 3012\n" +
            "SetCoolTime(ai, goal, 3001, 5, act[1], 1)", e.Text);
        Assert.Equal(undoBefore + 1, e.Core.Undo.Index);
        e.Undo();
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void Swap_WithoutWholeWord_IsPlainText_LeftToRight()
    {
        var b = Buf("ab ba aab");
        var f = new FindModel();
        f.SetQuery("ab", caseSensitive: true);
        Assert.Equal(3, f.BuildSwapAll(b, "ba", out _, out _, out string text, out _));
        Assert.Equal("ba ab aba", text); // left to right, non-overlapping: "aab" = a + ab -> a + ba
    }

    [Fact]
    public void Swap_NothingToDo_SameOrEmptyOther()
    {
        var b = Buf("3001 3012");
        var f = new FindModel();
        f.SetQuery("3001", caseSensitive: true);
        Assert.Equal(0, f.BuildSwapAll(b, "", out _, out _, out _, out _));
        Assert.Equal(0, f.BuildSwapAll(b, "3001", out _, out _, out _, out _));
        f.SetQuery("4000", caseSensitive: true);
        Assert.Equal(1, f.BuildSwapAll(b, "3012", out _, out _, out string t, out int other)); // only B occurs
        Assert.Equal("3001 4000", t);
        Assert.Equal(1, other);
    }

    [Fact]
    public void Swap_FromTheFindBar_ShowsSwappedStatus()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = Code;
        var k = new FakeKeys();
        void Step(FakeKeys f) => e.ProcessKeys(f, editorFocused: true, textFocused: true, pageLines: 20);
        Step(k.Frame(ctrl: true, keys: EditorKey.H));
        Step(k.Frame("3001"));
        Step(k.Frame(keys: EditorKey.Tab));
        Step(k.Frame("3012"));
        e.SetFindOptions(matchCase: true, wholeWord: true);
        Assert.Equal(4, e.SwapAll());
        Span<char> buf = stackalloc char[48];
        Assert.Equal("Swapped 4 matches", new string(buf[..e.FormatMatchStatus(buf)]));
        Assert.Contains("a3001", e.Text);
        Assert.Contains("30012", e.Text);
    }
}
