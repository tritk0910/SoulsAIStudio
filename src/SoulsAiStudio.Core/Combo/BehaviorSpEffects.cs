using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.Core.Combo;

/// <summary>A TAE event of an anim (type, window in TAE frames, raw parameter bytes).</summary>
public sealed record TaeEventData(int Type, float Frame, float EndFrame, byte[] Params);

/// <summary>BehaviorParam row: refType (0 attack, 1 bullet, 2 SpEffect) and refId.</summary>
public sealed record BehaviorRow(int RefType, int RefId);

/// <summary>BulletParam row fields that apply SpEffects.</summary>
public sealed record BulletRow(int AtkId, IReadOnlyList<int> SpEffectIds, int ShooterSpEffectId);

/// <summary>Param lookups used to resolve behavior events (implemented over DSAS's loaded params; faked in tests).</summary>
public interface IBehaviorParamSource
{
    BehaviorRow? Behavior(long id);
    /// <summary>AtkParam_Npc <c>spEffectId0..4</c> (values &gt; 0 only), or null when the row does not exist.</summary>
    IReadOnlyList<int>? AtkSpEffects(int atkId);
    BulletRow? Bullet(int id);
}

/// <summary>A SpEffect applied by a behavior event of an anim.</summary>
/// <param name="Kind"><see cref="SpEffectSourceKind.OnHit"/> (on the target, when the attack hits) or <see cref="SpEffectSourceKind.Behavior"/> (on self, when the event runs).</param>
/// <param name="Chain">Readable source chain, e.g. "TAE AttackBehavior (judge 100) → BehaviorParam 250600100 → AtkParam_Npc 5060100".</param>
public sealed record BehaviorSpEffect(int SpEffectId, float Frame, float EndFrame, SpEffectSourceKind Kind, string Chain, int EventType);

/// <summary>
/// Sekiro TAE behavior events (CLAUDE.md §4.3, from DSAS's TAE template): BehaviorJudgeID is read by offset.
/// </summary>
public static class SekiroBehaviorEvents
{
    public const int AttackBehavior = 1, Bullet = 2, BulletMidair = 4, CommonBehavior = 5;

    /// <summary>Offset of the s32 BehaviorJudgeID in the event's parameters, or -1 when the type is not a behavior event.</summary>
    public static int JudgeIdOffset(int type) => type switch
    {
        AttackBehavior or Bullet or BulletMidair => 0x8,
        CommonBehavior => 0x4,
        _ => -1,
    };

    public static string Name(int type) => type switch
    {
        AttackBehavior => "AttackBehavior",
        Bullet => "Bullet",
        BulletMidair => "Bullet (midair)",
        CommonBehavior => "CommonBehavior",
        _ => "event " + type,
    };

    /// <summary>
    /// BehaviorParam id of an NPC behavior event (ported from DSAS <c>TaeActionSimulationEnvironment.GetBehaviorParamFromEvBox</c>):
    /// CommonBehavior uses the judge id as is; others are <c>2VVVVVJJJ</c> (V = NpcParam behaviorVariationId, J = judge id).
    /// Null when the variation is unknown.
    /// </summary>
    public static long? BehaviorParamId(int eventType, int judgeId, int? behaviorVariationId) =>
        eventType == CommonBehavior ? judgeId
        : behaviorVariationId is int v ? 2_00000_000L + v * 1_000L + judgeId
        : null;
}

/// <summary>
/// SpEffect sources of an anim that are not TAE SpEffect events (CLAUDE.md §P2 "OnHit" / "BehaviorParam" / "Bullet"):
/// TAE AttackBehavior / Bullet / CommonBehavior → BehaviorParam → AtkParam_Npc <c>spEffectId0..4</c> (on the target when
/// the attack hits) or BulletParam (its own <c>spEffectId0..4</c> and its <c>atkId_Bullet</c>'s, on hit;
/// <c>spEffectIDForShooter</c> on self when fired); BehaviorParam refType SpEffect → that SpEffect on self.
/// </summary>
public static class BehaviorSpEffects
{
    public const int RefAttack = 0, RefBullet = 1, RefSpEffect = 2;

    public static List<BehaviorSpEffect> Resolve(IEnumerable<TaeEventData> events, int? behaviorVariationId, IBehaviorParamSource p)
    {
        var result = new List<BehaviorSpEffect>();
        var seen = new HashSet<(int, float, SpEffectSourceKind)>();
        void Add(int sp, TaeEventData e, SpEffectSourceKind kind, string chain)
        {
            if (sp > 0 && seen.Add((sp, e.Frame, kind)))
                result.Add(new BehaviorSpEffect(sp, e.Frame, Math.Max(e.Frame, e.EndFrame), kind, chain, e.Type));
        }

        foreach (var e in events)
        {
            int off = SekiroBehaviorEvents.JudgeIdOffset(e.Type);
            if (off < 0 || e.Params.Length < off + 4) continue;
            int judge = BitConverter.ToInt32(e.Params, off);
            if (judge < 0) continue;
            if (SekiroBehaviorEvents.BehaviorParamId(e.Type, judge, behaviorVariationId) is not long behId) continue;
            if (p.Behavior(behId) is not { } beh) continue;
            string chain = $"TAE {SekiroBehaviorEvents.Name(e.Type)} (judge {judge}) → BehaviorParam {behId}";
            switch (beh.RefType)
            {
                case RefAttack:
                    foreach (int sp in p.AtkSpEffects(beh.RefId) ?? [])
                        Add(sp, e, SpEffectSourceKind.OnHit, $"{chain} → AtkParam_Npc {beh.RefId}");
                    break;
                case RefBullet:
                    if (p.Bullet(beh.RefId) is not { } b) break;
                    string bc = $"{chain} → Bullet {beh.RefId}";
                    foreach (int sp in b.SpEffectIds) Add(sp, e, SpEffectSourceKind.OnHit, bc);
                    if (b.AtkId > 0)
                        foreach (int sp in p.AtkSpEffects(b.AtkId) ?? [])
                            Add(sp, e, SpEffectSourceKind.OnHit, $"{bc} → AtkParam_Npc {b.AtkId}");
                    Add(b.ShooterSpEffectId, e, SpEffectSourceKind.Behavior, $"{bc} (spEffectIDForShooter)");
                    break;
                case RefSpEffect:
                    Add(beh.RefId, e, SpEffectSourceKind.Behavior, chain);
                    break;
            }
        }
        return result.OrderBy(x => x.Frame).ToList();
    }
}
