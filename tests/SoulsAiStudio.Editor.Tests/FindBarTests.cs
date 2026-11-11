using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>Scripted key source: one "frame" = the keys/chars set before calling ProcessKeys.</summary>
sealed class FakeKeys : IKeyInput
{
    readonly HashSet<EditorKey> _pressed = new();
    string _chars = "";

    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public FakeKeys Frame(string chars = "", bool ctrl = false, bool shift = false, params EditorKey[] keys)
    {
        _pressed.Clear();
        foreach (var k in keys)
            _pressed.Add(k);
        _chars = chars;
        Ctrl = ctrl;
        Shift = shift;
        return this;
    }

    public bool IsPressed(EditorKey key) => _pressed.Contains(key);

    public int ReadTypedChars(Span<char> destination)
    {
        int n = Math.Min(_chars.Length, destination.Length);
        _chars.AsSpan(0, n).CopyTo(destination);
        return n;
    }
}

public class FindBarTests
{
    const string Code =
        "Goal.Interrupt = function(arg0, arg1, arg2)\n" +
        "    if interruptSpeffect == 3506000 then\n" +
        "    elseif interruptSpeffect == 5060040 then\n" +
        "        arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3037)\n" +
        "    end\n" +
        "    -- 5060040 again\n" +
        "end";

    static (LuaCodeEditor Editor, FakeKeys Keys) Setup()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = Code;
        return (e, new FakeKeys());
    }

    static void Step(LuaCodeEditor e, FakeKeys k, bool textFocused = true) => e.ProcessKeys(k, editorFocused: true, textFocused, pageLines: 20);

    [Fact]
    public void LineInput_TypingEditingAndSelection()
    {
        var f = new LineInput(8);
        foreach (var c in "50600")
            f.Insert(c);
        Assert.Equal("50600", f.Text.ToString());
        f.MoveLeft();
        f.Backspace();
        Assert.Equal("5060", f.Text.ToString());
        Assert.Equal(3, f.Cursor);
        f.Home();
        f.Delete();
        Assert.Equal("060", f.Text.ToString());
        f.SelectAll();
        f.Insert('x');
        Assert.Equal("x", f.Text.ToString());
        Assert.False(f.Insert('\r'));
        Assert.False(f.Insert('\b'));
        foreach (var c in "123456789")
            f.Insert(c);
        Assert.Equal(8, f.Text.Length); // capacity
    }

    [Fact]
    public void LineInput_DigitsOnly_ParseInt()
    {
        var f = new LineInput(9, digitsOnly: true);
        f.Insert("12a3\n99");
        Assert.Equal("123", f.Text.ToString());
        Assert.Equal(123, f.ParseInt());
        f.Clear();
        Assert.Equal(0, f.ParseInt());
    }

    [Fact]
    public void LineInput_TypingDoesNotAllocate()
    {
        var f = new LineInput(256);
        f.Insert('w'); // warm up
        f.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            f.Insert((char)('0' + i % 10));
            if (i % 3 == 0)
                f.Backspace();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void CtrlF_ThenTyping_GoesToFindField_NotTheCode()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Assert.True(e.FindOpen);
        Assert.Equal(LuaCodeEditor.FocusTarget.Find, e.Target);

        // chars arrive one frame at a time, exactly once each
        foreach (var c in "5060040")
            Step(e, k.Frame(c.ToString()));

        Assert.Equal("5060040", e.FindField.Text.ToString());
        Assert.Equal(Code, e.Text); // code untouched
        Assert.Equal(2, e.Find.CountMatches(e.Core.Buffer));
        Assert.Equal(new Coordinates(2, 32), e.Core.SelectionStart); // first match selected incrementally
        Assert.Equal("5060040", e.Core.GetSelectedText());

        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Equal(5, e.Core.SelectionStart.Line); // next match
        Step(e, k.Frame(shift: true, keys: EditorKey.Enter));
        Assert.Equal(2, e.Core.SelectionStart.Line); // previous
        Assert.True(e.FindOpen);

        Step(e, k.Frame(keys: EditorKey.F3));
        Assert.Equal(5, e.Core.SelectionStart.Line);

        Step(e, k.Frame(keys: EditorKey.Escape));
        Assert.False(e.FindOpen);
        Assert.Equal(LuaCodeEditor.FocusTarget.Text, e.Target);

        // typing now goes to the code (replaces the selected match)
        Step(e, k.Frame("X"));
        Assert.Contains("-- X again", e.Text);
    }

    [Fact]
    public void CtrlF_WithSelection_PrefillsAndReplacesOnType()
    {
        var (e, k) = Setup();
        e.Core.Select((1, 28), (1, 35)); // "3506000"
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Assert.Equal("3506000", e.FindField.Text.ToString());
        Assert.True(e.FindField.AllSelected);
        Step(e, k.Frame("3037"));
        Assert.Equal("3037", e.FindField.Text.ToString());
        Assert.Equal(3, e.Core.SelectionStart.Line);
    }

    [Fact]
    public void FindField_EditingKeys()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Step(e, k.Frame("30123"));
        Step(e, k.Frame(keys: EditorKey.Backspace));
        Step(e, k.Frame(keys: EditorKey.Home));
        Step(e, k.Frame(keys: EditorKey.Delete));
        Assert.Equal("012", e.FindField.Text.ToString());
        ((MemoryClipboard)e.Clipboard).Text = "GOAL_COMMON";
        Step(e, k.Frame(ctrl: true, keys: EditorKey.A));
        Step(e, k.Frame(ctrl: true, keys: EditorKey.V));
        Assert.Equal("GOAL_COMMON", e.FindField.Text.ToString());
        Assert.Equal(3, e.Core.SelectionStart.Line);
        // Ctrl+V/A must not have touched the code
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void CtrlG_DigitsEnter_JumpsToLine()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.G));
        Assert.True(e.GotoOpen);
        Step(e, k.Frame("4x"));
        Assert.Equal("4", e.GotoField.Text.ToString());
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.False(e.GotoOpen);
        Assert.Equal((3, 0), e.Cursor);
        Assert.Equal(LuaCodeEditor.FocusTarget.Text, e.Target);
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void CharsWhileUnfocused_AreDrained_NotDelivered()
    {
        var (e, k) = Setup();
        e.ProcessKeys(k.Frame("abc"), editorFocused: false, textFocused: false, pageLines: 20);
        Assert.Equal(Code, e.Text);
        // text area only types when its window is focused
        Step(e, k.Frame("z"), textFocused: false);
        Assert.Equal(Code, e.Text);
        Step(e, k.Frame("z"), textFocused: true);
        Assert.StartsWith("zGoal", e.Text);
    }

    [Fact]
    public void IdleFrameWithFindOpen_DoesNotAllocate()
    {
        var (e, k) = Setup();
        Step(e, k.Frame(ctrl: true, keys: EditorKey.F));
        Step(e, k.Frame("5060040"));
        var idle = k.Frame();
        Step(e, idle);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            Step(e, idle);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
