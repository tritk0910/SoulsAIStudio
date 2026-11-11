using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

public class TextModelTests
{
    static EditorCore Core(string text)
    {
        var c = new EditorCore { Clipboard = new MemoryClipboard() };
        c.SetText(text);
        return c;
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("\n\n\n")]
    [InlineData("-- Nhánh rẽ khi trúng đòn: ơ ư đ ă ạ ế ỹ 危\nend")]
    public void SetGetText_RoundTrips(string text)
    {
        var b = new TextBuffer();
        b.SetText(text);
        Assert.Equal(text, b.GetText());
    }

    [Fact]
    public void Insert_MultiLine_SplitsAndReturnsEnd()
    {
        var b = new TextBuffer();
        b.SetText("hello world");
        var end = b.Insert(new Coordinates(0, 5), "\nA\nBC");
        Assert.Equal("hello\nA\nBC world", b.GetText());
        Assert.Equal(new Coordinates(2, 2), end);
        Assert.Equal(3, b.LineCount);
    }

    [Fact]
    public void Delete_AcrossLines_Joins()
    {
        var b = new TextBuffer();
        b.SetText("abc\ndef\nghi");
        b.Delete(new Coordinates(0, 1), new Coordinates(2, 1));
        Assert.Equal("ahi", b.GetText());
        Assert.Equal("bc\ndef\ng", Snapshot("abc\ndef\nghi", (0, 1), (2, 1)));
    }

    static string Snapshot(string text, Coordinates a, Coordinates b)
    {
        var buf = new TextBuffer();
        buf.SetText(text);
        return buf.GetText(a, b);
    }

    [Fact]
    public void Typing_MergesIntoWordUndoSteps()
    {
        var c = Core("");
        foreach (var ch in "local x")
            c.TypeChar(ch);
        Assert.Equal("local x", c.Buffer.GetText());
        Assert.Equal(2, c.Undo.Count); // "local" + " x"
        c.DoUndo();
        Assert.Equal("local", c.Buffer.GetText());
        c.DoUndo();
        Assert.Equal("", c.Buffer.GetText());
        c.DoRedo();
        c.DoRedo();
        Assert.Equal("local x", c.Buffer.GetText());
        Assert.Equal(new Coordinates(0, 7), c.Cursor);
    }

    [Fact]
    public void Dirty_TracksSavePointThroughUndoRedo()
    {
        var c = Core("abc");
        Assert.False(c.Undo.IsDirty);
        c.SetCursor((0, 3));
        c.TypeChar('d');
        Assert.True(c.Undo.IsDirty);
        c.DoUndo();
        Assert.False(c.Undo.IsDirty);
        c.DoRedo();
        c.Undo.MarkClean();
        Assert.False(c.Undo.IsDirty);
        c.TypeChar('e'); // must not merge across the save point
        Assert.True(c.Undo.IsDirty);
        c.DoUndo();
        Assert.False(c.Undo.IsDirty);
        Assert.Equal("abcd", c.Buffer.GetText());

        // branch off below the save point: save point becomes unreachable
        c.DoUndo();
        c.TypeChar('z');
        Assert.True(c.Undo.IsDirty);
        c.DoUndo();
        Assert.True(c.Undo.IsDirty);
    }

    [Fact]
    public void Enter_AutoIndents_AndUndoRedoKeepsIndent()
    {
        var c = Core("    if x then");
        c.SetCursor((0, 13));
        c.TypeChar('\n');
        Assert.Equal("    if x then\n    ", c.Buffer.GetText());
        Assert.Equal(new Coordinates(1, 4), c.Cursor);
        c.DoUndo();
        Assert.Equal("    if x then", c.Buffer.GetText());
        c.DoRedo();
        Assert.Equal("    if x then\n    ", c.Buffer.GetText());
    }

    [Fact]
    public void Backspace_And_Delete_AcrossLineBreaks()
    {
        var c = Core("ab\ncd");
        c.SetCursor((1, 0));
        c.Backspace(false);
        Assert.Equal("abcd", c.Buffer.GetText());
        Assert.Equal(new Coordinates(0, 2), c.Cursor);
        c.DoUndo();
        Assert.Equal("ab\ncd", c.Buffer.GetText());
        Assert.Equal(new Coordinates(1, 0), c.Cursor);

        c.SetCursor((0, 2));
        c.Delete(false);
        Assert.Equal("abcd", c.Buffer.GetText());
        c.DoUndo();
        Assert.Equal("ab\ncd", c.Buffer.GetText());
    }

    [Fact]
    public void CtrlBackspace_DeletesWord()
    {
        var c = Core("arg2:AddSubGoal");
        c.SetCursor((0, 15));
        c.Backspace(true);
        Assert.Equal("arg2:", c.Buffer.GetText());
    }

    [Fact]
    public void Clipboard_CopyCutPaste()
    {
        var c = Core("line1\nline2\nline3");
        var clip = (MemoryClipboard)c.Clipboard;
        c.Select((0, 2), (1, 3));
        c.Copy();
        Assert.Equal(("ne1\nlin").Replace("\n", Environment.NewLine), clip.Text);

        c.Cut();
        Assert.Equal("lie2\nline3", c.Buffer.GetText());
        c.SetCursor((1, 5));
        c.Paste();
        Assert.Equal("lie2\nline3ne1\nlin", c.Buffer.GetText());
        c.DoUndo();
        c.DoUndo();
        Assert.Equal("line1\nline2\nline3", c.Buffer.GetText());
    }

    [Fact]
    public void Paste_NormalizesCrLf()
    {
        var c = Core("");
        ((MemoryClipboard)c.Clipboard).Text = "a\r\nb";
        c.Paste();
        Assert.Equal(2, c.Buffer.LineCount);
        Assert.Equal("a\nb", c.Buffer.GetText());
    }

    [Fact]
    public void Tab_IndentsBlock_AsOneUndoStep()
    {
        var c = Core("a\nb\nc");
        c.Select((0, 0), (2, 1));
        c.Tab(false);
        Assert.Equal("    a\n    b\n    c", c.Buffer.GetText());
        c.Tab(true);
        Assert.Equal("a\nb\nc", c.Buffer.GetText());
        c.DoUndo();
        Assert.Equal("    a\n    b\n    c", c.Buffer.GetText());
        c.DoUndo();
        Assert.Equal("a\nb\nc", c.Buffer.GetText());
    }

    [Fact]
    public void Tab_WithoutSelection_InsertsSpacesToTabStop()
    {
        var c = Core("ab");
        c.SetCursor((0, 2));
        c.Tab(false);
        Assert.Equal("ab  ", c.Buffer.GetText());
    }

    [Fact]
    public void ReadOnly_BlocksEdits_ButAllowsCopy()
    {
        var c = Core("abc");
        c.ReadOnly = true;
        c.SelectAll();
        Assert.False(c.TypeChar('x'));
        Assert.False(c.Paste());
        c.Copy();
        Assert.Equal("abc", ((MemoryClipboard)c.Clipboard).Text);
        Assert.Equal("abc", c.Buffer.GetText());
    }

    [Fact]
    public void Movement_WordHomeEndVertical()
    {
        var c = Core("    local f47_local7 = 1\nx\n    end");
        c.SetCursor((0, 0));
        c.MoveHome(false);
        Assert.Equal(new Coordinates(0, 4), c.Cursor); // smart home -> first non-blank
        c.MoveRight(false, true);
        Assert.Equal(new Coordinates(0, 10), c.Cursor); // after "local "
        c.MoveRight(true, true);
        Assert.Equal(new Coordinates(0, 21), c.Cursor);
        Assert.Equal("f47_local7 ", c.GetSelectedText());
        c.MoveEnd(false);
        Assert.Equal(new Coordinates(0, 24), c.Cursor);
        c.MoveDown(1, false);
        Assert.Equal(new Coordinates(1, 1), c.Cursor); // clamped
        c.MoveDown(1, false);
        Assert.Equal(new Coordinates(2, 7), c.Cursor); // preferred column 24 -> clamped to 7
        c.MoveLeft(false, false);
        Assert.Equal(new Coordinates(2, 6), c.Cursor);
        c.MoveToStartOfFile(true);
        Assert.Equal(new Coordinates(0, 0), c.Cursor);
        Assert.Equal(new Coordinates(2, 6), c.Anchor);
    }

    [Fact]
    public void Movement_SkipsSurrogatePairs()
    {
        var c = Core("a😀b");
        c.SetCursor((0, 1));
        c.MoveRight(false, false);
        Assert.Equal(new Coordinates(0, 3), c.Cursor);
        c.Backspace(false);
        Assert.Equal("ab", c.Buffer.GetText());
    }

    [Fact]
    public void Diagnostics_FollowLineInsertsAndRemoves()
    {
        var c = Core("a\nb\nc\nd");
        var store = new DiagnosticStore();
        store.Attach(c.Buffer);
        store.Set(new List<(int, int, int, bool, string)> { (2, 0, 1, true, "w"), (3, 0, 1, false, "e") });
        c.SetCursor((0, 1));
        c.TypeChar('\n');
        Assert.Equal(3, store[0].Line);
        Assert.Equal(4, store[1].Line);
        c.Select((2, 0), (3, 1)); // removes the line with the first diagnostic
        c.ReplaceSelection("");
        Assert.Equal(1, store.Count);
        Assert.Equal(3, store[0].Line);
        Assert.Equal("e", store[0].Message);
    }

    [Fact]
    public void Find_NextPrevWrapAndCount()
    {
        var b = new TextBuffer();
        b.SetText("Act01\nfoo act02\nACT03");
        var f = new FindModel();
        f.SetQuery("act", caseSensitive: false);
        Assert.Equal(3, f.CountMatches(b));
        Assert.True(f.FindNext(b, new Coordinates(0, 1), out var s, out var e));
        Assert.Equal(new Coordinates(1, 4), s);
        Assert.Equal(new Coordinates(1, 7), e);
        Assert.True(f.FindNext(b, new Coordinates(2, 1), out s, out _));
        Assert.Equal(new Coordinates(0, 0), s); // wrapped
        Assert.True(f.FindPrev(b, new Coordinates(0, 0), out s, out _));
        Assert.Equal(new Coordinates(2, 0), s); // wrapped backwards

        f.SetQuery("act", caseSensitive: true);
        Assert.Equal(1, f.CountMatches(b));

        f.SetQuery("zzz", caseSensitive: false);
        Assert.False(f.FindNext(b, default, out _, out _));
        Assert.Equal(0, f.CountMatches(b));
    }
}
