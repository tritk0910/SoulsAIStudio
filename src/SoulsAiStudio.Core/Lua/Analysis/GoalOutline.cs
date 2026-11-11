using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

public enum GoalGroup { Lifecycle, Interrupt, Act, Kengeki, Other }

/// <summary>One <c>Goal.X = function(...)</c> (or <c>function Goal.X(...)</c>) definition.</summary>
public sealed record GoalFunction(
    string Name,
    string Parameters,
    int Line,
    int EndLine,
    GoalGroup Group,
    int AddSubGoalCount,
    IReadOnlyList<int> AnimIds)
{
    public string QualifiedName => "Goal." + Name;
}

/// <summary>
/// Builds the goal navigator outline (CLAUDE.md §5b) from tokens — no regex over raw text, so strings and
/// comments never produce false entries.
/// </summary>
public static class GoalOutline
{
    public static IReadOnlyList<GoalFunction> Build(string source) => Build(source, LuaLexer.Tokenize(source));

    public static IReadOnlyList<GoalFunction> Build(string src, IReadOnlyList<LuaToken> all)
    {
        // Comments are irrelevant for structure.
        var t = all.Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var result = new List<GoalFunction>();

        for (int i = 0; i < t.Count; i++)
        {
            int nameIdx, funcIdx;
            // Goal . Name = function (
            if (t[i].Is(src, "Goal") && i + 4 < t.Count && t[i + 1].Is(src, ".") && t[i + 2].Kind == LuaTokenKind.Identifier
                && t[i + 3].Is(src, "=") && t[i + 4].Is(src, "function") && IsStatementStart(src, t, i))
            {
                nameIdx = i + 2; funcIdx = i + 4;
            }
            // function Goal . Name (
            else if (t[i].Is(src, "function") && i + 3 < t.Count && t[i + 1].Is(src, "Goal") && t[i + 2].Is(src, ".")
                     && t[i + 3].Kind == LuaTokenKind.Identifier)
            {
                nameIdx = i + 3; funcIdx = i;
            }
            else continue;

            int parenOpen = funcIdx == i ? nameIdx + 1 : funcIdx + 1;
            if (parenOpen >= t.Count || !t[parenOpen].Is(src, "(")) continue;
            int parenClose = parenOpen;
            while (parenClose < t.Count && !t[parenClose].Is(src, ")")) parenClose++;
            if (parenClose >= t.Count) break;

            string parameters = string.Join(", ", t.Skip(parenOpen + 1).Take(parenClose - parenOpen - 1)
                .Where(x => !x.Is(src, ",")).Select(x => x.Text(src).ToString()));

            int bodyEnd = FindFunctionEnd(src, t, parenClose + 1);
            var (count, anims) = ScanAddSubGoals(src, t, parenClose + 1, bodyEnd);
            string name = t[nameIdx].Text(src).ToString();
            result.Add(new GoalFunction(name, parameters, t[nameIdx].Line,
                bodyEnd < t.Count ? t[bodyEnd].Line : t[^1].Line, Classify(name), count, anims));
            i = Math.Max(i, parenClose);
        }
        return result;
    }

    public static GoalGroup Classify(string name)
    {
        if (name is "Initialize" or "Activate" or "Update" or "Terminate") return GoalGroup.Lifecycle;
        if (name.StartsWith("Kengeki", StringComparison.Ordinal)) return GoalGroup.Kengeki;
        if (name.StartsWith("Act", StringComparison.Ordinal) && name.Length > 3 && name[3..].All(char.IsDigit)) return GoalGroup.Act;
        if (name.Contains("Interrupt", StringComparison.Ordinal) || name.Contains("Interupt", StringComparison.Ordinal)
            || name is "Parry" or "ShootReaction" || name.EndsWith("Reaction", StringComparison.Ordinal))
            return GoalGroup.Interrupt;
        return GoalGroup.Other;
    }

    /// <summary>Finds the token index of the <c>end</c> closing a function whose body starts at <paramref name="from"/>.</summary>
    private static int FindFunctionEnd(string src, List<LuaToken> t, int from)
    {
        int depth = 1;
        for (int j = from; j < t.Count; j++)
        {
            if (t[j].Kind != LuaTokenKind.Keyword) continue;
            var s = t[j].Text(src);
            // 'do' opens a block only when not part of while/for (those are counted at while/for).
            if (s is "function" or "if" or "while" or "for" or "repeat") depth++;
            else if (s is "do" && !IsLoopDo(src, t, j)) depth++;
            else if (s is "end" or "until" && --depth == 0) return j;
        }
        return t.Count - 1;
    }

    /// <summary>True when this <c>do</c> belongs to a preceding <c>while</c>/<c>for</c> on the same statement.</summary>
    private static bool IsLoopDo(string src, List<LuaToken> t, int doIdx)
    {
        int parens = 0;
        for (int k = doIdx - 1; k >= 0; k--)
        {
            var s = t[k].Text(src);
            if (s is ")" or "]" or "}") parens++;
            else if (s is "(" or "[" or "{") parens--;
            if (parens > 0) continue;
            if (t[k].Kind == LuaTokenKind.Keyword)
            {
                if (s is "while" or "for") return true;
                if (s is not ("in" or "and" or "or" or "not" or "nil" or "true" or "false")) return false;
            }
        }
        return false;
    }

    private static bool IsStatementStart(string src, List<LuaToken> t, int i) =>
        i == 0 || !t[i - 1].Is(src, ".") && !t[i - 1].Is(src, ":");

    /// <summary>Counts <c>:AddSubGoal(</c> calls and collects literal anim ids (3rd argument).</summary>
    private static (int Count, List<int> Anims) ScanAddSubGoals(string src, List<LuaToken> t, int from, int to)
    {
        int count = 0;
        var anims = new List<int>();
        for (int j = from; j < to - 1; j++)
        {
            if (!t[j].Is(src, ":") || !t[j + 1].Is(src, "AddSubGoal")) continue;
            count++;
            // AddSubGoal ( GOAL , time , animId ...
            int a = j + 3;
            if (a + 4 < t.Count && t[a + 1].Is(src, ",") && t[a + 3].Is(src, ",")
                && t[a + 4].Kind == LuaTokenKind.Number && int.TryParse(t[a + 4].Text(src), out int anim))
                anims.Add(anim);
        }
        return (count, anims);
    }
}
