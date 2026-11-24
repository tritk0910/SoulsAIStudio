using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.Core.Tests.Lua;

public class LuaScopesTests
{
    private const string Src = """
        Goal.Act01 = function(ai, goal)
            local dist = ai:GetDist(TARGET_ENE_0)
            local r = ai:GetRandam_Int(1, 100)
            if dist > 3 then
                local r = 5
                goal:AddSubGoal(GOAL_X, r, dist)
            end
            for i = 1, 3 do
                goal:AddSubGoal(GOAL_X, i, r)
            end
            local function helper(x) return x + dist end
            local t = { dist = 1, r = r }
            return helper(r)
        end
        """;

    /// <summary>0-based (line, column) of the <paramref name="nth"/> occurrence of <paramref name="needle"/>, plus <paramref name="offset"/> columns.</summary>
    private static (int Line, int Col) Find(string src, string needle, int offset = 0, int nth = 0)
    {
        int idx = -1;
        for (int k = 0; k <= nth; k++) idx = src.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        int line = src.AsSpan(0, idx).Count('\n');
        int lineStart = src.LastIndexOf('\n', Math.Max(0, idx - 1)) + 1;
        if (idx == 0) lineStart = 0;
        return (line, idx - lineStart + offset);
    }

    private static LuaVariable? At(LuaScopes sc, string src, string needle, int offset = 0)
    {
        var (l, c) = Find(src, needle, offset);
        return sc.VariableAt(l, c);
    }

    [Fact]
    public void ResolvesToTheRightDeclaration_WithShadowing()
    {
        var sc = LuaScopes.Build(Src);
        var inner = At(sc, Src, "GOAL_X, r, dist", "GOAL_X, ".Length)!;   // inside the if: the inner `r = 5`
        Assert.Equal(4, inner.Declaration.Line);
        var outer = At(sc, Src, "i, r)", 3)!;                              // in the for body: the outer r
        Assert.Equal(2, outer.Declaration.Line);
        Assert.NotSame(inner, outer);
        Assert.Equal(4, outer.Occurrences.Count);                          // declaration, for body, `r = r` value, helper(r)
    }

    [Fact]
    public void FieldsTableKeysAndGlobalsAreNotVariables()
    {
        var sc = LuaScopes.Build(Src);
        Assert.Null(At(sc, Src, "dist = 1"));          // table key
        Assert.Null(At(sc, Src, "GetDist", 1));        // method name
        Assert.Null(At(sc, Src, "GOAL_X"));            // global
    }

    [Fact]
    public void ParametersAndLocalFunctions()
    {
        var sc = LuaScopes.Build(Src);
        var x = At(sc, Src, "return x + dist", 7)!;
        Assert.Equal("x", x.Name);
        Assert.Equal("helper", At(sc, Src, "return helper(r)", 7)!.Name);
        Assert.Equal("goal", At(sc, Src, "goal:AddSubGoal(GOAL_X, r", 1)!.Name);
    }

    [Fact]
    public void LocalInitializerSeesTheOuterVariable()
    {
        const string s = "local a = 1\nlocal function f()\n  local a = a + 1\n  return a\nend\n";
        var sc = LuaScopes.Build(s);
        Assert.Equal(0, sc.VariableAt(2, 12)!.Declaration.Line);   // the `a` in `a + 1`
        Assert.Equal(2, sc.VariableAt(3, 9)!.Declaration.Line);    // `return a` -> the inner a
    }

    [Fact]
    public void CheckRename_RejectsKeywords_InvalidNames_AndCaptures()
    {
        var sc = LuaScopes.Build(Src);
        var dist = At(sc, Src, "local dist", 6)!;
        Assert.Equal("dist", dist.Name);
        Assert.NotNull(sc.CheckRename(dist, "end"));
        Assert.NotNull(sc.CheckRename(dist, "1abc"));
        Assert.NotNull(sc.CheckRename(dist, "r"));          // clashes with local r (used in the same scope)
        Assert.NotNull(sc.CheckRename(dist, "goal"));       // parameter used in the same scope
        Assert.NotNull(sc.CheckRename(dist, "GOAL_X"));     // a global used inside the scope
        Assert.Null(sc.CheckRename(dist, "getDist"));
    }
}
