using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Lua;

public class SpEffectHandlerWriterTests
{
    private const string Script =
        "Goal.Activate = function(self, ai, goal)\n" +
        "    ai:AddObserveSpecialEffectAttribute(TARGET_SELF, 5025)\n" +
        "    ai:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 9493)\n" +
        "    goal:AddSubGoal(GOAL_COMMON_Attack, 10, 3000, TARGET_ENE_0, 999, 0)\n" +
        "end\n" +
        "Goal.Interrupt = function(arg0, arg1, arg2)\n" +
        "    local sp = arg1:GetSpecialEffectActivateInterruptType(0)\n" +
        "    if arg1:IsInterupt(INTERUPT_ActivateSpecialEffect) then\n" +
        "        if sp == 5025 then\n" +
        "            arg2:ClearSubGoal()\n" +
        "            arg2:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3041, TARGET_ENE_0, 999, 0)\n" +
        "            return true\n" +
        "        elseif sp == 9493 then\n" +
        "            if sp == 1 then\n" +
        "            end\n" +
        "            arg1:Replanning()\n" +
        "            return true\n" +
        "        end\n" +
        "    end\n" +
        "    return false\n" +
        "end\n";

    [Fact]
    public void AddsArmAtEndOfChainAndObserveAfterSameTarget()
    {
        var r = SpEffectHandlerWriter.Add(Script, 5060040, SpEffectHandlerWriter.TargetSelf);
        Assert.Null(r.Error);
        Assert.True(r.AddedHandler);
        Assert.True(r.AddedObserve);
        var lines = r.Text.Split('\n');
        Assert.Equal("    ai:AddObserveSpecialEffectAttribute(TARGET_SELF, 5060040)", lines[2]); // after the TARGET_SELF one
        int arm = Array.IndexOf(lines, "        elseif sp == 5060040 then");
        Assert.True(arm > 0);
        Assert.Equal("            arg2:ClearSubGoal()", lines[arm + 1]);
        Assert.Equal("            ", lines[arm + 2]);
        Assert.Equal("            return true", lines[arm + 3]);
        Assert.Equal("        end", lines[arm + 4]);
        Assert.Equal(arm + 2, r.CaretLine);
        Assert.Equal(12, r.CaretColumn);

        var table = SpEffectInterrupts.Build(r.Text);
        Assert.True(table.Handlers[5060040].ClearsQueue);
        Assert.Contains(SpEffectHandlerWriter.TargetSelf, table.ObservedTargets[5060040]);
        Assert.Equal(GoalOutline.Build(Script).Count, GoalOutline.Build(r.Text).Count);
    }

    [Fact]
    public void InsertsBeforeElse()
    {
        string src = Script.Replace("        elseif sp == 9493 then", "        else");
        var r = SpEffectHandlerWriter.Add(src, 77, SpEffectHandlerWriter.TargetEnemy);
        var lines = r.Text.Split('\n');
        int arm = Array.IndexOf(lines, "        elseif sp == 77 then");
        Assert.Equal("        else", lines[arm + 4]);
        Assert.Equal("    ai:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 77)", lines[3]);
    }

    [Fact]
    public void ExistingHandlerOnlyAddsMissingObserve()
    {
        var r = SpEffectHandlerWriter.Add(Script, 9493, SpEffectHandlerWriter.TargetSelf);
        Assert.False(r.AddedHandler);
        Assert.True(r.AddedObserve);
        Assert.Equal("        elseif sp == 9493 then", r.Text.Split('\n')[r.CaretLine]);

        var none = SpEffectHandlerWriter.Add(Script, 5025, SpEffectHandlerWriter.TargetSelf);
        Assert.False(none.Changed);
        Assert.Equal(Script, none.Text);
        Assert.Equal(8, none.CaretLine);
    }

    [Fact]
    public void KeepsCrlf()
    {
        string crlf = Script.Replace("\n", "\r\n");
        var r = SpEffectHandlerWriter.Add(crlf, 42, SpEffectHandlerWriter.TargetSelf);
        Assert.DoesNotContain("\n", r.Text.Replace("\r\n", ""));
    }

    [Fact]
    public void CreatesBlockWhenNoChain()
    {
        const string src =
            "Goal.Activate = function(a, b, c)\n" +
            "    c:AddSubGoal(GOAL_COMMON_Attack, 10, 3000, TARGET_ENE_0, 999, 0)\n" +
            "end\n" +
            "Goal.Interrupt = function(a, b, c)\n" +
            "    return false\n" +
            "end\n";
        var r = SpEffectHandlerWriter.Add(src, 123, SpEffectHandlerWriter.TargetEnemy);
        Assert.Null(r.Error);
        var table = SpEffectInterrupts.Build(r.Text);
        Assert.True(table.Handlers.ContainsKey(123));
        Assert.Contains(SpEffectHandlerWriter.TargetEnemy, table.ObservedTargets[123]);
        Assert.Contains("    b:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 123)", r.Text);
        Assert.EndsWith("    return false\nend\n", r.Text);
    }

    [Fact]
    public void NoInterruptIsAnError()
    {
        var r = SpEffectHandlerWriter.Add("Goal.Activate = function(a, b, c)\nend\n", 1, SpEffectHandlerWriter.TargetSelf);
        Assert.NotNull(r.Error);
        Assert.False(r.Changed);
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.PlainText), MemberType = typeof(LuaCorpus))]
    public void Corpus_AddedHandlerIsFound(string relPath)
    {
        string src = LuaCorpus.ReadText(relPath);
        var before = SpEffectInterrupts.Build(src);
        if (!GoalOutline.Build(src).Any(g => g.Name == "Interrupt") || !GoalOutline.Build(src).Any(g => g.Name == "Activate")) return;
        var r = SpEffectHandlerWriter.Add(src, 987654321, SpEffectHandlerWriter.TargetSelf);
        Assert.Null(r.Error);
        var after = SpEffectInterrupts.Build(r.Text);
        Assert.True(after.Handlers.ContainsKey(987654321), relPath);
        Assert.True(after.Handlers[987654321].ClearsQueue);
        Assert.Contains(SpEffectHandlerWriter.TargetSelf, after.ObservedTargets[987654321]);
        Assert.Equal(before.Handlers.Count + 1, after.Handlers.Count);
        GoalOutlineTests.AssertOutlineInvariants(relPath, GoalOutline.Build(r.Text));
        Assert.Equal(GoalOutline.Build(src).Select(g => g.Name), GoalOutline.Build(r.Text).Select(g => g.Name));
        Assert.Equal(string.Empty, r.Text.Split('\n')[r.CaretLine].Trim());
    }
}
