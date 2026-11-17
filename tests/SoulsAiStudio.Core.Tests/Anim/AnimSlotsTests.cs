using SoulsAiStudio.Core.Anim;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Anim;

public class AnimSlotsTests
{
    private static AnimEntryInfo Own(int id, string name = "") => new(id, AnimHeaderKind.Own, -1, name == "" ? AnimSlots.IdName(id) + ".hkt" : name);
    private static AnimEntryInfo Ovr(int id, int src) => new(id, AnimHeaderKind.OverrideHkx, src, "x");
    private static AnimEntryInfo Imp(int id, int src) => new(id, AnimHeaderKind.ImportOther, src, "x");

    [Fact]
    public void Swap_BothOwn_MovesEntriesAndHkx_RetargetsOthers_RenamesAutoLabels()
    {
        var entries = new[] { Own(3001), Own(3012, "x slash"), Ovr(3040, 3001), Imp(3041, 3012), Ovr(3042, 3005), Own(3005) };
        var plan = AnimSlots.PlanSwap(entries, new HashSet<int> { 3001, 3012, 3005 }, 3001, 3012);
        Assert.Equal([(3001, 3012), (3012, 3001)], plan.EntryMoves);
        Assert.Equal([(3001, 3012), (3012, 3001)], plan.HkxMoves);
        Assert.Equal([
            new AnimRefChange(3040, AnimHeaderKind.OverrideHkx, 3001, 3012),
            new AnimRefChange(3041, AnimHeaderKind.ImportOther, 3012, 3001)], plan.RefChanges);
        // Auto label follows the id; custom "x slash" stays.
        Assert.Equal([(3012, "a000_003001.hkt", "a000_003012.hkt")], plan.NameChanges);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Swap_PairReferencesEachOther_StaysConsistent()
    {
        // 3001 overrides 3012's motion. After the swap the entry (now at 3012) must point at 3001, where 3012's HKX went.
        var entries = new[] { Ovr(3001, 3012), Own(3012) };
        var plan = AnimSlots.PlanSwap(entries, new HashSet<int> { 3012 }, 3001, 3012);
        Assert.Equal([new AnimRefChange(3012, AnimHeaderKind.OverrideHkx, 3012, 3001)], plan.RefChanges);
        Assert.Equal([(3012, 3001)], plan.HkxMoves);
    }

    [Fact]
    public void Swap_OnlyOneExists_IsARename()
    {
        var plan = AnimSlots.PlanSwap(new[] { Own(3001), Ovr(3050, 3001) }, new HashSet<int> { 3001 }, 3001, 3099);
        Assert.Equal([(3001, 3099)], plan.EntryMoves);
        Assert.Equal([(3001, 3099)], plan.HkxMoves);
        Assert.Equal(3099, Assert.Single(plan.RefChanges).NewRef);
    }

    [Fact]
    public void Swap_EntryWithoutHkx_Warns_DifferentCategories_Warns()
    {
        var plan = AnimSlots.PlanSwap(new[] { Own(3001), Own(100003001) }, new HashSet<int> { 100003001 }, 3001, 100003001);
        Assert.Contains(plan.Warnings, w => w.Contains("different categories"));
        Assert.Contains(plan.Warnings, w => w.Contains("no HKX file"));
        Assert.Throws<ArgumentException>(() => AnimSlots.PlanSwap(new[] { Own(3001) }, new HashSet<int>(), 3001, 3001));
        Assert.Throws<ArgumentException>(() => AnimSlots.PlanSwap(new[] { Own(3001) }, new HashSet<int>(), 3002, 3003));
    }

    [Fact]
    public void FreeIds()
    {
        var entries = new[] { Own(3001), Own(3002) };
        var hkx = new HashSet<int> { 3001, 3002, 3003 };
        Assert.Equal(3004, AnimSlots.NextFreeId(entries, hkx, 3001));
        Assert.NotNull(AnimSlots.WhyIdTaken(entries, hkx, 3003)); // orphan HKX
        Assert.Null(AnimSlots.WhyIdTaken(entries, hkx, 3010));
        Assert.Equal("a100_003001", AnimSlots.IdName(100003001));
        Assert.Equal(1_000_003_001, AnimSlots.HkxBinderId(3001));
        Assert.Null(AnimSlots.RenameAutoName("a000_0030010.hkt", 3001, 3012));
    }

    [Fact]
    public void CheckHkx_TagfileVersionAndCompendium()
    {
        byte[] Make(string ver, string tcrf)
        {
            var b = new List<byte> { 0, 0, 0xA8, 0x0C };
            b.AddRange("TAG0"u8.ToArray()); b.AddRange(new byte[] { 0x40, 0, 0, 0x10 }); b.AddRange("SDKV"u8.ToArray());
            b.AddRange(System.Text.Encoding.ASCII.GetBytes(ver)); b.AddRange(new byte[16]); b.AddRange("TCRF"u8.ToArray());
            b.AddRange(Convert.FromHexString(tcrf)); b.AddRange(new byte[8]);
            return b.ToArray();
        }
        var known = new HashSet<string> { "41d3a3c0ce9670cf" };
        Assert.True(AnimSlots.CheckHkx(Make("20160200", "41d3a3c0ce9670cf"), known).Ok);
        Assert.Contains(AnimSlots.CheckHkx(Make("20160200", "df9409d977e7b641"), known).Problems, p => p.Contains("compendium"));
        Assert.Contains(AnimSlots.CheckHkx(Make("20180100", "41d3a3c0ce9670cf"), known).Problems, p => p.Contains("20180100"));
        Assert.False(AnimSlots.CheckHkx(new byte[64], known).IsSekiroTagfile);
        Assert.Equal(["41d3a3c0ce9670cf"], AnimSlots.CompendiumIds([], [Make("20160200", "41d3a3c0ce9670cf")]));
    }

    private const string Lua = """
        Goal.Act01 = function(a, ai, goal)
            local animationId = 3012
            goal:AddSubGoal(GOAL_COMMON_ComboAttackTunableSpin, 10, 3001, TARGET_ENE_0, 9999, 0, 0)
            goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, animationId, TARGET_ENE_0, 9999, 0, 0)
            goal:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3002, TARGET_ENE_0, 9999, 0, 0)
            local x = 3001 + 1
        end
        Goal.Activate = function(a, ai, goal)
            act[1] = SetCoolTime(ai, goal, 3012, 5, act[1], 1)
        end
        """;

    [Fact]
    public void Lua_SwapsAddSubGoalLocalAndSetCoolTime_LeavesOtherNumbers()
    {
        string src = Lua.ReplaceLineEndings("\n");
        var lits = AnimSlots.FindLuaAnimLiterals(src);
        Assert.Equal([3012, 3001, 3002, 3012], lits.Select(l => l.Value));
        var edit = AnimSlots.SwapLuaAnimIds(src, 3001, 3012, out int n);
        Assert.Equal(3, n);
        string after = TextEdits.Apply(src, edit!);
        Assert.Contains("local animationId = 3001", after);
        Assert.Contains("10, 3012, TARGET_ENE_0", after);
        Assert.Contains("10, 3002, TARGET_ENE_0", after);
        Assert.Contains("local x = 3001 + 1", after); // not an anim id
        Assert.Contains("SetCoolTime(ai, goal, 3001, 5", after);
        Assert.Null(AnimSlots.SwapLuaAnimIds(src, 4000, 4001, out _));
    }

    [Fact]
    public void GuessLuaCategory_PicksBestCoverage_TieIsNotSure()
    {
        var tae = new HashSet<int> { 3000, 3001, 3002, 100_003000, 100_003001 };
        Assert.Equal((0, true), AnimSlots.GuessLuaCategory([3000, 3001, 3002], tae));       // 3002 only in a000
        Assert.Equal((0, false), AnimSlots.GuessLuaCategory([3000, 3001], tae));            // both have them: ask
        Assert.Equal((100, true), AnimSlots.GuessLuaCategory([3000, 3001], new HashSet<int> { 3000, 100_003000, 100_003001 }));
        Assert.Equal((0, true), AnimSlots.GuessLuaCategory([3000], new HashSet<int> { 3000, 3005 })); // one category
    }

    [RequiresTestDataFact]
    public void Lua_OwlScript_SwapTwiceIsIdentity()
    {
        string src = File.ReadAllText(Samples.OwlBattle).ReplaceLineEndings("\n");
        var once = TextEdits.Apply(src, AnimSlots.SwapLuaAnimIds(src, 3005, 3012, out int n)!);
        Assert.True(n > 3);
        var twice = TextEdits.Apply(once, AnimSlots.SwapLuaAnimIds(once, 3005, 3012, out _)!);
        Assert.Equal(src, twice);
    }
}
