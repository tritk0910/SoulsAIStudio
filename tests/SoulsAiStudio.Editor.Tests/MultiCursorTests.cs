using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>VSCode-like multiple cursors: Ctrl+D, Ctrl+Shift+L, Alt+Click, Ctrl+Alt+Up/Down, Escape.</summary>
public class MultiCursorTests
{
    static (LuaCodeEditor E, FakeKeys K, MemoryClipboard Clip) Setup(string text)
    {
        var clip = new MemoryClipboard();
        var e = new LuaCodeEditor { Clipboard = clip };
        e.Text = text;
        return (e, new FakeKeys(), clip);
    }

    static void Step(LuaCodeEditor e, FakeKeys k) => e.ProcessKeys(k, editorFocused: true, textFocused: true, pageLines: 20);

    static string Carets(LuaCodeEditor e) =>
        string.Join(" ", e.Core.AllSelections().Select(s => s.Start == s.End ? $"{s.Start}" : $"{s.Start}-{s.End}"));

    [Fact]
    public void Typing_AtEveryCaret_IsOneUndoStep()
    {
        var (e, k, _) = Setup("abc\nabc\nabc");
        e.Core.SetCursor((0, 1));
        e.Core.AddCaret((1, 1));
        e.Core.AddCaret((2, 1));
        Assert.Equal(3, e.CaretCount);
        Step(e, k.Frame("X"));
        Assert.Equal("aXbc\naXbc\naXbc", e.Text);
        Assert.Equal("0:2 1:2 2:2", Carets(e));
        Step(e, k.Frame("Y"));
        Assert.Equal("aXYbc\naXYbc\naXYbc", e.Text);

        e.Undo();
        Assert.Equal("aXbc\naXbc\naXbc", e.Text);
        Assert.Equal("0:2 1:2 2:2", Carets(e)); // carets restored too
        e.Undo();
        Assert.Equal("abc\nabc\nabc", e.Text);
        Assert.Equal("0:1 1:1 2:1", Carets(e));
        e.Redo();
        Assert.Equal("aXbc\naXbc\naXbc", e.Text);
        Assert.Equal(3, e.CaretCount);
    }

    [Fact]
    public void SameLineCarets_ShiftCorrectly_AndEnterIndents()
    {
        var (e, k, _) = Setup("    a b c");
        e.Core.SetCursor((0, 5));
        e.Core.AddCaret((0, 7));
        Step(e, k.Frame("__"));
        Assert.Equal("    a__ b__ c", e.Text);
        Assert.Equal("0:7 0:11", Carets(e));
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Equal("    a__\n     b__\n     c", e.Text); // auto-indent of the line (the space after each caret stays)
        Assert.Equal("1:4 2:4", Carets(e));
        e.Undo();
        Assert.Equal("    a__ b__ c", e.Text);
    }

    [Fact]
    public void Backspace_And_Delete_AtEveryCaret()
    {
        var (e, k, _) = Setup("ab\ncd\nef");
        e.Core.SetCursor((0, 1));
        e.Core.AddCaret((1, 1));
        e.Core.AddCaret((2, 1));
        Step(e, k.Frame(keys: EditorKey.Backspace));
        Assert.Equal("b\nd\nf", e.Text);
        Step(e, k.Frame(keys: EditorKey.Delete));
        Assert.Equal("\n\n", e.Text);
        Step(e, k.Frame(keys: EditorKey.Backspace)); // joins lines; carets meet and merge
        Assert.Equal("", e.Text);
        Assert.Equal(1, e.CaretCount);
        e.Undo();
        Assert.Equal("\n\n", e.Text);
        Assert.Equal(3, e.CaretCount);
    }

    [Fact]
    public void Arrows_MoveEveryCaret_AndMerge()
    {
        var (e, k, _) = Setup("abcd\nabcd");
        e.Core.SetCursor((0, 2));
        e.Core.AddCaret((1, 2));
        Step(e, k.Frame(keys: EditorKey.Left));
        Assert.Equal("0:1 1:1", Carets(e));
        Step(e, k.Frame(shift: true, keys: EditorKey.End));
        Assert.Equal("0:1-0:4 1:1-1:4", Carets(e));
        Step(e, k.Frame("Z"));
        Assert.Equal("aZ\naZ", e.Text);
        // Down from both: the upper one lands where the lower one is (last line: end) -> merged
        Step(e, k.Frame(keys: EditorKey.Down));
        Assert.Equal(1, e.CaretCount);
    }

    [Fact]
    public void OverlappingSelections_Merge()
    {
        var (e, _, _) = Setup("hello world");
        e.Core.Select((0, 0), (0, 5));
        e.Core.AddCaret((0, 3)); // inside the selection -> merged into it
        Assert.Equal(1, e.CaretCount);
        e.Core.Select((0, 0), (0, 2));
        e.Core.AddCaret((0, 2)); // caret touching the selection end -> merged
        Assert.Equal(1, e.CaretCount);
        Assert.Equal(new Coordinates(0, 0), e.Core.SelectionStart);
        Assert.Equal(new Coordinates(0, 2), e.Core.SelectionEnd);
    }

    [Fact]
    public void CtrlShiftL_SelectsAll_ThenTypingReplacesEach()
    {
        var (e, k, _) = Setup("x = 3001\ny = 3001 + 3001\nz = 30012");
        e.Core.SetCursor((1, 5)); // inside the second 3001
        Step(e, k.Frame(ctrl: true, shift: true, keys: EditorKey.L));
        Assert.Equal(4, e.CaretCount); // 3 + the prefix of 30012 (plain text, like Ctrl+D)
        Assert.Equal(new Coordinates(1, 4), e.Core.SelectionStart); // primary stays where the cursor was
        Step(e, k.Frame("42"));
        Assert.Equal("x = 42\ny = 42 + 42\nz = 422", e.Text);
        e.Undo(); // one step per typed char
        e.Undo();
        Assert.Equal("x = 3001\ny = 3001 + 3001\nz = 30012", e.Text);
        Assert.Equal(4, e.CaretCount);
    }

    [Fact]
    public void CtrlD_ThenTyping_EditsBoth()
    {
        var (e, k, _) = Setup("foo(a)\nfoo(b)");
        e.Core.SetCursor((0, 1));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.D));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.D));
        Assert.Equal(2, e.CaretCount);
        Step(e, k.Frame("bar"));
        Assert.Equal("bar(a)\nbar(b)", e.Text);
    }

    [Fact]
    public void CtrlAltUpDown_AddCarets_EscapeCollapses()
    {
        var (e, k, _) = Setup("aaaa\nbb\ncccc\ndddd");
        e.Core.SetCursor((2, 3));
        k.Alt = true;
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Up));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Up));
        k.Alt = false;
        Assert.Equal("0:3 1:2 2:3", Carets(e)); // short line clamps, the next one keeps the column
        Assert.Equal(new Coordinates(0, 3), e.Core.Cursor); // newest is primary
        k.Alt = true;
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Down));
        k.Alt = false;
        Assert.Equal(4, e.CaretCount);
        Assert.Equal(new Coordinates(3, 3), e.Core.Cursor);

        Step(e, k.Frame(keys: EditorKey.Escape));
        Assert.Equal(1, e.CaretCount);
        Assert.Equal(new Coordinates(3, 3), e.Core.Cursor);
        Assert.Equal("aaaa\nbb\ncccc\ndddd", e.Text);
    }

    [Fact]
    public void Paste_DistributesLines_WhenCountMatches()
    {
        var (e, k, clip) = Setup("a\nb\nc");
        e.Core.SetCursor((0, 1));
        e.Core.AddCaret((1, 1));
        e.Core.AddCaret((2, 1));
        clip.SetText("1\r\n2\r\n3\r\n"); // trailing line break ignored
        Step(e, k.Frame(ctrl: true, keys: EditorKey.V));
        Assert.Equal("a1\nb2\nc3", e.Text);
        e.Undo();
        clip.SetText("xy");
        Step(e, k.Frame(ctrl: true, keys: EditorKey.V));
        Assert.Equal("axy\nbxy\ncxy", e.Text);
        e.Undo();
        clip.SetText("1\n2"); // 2 lines for 3 carets: the whole text at each
        Step(e, k.Frame(ctrl: true, keys: EditorKey.V));
        Assert.Equal("a1\n2\nb1\n2\nc1\n2", e.Text);
    }

    [Fact]
    public void CopyCut_WithSeveralSelections_RoundTripsThroughPaste()
    {
        var (e, k, clip) = Setup("one two\nthree four");
        e.Core.Select((0, 0), (0, 3));
        e.Core.AddCaret((1, 0));
        e.Core.MoveRight(select: true, word: true); // "three " on line 1, "one " on line 0
        Step(e, k.Frame(ctrl: true, keys: EditorKey.X));
        Assert.Equal("two\nfour", e.Text);
        Assert.Equal("one \nthree ".Replace("\n", Environment.NewLine), clip.GetText());
        // paste back: one line per caret
        Step(e, k.Frame(ctrl: true, keys: EditorKey.V));
        Assert.Equal("one two\nthree four", e.Text);
    }

    [Fact]
    public void Copy_WithEmptyCarets_CopiesTheirLines()
    {
        var (e, k, clip) = Setup("l1\nl2\nl3");
        e.Core.SetCursor((0, 0));
        e.Core.AddCaret((2, 1));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.C));
        Assert.Equal("l1\nl3\n".Replace("\n", Environment.NewLine), clip.GetText());
    }

    [Fact]
    public void Tab_InsertsAtEveryCaret()
    {
        var (e, k, _) = Setup("a\nb");
        e.Core.SetCursor((0, 1));
        e.Core.AddCaret((1, 0));
        Step(e, k.Frame(keys: EditorKey.Tab));
        Assert.Equal("a   \n    b", e.Text);
    }

    [Fact]
    public void LineCommands_And_Clicks_UsePrimaryOnly()
    {
        var (e, k, _) = Setup("a\nb\nc");
        e.Core.SetCursor((0, 0));
        e.Core.AddCaret((2, 0)); // primary on line 2
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Slash));
        Assert.Equal("a\nb\n-- c", e.Text);
        Assert.Equal(1, e.CaretCount);
        e.Undo();
        Assert.Equal(2, e.CaretCount); // undo brings the carets back
        e.Core.SetCursor((1, 0));
        Assert.Equal(1, e.CaretCount);
    }

    [Fact]
    public void Frame_WithManyCarets_ZeroAllocs_UnderOneMs()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3000; i++)
            sb.Append("    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, ").Append(3000 + i % 50).Append(", TARGET_ENE_0, 9999, 0, 0)\n");
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = sb.ToString();
        Assert.True(e.ColorizeAll());
        e.Core.Select((10, 9), (10, 19)); // "AddSubGoal"
        Assert.True(e.Core.SelectAllOccurrences());
        Assert.Equal(3000, e.CaretCount);

        var sink = new SoulsAiStudio.Editor.Rendering.NullDrawSink();
        var metrics = new SoulsAiStudio.Editor.Rendering.FixedGlyphMetrics { LineHeight = 17, CharWidth = 8 };
        var size = new System.Numerics.Vector2(1100, 1000);
        float[] scrolls = { 0, 1500 * 17f, 2900 * 17f };
        foreach (var s in scrolls)
            e.DrawHeadless(sink, metrics, s, size);

        const int frames = 200;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
            e.DrawHeadless(sink, metrics, scrolls[i % scrolls.Length], size);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        sw.Stop();
        Assert.Equal(0, allocated);
        Assert.True(sw.Elapsed.TotalMilliseconds / frames < 1.0, $"{sw.Elapsed.TotalMilliseconds / frames} ms/frame");
    }

    [Fact]
    public void ReadOnly_MultiCaretTyping_DoesNothing()
    {
        var (e, k, _) = Setup("ab\ncd");
        e.Core.SetCursor((0, 1));
        e.Core.AddCaret((1, 1));
        e.ReadOnly = true;
        Step(e, k.Frame("X"));
        Assert.Equal("ab\ncd", e.Text);
    }
}
