using SoulsAiStudio.Core.Tests.TestSupport;
using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.Lua;

/// <summary>
/// Inventory of out/bin/*.lua in the three unpacked folders. Future parser round-trip tests
/// should use <see cref="LuaCorpus.PlainText"/> (and later <see cref="LuaCorpus.Bytecode"/> after decompile).
/// Baseline (2026-11): 179 files = 10 plain text + 169 HavokScript bytecode.
/// </summary>
public class LuaCorpusTests(ITestOutputHelper output)
{
    [RequiresTestDataFact]
    public void Inventory_MatchesBaseline()
    {
        var all = LuaCorpus.Entries;
        foreach (var g in all.GroupBy(e => e.Map))
        {
            var plain = g.Where(e => !e.IsBytecode).ToList();
            output.WriteLine($"{g.Key}: {g.Count()} files ({g.Sum(e => e.Size):N0} B), {plain.Count} plain text, {g.Count(e => e.IsBytecode)} bytecode");
            foreach (var p in plain)
                output.WriteLine($"   {p.FileName,-20} {p.Size,8:N0} B {p.LineCount,5} lines");
        }

        Assert.Equal(56, all.Count(e => e.Map == "m10_00_00_00"));
        Assert.Equal(56, all.Count(e => e.Map == "m11_02_00_00"));
        Assert.Equal(67, all.Count(e => e.Map == "m25_00_00_00"));
        Assert.Equal(10, all.Count(e => !e.IsBytecode));
        Assert.Equal(169, all.Count(e => e.IsBytecode));
        Assert.All(all, e => Assert.Equal(".lua", Path.GetExtension(e.FileName)));
        Assert.All(all, e => Assert.Matches(@"^\d{6}_(battle|logic|platoon)\.lua$", e.FileName));
        // Besides <npcId>_battle/_logic there is a third kind: m25 has 000120_platoon.lua (group AI).
        Assert.Single(all, e => e.FileName.EndsWith("_platoon.lua"));
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.KeyBattleScripts), MemberType = typeof(LuaCorpus))]
    public void KeyBattleScripts_ArePlainTextWithGoalStructure(string relPath)
    {
        var entry = Assert.Single(LuaCorpus.Entries, e => e.RelPath == relPath);
        Assert.False(entry.IsBytecode);
        string text = LuaCorpus.ReadText(relPath);
        Assert.Contains("RegisterTableGoal(", text);
        Assert.Contains("Goal.Activate", text);
        Assert.Contains("Goal.Interrupt", text);
        output.WriteLine($"{relPath}: {entry.LineCount} lines, {entry.Size:N0} B");
    }

    /// <summary>Owl: the verified SpEffect interrupt branch from CLAUDE.md 4.1 exists in the sample.</summary>
    [RequiresTestDataFact]
    public void Owl_HasSpEffectInterruptBranch3506000()
    {
        string text = File.ReadAllText(Samples.OwlBattle);
        Assert.Contains("GetSpecialEffectActivateInterruptType(0)", text);
        Assert.Contains("INTERUPT_ActivateSpecialEffect", text);
        Assert.Contains("== 3506000", text);
        Assert.Contains("AddObserveSpecialEffectAttribute(", text);
        Assert.Contains(":TimingSetNumber(", text);
        Assert.Contains("GetNinsatsuNum()", text);
    }

    /// <summary>Every plain-text corpus file: ASCII, LF only, no BOM, ends with newline (what a printer must reproduce).</summary>
    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.PlainText), MemberType = typeof(LuaCorpus))]
    public void PlainText_EncodingAndLineEndings(string relPath)
    {
        byte[] b = LuaCorpus.ReadBytes(relPath);
        Assert.NotEmpty(b);
        Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, "has UTF-8 BOM");
        Assert.DoesNotContain(b, x => x >= 0x80);
        Assert.DoesNotContain((byte)'\r', b);
        Assert.Equal((byte)'\n', b[^1]);
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.Bytecode), MemberType = typeof(LuaCorpus))]
    public void Bytecode_HasHavokScriptHeader(string relPath)
    {
        byte[] b = LuaCorpus.ReadBytes(relPath);
        Assert.True(Samples.IsLuaBytecode(b));
        Assert.Equal(0x50, b[4]);
    }
}
