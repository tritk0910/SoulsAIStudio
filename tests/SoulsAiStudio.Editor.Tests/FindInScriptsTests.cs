using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>Editor-side pieces of "Find in Scripts": text search, external replace/swap, Ctrl+Shift+F request.</summary>
public class FindInScriptsTests
{
    const string Code = "x = 3001\r\ny = 30012 + 3001\r\n-- GOAL 3001\r\nz = a3001";

    [Fact]
    public void FindAll_UsesFindBarMatching()
    {
        var all = TextSearch.FindAll(Code, "3001", matchCase: false, wholeWord: false);
        Assert.Equal(5, all.Count);
        Assert.Equal(new TextMatch(1, 4, 4, "y = 30012 + 3001"), all[1]); // CR dropped from the line text

        var words = TextSearch.FindAll(Code, "3001", matchCase: false, wholeWord: true);
        Assert.Equal(new[] { (0, 4), (1, 12), (2, 8) }, words.Select(m => (m.Line, m.Column)));

        Assert.Single(TextSearch.FindAll(Code, "goal", matchCase: false, wholeWord: true));
        Assert.Empty(TextSearch.FindAll(Code, "goal", matchCase: true, wholeWord: false));
        Assert.Equal(2, TextSearch.FindAll(Code, "3001", false, false, maxMatches: 2).Count);
        Assert.Empty(TextSearch.FindAll(Code, "", false, false));
    }

    [Fact]
    public void FindAll_ManyMatchesOnOneLine()
    {
        string line = string.Concat(Enumerable.Repeat("ab ", 600));
        Assert.Equal(600, TextSearch.Count(line, "ab", true, true));
    }

    [Fact]
    public void ReplaceAllText_And_SwapAllText_AreOneUndoStep_AndRaiseTextChanged()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = "a(3001)\nb(3012)\nc(30012)";
        int changed = 0;
        e.TextChanged += () => changed++;

        Assert.Equal(2, e.SwapAllText("3001", "3012", matchCase: true, wholeWord: true));
        Assert.Equal("a(3012)\nb(3001)\nc(30012)", e.Text);
        Assert.True(e.IsDirty);
        Assert.Equal(1, changed);
        e.Undo();
        Assert.Equal("a(3001)\nb(3012)\nc(30012)", e.Text);

        Assert.Equal(2, e.ReplaceAllText("3001", "X", matchCase: true, wholeWord: false));
        Assert.Equal("a(X)\nb(3012)\nc(X2)", e.Text);
        e.Undo();
        Assert.False(e.IsDirty);

        e.ReadOnly = true;
        Assert.Equal(0, e.ReplaceAllText("3001", "X", true, false));
    }

    [Fact]
    public void SelectRange_SelectsTheMatch()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = "aa\nbb 3001 cc";
        e.SelectRange(1, 3, 7);
        Assert.Equal("3001", e.Core.GetSelectedText());
        Assert.Equal("bb 3001 cc", e.GetLineText(1));
        Assert.Equal("", e.GetLineText(9));
    }

    [Fact]
    public void CtrlShiftF_RequestsFindInScripts_WithSelectionOrWord()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = "AddSubGoal(GOAL_COMMON_Attack, 3001)";
        var k = new FakeKeys();
        e.Core.SetCursor((0, 14));
        Assert.False(e.TryTakeFindInFilesRequest(out _));
        e.ProcessKeys(k.Frame(ctrl: true, shift: true, keys: EditorKey.F), true, true, 20);
        Assert.False(e.FindOpen); // not the find bar
        Assert.True(e.TryTakeFindInFilesRequest(out string seed));
        Assert.Equal("GOAL_COMMON_Attack", seed);
        Assert.False(e.TryTakeFindInFilesRequest(out _)); // once

        e.Core.Select((0, 31), (0, 35));
        e.ProcessKeys(k.Frame(ctrl: true, shift: true, keys: EditorKey.F), true, true, 20);
        Assert.True(e.TryTakeFindInFilesRequest(out seed));
        Assert.Equal("3001", seed);
    }
}
