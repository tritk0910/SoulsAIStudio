using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Lua;

public class ComboSequenceTests
{
    private static IReadOnlyList<ComboNode> Build(string src, string goal) =>
        ComboSequence.Build(src, GoalOutline.Build(src).Single(g => g.Name == goal));

    [Fact]
    public void Kengeki03_FromUserExample_RandomBranch5050()
    {
        const string src = """
            Goal.Kengeki03 = function(f59_arg0, f59_arg1, f59_arg2)
                local f45_local7 = f59_arg0:GetRandam_Int(1, 100)
                f59_arg1:ClearSubGoal()
                f59_arg1:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0)
                if f45_local7 <= 50 then
                    f59_arg1:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3016, TARGET_ENE_0, 9999, 0, 0)
                else
                    f59_arg1:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3048, TARGET_ENE_0, 9999, 0, 0)
                end
            end
            """;
        var nodes = Build(src, "Kengeki03");
        var branch = Assert.Single(ComboSequence.Branches(nodes));
        Assert.Equal([50d, 50d], branch.Arms.Select(a => a.Probability!.Value));
        Assert.Equal([3002, 3016], ComboSequence.Flatten(nodes, _ => 0).Select(s => s.AnimId));
        Assert.Equal([3002, 3048], ComboSequence.Flatten(nodes, _ => 1).Select(s => s.AnimId));
        Assert.All(ComboSequence.Flatten(nodes, _ => 0), s => Assert.Equal("GOAL_COMMON_ComboRepeat", s.GoalType));
    }

    [Fact]
    public void FourWayRandom_NoElse_And_NonRandomCondition()
    {
        const string src = """
            Goal.X = function(a, ai, goal)
                local r = ai:GetRandam_Int(1, 100)
                if r <= 25 then goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3037, TARGET_ENE_0, 999, 0, 0)
                elseif r <= 50 then goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3018, TARGET_ENE_0, 999, 0, 0)
                elseif r <= 75 then goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3047, TARGET_ENE_0, 999, 0, 0)
                end
                if ai:GetNinsatsuNum() <= 1 then
                    goal:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3044, TARGET_ENE_0, 999, 0, 0)
                end
                while false do goal:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 9999, TARGET_ENE_0, 999, 0, 0) end
                goal:AddSubGoal(GOAL_COMMON_Wait, 1, TARGET_ENE_0)
            end
            """;
        var branches = ComboSequence.Branches(Build(src, "X")).ToList();
        Assert.Equal(2, branches.Count);
        Assert.Equal([25d, 25d, 25d, 25d], branches[0].Arms.Select(a => a.Probability!.Value));
        Assert.Equal("(no branch taken)", branches[0].Arms[^1].Condition);
        Assert.All(branches[1].Arms, a => Assert.Null(a.Probability));
        Assert.Contains("GetNinsatsuNum", branches[1].Arms[0].Condition);
        // Loop body skipped; Wait has no literal anim.
        Assert.Equal([3047, 3044], ComboSequence.Flatten(Build(src, "X"), b => b.Line == branches[0].Line ? 2 : 0).Select(s => s.AnimId));
    }

    [Fact]
    public void ClearSubGoal_ResetsQueuedSteps()
    {
        const string src = """
            Goal.Y = function(a, ai, goal)
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, 3000, TARGET_ENE_0, 0, 0)
                goal:ClearSubGoal()
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, 3001, TARGET_ENE_0, 0, 0)
            end
            """;
        Assert.Equal([3001], ComboSequence.Flatten(Build(src, "Y"), _ => 0).Select(s => s.AnimId));
    }

    [RequiresTestDataFact]
    public void Owl_EveryGoal_BuildsWithoutThrowing_AndActsHaveSteps()
    {
        string src = File.ReadAllText(Samples.OwlBattle);
        var goals = GoalOutline.Build(src);
        foreach (var g in goals)
        {
            var nodes = ComboSequence.Build(src, g);
            var flat = ComboSequence.Flatten(nodes, _ => 0);
            Assert.All(flat, s => Assert.InRange(s.Line, g.Line, g.EndLine));
        }
        var act50 = ComboSequence.Build(src, goals.Single(x => x.Name == "Act50"));
        Assert.Equal([3041], ComboSequence.Flatten(act50, _ => 0).Select(s => s.AnimId));
    }
}

public class ComboSequenceLocalsTests
{
    private const string TrueMonkAct05 = """
        Goal.Act05 = function (ai, goal, paramTbl)
            local distanceEnemy = ai:GetDist(TARGET_ENE_0)
            local stopDist = 7 - ai:GetMapHitRadius(TARGET_SELF)
            local goalLife = 3
            if stopDist < distanceEnemy then
                goal:AddSubGoal(GOAL_COMMON_ApproachTarget, goalLife, TARGET_ENE_0, stopDist, TARGET_SELF, walk, -1)
            end
            local animationId = 3010
            local animationId_2 = 3011
            local f8_local6 = 9 - ai:GetMapHitRadius(TARGET_SELF)
            local successDist = 9999
            goal:AddSubGoal(GOAL_COMMON_ComboTunable_SuccessAngle180, 10, animationId, TARGET_ENE_0, 9999, 0, 0, 0, 0)
            goal:AddSubGoal(GOAL_COMMON_ComboFinal, 10, animationId_2, TARGET_ENE_0, successDist, 0, 0)
            return 0
        end
        """;

    [Fact]
    public void AnimIdsFromNumericLocals_AreResolved()
    {
        var goal = GoalOutline.Build(TrueMonkAct05).Single(g => g.Name == "Act05");
        var steps = ComboSequence.Flatten(ComboSequence.Build(TrueMonkAct05, goal), _ => 0);
        Assert.Equal([3010, 3011], steps.Select(s => s.AnimId));
        Assert.Equal([3010, 3011], AnimReferences.Build(TrueMonkAct05).Select(a => a.AnimId));
    }

    [Fact]
    public void ExpressionsAndFieldAccess_AreNotTreatedAsConstants()
    {
        const string src = """
            Goal.Z = function (ai, goal)
                local a = 3000 + 5
                local b = 3020
                b = b
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, a, TARGET_ENE_0)
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, b, TARGET_ENE_0)
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, tbl.x, TARGET_ENE_0)
            end
            """;
        var goal = GoalOutline.Build(src).Single();
        Assert.Equal([3020], ComboSequence.Flatten(ComboSequence.Build(src, goal), _ => 0).Select(s => s.AnimId));
    }
}

public class SpEffectInterruptsTests
{
    [RequiresTestDataFact]
    public void Owl_InterruptTable_Has3506000_WithClearAndRandomBranches()
    {
        string src = File.ReadAllText(Samples.OwlBattle);
        var table = SpEffectInterrupts.Build(src);
        Assert.True(table.Handlers.Count > 10);
        var h = table.Handlers[3506000];
        Assert.True(h.ClearsQueue);
        Assert.NotEmpty(ComboSequence.Flatten(h.Body, _ => 0));
        Assert.Contains(3012, ComboSequence.Flatten(h.Body, _ => 0).Select(s => s.AnimId));
        Assert.True(table.Handlers[3506080].Replans);
        Assert.Equal("TARGET_ENE_0", table.Observed[110010]);
        Assert.Equal("TARGET_SELF", table.Observed[5030]);
    }

    [RequiresTestDataFact]
    public void Owl_ArmsWithOnlyNestedRandomSteps_AreCombos_NotReplans()
    {
        string src = File.ReadAllText(Samples.OwlBattle);
        var lines = src.Split('\n');
        var table = SpEffectInterrupts.Build(src);
        foreach (int id in new[] { 5060030, 5060040, 5060081, 5060029 })
        {
            var h = table.Handlers[id];
            Assert.False(h.Replans, $"{id} wrongly marked Replanning");
            Assert.Contains($"== {id}", lines[h.Line]); // line of its own arm, not of the if-chain
            Assert.NotEmpty(ComboSequence.Flatten(h.Body, _ => 0));
        }
        Assert.Equal([3060, 3061, 3016], ComboSequence.Flatten(table.Handlers[5060030].Body, _ => 0).Select(s => s.AnimId));
        Assert.True(table.Handlers[5060081].HasExtraCondition);
        Assert.False(table.Handlers[5060030].HasExtraCondition);
    }

    [Fact]
    public void ArmAfterReplanningArm_WithStepsOnlyInNestedBranch_IsNotReplans()
    {
        const string src = """
            Goal.Interrupt = function(arg0, arg1, arg2)
                local r = arg1:GetRandam_Int(1, 100)
                local sp = arg1:GetSpecialEffectActivateInterruptType(0)
                if sp == 1 then
                    arg1:Replanning()
                    return true
                elseif sp == 2 then
                    arg2:ClearSubGoal()
                    if r <= 50 then arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0, 999, 0, 0)
                    else arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 999, 0, 0)
                    end
                    return true
                end
                return false
            end
            """;
        var table = SpEffectInterrupts.Build(src);
        Assert.True(table.Handlers[1].Replans);
        Assert.Equal(3, table.Handlers[1].Line);
        Assert.False(table.Handlers[2].Replans);
        Assert.Equal(6, table.Handlers[2].Line);
    }

    [Fact]
    public void UserExample_5060040_FourWayRandom()
    {
        const string src = """
            Goal.Interrupt = function(arg0, arg1, arg2)
                local interruptRand = arg1:GetRandam_Int(1, 100)
                local interruptSpeffect = arg1:GetSpecialEffectActivateInterruptType(0)
                if arg1:IsInterupt(INTERUPT_ActivateSpecialEffect) then
                    if interruptSpeffect == 1 then
                        arg1:Replanning()
                        return true
                    elseif interruptSpeffect == 5060040 then
                        arg2:ClearSubGoal()
                        if interruptRand <= 25 then arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3037, TARGET_ENE_0, 999, 0, 0, 0, 0)
                        elseif interruptRand <= 50 then arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3018, TARGET_ENE_0, 999, 0, 0, 0, 0)
                        elseif interruptRand <= 75 then arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3047, TARGET_ENE_0, 999, 0, 0, 0, 0)
                        else arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3044, TARGET_ENE_0, 999, 0, 0, 0, 0)
                        end
                        return true
                    end
                end
                return false
            end
            """;
        var h = SpEffectInterrupts.Build(src).Handlers[5060040];
        Assert.True(h.ClearsQueue);
        Assert.Null(h.Chance);
        var branch = Assert.Single(ComboSequence.Branches(h.Body));
        Assert.Equal([25d, 25d, 25d, 25d], branch.Arms.Select(a => a.Probability!.Value));
        Assert.Equal([3044], ComboSequence.Flatten(h.Body, _ => 3).Select(s => s.AnimId));
    }
}

public class SpEffectInterruptChanceTests
{
    [RequiresTestDataFact]
    public void Owl_ConditionalArms_HaveRandomChance()
    {
        var table = SpEffectInterrupts.Build(File.ReadAllText(Samples.OwlBattle));
        Assert.Equal(75, table.Handlers[5060081].Chance);
        Assert.Equal(40, table.Handlers[5060029].Chance);
        Assert.Null(table.Handlers[5060030].Chance);
    }
}
