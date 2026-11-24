using System.Reflection;
using ModelContextProtocol.Server;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.Lua;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsAiStudio.Mcp;

namespace SoulsAiStudio.Core.Tests.Mcp;

/// <summary>
/// Tool-level tests of the read-only MCP facade. Each test works on a COPY of an unpacked luabnd inside a temp dir
/// (test-data/original is never written); one entry of the copy is replaced with inline Lua.
/// </summary>
public sealed class McpToolsTests : IDisposable
{
    private const string Map = "m10_00_00_00";
    private const string File506 = "506300_battle.lua";

    private const string Lua = """
        Goal.Activate = function(arg0, arg1, arg2)
            arg1:AddObserveSpecialEffectAttribute(TARGET_SELF, 5060040)
            arg1:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 3506000)
        end

        Goal.Kengeki03 = function(ai, goal, _)
            local r = ai:GetRandam_Int(1, 100)
            goal:ClearSubGoal()
            goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0)
            if r <= 50 then
                goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3016, TARGET_ENE_0, 9999, 0, 0)
            else
                goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3048, TARGET_ENE_0, 9999, 0, 0)
            end
        end

        Goal.Act02 = function(ai, goal, _)
            local r = ai:GetRandam_Int(1, 100)
            if r <= 30 then
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, 3010, TARGET_ENE_0, 9999, 0)
            elseif r <= 60 then
                goal:AddSubGoal(GOAL_COMMON_Attack, 10, 3011, TARGET_ENE_0, 9999, 0)
            end
        end

        Goal.Interrupt = function(ai, goal)
            local interruptSpeffect = ai:GetSpecialEffectActivateInterruptType(0)
            local interruptRand = ai:GetRandam_Int(1, 100)
            if ai:IsInterupt(INTERUPT_ActivateSpecialEffect) then
                if interruptSpeffect == 5060040 then
                    goal:ClearSubGoal()
                    if interruptRand <= 25 then
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3037, TARGET_ENE_0, 9999, 0, 0)
                    elseif interruptRand <= 50 then
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3018, TARGET_ENE_0, 9999, 0, 0)
                    else
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3044, TARGET_ENE_0, 9999, 0, 0)
                    end
                elseif interruptSpeffect == 7777777 then
                    goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3100, TARGET_ENE_0, 9999, 0, 0)
                end
            end
            return false
        end
        """;

    private readonly ModLayout? _layout;
    private readonly AiProjectService _svc = new();
    private readonly string _projPath = "";
    private readonly Dictionary<string, string> _modHashBefore = [];

    public McpToolsTests()
    {
        if (!TestPaths.HasTestData) return; // tests skip themselves
        _layout = ModLayout.Create("mcp").ModUnpacked(Map);
        File.WriteAllText(_layout.ModUnpackedLua(Map, File506), Lua.Replace("\r\n", "\n") + "\n");
        var script = ModLayout.Script(Map, File506);
        script.Character = "c5060";
        script.DisplayName = "Owl";
        var project = SasProject.Create(Path.Combine(_layout.Work.Path, "proj", "t.sasproj"), "t", _layout.Game, _layout.Mod,
            [script], [new ProjectCharacter { ChrId = "c5060", DisplayName = "Owl" }]);
        _projPath = project.FilePath;
        _modHashBefore = _layout.HashMod();
    }

    public void Dispose()
    {
        _svc.Dispose();
        _layout?.Dispose();
    }

    [RequiresTestDataFact]
    public void ListEntities_And_ListScripts()
    {
        _svc.OpenProject(_projPath);
        var e = _svc.ListEntities();
        Assert.Equal("c5060", Assert.Single(e.Entities).ChrId);
        Assert.Equal(File506, Assert.Single(e.Entities[0].Scripts));
        var s = _svc.ListScripts("owl");
        var info = Assert.Single(s.Scripts);
        Assert.Equal("506300", info.NpcId);
        Assert.Equal("battle", info.Kind);
        Assert.Empty(_svc.ListScripts("zzz").Scripts);
    }

    [RequiresTestDataFact]
    public async Task ReadLua_ReturnsRangeWithOneBasedLines_AndTruncates()
    {
        _svc.OpenProject(_projPath);
        var r = await _svc.ReadLuaAsync("506300_battle");
        Assert.False(r.WasBytecode);
        Assert.Equal(1, r.StartLine);
        Assert.StartsWith("Goal.Activate", r.Text);
        var part = await _svc.ReadLuaAsync(File506, 2, 3);
        Assert.Equal(2, part.StartLine);
        Assert.Equal(3, part.EndLine);
        Assert.Contains("AddObserveSpecialEffectAttribute(TARGET_SELF", part.Text);
        Assert.Equal(2, part.Text.Split('\n').Length);

        // Long file: capped at MaxLinesPerRead.
        File.WriteAllText(_layout!.ModUnpackedLua(Map, File506), string.Join('\n', Enumerable.Range(1, 1000).Select(i => $"-- line {i}")) + "\n");
        using var svc2 = new AiProjectService();
        svc2.OpenProject(_projPath);
        var big = await svc2.ReadLuaAsync(File506);
        Assert.True(big.Truncated);
        Assert.Equal(1000, big.TotalLines);
        Assert.Equal(AiProjectService.MaxLinesPerRead, big.EndLine);
    }

    [RequiresTestDataFact]
    public async Task ListGoals_FindsGoalsWithOneBasedLines()
    {
        _svc.OpenProject(_projPath);
        var g = await _svc.ListGoalsAsync(File506);
        Assert.Equal(new[] { "Goal.Activate", "Goal.Kengeki03", "Goal.Act02", "Goal.Interrupt" }, g.Goals.Select(x => x.Name));
        var k = g.Goals.Single(x => x.Name == "Goal.Kengeki03");
        Assert.Equal(6, k.Line);
        Assert.Equal(3, k.AddSubGoalCount);
        Assert.Equal(new[] { 3002, 3016, 3048 }, k.AnimIds);
        Assert.Equal("Kengeki", k.Group);
    }

    [RequiresTestDataFact]
    public async Task SpEffectHandlers_ParseObserveAndBranches()
    {
        _svc.OpenProject(_projPath);
        var t = await _svc.GetSpEffectHandlersAsync(File506, null, true);
        Assert.Equal(new[] { 5060040, 7777777 }, t.Handlers.Select(h => h.SpEffectId));
        var h = t.Handlers[0];
        Assert.True(h.ClearsQueue);
        Assert.Contains("TARGET_SELF", h.ObservedOn);
        var branch = Assert.Single(h.Body, n => n.Type == "branch");
        Assert.Equal(3, branch.Arms!.Count);
        Assert.Equal(25, branch.Arms[0].Probability);
        Assert.Equal(3037, branch.Arms[0].Body.Single(n => n.Type == "step").AnimId);
        Assert.Empty(t.Handlers[1].ObservedOn);
        Assert.Contains(t.Observed, o => o.SpEffectId == 3506000 && !o.HasHandler);

        var summary = await _svc.GetSpEffectHandlersAsync(File506);
        Assert.All(summary.Handlers, x => Assert.Null(x.Body));
        Assert.Equal(new[] { 3037, 3018, 3044 }, summary.Handlers[0].AnimIds);
        var one = await _svc.GetSpEffectHandlersAsync(File506, 5060040);
        Assert.Equal(5060040, Assert.Single(one.Handlers).SpEffectId);
        Assert.NotNull(one.Handlers[0].Body);
    }

    [RequiresTestDataFact]
    public async Task ComboSequence_Kengeki03()
    {
        _svc.OpenProject(_projPath);
        var c = await _svc.GetComboSequenceAsync(File506, "Kengeki03");
        Assert.Equal("Goal.Kengeki03", c.Goal);
        Assert.Equal(3, c.StepCount);
        Assert.Equal(new[] { "clear", "step", "branch" }, c.Nodes.Select(n => n.Type));
        var arms = c.Nodes[2].Arms!;
        Assert.Equal(50, arms[0].Probability);
        Assert.Equal(3016, arms[0].Body[0].AnimId);
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.GetComboSequenceAsync(File506, "Nope"));
    }

    [RequiresTestDataFact]
    public async Task DiagnoseCombo_ReportsHints()
    {
        _svc.OpenProject(_projPath);
        var d = await _svc.DiagnoseComboAsync(File506);
        Assert.Contains(d.Findings, f => f.Code == "handler-not-observed" && f.Message.Contains("7777777"));
        Assert.Contains(d.Findings, f => f.Code == "observed-no-handler" && f.Message.Contains("3506000"));
        Assert.Contains(d.Findings, f => f.Code == "handler-no-clear" && f.Message.Contains("7777777"));
        Assert.Contains(d.Findings, f => f.Code == "random-gap" && f.Message.Contains("Goal.Act02"));
        Assert.DoesNotContain(d.Findings, f => f.Code == "handler-not-observed" && f.Message.Contains("5060040"));
    }

    private const string NestedLua = """
        Goal.Activate = function(arg0, arg1, arg2)
            arg1:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 5001)
            arg1:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 5002)
            arg1:AddObserveSpecialEffectAttribute(TARGET_ENE_0, 5032)
        end

        Goal.Interrupt = function(ai, goal)
            local sp = ai:GetSpecialEffectActivateInterruptType(0)
            local rnd = ai:GetRandam_Int(1, 100)
            if ai:IsInterupt(INTERUPT_ActivateSpecialEffect) then
                if sp == 5001 then
                    if rnd <= 50 then
                        goal:ClearSubGoal()
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3001, TARGET_ENE_0, 9999, 0, 0)
                    else
                        goal:ClearSubGoal()
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0)
                    end
                elseif sp == 5002 then
                    if rnd <= 50 then
                        goal:ClearSubGoal()
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3003, TARGET_ENE_0, 9999, 0, 0)
                    else
                        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3004, TARGET_ENE_0, 9999, 0, 0)
                    end
                elseif sp == 5032 and ai:GetNinsatsuNum() <= 1 then
                    goal:ClearSubGoal()
                    goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3005, TARGET_ENE_0, 9999, 0, 0)
                elseif sp == 5032 then
                    goal:ClearSubGoal()
                    goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3006, TARGET_ENE_0, 9999, 0, 0)
                end
            end
            return false
        end
        """;

    private AiProjectService WithLua(string lua)
    {
        File.WriteAllText(_layout!.ModUnpackedLua(Map, File506), lua.Replace("\r\n", "\n") + "\n");
        var svc = new AiProjectService();
        svc.OpenProject(_projPath);
        return svc;
    }

    [RequiresTestDataFact]
    public async Task Diagnose_NestedClear_NoFalsePositive_PartialIsReportedAccurately()
    {
        using var svc = WithLua(NestedLua);
        var d = await svc.DiagnoseComboAsync(File506);
        Assert.DoesNotContain(d.Findings, f => f.Code == "handler-no-clear" && f.Message.Contains("5001"));
        Assert.DoesNotContain(d.Findings, f => f.Code == "handler-no-clear" && f.Message.Contains("5032"));
        Assert.DoesNotContain(d.Findings, f => f.Code == "handler-partial-clear" && f.Message.Contains("5001"));
        var partial = Assert.Single(d.Findings, f => f.Code == "handler-partial-clear");
        Assert.Contains("5002", partial.Message);
        Assert.Contains("only some branches", partial.Message);
        var t = await svc.GetSpEffectHandlersAsync(File506, null, false);
        Assert.Equal("all", t.Handlers.First(h => h.SpEffectId == 5001).ClearMode);
        Assert.Equal("some", t.Handlers.First(h => h.SpEffectId == 5002).ClearMode);
    }

    [Fact]
    public void ClearModeOf_Cases()
    {
        ComboStep St(int l) => new(l, 1, "G");
        Assert.Equal("n/a", AiProjectService.ClearModeOf([new ComboClear(0)]));
        Assert.Equal("none", AiProjectService.ClearModeOf([St(1)]));
        Assert.Equal("all", AiProjectService.ClearModeOf([new ComboClear(0), St(1)]));
        // clear only in the if arm, the else arm is empty, a step follows -> some
        Assert.Equal("some", AiProjectService.ClearModeOf([new ComboBranch(0, [new ComboArm("x", null, [new ComboClear(1)], 0), new ComboArm("else", null, [])]), St(3)]));
        Assert.Equal("all", AiProjectService.ClearModeOf([new ComboBranch(0, [new ComboArm("x", null, [new ComboClear(1), St(2)], 0), new ComboArm("else", null, [new ComboClear(3), St(4)], 3)])]));
    }

    [RequiresTestDataFact]
    public async Task DuplicateHandlerArms_AreAllExposed_AndFlagged()
    {
        using var svc = WithLua(NestedLua);
        var t = await svc.GetSpEffectHandlersAsync(File506, 5032);
        Assert.Equal(2, t.Handlers.Count);
        Assert.All(t.Handlers, h => { Assert.True(h.DuplicateId); Assert.Equal(2, h.ArmsForThisId); Assert.NotNull(h.Body); });
        Assert.NotEqual(t.Handlers[0].Line, t.Handlers[1].Line);
        Assert.Equal(new[] { 5032 }, t.DuplicateIds);
        Assert.Contains("ai:GetNinsatsuNum() <= 1", t.Handlers[0].Condition);
        var all = await svc.GetSpEffectHandlersAsync(File506);
        Assert.Equal(4, all.Total);
        Assert.Equal(new[] { 5001, 5002, 5032, 5032 }, all.Handlers.Select(h => h.SpEffectId));
        var d = await svc.DiagnoseComboAsync(File506);
        Assert.Contains(d.Findings, f => f.Code == "duplicate-handler" && f.Message.Contains("5032"));
    }

    private static string BigInterruptLua(int arms)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Goal.Interrupt = function(ai, goal)\n    local sp = ai:GetSpecialEffectActivateInterruptType(0)\n    local rnd = ai:GetRandam_Int(1, 100)\n");
        for (int i = 0; i < arms; i++)
        {
            sb.Append(i == 0 ? "    if" : "    elseif").Append($" sp == {6000 + i} then\n        goal:ClearSubGoal()\n");
            for (int k = 0; k < 8; k++)
                sb.Append($"        goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, {3000 + k}, TARGET_ENE_0, 9999, 0, 0)\n");
        }
        sb.Append("    end\n    return false\nend\n");
        return sb.ToString();
    }

    [RequiresTestDataFact]
    public async Task Handlers_AreCappedAndPageable()
    {
        using var svc = WithLua(BigInterruptLua(60));
        var small = await svc.GetSpEffectHandlersAsync(File506);
        Assert.Equal(60, small.Total);
        var page1 = await svc.GetSpEffectHandlersAsync(File506, null, true, default, null, 4000);
        Assert.True(page1.Truncated);
        Assert.InRange(page1.Handlers.Count, 1, 59);
        Assert.NotNull(page1.NextOffset);
        Assert.Contains("offset=", page1.Note);
        var seen = new List<int>(page1.Handlers.Select(h => h.SpEffectId));
        var cur = page1;
        while (cur.NextOffset != null)
        {
            cur = await svc.GetSpEffectHandlersAsync(File506, null, true, default, cur.NextOffset, 4000);
            seen.AddRange(cur.Handlers.Select(h => h.SpEffectId));
        }
        Assert.Equal(Enumerable.Range(6000, 60), seen);
        Assert.False(cur.Truncated);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetSpEffectHandlersAsync(File506, null, false, default, -1, null));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetSpEffectHandlersAsync(File506, null, false, default, 61, null));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetSpEffectHandlersAsync(File506, null, false, default, null, 10));
    }

    [RequiresTestDataFact]
    public async Task ComboSequence_IsCapped_AndNarrowsBySpEffect()
    {
        using var svc = WithLua(BigInterruptLua(60));
        var capped = await svc.GetComboSequenceAsync(File506, "Interrupt");
        Assert.True(capped.Truncated);
        Assert.True(capped.ReturnedNodes <= AiProjectService.DefaultComboNodes);
        Assert.Contains("spEffectId", capped.Note);
        Assert.Equal(60 * 8, capped.StepCount); // total is still the full count
        var one = await svc.GetComboSequenceAsync(File506, "Interrupt", default, 6003);
        Assert.False(one.Truncated);
        Assert.Equal(8, one.StepCount);
        Assert.Equal("branch", Assert.Single(one.Nodes).Type);
        var tiny = await svc.GetComboSequenceAsync(File506, "Interrupt", default, 6003, 3);
        Assert.True(tiny.Truncated);
        Assert.Equal(3, tiny.ReturnedNodes);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetComboSequenceAsync(File506, "Interrupt", default, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetComboSequenceAsync(File506, "Interrupt", default, null, 0));
    }

    [RequiresTestDataFact]
    public async Task ReadLua_CharCapEndsOnCompleteLine_AndValidatesRange()
    {
        // 300 lines x 300 chars = 90k chars: the 60k cap lands mid-line.
        using var svc = WithLua(string.Join('\n', Enumerable.Range(1, 300).Select(i => $"-- {i:000} " + new string('x', 293))));
        var r = await svc.ReadLuaAsync(File506, 1, 300);
        Assert.True(r.Truncated);
        Assert.True(r.Text.Length <= AiProjectService.MaxCharsPerRead);
        Assert.Equal(r.EndLine, r.Text.Split('\n').Length);
        Assert.All(r.Text.Split('\n'), line => Assert.Equal(300, line.Length)); // no partial line
        Assert.Equal(r.EndLine + 1, r.NextStartLine);
        Assert.Contains("last complete line", r.Note);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.ReadLuaAsync(File506, 301, null));
        Assert.Contains("beyond the end", ex.Message);
        Assert.Contains("startLine", (await Assert.ThrowsAsync<ArgumentException>(() => svc.ReadLuaAsync(File506, 0, null))).Message);
        Assert.Contains("startLine", (await Assert.ThrowsAsync<ArgumentException>(() => svc.ReadLuaAsync(File506, -5, 3))).Message);
        Assert.Contains("before startLine", (await Assert.ThrowsAsync<ArgumentException>(() => svc.ReadLuaAsync(File506, 10, 5))).Message);
        Assert.Contains("endLine", (await Assert.ThrowsAsync<ArgumentException>(() => svc.ReadLuaAsync(File506, 1, -2))).Message);
        var clamp = await svc.ReadLuaAsync(File506, 295, 9999);
        Assert.Equal(300, clamp.EndLine);
        Assert.Contains("clamped to 300", clamp.Note);
        Assert.False(clamp.Truncated);
        Assert.Null(clamp.NextStartLine);
    }

    [Theory]
    [InlineData("arg1 : GetNinsatsuNum ( ) <= 1", "arg1:GetNinsatsuNum() <= 1")]
    [InlineData("interruptSpeffect == 5032 and goal : HasSpecialEffectId ( TARGET_SELF , 12 )", "interruptSpeffect == 5032 and goal:HasSpecialEffectId(TARGET_SELF, 12)")]
    [InlineData("a . b <= 3 and x .. y", "a.b <= 3 and x .. y")]
    [InlineData("else", "else")]
    public void NormalizeCondition_TidiesLexerSpacing(string raw, string expected) =>
        Assert.Equal(expected, AiProjectService.NormalizeCondition(raw));

    [Fact]
    public void WrongTypedIntegerArguments_GiveReadableMcpException()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"a":1,"b":1.5,"c":"abc","d":null,"e":99999999999}""");
        var r = doc.RootElement;
        Assert.Equal(1, SoulsAiTools.Int(r.GetProperty("a"), "a"));
        Assert.Null(SoulsAiTools.Int(r.GetProperty("d"), "d"));
        Assert.Null(SoulsAiTools.Int(null, "x"));
        var e1 = Assert.Throws<ModelContextProtocol.McpException>(() => SoulsAiTools.Int(r.GetProperty("b"), "startLine"));
        Assert.Contains("startLine must be an integer (got 1.5)", e1.Message);
        var e2 = Assert.Throws<ModelContextProtocol.McpException>(() => SoulsAiTools.Int(r.GetProperty("c"), "startLine"));
        Assert.Contains("\"abc\"", e2.Message);
        Assert.Throws<ModelContextProtocol.McpException>(() => SoulsAiTools.Int(r.GetProperty("e"), "x"));
    }

    [RequiresTestDataFact]
    public async Task Errors_AreClear()
    {
        using var noProject = new AiProjectService();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => noProject.ListGoalsAsync("x"));
        Assert.Contains("open_project", ex.Message);
        _svc.OpenProject(_projPath);
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.ListGoalsAsync("does-not-exist"));
        Assert.Throws<FileNotFoundException>(() => _svc.OpenProject(Path.Combine(_layout!.Work.Path, "missing.sasproj")));
    }

    [RequiresTestDataFact]
    public async Task NothingIsWritten_ToModOrProject()
    {
        _svc.OpenProject(_projPath);
        await _svc.ReadLuaAsync(File506);
        await _svc.ListGoalsAsync(File506);
        await _svc.GetSpEffectHandlersAsync(File506, null, true);
        await _svc.DiagnoseComboAsync(File506);
        Assert.Equal(_modHashBefore, _layout!.HashMod());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_layout.Work.Path, "proj", "cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_layout.Work.Path, "proj", "backups")));
    }

    [Fact]
    public void AllTools_AreMarkedReadOnly_AndNoWriteVerbsExist()
    {
        var tools = typeof(SoulsAiTools).GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        Assert.Equal(8, tools.Count);
        foreach (var m in tools)
        {
            var a = m.GetCustomAttribute<McpServerToolAttribute>()!;
            Assert.True(a.ReadOnly, m.Name);
            Assert.DoesNotContain(m.Name, new[] { "Save", "Write", "Delete", "Repack" });
        }
    }

    [McpDecompilerFact]
    public async Task ReadLua_DecompilesBytecodeEntry_IntoTempCache()
    {
        // Bytecode entry of the real m10 luabnd (packed copy in a temp layout), not the unpacked folder.
        OodleSupport.EnsureLoaded();
        var entry = LuaDecompilerTests.FirstBytecode(Map);
        using var l = ModLayout.Create("mcp-bc").GamePacked(Map);
        var sc = new ProjectScript { Luabnd = Map, EntryPath = entry.RelativePath };
        var p = SasProject.Create(Path.Combine(l.Work.Path, "proj", "b.sasproj"), "b", l.Game, l.Mod, [sc]);
        using var svc = new AiProjectService();
        Environment.SetEnvironmentVariable("SAS_DECOMPILER", RequiresDecompilerFactAttribute.ExePath);
        svc.OpenProject(p.FilePath);
        var r = await svc.ReadLuaAsync(entry.FileName, 1, 20);
        Assert.True(r.WasBytecode);
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(l.Work.Path, "proj", "cache")));
    }
}

/// <summary>Skips unless test data, Oodle and DSLuaDecompiler.exe are all available.</summary>
public sealed class McpDecompilerFactAttribute : FactAttribute
{
    public McpDecompilerFactAttribute()
    {
        var inner = new RequiresDecompilerFactAttribute();
        if (inner.Skip != null) Skip = inner.Skip;
        else if (!OodleSupport.Available) Skip = OodleSupport.MissingReason;
    }
}
