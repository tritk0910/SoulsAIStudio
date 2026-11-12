using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>One <c>if interruptSpeffect == S then …</c> arm of the AI's interrupt handler.</summary>
/// <param name="Line">0-based line of the arm (<c>elseif interruptSpeffect == S then</c>).</param>
/// <param name="ClearsQueue">The arm starts with <c>ClearSubGoal()</c>: its steps replace the rest of the current combo.</param>
/// <param name="Condition">The arm's full condition (may add e.g. <c>and interruptRand &lt;= 75</c> or a timer check).</param>
public sealed record SpEffectHandler(int SpEffectId, int Line, bool ClearsQueue, bool Replans, IReadOnlyList<ComboNode> Body, string Condition = "", double? Chance = null)
{
    /// <summary>Chance (0–100) that the arm fires when the SpEffect activates, from <c>and rand &lt;= N</c> in its
    /// condition; null = no random part (timer/state checks are not counted).</summary>
    public double ChancePercent => Chance ?? 100;

    /// <summary>The arm tests more than <c>v == S</c> (extra random/timer/state conditions).</summary>
    public bool HasExtraCondition => Condition.Contains(" and ", StringComparison.Ordinal);
}

/// <summary>
/// Reads the SpEffect interrupt table of a battle script (CLAUDE.md P2): in <c>Goal.Interrupt</c>, the variable assigned
/// from <c>GetSpecialEffectActivateInterruptType(...)</c> is compared to SpEffect ids; each matching arm's body is a
/// combo sequence (with its own random/condition branches) that takes over when that SpEffect activates. Also lists
/// the observed SpEffects (<c>AddObserveSpecialEffectAttribute(target, id)</c>).
/// </summary>
public static class SpEffectInterrupts
{
    public sealed record Table(IReadOnlyDictionary<int, SpEffectHandler> Handlers, IReadOnlyDictionary<int, string> Observed);

    public static Table Build(string src) => Build(src, LuaLexer.Tokenize(src), GoalOutline.Build(src));

    public static Table Build(string src, IReadOnlyList<LuaToken> all, IReadOnlyList<GoalFunction> outline)
    {
        var t = all.Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var spVars = new HashSet<string>(StringComparer.Ordinal);
        var randVars = new HashSet<string>(StringComparer.Ordinal);
        var observed = new Dictionary<int, string>();
        for (int j = 0; j + 6 < t.Count; j++)
        {
            // name = obj : GetSpecialEffectActivateInterruptType (
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 3].Is(src, ":")
                && t[j + 4].Is(src, "GetSpecialEffectActivateInterruptType"))
                spVars.Add(t[j].Text(src).ToString());
            // name = obj : GetRandam_Int (
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 3].Is(src, ":")
                && t[j + 4].Is(src, "GetRandam_Int"))
                randVars.Add(t[j].Text(src).ToString());
            // : AddObserveSpecialEffectAttribute ( TARGET , id )
            if (t[j].Is(src, ":") && t[j + 1].Is(src, "AddObserveSpecialEffectAttribute") && t[j + 2].Is(src, "(")
                && t[j + 4].Is(src, ",") && t[j + 5].Kind == LuaTokenKind.Number && int.TryParse(t[j + 5].Text(src), out int obs))
                observed[obs] = t[j + 3].Text(src).ToString();
        }

        var handlers = new Dictionary<int, SpEffectHandler>();
        foreach (var goal in outline.Where(g => g.Name == "Interrupt"))
        {
            var nodes = ComboSequence.Build(src, all, goal);
            foreach (var branch in ComboSequence.Branches(nodes))
                for (int a = 0; a < branch.Arms.Count; a++)
                {
                    var arm = branch.Arms[a];
                    int line = arm.Line >= 0 ? arm.Line : branch.Line;
                    // The arm's source lines end where the next arm starts (or a few lines on for the last one).
                    int endLine = a + 1 < branch.Arms.Count && branch.Arms[a + 1].Line > line ? branch.Arms[a + 1].Line - 1 : line + 3;
                    foreach (int id in SpEffectIds(arm.Condition, spVars))
                        handlers.TryAdd(id, new SpEffectHandler(id, line,
                            arm.Body.FirstOrDefault() is ComboClear,
                            Replans(src, t, line, endLine, arm), arm.Body, arm.Condition, RandomChance(arm.Condition, randVars)));
                }
        }
        return new Table(handlers, observed);
    }

    /// <summary><c>... and r &lt;= 75</c> with r a GetRandam_Int(1, 100) variable → 75 (smallest if several).</summary>
    private static double? RandomChance(string condition, HashSet<string> randVars)
    {
        double? chance = null;
        var parts = condition.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int k = 0; k + 2 < parts.Length; k++)
            if (randVars.Contains(parts[k]) && parts[k + 1] == "<=" && double.TryParse(parts[k + 2], out double n))
                chance = Math.Min(chance ?? 100, Math.Clamp(n, 0, 100));
        return chance;
    }

    /// <summary><c>v == 123</c>, also <c>v == 1 or v == 2</c>.</summary>
    private static IEnumerable<int> SpEffectIds(string condition, HashSet<string> spVars)
    {
        var parts = condition.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int k = 0; k + 2 < parts.Length; k++)
            if (spVars.Contains(parts[k]) && parts[k + 1] == "==" && int.TryParse(parts[k + 2], out int id))
                yield return id;
    }

    /// <summary>
    /// Arm without combo steps (in any nested branch) that calls <c>Replanning()</c> within its own lines
    /// (re-runs Activate instead of a combo).
    /// </summary>
    private static bool Replans(string src, List<LuaToken> t, int line, int endLine, ComboArm arm) =>
        !HasSteps(arm.Body) && arm.Condition.Length > 0
        && t.Any(x => x.Line >= line && x.Line <= endLine && x.Is(src, "Replanning"));

    private static bool HasSteps(IReadOnlyList<ComboNode> nodes) =>
        nodes.Any(n => n is ComboStep || n is ComboBranch b && b.Arms.Any(a => HasSteps(a.Body)));
}
