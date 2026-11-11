using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>One <c>:AddSubGoal(GOAL_X, time, animId, ...)</c> with a literal anim id.</summary>
public readonly record struct AnimReference(int Line, int AnimId, string GoalType);

/// <summary>Finds literal anim ids passed to <c>AddSubGoal</c>, by line (for "play this anim" from the editor).</summary>
public static class AnimReferences
{
    public static IReadOnlyList<AnimReference> Build(string src) => Build(src, LuaLexer.Tokenize(src));

    public static IReadOnlyList<AnimReference> Build(string src, IReadOnlyList<LuaToken> all)
    {
        var t = all.Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var result = new List<AnimReference>();
        for (int j = 0; j + 7 < t.Count; j++)
        {
            // : AddSubGoal ( GOAL , time , animId
            if (!t[j].Is(src, ":") || !t[j + 1].Is(src, "AddSubGoal") || !t[j + 2].Is(src, "(")) continue;
            if (t[j + 3].Kind != LuaTokenKind.Identifier || !t[j + 4].Is(src, ",")) continue;
            int k = j + 5;
            if (t[k].Is(src, "-")) k++; // negative time
            if (!t[k + 1].Is(src, ",") || t[k + 2].Kind != LuaTokenKind.Number) continue;
            if (int.TryParse(t[k + 2].Text(src), out int anim))
                result.Add(new AnimReference(t[k + 2].Line, anim, t[j + 3].Text(src).ToString()));
        }
        return result;
    }
}
