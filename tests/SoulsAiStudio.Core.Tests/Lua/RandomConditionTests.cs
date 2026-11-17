using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.Core.Tests.Lua;

public class RandomConditionTests
{
    static readonly HashSet<string> Rand = ["interruptRand", "interruptRand2", "r"];
    static readonly HashSet<string> Sp = ["interruptspeffect"];

    static (bool Ok, bool Unknown) Eval(string cond, Dictionary<string, int> values)
    {
        bool ok = RandomCondition.Evaluate(cond, Rand, Sp, v => values[v], out bool unknown);
        return (ok, unknown);
    }

    [Fact]
    public void RandomTests_UseTheGivenValue_SharedAcrossConditions()
    {
        var v = new Dictionary<string, int> { ["interruptRand"] = 30 };
        Assert.Equal((true, false), Eval("interruptspeffect == 3549652 and interruptRand <= 45", v));
        Assert.Equal((false, false), Eval("interruptRand <= 20", v));
        Assert.Equal((true, false), Eval("interruptRand <= 40", v)); // same value: the 2nd arm of the inner if
        Assert.Equal((true, false), Eval("( interruptRand < 31 )", v));
        Assert.Equal((true, false), Eval("50 >= interruptRand", v));
        Assert.Equal((false, false), Eval("interruptRand > 30", v));
    }

    [Fact]
    public void StatePartsAreUnknown_ButAFailingRandomPartStillDecides()
    {
        var v = new Dictionary<string, int> { ["interruptRand"] = 70 };
        Assert.Equal((true, true), Eval("interruptspeffect == 3549800 and arg1 : IsFinishTimer ( 8 ) == true", v));
        Assert.Equal((false, true), Eval("interruptspeffect == 3549796 and interruptRand <= 40 and arg1 : IsFinishTimer ( 8 ) == true", v));
        Assert.Equal((true, true), Eval("interruptRand <= 50 or interruptDist <= 3", v)); // 'or': unknown as a whole
        Assert.Equal((true, false), Eval("else", v));
        Assert.Equal((true, false), Eval("(no branch taken)", v));
    }

    [Fact]
    public void Roll_IsCalledOncePerVariable_ByTheCaller()
    {
        var values = new Dictionary<string, int>();
        int calls = 0;
        int Roll(string name)
        {
            if (!values.TryGetValue(name, out int x)) { calls++; values[name] = x = 10; }
            return x;
        }
        RandomCondition.Evaluate("r <= 25", Rand, Sp, Roll, out _);
        RandomCondition.Evaluate("r <= 50", Rand, Sp, Roll, out _);
        RandomCondition.Evaluate("interruptRand2 <= 50", Rand, Sp, Roll, out _);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Handler_OuterDistance_OnlyForTheArmItself()
    {
        const string src = """
            Goal.Interrupt = function(arg0, arg1, arg2)
                local sp = arg1:GetSpecialEffectActivateInterruptType(0)
                local dist = arg1:GetDist(TARGET_ENE_0)
                local rand = arg1:GetRandam_Int(1, 100)
                if arg1:IsInterupt(INTERUPT_ActivateSpecialEffect) then
                    if sp == 100 and dist <= 3 then
                        arg2:ClearSubGoal()
                        arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0, 9999, 0, 0)
                    elseif sp == 200 then
                        arg2:ClearSubGoal()
                        if dist <= 3 then
                            arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0)
                        else
                            arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3003, TARGET_ENE_0, 9999, 0, 0)
                        end
                    end
                end
            end
            """;
        var t = SpEffectInterrupts.Build(src.ReplaceLineEndings("\n"));
        Assert.True(t.Handlers[100].OuterDistance);
        Assert.False(t.Handlers[200].OuterDistance);  // distance only inside: Random path may still roll it
        Assert.True(t.Handlers[200].DependsOnDistance);
        Assert.Contains("rand", t.RandomVars);
        Assert.Contains("sp", t.SpEffectVars);
    }
}
