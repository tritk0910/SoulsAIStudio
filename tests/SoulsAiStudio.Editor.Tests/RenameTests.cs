using System.Text.RegularExpressions;
using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Tests;

public class RenameTests
{
    const string Code =
        "local dist = ai:GetDist(TARGET_ENE_0)\n" +
        "if dist > 3 then\n" +
        "    goal:AddSubGoal(GOAL_X, dist)\n" +
        "end\n" +
        "local distance = 1\n";

    /// <summary>Stand-in for the host's scope analysis: every whole word <c>dist</c> is the same variable, declared on line 0.</summary>
    static EditorSymbol? Provider(LuaCodeEditor e, int line, int col)
    {
        string text = e.Text;
        var spans = new List<EditorSpan>();
        var lines = text.Split('\n');
        for (int l = 0; l < lines.Length; l++)
            foreach (Match m in Regex.Matches(lines[l], @"\bdist\b"))
                spans.Add(new EditorSpan(l, m.Index, m.Length));
        if (!spans.Any(s => s.Line == line && col >= s.Column && col <= s.Column + s.Length)) return null;
        return new EditorSymbol("dist", spans[0], spans);
    }

    static (LuaCodeEditor Editor, FakeKeys Keys) Setup()
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        e.Text = Code;
        e.SymbolProvider = (l, c) => Provider(e, l, c);
        e.RenameValidator = (_, name) => name == "distance" ? "'distance' is already a variable in this scope." : null;
        return (e, new FakeKeys());
    }

    static void Step(LuaCodeEditor e, FakeKeys k) => e.ProcessKeys(k, editorFocused: true, textFocused: true, pageLines: 20);

    [Fact]
    public void F2_RenamesEveryUse_AsOneUndoStep()
    {
        var (e, k) = Setup();
        e.SelectRange(1, 4, 4); // inside `dist` of `if dist > 3`
        Step(e, k.Frame(keys: EditorKey.F2));
        Assert.True(e.RenameOpen);
        Step(e, k.Frame("getDist"));
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.False(e.RenameOpen);
        Assert.Equal(
            "local getDist = ai:GetDist(TARGET_ENE_0)\n" +
            "if getDist > 3 then\n" +
            "    goal:AddSubGoal(GOAL_X, getDist)\n" +
            "end\n" +
            "local distance = 1\n", e.Text);
        e.Undo();
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void ValidatorError_KeepsTheBarOpen_AndEscapeCancels()
    {
        var (e, k) = Setup();
        e.SelectRange(0, 7, 7);
        Step(e, k.Frame(keys: EditorKey.F2));
        Step(e, k.Frame("distance"));
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.True(e.RenameOpen);
        Assert.Contains("already a variable", e.RenameError);
        Assert.Equal(Code, e.Text);
        Step(e, k.Frame(keys: EditorKey.Escape));
        Assert.False(e.RenameOpen);
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void NotOnAVariable_ShowsAMessage_AndChangesNothing()
    {
        var (e, k) = Setup();
        e.SelectRange(0, 20, 20); // inside GetDist
        Step(e, k.Frame(keys: EditorKey.F2));
        Assert.True(e.RenameOpen);
        Assert.Contains("local variable", e.RenameError);
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.False(e.RenameOpen);
        Assert.Equal(Code, e.Text);
    }

    [Fact]
    public void GoToDefinitionAtCursor_SelectsTheDeclaration_NotWhenAlreadyThere()
    {
        var (e, _) = Setup();
        e.SelectRange(2, 28, 28); // the `dist` argument on line 2
        Assert.True(e.GoToDefinitionAtCursor());
        Assert.Equal((0, 10), e.Cursor); // the selection ends after `dist` on line 0
        Assert.False(e.GoToDefinitionAtCursor()); // already on the declaration
        e.SelectRange(0, 20, 20); // GetDist: not a variable
        Assert.False(e.GoToDefinitionAtCursor());
    }
}
