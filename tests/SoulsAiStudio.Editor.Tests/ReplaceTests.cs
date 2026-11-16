using System.Diagnostics;
using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;
using Xunit.Abstractions;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>Find bar match index ("3 of 17"), Replace, Replace All (one undo step) and their keyboard routing.</summary>
public class ReplaceTests
{
    readonly ITestOutputHelper _out;

    public ReplaceTests(ITestOutputHelper output) => _out = output;

    const string Code =
        "local r = ai:GetRandam_Int(1, 100)\n" +
        "goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002)\n" +
        "if r <= 50 then goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3016) end\n" +
        "goal:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3048)\n" +
        "-- goal:AddSubGoal in a comment";

    static (LuaCodeEditor Editor, FakeKeys Keys) Setup(string text = Code)
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = text;
        return (e, new FakeKeys());
    }

    static void Step(LuaCodeEditor e, FakeKeys k) => e.ProcessKeys(k, editorFocused: true, textFocused: true, pageLines: 20);

    static string Status(LuaCodeEditor e)
    {
        Span<char> buf = stackalloc char[48];
        return new string(buf[..e.FormatMatchStatus(buf)]);
    }

    // ---------------------------------------------------------------- match index (FindModel)

    [Fact]
    public void MatchIndex_CountsInDocumentOrder()
    {
        var b = new TextBuffer();
        b.SetText(Code);
        var f = new FindModel();
        f.SetQuery("AddSubGoal", caseSensitive: true);
        Assert.Equal(4, f.CountMatches(b));
        Assert.Equal(1, f.MatchIndex(b, (1, 5), (1, 15)));
        Assert.Equal(2, f.MatchIndex(b, (2, 21), (2, 31)));
        Assert.Equal(3, f.MatchIndex(b, (3, 5), (3, 15)));
        Assert.Equal(4, f.MatchIndex(b, (4, 8), (4, 18)));
        Assert.Equal(0, f.MatchIndex(b, (1, 4), (1, 14))); // not on a match
        Assert.Equal(0, f.MatchIndex(b, (1, 5), (1, 5)));  // just a cursor
    }

    [Fact]
    public void MatchIndex_CaseInsensitive_AndOverlap()
    {
        var b = new TextBuffer();
        b.SetText("aaa aa\nAA");
        var f = new FindModel();
        f.SetQuery("aa", caseSensitive: false);
        Assert.Equal(3, f.CountMatches(b));              // "aa|a", "aa", "AA" (non-overlapping)
        Assert.Equal(1, f.MatchIndex(b, (0, 0), (0, 2)));
        Assert.Equal(0, f.MatchIndex(b, (0, 1), (0, 3))); // overlapping occurrence is not one of the counted matches
        Assert.Equal(2, f.MatchIndex(b, (0, 4), (0, 6)));
        Assert.Equal(3, f.MatchIndex(b, (1, 0), (1, 2)));
    }

    [Fact]
    public void FindBar_ShowsIndexOfCount_AndFollowsF3()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Assert.Equal("", Status(e));
        Step(e, k.Frame("AddSubGoal"));
        Assert.Equal("1 of 4", Status(e));
        Step(e, k.Frame(keys: EditorKey.F3));
        Assert.Equal("2 of 4", Status(e));
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Equal("3 of 4", Status(e));
        Step(e, k.Frame(shift: true, keys: EditorKey.F3));
        Assert.Equal("2 of 4", Status(e));
        // cursor moved away from a match
        e.Core.SetCursor((0, 0));
        Assert.Equal("- of 4", Status(e));
        Step(e, k.Frame(keys: EditorKey.F3));
        Assert.Equal("1 of 4", Status(e));
        // no results
        Step(e, k.Frame(ctrl: true, keys: EditorKey.A));
        Step(e, k.Frame("zzz"));
        Assert.Equal("No results", Status(e));
    }

    [Fact]
    public void StatusLabel_DoesNotAllocate()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Step(e, k.Frame("AddSubGoal"));
        Status(e);
        Span<char> buf = stackalloc char[48];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            e.FormatMatchStatus(buf);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---------------------------------------------------------------- replace

    [Fact]
    public void CtrlH_OpensReplace_TabSwitchesFields_EnterReplacesAndAdvances()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.H));
        Assert.True(e.FindOpen);
        Assert.True(e.ReplaceOpen);
        Assert.Equal(LuaCodeEditor.FocusTarget.Find, e.Target);
        Step(e, k.Frame("ComboRepeat"));
        Assert.Equal("1 of 2", Status(e));

        Step(e, k.Frame(keys: EditorKey.Tab));
        Assert.Equal(LuaCodeEditor.FocusTarget.Replace, e.Target);
        Step(e, k.Frame("ComboAttack"));
        Assert.Equal("ComboAttack", e.ReplaceField.Text.ToString());
        Assert.Equal("ComboRepeat", e.FindField.Text.ToString()); // typing went to the replace field only

        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Contains("GOAL_COMMON_ComboAttack, 10, 3002", e.Text);
        Assert.Contains("GOAL_COMMON_ComboRepeat, 10, 3016", e.Text);
        Assert.Equal("1 of 1", Status(e)); // the next match is selected

        Step(e, k.Frame("\r")); // typed CR = Enter (cooldown makes the earlier key not double fire)
        Step(e, k.Frame()); Step(e, k.Frame()); Step(e, k.Frame());
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.DoesNotContain("ComboRepeat", e.Text);

        Step(e, k.Frame(keys: EditorKey.Tab));
        Assert.Equal(LuaCodeEditor.FocusTarget.Find, e.Target);
        Step(e, k.Frame(keys: EditorKey.Escape));
        Assert.False(e.FindOpen);
        Assert.Equal(LuaCodeEditor.FocusTarget.Text, e.Target);
    }

    [Fact]
    public void ReplaceOne_WhenSelectionIsNotAMatch_OnlySelectsNext()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.H));
        Step(e, k.Frame("3016"));
        e.Core.SetCursor((0, 0)); // user clicked elsewhere
        Step(e, k.Frame(keys: EditorKey.Tab));
        Step(e, k.Frame("9999"));
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Equal(Code, e.Text);
        Assert.Equal("1 of 1", Status(e));
        Step(e, k.Frame()); Step(e, k.Frame()); Step(e, k.Frame());
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Contains("10, 9999)", e.Text);
    }

    [Fact]
    public void ReplaceAll_IsOneUndoStep_KeepsCursorLine_RespectsCase()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((3, 4));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.H));
        Step(e, k.Frame("addsubgoal"));
        Step(e, k.Frame(keys: EditorKey.Tab));
        Step(e, k.Frame("AddGoal"));
        int undoBefore = e.Core.Undo.Index;
        Step(e, k.Frame("\n", ctrl: true, keys: EditorKey.Enter)); // Ctrl+Enter (key + LF char, same frame)
        Assert.Equal(Code.Replace("AddSubGoal", "AddGoal"), e.Text);
        Assert.Equal("Replaced 4 matches", Status(e));
        Assert.Equal(undoBefore + 1, e.Core.Undo.Index);
        Assert.Equal(3, e.Core.Cursor.Line);

        e.Undo();
        Assert.Equal(Code, e.Text);
        e.Redo();
        Assert.Equal(Code.Replace("AddSubGoal", "AddGoal"), e.Text);
        e.Undo();

        // case sensitive: no match for the lower-case query
        Step(e, k.Frame(keys: EditorKey.Tab)); // back to find
        var f = e.Find;
        f.SetQuery("addsubgoal", caseSensitive: true);
        Assert.Equal(0, e.Core.ReplaceAll(f, "x"));
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void ReplaceAll_ReplacementContainingQuery_DoesNotLoop()
    {
        var (e, _) = Setup("a a\na");
        var f = new FindModel();
        f.SetQuery("a", caseSensitive: true);
        Assert.Equal(3, e.Core.ReplaceAll(f, "aa"));
        Assert.Equal("aa aa\naa", e.Text);
    }

    [Fact]
    public void ReplaceAll_ReadOnly_DoesNothing()
    {
        var (e, _) = Setup();
        e.ReadOnly = true;
        var f = new FindModel();
        f.SetQuery("goal", caseSensitive: true);
        Assert.Equal(0, e.Core.ReplaceAll(f, "x"));
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void CtrlF_FromCode_HidesReplaceRow_CtrlH_ShowsIt()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.H));
        Assert.True(e.ReplaceOpen);
        Step(e, k.Frame(keys: EditorKey.Escape));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Assert.False(e.ReplaceOpen);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.H));
        Assert.True(e.ReplaceOpen);
        // Ctrl+F while already in the bar keeps the replace row
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Assert.True(e.ReplaceOpen);
    }

    // ---------------------------------------------------------------- perf

    static string BigScript(int lines)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            switch (i % 4)
            {
                case 0: sb.Append("Goal.Act").Append(i).Append(" = function(arg0, arg1, arg2)\n"); break;
                case 1: sb.Append("    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, ").Append(3000 + i % 50).Append(", TARGET_ENE_0, 9999, 0, 0)\n"); break;
                case 2: sb.Append("    local f").Append(i).Append("_local1 = arg1:GetRandam_Int(1, 100)\n"); break;
                default: sb.Append("end\n"); break;
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Perf_ReplaceAll_20kLines()
    {
        var text = BigScript(20_000);
        var (e, _) = Setup(text);
        var f = new FindModel();
        f.SetQuery("GOAL_COMMON_ComboRepeat", caseSensitive: true);
        e.Core.ReplaceAll(f, "GOAL_COMMON_ComboAttack"); // warm-up (JIT)
        e.Undo();

        var sw = Stopwatch.StartNew();
        int n = e.Core.ReplaceAll(f, "GOAL_COMMON_ComboAttack");
        sw.Stop();
        var swUndo = Stopwatch.StartNew();
        e.Undo();
        swUndo.Stop();
        var swCount = Stopwatch.StartNew();
        int count = f.CountMatches(e.Core.Buffer);
        swCount.Stop();
        _out.WriteLine($"Replace All: {n} replacements in {e.LineCount} lines: {sw.Elapsed.TotalMilliseconds:F1} ms; undo {swUndo.Elapsed.TotalMilliseconds:F1} ms; count {swCount.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(5000, n);
        Assert.Equal(5000, count);
        Assert.Equal(text, e.Text);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Replace All took {sw.ElapsedMilliseconds} ms");

        // index lookup per frame once counted: O(line)
        var start = new Coordinates(19_997, 20);
        var end = new Coordinates(19_997, 43);
        f.MatchIndex(e.Core.Buffer, start, end);
        var swIdx = Stopwatch.StartNew();
        int idx = 0;
        for (int i = 0; i < 1000; i++)
            idx = f.MatchIndex(e.Core.Buffer, start, end);
        swIdx.Stop();
        _out.WriteLine($"MatchIndex (cached count): {swIdx.Elapsed.TotalMilliseconds:F3} us/call");
        Assert.Equal(5000, idx);
    }
}
