using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Lua;

public class HotReloadStubTests
{
    private const string Script =
        "Goal.Activate = function(self, ai, goal)\n" +
        "    goal:AddSubGoal(GOAL_COMMON_Attack, 10, 3000, TARGET_ENE_0, 999, 0)\n" +
        "end\n" +
        "Goal.Interrupt = function(arg0, arg1, arg2)\n" +
        "    return false\n" +
        "end\n" +
        "Goal.Parry = function(f48_arg0, f48_arg1, f48_arg2, f48_arg3)\n" +
        "    local parryDist = f48_arg0:GetDist(TARGET_ENE_0)\n" +
        "    return false\n" +
        "end\n" +
        "Goal.ShootReaction = function(f40_arg0, f40_arg1)\n" +
        "    f40_arg1:ClearSubGoal()\n" +
        "    return true\n" +
        "end\n";

    private const string Lua = @"C:\Mods\x\script\sas_hotreload\m11_02_00_00\540300_battle.lua";
    private const string Ver = @"C:\Mods\x\script\sas_hotreload\m11_02_00_00\540300_battle.version";

    private static List<int> Begins(string text)
    {
        var lines = text.Split('\n');
        return Enumerable.Range(0, lines.Length).Where(i => lines[i].TrimStart().StartsWith(HotReloadStub.BeginMarker)).ToList();
    }

    [Fact]
    public void InsertsAtTopOfParryAndShootReaction_NotInterrupt()
    {
        var r = HotReloadStub.Insert(Script, Lua, Ver, HotReloadStub.GlobalName("540300_battle.lua"));
        Assert.Null(r.Error);
        var lines = r.Text.Split('\n').ToList();
        var begins = Begins(r.Text);
        Assert.Equal(2, begins.Count);
        Assert.Equal(7, r.Line);
        Assert.StartsWith("Goal.Parry", lines[begins[0] - 1]);
        Assert.StartsWith("Goal.ShootReaction", lines[begins[1] - 1]);
        Assert.Equal("    do -- reloads this script once after each Save", lines[begins[0] + 1]);
        Assert.Equal("    do -- reloads this script once after each Save", lines[begins[1] + 1]);
        Assert.DoesNotContain("109200", r.Text);
        Assert.Equal("    local parryDist = f48_arg0:GetDist(TARGET_ENE_0)", lines[lines.FindIndex(l => l.Contains(HotReloadStub.EndMarker)) + 1]);
        Assert.Equal("Goal.Interrupt = function(arg0, arg1, arg2)", lines[3]);
        Assert.Equal("    return false", lines[4]); // Interrupt untouched
        Assert.Contains(@"loadfile(""C:\\Mods\\x\\script\\sas_hotreload\\m11_02_00_00\\540300_battle.version"")", r.Text);
        Assert.Contains("SAS_HOT_RELOAD_540300_battle = sasHotVer", r.Text);
        Assert.Equal(GoalOutline.Build(Script).Select(g => g.Name), GoalOutline.Build(r.Text).Select(g => g.Name));
    }

    [Fact]
    public void OnlyShootReaction_GetsOneBlock()
    {
        string src = "Goal.ShootReaction = function(a, b)\n    return true\nend\n";
        var r = HotReloadStub.Insert(src, Lua, Ver, "G");
        Assert.Null(r.Error);
        Assert.Single(Begins(r.Text));
        Assert.Equal(src, HotReloadStub.Remove(r.Text).Text);
    }

    [Fact]
    public void RemoveRestoresOriginal_AndReinsertReplaces()
    {
        var a = HotReloadStub.Insert(Script, Lua, Ver, "G");
        var b = HotReloadStub.Insert(a.Text, Lua, Ver, "G");
        Assert.Equal(2, Begins(b.Text).Count);
        Assert.Equal(Script, HotReloadStub.Remove(b.Text).Text);
        Assert.Equal(Script, HotReloadStub.Remove(Script).Text);
        Assert.False(HotReloadStub.Has(Script));
    }

    [Fact]
    public void KeepsCrlf()
    {
        string crlf = Script.Replace("\n", "\r\n");
        var r = HotReloadStub.Insert(crlf, Lua, Ver, "G");
        Assert.DoesNotContain("\n", r.Text.Replace("\r\n", ""));
        Assert.Equal(crlf, HotReloadStub.Remove(r.Text).Text);
    }

    [Fact]
    public void NoParryNorShootReaction_Fails()
    {
        var r = HotReloadStub.Insert("Goal.Interrupt = function(a, b, c)\nend\n", Lua, Ver, "G");
        Assert.True(r.Failed);
    }

    [Fact]
    public void MissingEndMarker_FailsWithoutChange()
    {
        string broken = Script.Replace("    local parryDist", "    " + HotReloadStub.BeginMarker + "\n    local parryDist");
        var r = HotReloadStub.Remove(broken);
        Assert.True(r.Failed);
        Assert.Equal(broken, r.Text);
    }

    [Theory]
    [InlineData("540300_battle.lua", "SAS_HOT_RELOAD_540300_battle")]
    [InlineData("000120-platoon.lua", "SAS_HOT_RELOAD_000120_platoon")]
    public void GlobalNames(string file, string expected) => Assert.Equal(expected, HotReloadStub.GlobalName(file));

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.PlainText), MemberType = typeof(LuaCorpus))]
    public void Corpus_InsertKeepsStructure_RemoveRoundTrips(string relPath)
    {
        string src = LuaCorpus.ReadText(relPath);
        int hosts = GoalOutline.Build(src).Count(g => HotReloadStub.HostGoals.Contains(g.Name));
        if (hosts == 0) return;
        var r = HotReloadStub.Insert(src, Lua, Ver, "G");
        Assert.Null(r.Error);
        Assert.Equal(hosts, Begins(r.Text).Count);
        Assert.Equal(GoalOutline.Build(src).Select(g => g.Name), GoalOutline.Build(r.Text).Select(g => g.Name));
        Assert.Equal(SpEffectInterrupts.Build(src).Handlers.Keys.OrderBy(k => k), SpEffectInterrupts.Build(r.Text).Handlers.Keys.OrderBy(k => k));
        Assert.Equal(src, HotReloadStub.Remove(r.Text).Text);
    }
}
