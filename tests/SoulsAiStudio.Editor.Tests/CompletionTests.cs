using System.Diagnostics;
using SoulsAiStudio.Editor.Completion;
using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Text;
using Xunit.Abstractions;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>Autocomplete: scanner, ranking/filtering, popup triggers and key routing (no ImGui).</summary>
public class CompletionTests
{
    readonly ITestOutputHelper _out;

    public CompletionTests(ITestOutputHelper output) => _out = output;

    const string Script =
        "RegisterTableGoal(GOAL_Owl506300_Battle, \"GOAL_Owl506300_Battle\")\n" +
        "Goal.Activate = function(arg0, arg1, arg2)\n" +
        "    local interruptRand = arg1:GetRandam_Int(1, 100)\n" +
        "    arg1:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 3506000)\n" +
        "    -- arg1:CommentedMethod(NOT_A_CONSTANT)\n" +
        "    local s = \"inString\"\n" +
        "    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0):TimingSetNumber(13, 0, AI_TIMING_SET__ACTIVATE)\n" +
        "    for i, v in ipairs(list) do end\n" +
        "    local x = math.floor(interruptRand / 2)\n" +
        "end\n";

    static (LuaCodeEditor Editor, FakeKeys Keys) Setup(string text, ICompletionProvider? provider = null)
    {
        var e = new LuaCodeEditor { Clipboard = new MemoryClipboard(), CompletionProvider = provider };
        e.Text = text;
        e.Core.SetCursor(e.Core.Buffer.EndOfText);
        e.Completion.ScanBufferNow();
        return (e, new FakeKeys());
    }

    static void Step(LuaCodeEditor e, FakeKeys k) => e.ProcessKeys(k, editorFocused: true, textFocused: true, pageLines: 20);

    static void Type(LuaCodeEditor e, FakeKeys k, string s)
    {
        foreach (var c in s)
            Step(e, k.Frame(c.ToString()));
    }

    static string[] Labels(LuaCodeEditor e) => e.Completion.Items.Select(i => i.Label).ToArray();

    // ---------------------------------------------------------------- scanner

    [Fact]
    public void Scanner_ClassifiesNames_IgnoresStringsAndComments()
    {
        var set = LuaCompletionScanner.Scan(Script);
        CompletionKind? G(string n) => set.Globals.FirstOrDefault(i => i.Label == n)?.Kind;

        Assert.Equal(CompletionKind.Function, G("RegisterTableGoal"));
        Assert.Equal(CompletionKind.Constant, G("GOAL_Owl506300_Battle"));
        Assert.Equal(CompletionKind.Constant, G("TARGET_ENE_0"));
        Assert.Equal(CompletionKind.Constant, G("AI_TIMING_SET__ACTIVATE"));
        Assert.Equal(CompletionKind.Local, G("interruptRand"));
        Assert.Equal(CompletionKind.Local, G("arg0"));   // function parameter
        Assert.Equal(CompletionKind.Local, G("arg2"));
        Assert.Equal(CompletionKind.Local, G("i"));      // for variable
        Assert.Equal(CompletionKind.Local, G("v"));
        Assert.Equal(CompletionKind.Variable, G("Goal"));
        Assert.Null(G("inString"));
        Assert.Null(G("NOT_A_CONSTANT"));
        Assert.Null(G("Activate"));                      // after '.', a field
        Assert.Null(G("local"));                         // keywords never come from the scanner

        var methods = set.Methods.Select(m => m.Label).ToHashSet();
        Assert.Contains("GetRandam_Int", methods);
        Assert.Contains("AddObserveSpecialEffectAttribute", methods);
        Assert.Contains("AddSubGoal", methods);
        Assert.Contains("TimingSetNumber", methods); // chained call
        Assert.DoesNotContain("CommentedMethod", methods);
        Assert.Contains("Activate", set.Fields.Select(f => f.Label));
        Assert.Contains("floor", set.Fields.Select(f => f.Label));
    }

    [Fact]
    public void Scanner_SkipsTheWordBeingTyped()
    {
        var set = LuaCompletionScanner.Scan("local abc = 1\nab", skipLine: 1, skipColumn: 2);
        Assert.Contains(set.Globals, i => i.Label == "abc");
        Assert.DoesNotContain(set.Globals, i => i.Label == "ab");
    }

    // ---------------------------------------------------------------- ranking

    [Theory]
    [InlineData("GetDist", "Get", CompletionFilter.RankPrefixExact)]
    [InlineData("GetDist", "get", CompletionFilter.RankPrefix)]
    [InlineData("GetSpecialEffectActivateInterruptType", "GSEA", CompletionFilter.RankHump)]
    [InlineData("GetSpecialEffectActivateInterruptType", "gseai", CompletionFilter.RankHump)]
    [InlineData("GOAL_COMMON_ComboRepeat", "gcr", CompletionFilter.RankHump)]
    [InlineData("GOAL_COMMON_ComboRepeat", "GCCRep", CompletionFilter.RankHump)]
    [InlineData("AddObserveSpecialEffectAttribute", "special", CompletionFilter.RankHump)] // word start
    [InlineData("AddObserveSpecialEffectAttribute", "pecial", CompletionFilter.RankSubstring)]
    [InlineData("GetDist", "xyz", -1)]
    [InlineData("Get", "GetDist", -1)]
    public void Rank(string label, string query, int expected) => Assert.Equal(expected, CompletionFilter.Rank(label, query));

    [Fact]
    public void Filter_OrdersPrefixThenHumpThenSubstring()
    {
        var items = new[] { "ClearSubGoal", "AddSubGoal", "subGoalCount", "SubGoal", "SetUpBoss", "subgoal_x", "Sub" }
            .Select(s => new CompletionItem(s, CompletionKind.Method)).ToArray();
        var f = new CompletionFilter();
        var r = f.Run("Sub", items).Select(i => i.Label).ToArray();
        // exact-case prefix (short first), case-insensitive prefix, then humps (S-U-B in SetUpBoss, Sub in AddSubGoal)
        Assert.Equal(new[] { "Sub", "SubGoal", "subgoal_x", "subGoalCount", "SetUpBoss", "AddSubGoal", "ClearSubGoal" }, r);
    }

    // ---------------------------------------------------------------- popup behaviour

    [Fact]
    public void TypingTwoIdentifierChars_OpensWithBufferAndSeedNames()
    {
        var (e, k) = Setup(Script + "    ", new WorkspaceCompletionProvider());
        Type(e, k, "i");
        Assert.False(e.Completion.IsOpen); // one char is not enough
        Type(e, k, "n");
        Assert.True(e.Completion.IsOpen);
        Assert.Equal("in", Labels(e)[0]);           // exact prefix, shortest first (keyword)
        Assert.Contains("interruptRand", Labels(e)); // buffer local
        Assert.Contains("INTERUPT_Damaged", Labels(e)); // seed constant (case-insensitive prefix)
        Type(e, k, "te");
        Assert.Equal(new[] { "interruptRand" }, Labels(e).Take(1));
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.False(e.Completion.IsOpen);
        Assert.EndsWith("    interruptRand", e.Text);
        Assert.Equal(1, e.LineCount - Script.Count(c => c == '\n')); // Enter did not insert a newline
    }

    [Fact]
    public void AfterColon_ShowsMethods_FromSeedsAndBuffer()
    {
        var (e, k) = Setup(Script + "    arg1", new WorkspaceCompletionProvider());
        Type(e, k, ":");
        Assert.True(e.Completion.IsOpen);
        Assert.Equal(CompletionController.Context.Method, e.Completion.Kind);
        var labels = Labels(e);
        Assert.Contains("GetRandam_Int", labels);      // buffer
        Assert.Contains("ClearSubGoal", labels);       // seed
        Assert.DoesNotContain("TARGET_ENE_0", labels); // not a method
        Type(e, k, "gsea");
        Assert.Equal("GetSpecialEffectActivateInterruptType", Labels(e)[0]);
        Step(e, k.Frame(keys: EditorKey.Tab));
        Assert.EndsWith("arg1:GetSpecialEffectActivateInterruptType", e.Text);
    }

    [Fact]
    public void LibraryMembers_AfterDot()
    {
        var (e, k) = Setup("local y = math");
        Type(e, k, ".fl");
        Assert.True(e.Completion.IsOpen);
        Assert.Equal(CompletionController.Context.Field, e.Completion.Kind);
        Assert.Equal(new[] { "floor" }, Labels(e));
    }

    [Fact]
    public void NoPopup_InStringsCommentsNumbersAndConcat()
    {
        var (e, k) = Setup("local s = \"ab");
        Type(e, k, "cd");
        Assert.False(e.Completion.IsOpen);

        (e, k) = Setup("-- ab");
        Type(e, k, "cd");
        Assert.False(e.Completion.IsOpen);

        (e, k) = Setup("local n = 30");
        Type(e, k, "12");
        Assert.False(e.Completion.IsOpen);

        (e, k) = Setup("local n = 3");
        Type(e, k, ".");
        Assert.False(e.Completion.IsOpen);

        (e, k) = Setup("local n = a .");
        Type(e, k, ".");
        Assert.False(e.Completion.IsOpen);
    }

    [Fact]
    public void ArrowsMoveSelection_EscapeCloses_KeysNotStolenWhenClosed()
    {
        var (e, k) = Setup(Script + "    TA", new WorkspaceCompletionProvider());
        Type(e, k, "R");
        Assert.True(e.Completion.IsOpen);
        var cursor = e.Core.Cursor;
        Step(e, k.Frame(keys: EditorKey.Down));
        Assert.Equal(1, e.Completion.Selected);
        Assert.Equal(cursor, e.Core.Cursor); // the code cursor did not move
        Step(e, k.Frame(keys: EditorKey.Up));
        Step(e, k.Frame(keys: EditorKey.Up));
        Assert.Equal(e.Completion.Items.Count - 1, e.Completion.Selected); // wraps

        Step(e, k.Frame(keys: EditorKey.Escape));
        Assert.False(e.Completion.IsOpen);
        string before = e.Text;
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.Equal(before + "\n    ", e.Text); // Enter is a newline again (auto-indent)
        Step(e, k.Frame(keys: EditorKey.Up));
        Assert.Equal(cursor.Line, e.Core.Cursor.Line);
    }

    [Fact]
    public void CtrlSpace_OpensWithEmptyPrefix_AndDropsTheSpaceChar()
    {
        var (e, k) = Setup("x = ");
        Step(e, k.Frame(" ", ctrl: true, keys: EditorKey.Space));
        Assert.True(e.Completion.IsOpen);
        Assert.Equal("x = ", e.Text);
        Assert.Contains("and", Labels(e));
        Assert.Contains("x", Labels(e));
    }

    [Fact]
    public void Closes_WhenCursorLeavesTheWord_OrNonWordCharTyped()
    {
        var (e, k) = Setup(Script + "    interr");
        Type(e, k, "u");
        Assert.True(e.Completion.IsOpen);
        Type(e, k, "(");
        Assert.False(e.Completion.IsOpen);

        (e, k) = Setup(Script + "    interr");
        Type(e, k, "u");
        Step(e, k.Frame(keys: EditorKey.Home));
        Assert.False(e.Completion.IsOpen);

        (e, k) = Setup(Script + "    in");
        Type(e, k, "t");
        Step(e, k.Frame(keys: EditorKey.Backspace));
        Step(e, k.Frame(keys: EditorKey.Backspace));
        Step(e, k.Frame(keys: EditorKey.Backspace));
        Assert.False(e.Completion.IsOpen); // auto popup with an empty word closes
    }

    [Fact]
    public void ExactOnlyMatch_DoesNotKeepPopupOpen()
    {
        var (e, k) = Setup("if x then\n    x = 1\n");
        Type(e, k, "en");
        Assert.True(e.Completion.IsOpen);
        Type(e, k, "d");
        Assert.False(e.Completion.IsOpen); // only "end" == typed word
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.EndsWith("end\n", e.Text);
    }

    [Fact]
    public void Accept_IsOneUndoStep()
    {
        var (e, k) = Setup(Script + "    ", new WorkspaceCompletionProvider());
        Type(e, k, "GOAL_COMMON_Com");
        Assert.True(e.Completion.IsOpen);
        string typed = e.Text;
        Step(e, k.Frame(keys: EditorKey.Enter));
        Assert.NotEqual(typed, e.Text);
        e.Undo();
        Assert.Equal(typed, e.Text);
    }

    [Fact]
    public void Workspace_DropsOtherFilesLocals_KeepsConstantsAndMethods()
    {
        var ws = new WorkspaceCompletionProvider(CompletionSet.Empty);
        ws.SetDocument("other", LuaCompletionScanner.Scan("local otherLocal = ai:OtherMethod(OTHER_CONST)\nOtherGlobalFn(1)"));
        var cur = ws.Current;
        Assert.Contains(cur.Globals, i => i.Label == "OTHER_CONST");
        Assert.Contains(cur.Globals, i => i.Label == "OtherGlobalFn");
        Assert.Contains(cur.Methods, i => i.Label == "OtherMethod");
        Assert.DoesNotContain(cur.Globals, i => i.Label == "otherLocal");
        Assert.Same(cur, ws.Current); // cached until something changes
        ws.RemoveDocument("other");
        Assert.Empty(ws.Current.Globals);
    }

    [Fact]
    public void ClosedPopup_IdleFrames_DoNotAllocate()
    {
        var (e, k) = Setup(Script, new WorkspaceCompletionProvider());
        var idle = k.Frame();
        Step(e, idle);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            Step(e, idle);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void OpenPopup_IdleUpdate_DoesNotAllocate()
    {
        var (e, k) = Setup(Script + "    ar", new WorkspaceCompletionProvider());
        Type(e, k, "g");
        Assert.True(e.Completion.IsOpen);
        var idle = k.Frame();
        Step(e, idle);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            Step(e, idle);
            e.Completion.Update();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---------------------------------------------------------------- perf

    [Fact]
    public void Perf_FilterLargeIdentifierSet_And_Scan20kLines()
    {
        var rnd = new Random(42);
        var parts = new[] { "Get", "Set", "Is", "Has", "Add", "Clear", "Special", "Effect", "Target", "Dist", "Goal", "Combo", "Attack", "Repeat", "Timer", "Number" };
        var names = new HashSet<string>();
        while (names.Count < 50_000)
        {
            var sb = new System.Text.StringBuilder();
            int n = rnd.Next(2, 6);
            for (int i = 0; i < n; i++)
                sb.Append(parts[rnd.Next(parts.Length)]);
            sb.Append(rnd.Next(1000));
            names.Add(sb.ToString());
        }
        // as CompletionSet keeps them (sorted once, off the keystroke path)
        var items = new CompletionSet(Array.Empty<CompletionItem>(), names.Select(s => new CompletionItem(s, CompletionKind.Method)).ToArray(), Array.Empty<CompletionItem>()).Methods;
        var f = new CompletionFilter();
        f.Run("ge", items, presorted: true); // warm-up

        string[] queries = { "g", "ge", "get", "gse", "GetSpecial", "special", "zz" };
        foreach (var q in queries)
        {
            var sw = Stopwatch.StartNew();
            const int reps = 20;
            int count = 0;
            for (int r = 0; r < reps; r++)
                count = f.Run(q, items, presorted: true).Count;
            sw.Stop();
            _out.WriteLine($"filter 50k items '{q}': {sw.Elapsed.TotalMilliseconds / reps:F2} ms ({count} shown, max 500)");
            Assert.True(sw.Elapsed.TotalMilliseconds / reps < 50);
        }

        var script = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            script.Append("Goal.Act").Append(i).Append(" = function(arg0, arg1, arg2)\n");
            script.Append("    local f").Append(i).Append("_local1 = arg1:GetRandam_Int(1, 100)\n");
            script.Append("    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3000, TARGET_ENE_0, 9999, 0, 0) -- comment\n");
            script.Append("end\n");
        }
        var text = script.ToString();
        LuaCompletionScanner.Scan(text);
        var sw2 = Stopwatch.StartNew();
        var set = LuaCompletionScanner.Scan(text);
        sw2.Stop();
        _out.WriteLine($"scan 20k lines ({text.Length / 1024} KB): {sw2.Elapsed.TotalMilliseconds:F1} ms -> {set.Count} names (worker thread)");
        Assert.True(sw2.ElapsedMilliseconds < 1000);

        // per-keystroke cost with the popup open over the merged set of a big buffer
        var (e, k) = Setup(text + "    ", new WorkspaceCompletionProvider());
        Type(e, k, "f1");
        var sw3 = Stopwatch.StartNew();
        Type(e, k, "23");
        sw3.Stop();
        _out.WriteLine($"popup refilter per keystroke ({set.Globals.Length} globals): {sw3.Elapsed.TotalMilliseconds / 2:F2} ms");
        Assert.True(e.Completion.IsOpen);
        Assert.StartsWith("f123", Labels(e)[0]);
    }
}
