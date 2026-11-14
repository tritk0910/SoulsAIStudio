using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

/// <summary>"Animations only" projects: characters without AI scripts, chr folder scan, copy of a game anibnd into the mod.</summary>
public class AnimationProjectTests
{
    private static (TempWorkDir Work, string Game, string Mod) MakeLayout()
    {
        var work = TempWorkDir.Create("animproj");
        string game = Path.Combine(work.Path, "game"), mod = Path.Combine(work.Path, "mod");
        Directory.CreateDirectory(Path.Combine(game, "chr"));
        Directory.CreateDirectory(Path.Combine(mod, "chr"));
        // Fake files: only names and sizes matter here.
        Write(Path.Combine(game, "chr", "c5060.anibnd.dcx"), 10_000, 1);
        Write(Path.Combine(game, "chr", "c5060.chrbnd.dcx"), 100, 2);
        Write(Path.Combine(game, "chr", "c5063.anibnd.dcx"), 500, 3);   // stub
        Write(Path.Combine(game, "chr", "c5400.anibnd.dcx"), 20_000, 4);
        Write(Path.Combine(mod, "chr", "c5400.anibnd.dcx"), 30_000, 5);  // mod overrides game
        Write(Path.Combine(game, "chr", "c5400.chrbnd.dcx"), 100, 6);
        Write(Path.Combine(game, "chr", "c5400.anibnd.dcx.bak"), 100, 7); // ignored
        Write(Path.Combine(game, "chr", "c5400_x.anibnd.dcx"), 100, 8);   // ignored
        return (work, game, mod);
    }

    private static void Write(string path, int size, byte fill) => File.WriteAllBytes(path, Enumerable.Repeat(fill, size).ToArray());

    [Fact]
    public void Project_WithCharactersOnly_RoundTrips()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        string path = Path.Combine(work.Path, "proj", "Anims.sasproj");
        var p = SasProject.Create(path, "Anims", game, mod, [],
            [new ProjectCharacter { ChrId = "c5060", DisplayName = "Owl" }, new ProjectCharacter { ChrId = "c5400" }]);
        Assert.True(p.IsAnimationsOnly);
        p.Ui.OpenCharacter = "c5400";
        p.Ui.Layout = WorkspaceLayout.Animation;
        p.Save();

        Assert.Contains("\"animation\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        var loaded = SasProject.Load(path);
        Assert.True(loaded.IsAnimationsOnly);
        Assert.Equal(["c5060", "c5400"], loaded.Characters.Select(c => c.ChrId));
        Assert.Equal("c5060  Owl", loaded.Characters[0].Label);
        Assert.Equal("c5400", loaded.Ui.OpenCharacter);
        Assert.Equal(WorkspaceLayout.Animation, loaded.Ui.Layout);
    }

    [Fact]
    public void OldProjectFile_WithoutCharacters_Loads()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        string path = Path.Combine(work.Path, "old.sasproj");
        File.WriteAllText(path, $$"""
            { "formatVersion": 1, "name": "old", "gameDir": {{System.Text.Json.JsonSerializer.Serialize(game)}},
              "modDir": {{System.Text.Json.JsonSerializer.Serialize(mod)}},
              "scripts": [ { "luabnd": "m10_00_00_00", "entryPath": "out\\bin\\506300_battle.lua" } ],
              "ui": { "openScriptKey": null } }
            """);
        var loaded = SasProject.Load(path);
        Assert.Empty(loaded.Characters);
        Assert.False(loaded.IsAnimationsOnly);
        Assert.Null(loaded.Ui.Layout);
    }

    [Theory]
    [InlineData("c5060", "c5060")]
    [InlineData(" C5060 ", "c5060")]
    [InlineData("5060", "c5060")]
    [InlineData("c506", null)]
    [InlineData("c50600", null)]
    [InlineData("x5060", null)]
    [InlineData("", null)]
    public void NormalizeChrId(string input, string? expected) => Assert.Equal(expected, CharacterLocator.NormalizeChrId(input));

    [Fact]
    public void Scan_ListsModAndGameCharacters()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        var list = new CharacterLocator(game, mod).Scan();
        Assert.Equal(["c5060", "c5063", "c5400"], list.Select(c => c.ChrId));
        var owl = list[0];
        Assert.False(owl.InMod); Assert.True(owl.InGame); Assert.True(owl.HasChrbnd); Assert.False(owl.IsStub);
        Assert.True(list[1].IsStub);
        Assert.False(list[1].HasChrbnd);
        Assert.True(list[2].InMod); Assert.True(list[2].InGame);
    }

    [Fact]
    public void ResolveId_PrefersMod()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        var loc = new CharacterLocator(game, mod);
        var isshin = loc.ResolveId("C5400")!;
        Assert.Equal("c5400", isshin.ChrId);
        Assert.True(isshin.AnibndFromMod);
        Assert.StartsWith(mod, isshin.AnibndPath);
        Assert.StartsWith(game, isshin.ChrbndPath);
        Assert.False(loc.ResolveId("c5060")!.AnibndFromMod);
        Assert.Null(loc.ResolveId("c9999"));
        Assert.Null(loc.ResolveId("nope"));
    }

    [Fact]
    public void CopyAnibndToMod_CopiesOnce_AndNeverOverwrites()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        var loc = new CharacterLocator(game, mod);
        string target = loc.CopyAnibndToMod("c5060");
        Assert.Equal(Path.Combine(mod, "chr", "c5060.anibnd.dcx"), target);
        Assert.Equal(File.ReadAllBytes(Path.Combine(game, "chr", "c5060.anibnd.dcx")), File.ReadAllBytes(target));
        Assert.True(loc.ResolveId("c5060")!.AnibndFromMod);

        Assert.Throws<IOException>(() => loc.CopyAnibndToMod("c5060"));
        Assert.Throws<IOException>(() => loc.CopyAnibndToMod("c5400")); // mod already has its own (never overwritten)
        Assert.Equal(30_000, new FileInfo(Path.Combine(mod, "chr", "c5400.anibnd.dcx")).Length);
        Assert.Throws<FileNotFoundException>(() => loc.CopyAnibndToMod("c9999"));
        Assert.Throws<ArgumentException>(() => loc.CopyAnibndToMod("bad"));
    }
}
