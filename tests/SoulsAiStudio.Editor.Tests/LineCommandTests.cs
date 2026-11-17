using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>VSCode-style line commands: Alt+Up/Down, Shift+Alt+Up/Down, Ctrl+L, Ctrl+Shift+K, Ctrl+Enter, Ctrl+/, Ctrl+D, Ctrl+[ ].</summary>
public class LineCommandTests
{
    const string Code = "a = 1\n    b = 2\nc = 3\nd = 4";

    static (LuaCodeEditor E, FakeKeys K) Setup(string text = Code)
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = text;
        return (e, new FakeKeys());
    }

    static void Step(LuaCodeEditor e, FakeKeys k) => e.ProcessKeys(k, editorFocused: true, textFocused: true, pageLines: 20);

    [Fact]
    public void AltUpDown_MovesLines_KeepsCursor_OneUndoStep()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((1, 6));
        k.Alt = true;
        Step(e, k.Frame(keys: EditorKey.Up));
        k.Alt = false;
        Assert.Equal("    b = 2\na = 1\nc = 3\nd = 4", e.Text);
        Assert.Equal(new Coordinates(0, 6), e.Core.Cursor);
        Assert.False(e.Core.MoveLines(-1)); // already at the top
        e.Undo();
        Assert.Equal(Code, e.Text);

        // a 2-line selection moves down as a block
        e.Core.Select((1, 0), (2, 3));
        Assert.True(e.Core.MoveLines(1));
        Assert.Equal("a = 1\nd = 4\n    b = 2\nc = 3", e.Text);
        Assert.Equal(new Coordinates(2, 0), e.Core.SelectionStart);
        Assert.Equal(new Coordinates(3, 3), e.Core.SelectionEnd);
        Assert.False(e.Core.MoveLines(1)); // at the bottom
    }

    [Fact]
    public void ShiftAltUpDown_CopiesLines()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((2, 2));
        k.Alt = true;
        Step(e, k.Frame(shift: true, keys: EditorKey.Down));
        k.Alt = false;
        Assert.Equal("a = 1\n    b = 2\nc = 3\nc = 3\nd = 4", e.Text);
        Assert.Equal(new Coordinates(3, 2), e.Core.Cursor); // on the new copy
        e.Undo();
        e.Core.SetCursor((0, 1));
        Assert.True(e.Core.CopyLines(-1));
        Assert.Equal("a = 1\na = 1\n    b = 2\nc = 3\nd = 4", e.Text);
        Assert.Equal(new Coordinates(0, 1), e.Core.Cursor);
    }

    [Fact]
    public void CtrlL_SelectsLine_ThenExtends()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((1, 3));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.L));
        Assert.Equal(new Coordinates(1, 0), e.Core.SelectionStart);
        Assert.Equal(new Coordinates(2, 0), e.Core.SelectionEnd);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.L));
        Assert.Equal(new Coordinates(3, 0), e.Core.SelectionEnd);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.L));
        Assert.Equal(new Coordinates(3, 5), e.Core.SelectionEnd); // last line: up to the end of the text
    }

    [Fact]
    public void CtrlShiftK_DeletesLines()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((1, 7));
        Step(e, k.Frame(ctrl: true, shift: true, keys: EditorKey.K));
        Assert.Equal("a = 1\nc = 3\nd = 4", e.Text);
        Assert.Equal(new Coordinates(1, 5), e.Core.Cursor);
        e.Core.SetCursor((2, 0));
        Assert.True(e.Core.DeleteLines()); // last line
        Assert.Equal("a = 1\nc = 3", e.Text);
        e.Core.SelectAll();
        Assert.True(e.Core.DeleteLines());
        Assert.Equal("", e.Text);
        e.Undo();
        Assert.Equal("a = 1\nc = 3", e.Text);
    }

    [Fact]
    public void CtrlEnter_InsertsLineBelowOrAbove_WithIndent()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((1, 5));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Enter));
        Assert.Equal("a = 1\n    b = 2\n    \nc = 3\nd = 4", e.Text);
        Assert.Equal(new Coordinates(2, 4), e.Core.Cursor);
        e.Undo();
        e.Core.SetCursor((1, 5));
        Step(e, k.Frame(ctrl: true, shift: true, keys: EditorKey.Enter));
        Assert.Equal("a = 1\n    \n    b = 2\nc = 3\nd = 4", e.Text);
        Assert.Equal(new Coordinates(1, 4), e.Core.Cursor);
    }

    [Fact]
    public void CtrlSlash_TogglesComment_AtCommonIndent()
    {
        var (e, k) = Setup("    if x then\n\n        y()\n    end");
        e.Core.Select((0, 0), (3, 7));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Slash));
        Assert.Equal("    -- if x then\n\n    --     y()\n    -- end", e.Text);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.Slash));
        Assert.Equal("    if x then\n\n        y()\n    end", e.Text);

        // a mix (one already commented) comments everything
        var (e2, _) = Setup("-- a\nb");
        e2.Core.SelectAll();
        Assert.True(e2.Core.ToggleLineComment());
        Assert.Equal("-- -- a\n-- b", e2.Text);
        // cursor after the comment marker stays on the same character
        var (e3, _) = Setup("foo()");
        e3.Core.SetCursor((0, 3));
        e3.Core.ToggleLineComment();
        Assert.Equal(new Coordinates(0, 6), e3.Core.Cursor);
    }

    [Fact]
    public void CtrlD_SelectsWordThenAddsNextOccurrence()
    {
        var (e, k) = Setup("x = 3001\ny = 3001 + 3001");
        e.Core.SetCursor((0, 5));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.D));
        Assert.Equal("3001", e.Core.GetSelectedText());
        Assert.Equal(new Coordinates(0, 4), e.Core.SelectionStart);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.D));
        Assert.Equal(new Coordinates(1, 4), e.Core.SelectionStart);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.D));
        Assert.Equal(new Coordinates(1, 11), e.Core.SelectionStart);
        Assert.Equal(3, e.Core.CaretCount); // Ctrl+D ADDS selections
        Assert.False(e.Core.SelectNextOccurrence()); // wraps: every occurrence already selected
        Assert.Equal(new Coordinates(1, 11), e.Core.SelectionStart);
        Assert.Equal(3, e.Core.CaretCount);
    }

    [Fact]
    public void CtrlBrackets_IndentAndOutdent()
    {
        var (e, k) = Setup();
        e.Core.SetCursor((0, 0));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.BracketRight));
        Assert.StartsWith("    a = 1", e.Text);
        Step(e, k.Frame(ctrl: true, keys: EditorKey.BracketLeft));
        Assert.Equal(Code, e.Text);
    }
}

public class CtrlDWordLeftTests
{
    [Fact]
    public void CtrlD_RightAfterAWord_SelectsThatWord()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = "f(10, 3060, X)";
        e.Core.SetCursor((0, 10)); // just after 3060, before ','
        e.Core.SelectNextOccurrence();
        Assert.Equal("3060", e.Core.GetSelectedText());
    }
}
