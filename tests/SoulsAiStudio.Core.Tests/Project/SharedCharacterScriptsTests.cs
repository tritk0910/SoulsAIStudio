using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

public class SharedCharacterScriptsTests
{
    private static ScriptCandidate Cand(string luabnd, string file, LuabndSource src = LuabndSource.ModPacked) =>
        new(luabnd, @"out\bin\" + file, src, IsBytecode: false, Size: 100);

    [Fact]
    public void FindsEveryScriptOfTheCharacter_UsesProjectOverrides()
    {
        using var dir = TempWorkDir.Create("shared");
        string game = dir.Combine("game"), mod = dir.Combine("mod");
        Directory.CreateDirectory(Path.Combine(game, "chr"));
        Directory.CreateDirectory(Path.Combine(mod, "chr"));
        // c5400: real anibnd in the mod; c5403 is a stub variant (falls back to c5400); c5060 has its own.
        File.WriteAllBytes(Path.Combine(mod, "chr", "c5400.anibnd.dcx"), new byte[10_000]);
        File.WriteAllBytes(Path.Combine(mod, "chr", "c5400.chrbnd.dcx"), new byte[10]);
        File.WriteAllBytes(Path.Combine(game, "chr", "c5403.anibnd.dcx"), new byte[100]);
        File.WriteAllBytes(Path.Combine(game, "chr", "c5060.anibnd.dcx"), new byte[10_000]);
        File.WriteAllBytes(Path.Combine(game, "chr", "c5060.chrbnd.dcx"), new byte[10]);
        var locator = new CharacterLocator(game, mod);

        var candidates = new[]
        {
            Cand("m11_02_00_00", "540300_battle.lua"),
            Cand("m11_02_00_00", "540310_battle.lua"),
            Cand("m11_01_00_00", "540000_battle.lua", LuabndSource.Game),
            Cand("m10_00_00_00", "506000_battle.lua"),
            Cand("m10_00_00_00", "506300_battle.lua"), // project says it drives c5400
            Cand("aicommon", "common_func.lua"),
        };
        var project = new List<ProjectScript>
        {
            new() { Luabnd = "m11_02_00_00", EntryPath = @"out\bin\540310_battle.lua", AnimCategory = 100 },
            new() { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_battle.lua", Character = "c5400" },
        };

        var found = SharedCharacterScripts.Find(candidates, project, locator, Path.Combine(mod, "chr", "c5400.anibnd.dcx"));

        Assert.Equal(["540300_battle.lua", "540310_battle.lua", "540000_battle.lua", "506300_battle.lua"], found.Select(f => f.Script.FileName));
        Assert.Equal([false, true, false, true], found.Select(f => f.InProject));
        Assert.Same(project[0], found[1].Script); // keeps the project's anim category (a100)
        Assert.Equal(100, found[1].Script.AnimCategory);
    }
}
