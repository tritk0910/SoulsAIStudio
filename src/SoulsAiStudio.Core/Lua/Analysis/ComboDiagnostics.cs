namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>
/// Timing / observe hints for a SpEffect interrupt candidate of a combo step (CLAUDE.md P2 "Diagnostics combo"). Hints,
/// not errors: the analysis only knows the data it loaded.
/// </summary>
public static class ComboDiagnostics
{
    /// <summary>The observe target text means the character itself (<c>TARGET_SELF</c>).</summary>
    public static bool IsSelf(string target) => target.Contains("SELF", StringComparison.OrdinalIgnoreCase);

    /// <summary>The observe target text means the enemy of the AI (the player: <c>TARGET_ENE_0</c>).</summary>
    public static bool IsEnemy(string target) => target.Contains("ENE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Targets this SpEffect is observed on (empty = never observed).</summary>
    public static IReadOnlyList<string> ObservedOn(SpEffectInterrupts.Table table, int spEffectId) =>
        table.ObservedTargets.TryGetValue(spEffectId, out var all) ? all
        : table.Observed.TryGetValue(spEffectId, out var one) ? [one] : [];

    /// <summary>
    /// True when Goal.Interrupt can see this SpEffect: it is observed on the side it lands on (target for on-hit
    /// SpEffects, the character itself otherwise). Unknown target texts (e.g. a variable) count as matching.
    /// </summary>
    public static bool ObservedOnRightTarget(SpEffectInterrupts.Table table, InterruptCandidate c)
    {
        var on = ObservedOn(table, c.SpEffectId);
        if (on.Count == 0) return false;
        return on.Any(t => c.OnTarget ? IsEnemy(t) || !IsSelf(t) : IsSelf(t) || !IsEnemy(t));
    }

    /// <summary>
    /// Warnings for a candidate of a step: (a) starts at/after the ComboAttack Queued frame while a next anim is queued,
    /// (b) never observed, (c) observed on the wrong target. Empty when nothing to report.
    /// </summary>
    /// <param name="queuedFrame">Start of the anim's "ComboAttack Queued" window, or null.</param>
    /// <param name="hasNext">Another anim is queued after this step on the current path.</param>
    public static List<string> Warnings(SpEffectInterrupts.Table table, InterruptCandidate c, float? queuedFrame, bool hasNext)
    {
        var w = new List<string>();
        if (hasNext && queuedFrame is float q && c.Frame >= q)
            w.Add($"never fires: starts at frame {c.Frame:0}, the next anim starts at {q:0} (ComboAttack Queued)");
        var on = ObservedOn(table, c.SpEffectId);
        if (on.Count == 0)
            w.Add("not observed: no AddObserveSpecialEffectAttribute for it, Goal.Interrupt never sees it");
        else if (!ObservedOnRightTarget(table, c))
            w.Add(c.OnTarget
                ? $"observed on {string.Join(", ", on)}, but an on-hit SpEffect lands on the target: needs TARGET_ENE_0"
                : $"observed on {string.Join(", ", on)}, but this SpEffect is on the character itself: needs TARGET_SELF");
        return w;
    }
}
