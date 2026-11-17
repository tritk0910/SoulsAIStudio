using System.Diagnostics;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>Where a SpEffect that can interrupt an anim comes from (CLAUDE.md §P2 "SpEffectSource").</summary>
public enum SpEffectSourceKind
{
    /// <summary>SpEffect event in the anim's TAE (applied to the character itself).</summary>
    Tae,
    /// <summary>Cycle SpEffect (SpEffectParam cycleOccurrenceSpEffectId) of a TAE SpEffect (on the character itself).</summary>
    Cycle,
    /// <summary>Applied to the target when an attack/bullet of the anim HITS (AtkParam / Bullet spEffectId0..4): conditional.</summary>
    OnHit,
    /// <summary>Applied to the character itself when a behavior/bullet event runs (BehaviorParam refType SpEffect, Bullet spEffectIDForShooter).</summary>
    Behavior,
}

/// <summary>
/// A SpEffect that can activate while an anim plays and that Goal.Interrupt handles (an entry of a step's interrupt dropdown).
/// </summary>
/// <param name="Frame">Start frame (TAE frames) of the SpEffect window (hitbox window for <see cref="SpEffectSourceKind.OnHit"/>).</param>
/// <param name="Via">Display text of the source chain (e.g. " (AtkParam 3000100 on hit)"), may be empty.</param>
/// <param name="Blocked">Never interrupts in game (e.g. an on-hit SpEffect not observed on the target): the simulation skips it.</param>
/// <param name="Detail">Full source chain for tooltips (e.g. "TAE AttackBehavior (judge 100) → BehaviorParam … → AtkParam_Npc …").</param>
public sealed record InterruptCandidate(int SpEffectId, float Frame, float EndFrame, SpEffectHandler Handler,
    SpEffectSourceKind Kind = SpEffectSourceKind.Tae, string Via = "", bool Blocked = false, string Detail = "")
{
    public bool OnHit => Kind == SpEffectSourceKind.OnHit;

    /// <summary>The SpEffect lands on the hit target, not on the character running the AI.</summary>
    public bool OnTarget => Kind == SpEffectSourceKind.OnHit;
}

/// <summary>A step on a played combo path; <paramref name="Cut"/> = frame where a chosen SpEffect interrupt cuts it.</summary>
public sealed record ComboPathItem(ComboStep Step, int Level, float? Cut, int? SpEffectId = null, float? SpStart = null);

/// <summary>
/// The user's (or a simulated) choices along a combo: the arm of each branch, the SpEffect interrupt of each step and its
/// trigger frame, keyed by (source line, interrupt level) — level 0 = the goal, 1 = a handler run from it, …
/// </summary>
public sealed class ComboChoices
{
    public Dictionary<(int Line, int Level), int> Arms { get; } = new();
    public Dictionary<(int Line, int Level), int> Interrupts { get; } = new();
    public Dictionary<(int Line, int Level), float> Triggers { get; } = new();
    private readonly Dictionary<int, Func<ComboBranch, int>> _armsAt = new();

    public void Clear()
    {
        Arms.Clear();
        Interrupts.Clear();
        Triggers.Clear();
    }

    /// <summary>Arm chooser for branches at an interrupt level (chosen arm, else <see cref="DefaultArm"/>).</summary>
    public Func<ComboBranch, int> ArmsAt(int level)
    {
        if (!_armsAt.TryGetValue(level, out var f))
            _armsAt[level] = f = b => Arms.TryGetValue((b.Line, level), out int k) ? k : DefaultArm(b);
        return f;
    }

    public const string NoBranchTaken = "(no branch taken)";

    /// <summary>
    /// A branch decided by game state (distance, HP, SpEffect…) with no <c>else</c> defaults to "(no branch taken)":
    /// nothing happens unless the user says the condition holds. Random branches default to their first arm.
    /// </summary>
    public static int DefaultArm(ComboBranch b)
    {
        if (b.Arms.Any(a => a.Probability != null)) return 0;
        int none = b.Arms.Count - 1;
        return none >= 0 && b.Arms[none].Condition == NoBranchTaken ? none : 0;
    }
}

/// <summary>Result of <see cref="ComboSimulator.Run"/>: how often each path / anim / SpEffect interrupt came up.</summary>
public sealed record ComboStatistics(
    int Runs,
    IReadOnlyList<ComboStatistics.PathCount> Paths,
    IReadOnlyList<ComboStatistics.AnimCount> Anims,
    IReadOnlyList<ComboStatistics.InterruptCount> Interrupts,
    TimeSpan Elapsed)
{
    /// <param name="Items">The path (one representative run).</param>
    public sealed record PathCount(string Key, IReadOnlyList<ComboPathItem> Items, int Count);
    /// <param name="Cut">Runs where this anim was cut by a SpEffect interrupt.</param>
    public sealed record AnimCount(int AnimId, int Count, int Cut);
    /// <param name="CutAnimId">The anim the SpEffect interrupted.</param>
    public sealed record InterruptCount(int SpEffectId, int CutAnimId, int Count);

    /// <summary>Number of different paths seen (<see cref="Paths"/> only keeps the most frequent).</summary>
    public int DistinctPaths { get; init; }

    public double Percent(int count) => Runs == 0 ? 0 : 100.0 * count / Runs;

    /// <summary>"3002 [5060040] → 3037 → 3018": anims in order, the SpEffect that cut an anim in brackets.</summary>
    public static string PathKey(IReadOnlyList<ComboPathItem> items) =>
        string.Join(" → ", items.Select(p => p.SpEffectId is int sp ? $"{p.Step.AnimId} [{sp}]" : p.Step.AnimId.ToString()));
}

/// <summary>
/// Top-down combo simulation, like the game (CLAUDE.md P2): the goal's branches are rolled when the goal runs (one value per
/// random variable, shared by every <c>if</c> that reads it). Then each anim plays in order; at each SpEffect that
/// Goal.Interrupt handles, in time order and before the anim's "ComboAttack Queued" frame, the interrupt runs right there
/// with ONE fresh roll per random variable used by its own condition AND the <c>if</c>s inside it. If it fires the anim
/// is cut and the new chain plays the same way; a <c>ClearSubGoal</c> drops the rest. Game-state checks (timers,
/// SpEffects, HP, distance inside the arm) are a coin flip; arms whose own condition tests the distance are never picked.
/// On-hit SpEffects only count when <see cref="AttacksHit"/> is set.
/// </summary>
public sealed class ComboSimulator
{
    public const int DefaultMaxDepth = 6;

    private readonly SpEffectInterrupts.Table? _table;
    private readonly Func<int, IReadOnlyList<InterruptCandidate>> _candidates;
    private readonly Func<int, float?> _queuedFrame;
    private readonly Dictionary<int, IReadOnlyList<InterruptCandidate>> _candidateMemo = new();
    private readonly Dictionary<int, float?> _queuedMemo = new();
    private readonly IReadOnlySet<string> _randomVars;
    private readonly IReadOnlySet<string> _spVars;
    private static readonly IReadOnlySet<string> NoVars = new HashSet<string>();

    /// <param name="candidates">Anim (Lua id) → its interrupt candidates (sorted by frame); memoized per simulator.</param>
    /// <param name="queuedFrame">Anim (Lua id) → start frame of its "ComboAttack Queued" window, or null; memoized.</param>
    public ComboSimulator(SpEffectInterrupts.Table? table, Func<int, IReadOnlyList<InterruptCandidate>> candidates,
        Func<int, float?> queuedFrame, int maxDepth = DefaultMaxDepth)
    {
        _table = table;
        _candidates = candidates;
        _queuedFrame = queuedFrame;
        MaxDepth = maxDepth;
        _randomVars = table?.RandomVars ?? NoVars;
        _spVars = table?.SpEffectVars ?? NoVars;
    }

    public int MaxDepth { get; }

    /// <summary>On-hit SpEffects fire (the attacks hit the target). Off: they are never picked.</summary>
    public bool AttacksHit { get; set; }

    public IReadOnlyList<InterruptCandidate> Candidates(int animId)
    {
        if (!_candidateMemo.TryGetValue(animId, out var c)) _candidateMemo[animId] = c = _candidates(animId);
        return c;
    }

    public float? QueuedFrame(int animId)
    {
        if (!_queuedMemo.TryGetValue(animId, out var q)) _queuedMemo[animId] = q = _queuedFrame(animId);
        return q;
    }

    /// <summary>Clears <paramref name="into"/> and fills it with one random top-down run.</summary>
    public void Roll(IReadOnlyList<ComboNode> nodes, ComboChoices into, Random rng)
    {
        into.Clear();
        RollBranches(nodes, 0, new Dictionary<string, int>(), NoVars, into, rng);
        RollInterrupts(nodes, 0, into, rng);
    }

    /// <summary>
    /// Evaluates a condition with the given rolls (a variable is rolled 1–100 on first use, then reused). Unknown
    /// game-state parts are a coin flip.
    /// </summary>
    private bool Passes(string condition, Dictionary<string, int> rolls, IReadOnlySet<string> matched, Random rng)
    {
        bool ok = RandomCondition.Evaluate(condition, _randomVars, matched,
            v => rolls.TryGetValue(v, out int x) ? x : rolls[v] = rng.Next(1, 101), out bool unknown);
        return ok && (!unknown || rng.Next(2) == 0);
    }

    /// <summary>Rolls the branches of a chain top-down (only the arms actually taken are entered).</summary>
    private void RollBranches(IReadOnlyList<ComboNode> nodes, int level, Dictionary<string, int> rolls, IReadOnlySet<string> matched, ComboChoices into, Random rng)
    {
        foreach (var n in nodes)
        {
            if (n is not ComboBranch b) continue;
            int pick = b.Arms.Count - 1;
            for (int k = 0; k < b.Arms.Count; k++)
                if (Passes(b.Arms[k].Condition, rolls, matched, rng)) { pick = k; break; }
            into.Arms[(b.Line, level)] = pick;
            RollBranches(b.Arms[pick].Body, level, rolls, matched, into, rng);
        }
    }

    /// <summary>True when the simulation may pick this candidate at all (before rolling its arm).</summary>
    public bool CanFire(InterruptCandidate c, float? queued) =>
        !c.Blocked && (!c.OnHit || AttacksHit) && !(queued is float q && c.Frame >= q) && !c.Handler.OuterDistance;

    private void RollInterrupts(IReadOnlyList<ComboNode> nodes, int level, ComboChoices into, Random rng)
    {
        if (level >= MaxDepth) return;
        var steps = ComboSequence.Flatten(nodes, into.ArmsAt(level));
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var c = Candidates(s.AnimId);
            // Only while another anim is queued: the next anim takes over at "ComboAttack Queued".
            float? queued = i < steps.Count - 1 ? QueuedFrame(s.AnimId) : null;
            int pick = -1;
            for (int k = 0; k < c.Count && pick < 0; k++)
            {
                if (!CanFire(c[k], queued)) continue;
                var h = c[k].Handler;
                // Goal.Interrupt runs now: one fresh roll per random variable for the arm AND the ifs inside it.
                var rolls = new Dictionary<string, int>();
                if (!Passes(h.Condition, rolls, _spVars, rng)) continue;
                RollBranches(h.Body, level + 1, rolls, _spVars, into, rng);
                // The arm's branches may add nothing (e.g. "no branch taken"): the anim keeps playing.
                if (!ComboSequence.ClearsOnPath(h.Body, into.ArmsAt(level + 1)) && ComboSequence.Flatten(h.Body, into.ArmsAt(level + 1)).Count == 0) continue;
                pick = k;
            }
            into.Interrupts[(s.Line, level)] = pick;
            if (pick < 0) continue;
            RollInterrupts(c[pick].Handler.Body, level + 1, into, rng);
            if (ComboSequence.ClearsOnPath(c[pick].Handler.Body, into.ArmsAt(level + 1))) return;
        }
    }

    /// <summary>The chosen interrupt of a step from this simulator's candidates (trigger frame clamped to the SpEffect window).</summary>
    public (float Frame, float Start, float End, InterruptCandidate Candidate)? Chosen(ComboStep s, int level, ComboChoices c)
    {
        if (!c.Interrupts.TryGetValue((s.Line, level), out int k) || k < 0) return null;
        var list = Candidates(s.AnimId);
        if (k >= list.Count) return null;
        float start = list[k].Frame, end = Math.Max(list[k].Frame, list[k].EndFrame);
        float at = c.Triggers.TryGetValue((s.Line, level), out float t) ? Math.Clamp(t, start, end) : start;
        return (at, start, end, list[k]);
    }

    public List<ComboPathItem> BuildPath(IReadOnlyList<ComboNode> nodes, ComboChoices c) =>
        BuildPath(nodes, c, (s, level) => Chosen(s, level, c) is { } x ? (x.Frame, x.Start, x.Candidate.Handler) : null, MaxDepth);

    /// <summary>
    /// Flattens the chosen branches; where a SpEffect interrupt is chosen on a step, that anim is cut at the trigger frame
    /// and the handler's sequence follows (replacing the rest when its path runs ClearSubGoal, queued after it otherwise).
    /// </summary>
    public static List<ComboPathItem> BuildPath(IReadOnlyList<ComboNode> nodes, ComboChoices c,
        Func<ComboStep, int, (float Frame, float SpStart, SpEffectHandler Handler)?> chosen, int maxDepth, int depth = 0)
    {
        var result = new List<ComboPathItem>();
        var steps = ComboSequence.Flatten(nodes, c.ArmsAt(depth));
        List<ComboPathItem>? appendAfter = null;
        foreach (var s in steps)
        {
            var ch = depth >= maxDepth ? null : chosen(s, depth);
            if (ch == null) { result.Add(new ComboPathItem(s, depth, null)); continue; }
            var (frame, spStart, handler) = ch.Value;
            var rest = BuildPath(handler.Body, c, chosen, maxDepth, depth + 1);
            bool clears = ComboSequence.ClearsOnPath(handler.Body, c.ArmsAt(depth + 1));
            // The chosen arm neither clears nor adds anims (e.g. random arm "no branch taken"): the anim keeps playing.
            if (!clears && rest.Count == 0) { result.Add(new ComboPathItem(s, depth, null)); continue; }
            result.Add(new ComboPathItem(s, depth, frame, handler.SpEffectId, spStart));
            if (clears) { result.AddRange(rest); return result; }
            (appendAfter ??= new()).AddRange(rest);
        }
        if (appendAfter != null) result.AddRange(appendAfter);
        return result;
    }

    /// <summary>Runs the simulation <paramref name="runs"/> times (fresh choices each time) and counts the outcomes.</summary>
    public ComboStatistics Run(IReadOnlyList<ComboNode> nodes, int runs, Random rng, int topPaths = 10)
    {
        var sw = Stopwatch.StartNew();
        var paths = new Dictionary<string, (List<ComboPathItem> Items, int Count)>();
        var anims = new Dictionary<int, (int Count, int Cut)>();
        var interrupts = new Dictionary<(int Sp, int Anim), int>();
        var choices = new ComboChoices();
        var seenAnims = new HashSet<int>();
        var seenCut = new HashSet<int>();
        var seenSp = new HashSet<(int, int)>();
        for (int r = 0; r < runs; r++)
        {
            Roll(nodes, choices, rng);
            var path = BuildPath(nodes, choices);
            string key = ComboStatistics.PathKey(path);
            paths[key] = paths.TryGetValue(key, out var pc) ? (pc.Items, pc.Count + 1) : (path, 1);
            seenAnims.Clear();
            seenCut.Clear();
            seenSp.Clear();
            foreach (var p in path)
            {
                seenAnims.Add(p.Step.AnimId);
                if (p.SpEffectId is int sp) { seenCut.Add(p.Step.AnimId); seenSp.Add((sp, p.Step.AnimId)); }
            }
            foreach (int a in seenAnims)
            {
                var prev = anims.TryGetValue(a, out var x) ? x : (0, 0);
                anims[a] = (prev.Item1 + 1, prev.Item2 + (seenCut.Contains(a) ? 1 : 0));
            }
            foreach (var k in seenSp) interrupts[k] = interrupts.TryGetValue(k, out int n) ? n + 1 : 1;
        }
        sw.Stop();
        return new ComboStatistics(runs,
            paths.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(topPaths)
                .Select(kv => new ComboStatistics.PathCount(kv.Key, kv.Value.Items, kv.Value.Count)).ToList(),
            anims.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key)
                .Select(kv => new ComboStatistics.AnimCount(kv.Key, kv.Value.Count, kv.Value.Cut)).ToList(),
            interrupts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Sp).ThenBy(kv => kv.Key.Anim)
                .Select(kv => new ComboStatistics.InterruptCount(kv.Key.Sp, kv.Key.Anim, kv.Value)).ToList(),
            sw.Elapsed) { DistinctPaths = paths.Count };
    }
}
