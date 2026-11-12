using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>One <c>if interruptSpeffect == S then …</c> arm of the AI's interrupt handler.</summary>
/// <param name="ClearsQueue">The arm starts with <c>ClearSubGoal()</c>: its steps replace the rest of the current combo.</param>
public sealed record SpEffectHandler(int SpEffectId, int Line, bool ClearsQueue, bool Replans, IReadOnlyList<ComboNode> Body);

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
        var observed = new Dictionary<int, string>();
        for (int j = 0; j + 6 < t.Count; j++)
        {
            // name = obj : GetSpecialEffectActivateInterruptType (
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 3].Is(src, ":")
                && t[j + 4].Is(src, "GetSpecialEffectActivateInterruptType"))
                spVars.Add(t[j].Text(src).ToString());
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
                foreach (var arm in branch.Arms)
                    foreach (int id in SpEffectIds(arm.Condition, spVars))
                        handlers.TryAdd(id, new SpEffectHandler(id, branch.Line,
                            arm.Body.FirstOrDefault() is ComboClear,
                            Replans(src, t, branch.Line, arm), arm.Body));
        }
        return new Table(handlers, observed);
    }

    /// <summary><c>v == 123</c>, also <c>v == 1 or v == 2</c>.</summary>
    private static IEnumerable<int> SpEffectIds(string condition, HashSet<string> spVars)
    {
        var parts = condition.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int k = 0; k + 2 < parts.Length; k++)
            if (spVars.Contains(parts[k]) && parts[k + 1] == "==" && int.TryParse(parts[k + 2], out int id))
                yield return id;
    }

    /// <summary>Arm without combo steps that calls <c>Replanning()</c> (re-runs Activate instead of a combo).</summary>
    private static bool Replans(string src, List<LuaToken> t, int line, ComboArm arm) =>
        !arm.Body.OfType<ComboStep>().Any() && arm.Condition.Length > 0
        && t.Any(x => x.Line >= line && x.Line <= line + 3 && x.Is(src, "Replanning"));
}
