using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Lua;

public class WeightTableTests
{
    private static WeightTable Table(string src, string fn) =>
        WeightTable.BuildAll(src).Single(w => w.Goal.Name == fn);

    private static readonly string Kengeki = KengekiRaw.ReplaceLineEndings("\n");

    private const string KengekiRaw = """
        Goal.Kengeki_Activate = function(a0, ai, goal, a3)
            local sp = ReturnKengekiSpecialEffect(ai)
            if sp == 0 then
                return false
            end
            local kengeki = {}
            local funcs = {}
            local p = {}
            Common_Clear_Param(kengeki, funcs, p)
            local d = ai:GetDist(TARGET_ENE_0)
            local r = ai:GetRandam_Int(1, 100)
            if ai:GetNumber(3) == 1 then
                kengeki[9] = 100
            elseif sp == 200210 then
                if d >= 3 then
                    kengeki[26] = 100
                else
                    kengeki[1] = 100
                    kengeki[12] = 300
                end
            elseif sp == 200211 then
                if r <= 80 then
                    kengeki[2] = 100
                else
                    kengeki[3] = 100
                end
            end
            kengeki[12] = SetCoolTime(ai, goal, 3005, 4, kengeki[12], 1)
            funcs[1] = REGIST_FUNC(ai, goal, a0.Kengeki01)
            funcs[2] = REGIST_FUNC(ai, goal, a0.Kengeki02)
            funcs[3] = REGIST_FUNC(ai, goal, a0.Kengeki03)
            funcs[12] = REGIST_FUNC(ai, goal, a0.Kengeki12)
            funcs[26] = REGIST_FUNC(ai, goal, a0.Kengeki26)
            funcs[26] = REGIST_FUNC(ai, goal, a0.NoAction)
            return Common_Kengeki_Activate(ai, goal, kengeki, funcs, nil, p)
        end
        """;

    private static Dictionary<int, double> Chances(WeightEvaluation e) =>
        e.Rows.GroupBy(r => r.Slot).ToDictionary(g => g.Key, g => Math.Round(g.Sum(r => r.Chance), 3));

    [Fact]
    public void Kengeki_Structure()
    {
        var w = Table(Kengeki, "Kengeki_Activate");
        Assert.Equal("kengeki", w.TableVar);
        Assert.Equal("funcs", w.FuncVar);
        Assert.Equal("sp", w.SelectorVar);
        Assert.Equal("Common_Kengeki_Activate", w.Picker);
        Assert.Equal([200210, 200211], w.SelectorValues);
        Assert.Equal([3d], w.DistanceThresholds);
        Assert.True(w.UsesRandom);
        Assert.Equal("NoAction", w.Slots[26].Function);
        Assert.Contains(w.Warnings, x => x.Contains("registered twice") && x.Contains("Kengeki26"));
        Assert.Contains(w.Warnings, x => x.Contains("kengeki[9]"));
        Assert.Contains(w.Atoms, a => a.Key == "ai:GetNumber(3) == 1");
    }

    [Fact]
    public void Kengeki_Evaluate_BySelectorDistanceRandomAndToggle()
    {
        var w = Table(Kengeki, "Kengeki_Activate");
        var s = new WeightScenario { Selector = 200210, Distance = 1 };
        var e = w.Evaluate(s);
        Assert.Equal(new Dictionary<int, double> { [1] = 25, [12] = 75 }, Chances(e));
        Assert.Equal("else", e.CaseArm!.Text);
        Assert.Equal([(3005, 4d)], e.Rows.Single(r => r.Slot == 12).Cooldowns);

        s.Distance = 5;
        e = w.Evaluate(s);
        Assert.Equal(new Dictionary<int, double> { [26] = 100 }, Chances(e));
        Assert.Equal("NoAction", e.Rows.Single().Function);

        s.Selector = 200211;
        Assert.Equal(new Dictionary<int, double> { [2] = 80, [3] = 20 }, Chances(w.Evaluate(s)));
        // The case arm is the selector arm (the random arms below it differ per roll).
        Assert.Equal("sp == 200211", w.Evaluate(s).CaseArm!.Text);

        s.TrueAtoms.Add("ai:GetNumber(3) == 1");
        Assert.Equal(new Dictionary<int, double> { [9] = 100 }, Chances(w.Evaluate(s)));

        // No kengeki SpEffect: the function returns before any weight.
        var none = w.Evaluate(new WeightScenario { Selector = null });
        Assert.True(none.Returned);
        Assert.Empty(none.Rows);
    }

    [Fact]
    public void Edits_SetWeight_Remove_Add_AddCase_SetSelector()
    {
        var w = Table(Kengeki, "Kengeki_Activate");
        var e = w.Evaluate(new WeightScenario { Selector = 200210, Distance = 1 });
        var row12 = e.Rows.Single(r => r.Slot == 12);

        string s1 = TextEdits.Apply(Kengeki, WeightTable.SetWeight(row12.Source!, 50));
        Assert.Contains("            kengeki[12] = 50\n", s1);
        var e1 = Table(s1, "Kengeki_Activate").Evaluate(new WeightScenario { Selector = 200210, Distance = 1 });
        Assert.Equal(new Dictionary<int, double> { [1] = 66.667, [12] = 33.333 }, Chances(e1));

        // 50% for slot 1 with slot 12 at 300 -> weight 300
        Assert.Equal(300, WeightTable.WeightForChance(e, e.Rows.Single(r => r.Slot == 1), 50));

        string s2 = TextEdits.Apply(Kengeki, w.RemoveAssign(Kengeki, row12.Source!));
        Assert.DoesNotContain("kengeki[12] = 300", s2);
        Assert.Equal(Kengeki.Split('\n').Length - 1, s2.Split('\n').Length);

        string s3 = TextEdits.Apply(Kengeki, w.AddAssign(Kengeki, e.CaseArm!, 3, 150));
        Assert.Contains("            kengeki[12] = 300\n            kengeki[3] = 150\n", s3);
        Assert.Equal(new Dictionary<int, double> { [1] = 100 / 5.5, [3] = 150 / 5.5, [12] = 300 / 5.5 }.ToDictionary(k => k.Key, k => Math.Round(k.Value, 3)),
            Chances(Table(s3, "Kengeki_Activate").Evaluate(new WeightScenario { Selector = 200210, Distance = 1 })));

        string s4 = TextEdits.Apply(Kengeki, w.AddSelectorCase(Kengeki, 200215, 2, 100)!);
        Assert.Contains("    elseif sp == 200215 then\n        kengeki[2] = 100\n    end\n    kengeki[12] = SetCoolTime", s4);
        var w4 = Table(s4, "Kengeki_Activate");
        Assert.Equal([200210, 200211, 200215], w4.SelectorValues);
        Assert.Equal(new Dictionary<int, double> { [2] = 100 }, Chances(w4.Evaluate(new WeightScenario { Selector = 200215 })));

        var arm = w.SelectorArm(200211)!.Value;
        string s5 = TextEdits.Apply(Kengeki, WeightTable.SetSelectorValue(arm.Compare, 200216));
        Assert.Contains("elseif sp == 200216 then", s5);
        Assert.DoesNotContain("200211", s5);
    }

    [Fact]
    public void AddSelectorCase_GoesBeforeElse()
    {
        const string srcRaw = """
            Goal.Kengeki_Activate = function(a0, ai, goal)
                local sp = ReturnKengekiSpecialEffect(ai)
                local k = {}
                local f = {}
                if sp == 200200 then
                    k[1] = 100
                else
                    k[2] = 100
                end
                return Common_Kengeki_Activate(ai, goal, k, f, nil, nil)
            end
            """;
        string src = srcRaw.ReplaceLineEndings("\n");
        var w = Table(src, "Kengeki_Activate");
        string s = TextEdits.Apply(src, w.AddSelectorCase(src, 200201, 3, 50)!);
        Assert.Contains("        k[1] = 100\n    elseif sp == 200201 then\n        k[3] = 50\n    else\n", s);
    }

    [Fact]
    public void Conditions_NotAndParensAndReversedCompare()
    {
        const string src = """
            Goal.Activate = function(a0, ai, goal)
                local act = {}
                local f = {}
                local d = ai:GetDist(TARGET_ENE_0)
                local hp = ai:GetHpRate(TARGET_SELF)
                if (not ai:HasSpecialEffectId(TARGET_SELF, 5)) and 4 > d then
                    act[1] = 100
                elseif hp <= 0.5 or ai:GetDist(TARGET_ENE_0) >= 8 then
                    act[2] = 100
                else
                    act[3] = 100
                end
                Common_Battle_Activate(ai, goal, act, f, nil, nil)
            end
            """;
        var w = Table(src, "Activate");
        Assert.True(w.UsesHp);
        Assert.Equal([4d, 8d], w.DistanceThresholds);
        Assert.Equal([1], w.Evaluate(new WeightScenario { Distance = 2 }).Rows.Select(r => r.Slot));
        Assert.Equal([3], w.Evaluate(new WeightScenario { Distance = 5 }).Rows.Select(r => r.Slot));
        Assert.Equal([2], w.Evaluate(new WeightScenario { Distance = 9 }).Rows.Select(r => r.Slot));
        Assert.Equal([2], w.Evaluate(new WeightScenario { Distance = 5, HpRate = 0.3 }).Rows.Select(r => r.Slot));
        var s = new WeightScenario { Distance = 2 };
        s.TrueAtoms.Add("ai:HasSpecialEffectId(TARGET_SELF, 5)");
        Assert.Equal([3], w.Evaluate(s).Rows.Select(r => r.Slot));
    }

    // ---------------------------------------------------------------- real scripts

    [RequiresTestDataFact]
    public void Owl_Activate_FarAndNear()
    {
        string src = File.ReadAllText(Samples.OwlBattle);
        var w = Table(src, "Activate");
        Assert.Equal("act", w.TableVar);
        Assert.Equal("Common_Battle_Activate", w.Picker);
        Assert.Null(w.SelectorVar);
        Assert.Equal("Act35", w.Slots[35].Function);
        Assert.Contains(10d, w.DistanceThresholds);

        var far = w.Evaluate(new WeightScenario { Distance = 12 });
        Assert.False(far.Returned);
        Assert.Equal(2750, far.TotalWeight);
        Assert.Equal(Math.Round(500 / 2750.0 * 100, 3), Chances(far)[35]);
        Assert.Equal("getDist >= 10", far.CaseArm!.Text);
        Assert.Equal(Enumerable.Range(0, 0), far.Rows.Where(r => r.Function == null).Select(r => r.Slot));

        var near = w.Evaluate(new WeightScenario { Distance = 1 });
        Assert.Equal(400, near.Rows.Single(r => r.Slot == 1).Weight);
        Assert.Equal("else", near.CaseArm!.Text);
    }

    [RequiresTestDataFact]
    public void Owl_Kengeki_StrongParryRight()
    {
        string src = File.ReadAllText(Samples.OwlBattle);
        var w = Table(src, "Kengeki_Activate");
        Assert.Equal("f56_local0", w.SelectorVar);
        Assert.Equal([200226, 200210, 200211, 200200, 200201, 200215, 200216], w.SelectorValues);
        var near = w.Evaluate(new WeightScenario { Selector = 200210, Distance = 1 });
        Assert.Equal(new Dictionary<int, double> { [1] = 10, [5] = 10, [12] = 30, [15] = 10, [27] = 10, [31] = 30 }, Chances(near));
        Assert.Equal("Act04", near.Rows.Single(r => r.Slot == 15).Function); // slot 15 is re-registered to Act04
        var far = w.Evaluate(new WeightScenario { Selector = 200210, Distance = 4 });
        Assert.Equal("NoAction", far.Rows.Single().Function);
        Assert.Contains(w.Warnings, x => x.Contains("[14] is registered twice"));
    }

    [RequiresTestDataFact]
    public void Perf_BuildAndEvaluate_AreCheap()
    {
        string src = File.ReadAllText(Samples.TrueMonkBattle);
        for (int i = 0; i < 3; i++) WeightTable.BuildFor(src, "Kengeki_Activate")!.Evaluate(new WeightScenario { Selector = 200200 }); // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int n = 20;
        WeightTable? w = null;
        for (int i = 0; i < n; i++) w = WeightTable.BuildFor(src, "Kengeki_Activate");
        double build = sw.Elapsed.TotalMilliseconds / n;
        sw.Restart();
        for (int i = 0; i < n; i++) w!.Evaluate(new WeightScenario { Selector = 200200, Distance = 5 }); // 100 random rolls
        double eval = sw.Elapsed.TotalMilliseconds / n;
        Assert.True(build < 50, $"build {build:0.0} ms");
        Assert.True(eval < 10, $"evaluate {eval:0.00} ms");
    }

    [RequiresTestDataFact]
    public void TrueMonk_Kengeki_RandomAndBooleanLocal()
    {
        string src = File.ReadAllText(Samples.TrueMonkBattle);
        var w = Table(src, "Kengeki_Activate");
        Assert.Equal("kengekiEffect", w.SelectorVar);
        Assert.Equal("probabilities", w.TableVar);
        var s = new WeightScenario { Selector = 200200, Distance = 5 };
        Assert.Equal(new Dictionary<int, double> { [18] = 100 }, Chances(w.Evaluate(s)));
        s.TrueAtoms.Add("hasEffect200031");
        Assert.Equal(new Dictionary<int, double> { [17] = 80, [18] = 20 }, Chances(w.Evaluate(s)));
        Assert.Contains(w.Atoms, a => a.Key == "hasEffect200031" && a.Hint!.Contains("HasSpecialEffectId(TARGET_SELF, 200031)"));
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.KeyBattleScripts), MemberType = typeof(LuaCorpus))]
    public void KeyScripts_HaveActivateAndKengekiTables(string rel)
    {
        string src = LuaCorpus.ReadText(rel);
        var all = WeightTable.BuildAll(src);
        Assert.Contains(all, w => w.Goal.Name == "Activate");
        Assert.Contains(all, w => w.Goal.Name == "Kengeki_Activate");
        foreach (var w in all)
            foreach (double d in new[] { 0.5, 2, 4, 6, 9, 15 })
            {
                var e = w.Evaluate(new WeightScenario { Distance = d, Selector = w.SelectorValues.FirstOrDefault() });
                if (e.Rows.Count > 0) Assert.InRange(e.Rows.Sum(r => r.Chance), 99.9, 100.1);
            }
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.PlainText), MemberType = typeof(LuaCorpus))]
    public void Corpus_BuildsWithoutThrowing(string rel)
    {
        string src = LuaCorpus.ReadText(rel);
        foreach (var w in WeightTable.BuildAll(src))
            w.Evaluate(new WeightScenario());
    }
}
