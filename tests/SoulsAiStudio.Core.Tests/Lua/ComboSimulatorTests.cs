using SoulsAiStudio.Core.Combo;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.Core.Tests.Lua;

public class ComboSimulatorTests
{
    private const string Src = """
        Goal.Act01 = function(arg0, arg1, arg2)
            arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 100)
            arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 200)
            arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 300)
            arg1:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 400)
            arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 500)
            arg2:AddSubGoal(GOAL_COMMON_ComboAttack, 10, 3000, TARGET_ENE_0, 999, 0, 0)
            arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0, 999, 0, 0)
            arg2:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3002, TARGET_ENE_0, 999, 0, 0)
        end

        Goal.Interrupt = function(arg0, arg1, arg2)
            local interruptRand = arg1:GetRandam_Int(1, 100)
            local sp = arg1:GetSpecialEffectActivateInterruptType(0)
            if arg1:IsInterupt(INTERUPT_ActivateSpecialEffect) then
                if sp == 100 then
                    arg2:ClearSubGoal()
                    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 4000, TARGET_ENE_0, 999, 0, 0)
                    return true
                elseif sp == 200 and interruptRand <= 30 then
                    arg2:ClearSubGoal()
                    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 4100, TARGET_ENE_0, 999, 0, 0)
                    return true
                elseif sp == 300 and interruptRand <= 45 then
                    arg2:ClearSubGoal()
                    if interruptRand <= 60 then
                        arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 4200, TARGET_ENE_0, 999, 0, 0)
                    else
                        arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 4201, TARGET_ENE_0, 999, 0, 0)
                    end
                    return true
                elseif sp == 400 then
                    arg2:ClearSubGoal()
                    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 4400, TARGET_ENE_0, 999, 0, 0)
                    return true
                elseif sp == 500 then
                    arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 4500, TARGET_ENE_0, 999, 0, 0)
                    return true
                end
            end
            return false
        end
        """;

    private static readonly SpEffectInterrupts.Table Table = SpEffectInterrupts.Build(Src);
    private static readonly IReadOnlyList<ComboNode> Act01 = ComboSequence.Build(Src, GoalOutline.Build(Src).Single(g => g.Name == "Act01"));

    /// <summary>Simulator where anim 3000 has the given SpEffects (frame) and a ComboAttack Queued frame of 20.</summary>
    private static ComboSimulator Sim(params (int Sp, float Frame, SpEffectSourceKind Kind)[] on3000)
        => Sim(Table, on3000);

    /// <summary>Same, against <paramref name="table"/>; candidates are blocked by the observe check as in the Combo panel.</summary>
    private static ComboSimulator Sim(SpEffectInterrupts.Table table, params (int Sp, float Frame, SpEffectSourceKind Kind)[] on3000)
    {
        var list = on3000.Select(x => ComboDiagnostics.WithObserveCheck(table,
            new InterruptCandidate(x.Sp, x.Frame, x.Frame + 5, table.Handlers[x.Sp], x.Kind))).ToList();
        return new ComboSimulator(table, a => a == 3000 ? list : [], a => a == 3000 ? 20f : null);
    }

    [Fact]
    public void UnobservedSpEffect_NeverPicked()
    {
        // 100 handled ("always" arm) but its AddObserveSpecialEffectAttribute removed: Goal.Interrupt never sees it.
        var noObserve = SpEffectInterrupts.Build(Src.Replace("arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 100)", ""));
        var stats = Sim(noObserve, (100, 10, SpEffectSourceKind.Tae)).Run(Act01, 300, new Random(1));
        Assert.Equal("3000 → 3001 → 3002", Assert.Single(stats.Paths).Key);
        Assert.DoesNotContain(stats.Interrupts, i => i.SpEffectId == 100);
    }

    [Fact]
    public void SelfSpEffectObservedOnlyOnEnemy_NeverPicked()
    {
        // 400 is observed on TARGET_ENE_0 only; as a TAE SpEffect it lands on the character itself.
        var stats = Sim((400, 10, SpEffectSourceKind.Tae)).Run(Act01, 300, new Random(1));
        Assert.DoesNotContain(stats.Interrupts, i => i.SpEffectId == 400);
        Assert.True(ComboDiagnostics.WithObserveCheck(Table, new InterruptCandidate(400, 0, 1, Table.Handlers[400])).Blocked);
        Assert.False(ComboDiagnostics.WithObserveCheck(Table, new InterruptCandidate(100, 0, 1, Table.Handlers[100])).Blocked);
    }

    [Fact]
    public void Table_HasHandlersAndObservers()
    {
        Assert.Equal([100, 200, 300, 400, 500], Table.Handlers.Keys.Order());
        Assert.Equal(30, Table.Handlers[200].Chance);
        Assert.Equal(["TARGET_ENE_0"], Table.ObservedTargets[400]);
    }

    [Fact]
    public void AlwaysArm_Fires100Percent_AndClearSubGoalMakesLaterStepsUnreachable()
    {
        var stats = Sim((100, 10, SpEffectSourceKind.Tae)).Run(Act01, 500, new Random(1));
        var path = Assert.Single(stats.Paths);
        Assert.Equal(500, path.Count);
        Assert.Equal("3000 [100] → 4000", path.Key);
        Assert.Equal(500, stats.Interrupts.Single(i => i.SpEffectId == 100 && i.CutAnimId == 3000).Count);
        Assert.DoesNotContain(stats.Anims, a => a.AnimId is 3001 or 3002);
        Assert.Equal(500, stats.Anims.Single(a => a.AnimId == 3000).Cut);
    }

    [Fact]
    public void RandomArm_FiresAboutAsOftenAsItsThreshold()
    {
        const int runs = 4000;
        var stats = Sim((200, 10, SpEffectSourceKind.Tae)).Run(Act01, runs, new Random(42));
        double fired = stats.Percent(stats.Interrupts.Single(i => i.SpEffectId == 200).Count);
        Assert.InRange(fired, 27, 33);
        // Not fired: the combo goes on normally.
        Assert.InRange(stats.Percent(stats.Anims.Single(a => a.AnimId == 3001).Count), 67, 73);
    }

    [Fact]
    public void InterruptRand_IsSharedByArmConditionAndInnerIf()
    {
        const int runs = 4000;
        var stats = Sim((300, 10, SpEffectSourceKind.Tae)).Run(Act01, runs, new Random(7));
        Assert.InRange(stats.Percent(stats.Interrupts.Single(i => i.SpEffectId == 300).Count), 41, 49);
        Assert.Contains(stats.Anims, a => a.AnimId == 4200);
        Assert.DoesNotContain(stats.Anims, a => a.AnimId == 4201); // rand <= 45 → rand <= 60 always
    }

    [Fact]
    public void SpEffectAtOrAfterQueuedFrame_NeverFires()
    {
        var stats = Sim((100, 20, SpEffectSourceKind.Tae)).Run(Act01, 200, new Random(3));
        Assert.Empty(stats.Interrupts);
        Assert.Equal("3000 → 3001 → 3002", Assert.Single(stats.Paths).Key);
    }

    [Fact]
    public void EarlierSpEffectWins_LaterOneIsNotReached()
    {
        var stats = Sim((100, 5, SpEffectSourceKind.Tae), (200, 10, SpEffectSourceKind.Tae)).Run(Act01, 300, new Random(5));
        Assert.Equal("3000 [100] → 4000", Assert.Single(stats.Paths).Key);
    }

    [Fact]
    public void OnHit_FiresOnlyWhenAttacksHit()
    {
        var sim = Sim((400, 10, SpEffectSourceKind.OnHit));
        Assert.Empty(sim.Run(Act01, 100, new Random(1)).Interrupts);
        sim.AttacksHit = true;
        Assert.Equal("3000 [400] → 4400", Assert.Single(sim.Run(Act01, 100, new Random(1)).Paths).Key);
    }

    [Fact]
    public void OnHit_ObservedOnlyOnSelf_IsBlocked()
    {
        var sim = Sim((100, 10, SpEffectSourceKind.OnHit)); // 100 is observed on TARGET_SELF only
        sim.AttacksHit = true;
        Assert.Empty(sim.Run(Act01, 100, new Random(1)).Interrupts);
    }

    [Fact]
    public void ArmWithoutClear_AppendsAfterTheCombo()
    {
        var stats = Sim((500, 10, SpEffectSourceKind.Tae)).Run(Act01, 50, new Random(1));
        Assert.Equal("3000 [500] → 3001 → 3002 → 4500", Assert.Single(stats.Paths).Key);
    }

    [Fact]
    public void Roll_WritesChoicesUsableByBuildPath()
    {
        var sim = Sim((100, 10, SpEffectSourceKind.Tae));
        var choices = new ComboChoices();
        sim.Roll(Act01, choices, new Random(1));
        var path = sim.BuildPath(Act01, choices);
        Assert.Equal([3000, 4000], path.Select(p => p.Step.AnimId));
        Assert.Equal(10f, path[0].Cut);
        Assert.Equal(1, path[1].Level);
    }

    [Fact]
    public void Statistics_ThousandRuns_AreFast()
    {
        var stats = Sim((200, 10, SpEffectSourceKind.Tae), (300, 12, SpEffectSourceKind.Tae)).Run(Act01, 1000, new Random(9));
        Assert.True(stats.Elapsed.TotalMilliseconds < 300, $"{stats.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public void Diagnostics_Timing_NotObserved_WrongTarget()
    {
        var h = Table.Handlers[100];
        Assert.Contains(ComboDiagnostics.Warnings(Table, new InterruptCandidate(100, 25, 30, h), 20, hasNext: true), w => w.StartsWith("never fires"));
        Assert.Empty(ComboDiagnostics.Warnings(Table, new InterruptCandidate(100, 25, 30, h), 20, hasNext: false));
        Assert.Empty(ComboDiagnostics.Warnings(Table, new InterruptCandidate(100, 10, 30, h), 20, hasNext: true));

        var noObserve = SpEffectInterrupts.Build(Src.Replace("arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 100)", ""));
        Assert.Contains(ComboDiagnostics.Warnings(noObserve, new InterruptCandidate(100, 0, 1, h), null, false), w => w.StartsWith("not observed"));

        Assert.Contains(ComboDiagnostics.Warnings(Table, new InterruptCandidate(100, 0, 1, h, SpEffectSourceKind.OnHit), null, false), w => w.Contains("needs TARGET_ENE_0"));
        Assert.Contains(ComboDiagnostics.Warnings(Table, new InterruptCandidate(400, 0, 1, Table.Handlers[400]), null, false), w => w.Contains("needs TARGET_SELF"));
        Assert.Empty(ComboDiagnostics.Warnings(Table, new InterruptCandidate(400, 0, 1, Table.Handlers[400], SpEffectSourceKind.OnHit), null, false));
    }
}

public class BehaviorSpEffectsTests
{
    private sealed class FakeParams : IBehaviorParamSource
    {
        public Dictionary<long, BehaviorRow> Beh = new();
        public Dictionary<int, int[]> Atk = new();
        public Dictionary<int, BulletRow> Bullets = new();
        public BehaviorRow? Behavior(long id) => Beh.GetValueOrDefault(id);
        public IReadOnlyList<int>? AtkSpEffects(int atkId) => Atk.GetValueOrDefault(atkId);
        public BulletRow? Bullet(int id) => Bullets.GetValueOrDefault(id);
    }

    private static byte[] Judge(int offset, int judge)
    {
        var b = new byte[16];
        BitConverter.GetBytes(judge).CopyTo(b, offset);
        return b;
    }

    [Fact]
    public void NpcBehaviorId_FollowsDsasFormula()
    {
        Assert.Equal(250600100L, SekiroBehaviorEvents.BehaviorParamId(1, 100, 50600));
        Assert.Equal(12345L, SekiroBehaviorEvents.BehaviorParamId(5, 12345, 50600));
        Assert.Null(SekiroBehaviorEvents.BehaviorParamId(1, 100, null));
    }

    [Fact]
    public void Resolve_AttackBulletAndCommonBehavior()
    {
        var p = new FakeParams();
        p.Beh[250600100] = new BehaviorRow(BehaviorSpEffects.RefAttack, 5060100);
        p.Atk[5060100] = [7001, 7002];
        p.Beh[250600200] = new BehaviorRow(BehaviorSpEffects.RefBullet, 506000);
        p.Bullets[506000] = new BulletRow(5060200, [7003], 7004);
        p.Atk[5060200] = [7005];
        p.Beh[9999] = new BehaviorRow(BehaviorSpEffects.RefSpEffect, 7006);
        var events = new[]
        {
            new TaeEventData(SekiroBehaviorEvents.AttackBehavior, 10, 14, Judge(8, 100)),
            new TaeEventData(SekiroBehaviorEvents.Bullet, 20, 21, Judge(8, 200)),
            new TaeEventData(SekiroBehaviorEvents.CommonBehavior, 5, 6, Judge(4, 9999)),
            new TaeEventData(67, 1, 2, Judge(0, 1234)), // SpEffect event: not a behavior
        };
        var r = BehaviorSpEffects.Resolve(events, 50600, p);
        Assert.Equal([7006, 7001, 7002, 7003, 7005, 7004], r.Select(x => x.SpEffectId));
        Assert.Equal(SpEffectSourceKind.Behavior, r[0].Kind);
        Assert.All(r.Where(x => x.SpEffectId is 7001 or 7002 or 7003 or 7005), x => Assert.Equal(SpEffectSourceKind.OnHit, x.Kind));
        Assert.Equal(SpEffectSourceKind.Behavior, r.Single(x => x.SpEffectId == 7004).Kind);
        Assert.Equal((10f, 14f), (r[1].Frame, r[1].EndFrame));
        Assert.Contains("AtkParam_Npc 5060100", r[1].Chain);
        Assert.Contains("Bullet 506000 → AtkParam_Npc 5060200", r.Single(x => x.SpEffectId == 7005).Chain);
    }
}
