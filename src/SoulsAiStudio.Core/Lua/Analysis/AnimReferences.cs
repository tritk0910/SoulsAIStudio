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
        var numericLocals = new Dictionary<string, int>(StringComparer.Ordinal); // local animationId = 3010
        for (int j = 0; j + 7 < t.Count; j++)
        {
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 2].Kind == LuaTokenKind.Number
                && !(j > 0 && (t[j - 1].Is(src, ".") || t[j - 1].Is(src, ":") || t[j - 1].Is(src, ",")))
                && t[j + 3].Text(src) is not ("+" or "-" or "*" or "/" or "%" or "^" or ".." or "," or ".")
                && int.TryParse(t[j + 2].Text(src), out int lit))
                numericLocals[t[j].Text(src).ToString()] = lit;

            // : AddSubGoal ( GOAL , time , animId
            if (!t[j].Is(src, ":") || !t[j + 1].Is(src, "AddSubGoal") || !t[j + 2].Is(src, "(")) continue;
            if (t[j + 3].Kind != LuaTokenKind.Identifier || !t[j + 4].Is(src, ",")) continue;
            int k = j + 5;
            if (t[k].Is(src, "-")) k++; // negative time
            if (!t[k + 1].Is(src, ",")) continue;
            var arg = t[k + 2];
            if (arg.Kind == LuaTokenKind.Number && int.TryParse(arg.Text(src), out int anim))
                result.Add(new AnimReference(arg.Line, anim, t[j + 3].Text(src).ToString()));
            else if (arg.Kind == LuaTokenKind.Identifier && !t[k + 3].Is(src, ".") && !t[k + 3].Is(src, ":") && !t[k + 3].Is(src, "(")
                     && numericLocals.TryGetValue(arg.Text(src).ToString(), out int viaLocal))
                result.Add(new AnimReference(arg.Line, viaLocal, t[j + 3].Text(src).ToString()));
        }
        return result;
    }
}
