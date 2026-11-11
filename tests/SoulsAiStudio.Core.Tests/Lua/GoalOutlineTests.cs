using System.Text.RegularExpressions;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Tests.TestSupport;
using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.Lua;

public class GoalOutlineTests(ITestOutputHelper output)
{
    internal static void AssertOutlineInvariants(string label, IReadOnlyList<GoalFunction> goals)
    {
        foreach (var g in goals)
            Assert.True(g.EndLine >= g.Line, $"{label}: {g.Name} EndLine {g.EndLine} < Line {g.Line}");
        var dupes = goals.GroupBy(g => g.Name).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        Assert.True(dupes.Count == 0, $"{label}: duplicate goal names: {string.Join(", ", dupes)}");
        var sorted = goals.OrderBy(g => g.Line).ToList();
        for (int i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i].Line > sorted[i - 1].EndLine,
                $"{label}: {sorted[i].Name}@{sorted[i].Line} overlaps {sorted[i - 1].Name}@{sorted[i - 1].Line}..{sorted[i - 1].EndLine}");
    }

    private static readonly Regex DefLine = new(@"^\s*(Goal\.\w+\s*=\s*function\b|function\s+Goal\.\w+\s*\()", RegexOptions.Multiline);

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.PlainText), MemberType = typeof(LuaCorpus))]
    public void Corpus_PlainText_OutlineInvariants(string relPath)
    {
        string src = LuaCorpus.ReadText(relPath);
        var goals = GoalOutline.Build(src);
        AssertOutlineInvariants(relPath, goals);

        // Cross-check against a line-anchored regex (corpus has no definitions inside strings/comments).
        int regexCount = DefLine.Matches(src).Count;
        output.WriteLine($"{relPath}: {goals.Count} goals (regex {regexCount})");
        Assert.Equal(regexCount, goals.Count);

        // Each function's Line really is the definition line and EndLine holds an `end`.
        string[] lines = src.Split('\n');
        foreach (var g in goals)
        {
            Assert.Contains("Goal." + g.Name, lines[g.Line]);
            Assert.Matches(@"\bend\b", lines[g.EndLine]);
        }
    }

    [RequiresTestDataFact]
    public void Aicommon_PlainText_DoesNotCrash()
    {
        OodleSupport.EnsureLoaded();
        var a = SoulsAiStudio.Core.Formats.Luabnd.LuabndArchive.LoadPacked(Samples.LuabndPath("aicommon"));
        foreach (var e in a.LuaScripts.Where(e => !e.IsBytecode))
            AssertOutlineInvariants(e.FileName, GoalOutline.Build(e.GetText()));
    }

    [Fact]
    public void FunctionStatementSyntax()
    {
        var g = Assert.Single(GoalOutline.Build("function Goal.X(a, b)\n  return a\nend\n"));
        Assert.Equal(("X", "a, b", 0, 2), (g.Name, g.Parameters, g.Line, g.EndLine));
    }

    [Fact]
    public void NestedBlocksAndClosures_EndLinesCorrect()
    {
        string src = """
            Goal.A = function(ai, goal, _)
              local f = function(x) if x then return 1 end end
              for i = 1, 3 do f(i) end
              for k, v in pairs({}) do end
              while false do break end
              repeat local y = 1 until true
              do local z = 2 end
              if a then elseif b then else end
              local t = { do_it = function() end, ["end"] = 1 }
            end
            function Goal.B()
              do do end end
            end
            Goal.C = function() end
            """;
        var goals = GoalOutline.Build(src.Replace("\r\n", "\n"));
        Assert.Equal(["A", "B", "C"], goals.Select(g => g.Name));
        Assert.Equal((0, 9), (goals[0].Line, goals[0].EndLine));
        Assert.Equal((10, 12), (goals[1].Line, goals[1].EndLine));
        Assert.Equal((13, 13), (goals[2].Line, goals[2].EndLine));
        Assert.Equal("ai, goal, _", goals[0].Parameters);
    }

    [Fact]
    public void WhileConditionWithCallAndParens_DoIsLoopDo()
    {
        string src = "Goal.A = function()\n while (f(x) and g[1]) do\n  h()\n end\n for i = (1), #t do end\nend\nGoal.B = function() end\n";
        var goals = GoalOutline.Build(src);
        Assert.Equal(["A", "B"], goals.Select(g => g.Name));
        Assert.Equal(5, goals[0].EndLine);
    }

    [Fact]
    public void StringsAndComments_NeverCreateGoals()
    {
        string src = """
            local s = "Goal.Fake1 = function() end"
            local s2 = 'function Goal.Fake2() end'
            -- Goal.Fake3 = function() end
            --[==[
            Goal.Fake4 = function() end
            ]==]
            local t = [[Goal.Fake5 = function() end]]
            Goal.Real = function()
              local q = "end end end"
              -- end
            end
            """;
        var goals = GoalOutline.Build(src.Replace("\r\n", "\n"));
        var g = Assert.Single(goals);
        Assert.Equal(("Real", 7, 10), (g.Name, g.Line, g.EndLine));
    }

    [Fact]
    public void QualifiedAccess_IsNotADefinition()
    {
        var goals = GoalOutline.Build("x.Goal.Y = function() end\nself:Goal.Z = 1\nGoal.Real = function() end\n");
        Assert.Equal(["Real"], goals.Select(g => g.Name));
    }

    [Fact]
    public void AddSubGoal_CountAndAnimIds_IncludingChainedCalls()
    {
        string src = """
            Goal.Act01 = function(ai, goal, _)
              goal:AddSubGoal(GOAL_COMMON_ComboAttackTunableSpin, 10, 3000, TARGET_ENE_0, 9999, 0, 0)
              goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 1, 3012, TARGET_ENE_0, 9999, 0, 0):TimingSetNumber(13, 0, AI_TIMING_SET__ACTIVATE)
              goal:AddSubGoal(GOAL_COMMON_Wait, f(), TARGET_SELF)
              -- goal:AddSubGoal(GOAL_COMMON_Attack, 10, 9999, TARGET_ENE_0)
              local s = "goal:AddSubGoal(GOAL_COMMON_Attack, 10, 8888)"
            end
            """;
        var g = Assert.Single(GoalOutline.Build(src.Replace("\r\n", "\n")));
        Assert.Equal(3, g.AddSubGoalCount);
        Assert.Equal([3000, 3012], g.AnimIds);
        Assert.Equal(GoalGroup.Act, g.Group);
    }

    [Fact]
    public void UnterminatedFunction_DoesNotThrow_EndLineAtEof()
    {
        var g = Assert.Single(GoalOutline.Build("Goal.A = function()\n if x then\n"));
        Assert.True(g.EndLine >= g.Line);
        Assert.Empty(GoalOutline.Build("Goal.A = function("));
        Assert.Empty(GoalOutline.Build(""));
    }

    [Theory]
    [InlineData("Initialize", GoalGroup.Lifecycle)]
    [InlineData("Terminate", GoalGroup.Lifecycle)]
    [InlineData("Act07", GoalGroup.Act)]
    [InlineData("Act", GoalGroup.Other)]
    [InlineData("ActAfter", GoalGroup.Other)]
    [InlineData("Kengeki_Activate", GoalGroup.Kengeki)]
    [InlineData("Interrupt", GoalGroup.Interrupt)]
    [InlineData("Parry", GoalGroup.Interrupt)]
    [InlineData("ShootReaction", GoalGroup.Interrupt)]
    [InlineData("ActAfter_AdjustSpace", GoalGroup.Other)]
    public void Classify(string name, GoalGroup expected) => Assert.Equal(expected, GoalOutline.Classify(name));
}
