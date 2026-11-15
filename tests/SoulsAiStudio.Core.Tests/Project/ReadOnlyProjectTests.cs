using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

/// <summary>Projects without a mod folder: read the game, never write anything.</summary>
public class ReadOnlyProjectTests
{
    private const string Map = "m10_00_00_00";

    private static (TempWorkDir Work, string Game) MakeGame(bool withScript = false)
    {
        var work = TempWorkDir.Create("readonly");
        string game = Path.Combine(work.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "chr"));
        Directory.CreateDirectory(Path.Combine(game, "script"));
        File.WriteAllBytes(Path.Combine(game, "chr", "c5060.anibnd.dcx"), Enumerable.Repeat((byte)7, 10_000).ToArray());
        File.WriteAllBytes(Path.Combine(game, "chr", "c5060.chrbnd.dcx"), [1, 2, 3]);
        if (withScript) File.Copy(Samples.LuabndPath(Map), Path.Combine(game, "script", Map + ".luabnd.dcx"));
        return (work, game);
    }

    [Fact]
    public void Project_WithoutModFolder_IsValid_AndReadOnly()
    {
        var (work, game) = MakeGame();
        using var _ = work;
        var p = SasProject.Create(Path.Combine(work.Path, "p", "p.sasproj"), "p", game, "", [],
            [new ProjectCharacter { ChrId = "c5060" }]);
        Assert.True(p.IsReadOnly);
        Assert.True(SasProject.Load(p.FilePath).IsReadOnly);
    }

    [Fact]
    public void Locator_WithoutMod_FindsGameFiles_AndRefusesCopyToMod()
    {
        var (work, game) = MakeGame();
        using var _ = work;
        var loc = new CharacterLocator(game, "");
        var files = loc.ResolveId("c5060")!;
        Assert.StartsWith(game, files.AnibndPath);
        Assert.False(files.AnibndFromMod);
        Assert.Equal(["c5060"], loc.Scan().Select(c => c.ChrId));
        Assert.Throws<InvalidOperationException>(() => loc.CopyAnibndToMod("c5060"));
    }

    [Fact]
    public void GameView_CopiesIntoViewDir_RefreshesWhenGameFileChanges_LeavesGameUntouched()
    {
        var (work, game) = MakeGame();
        using var _ = work;
        string source = Path.Combine(game, "chr", "c5060.anibnd.dcx");
        var before = File.GetLastWriteTimeUtc(source);
        string view = Path.Combine(work.Path, "p", "cache", "gameview");
        var loc = new CharacterLocator(game, "");

        var v = loc.GameView("c5060", view)!;
        Assert.True(v.IsReadOnlyView);
        Assert.Equal(Path.Combine(view, "chr", "c5060.anibnd.dcx"), v.AnibndPath);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(v.AnibndPath));
        Assert.StartsWith(game, v.ChrbndPath); // chrbnd read in place
        Assert.Equal(before, File.GetLastWriteTimeUtc(source));

        // Same file: not copied again (DSAS may have written its own files next to the copy).
        File.SetLastWriteTimeUtc(v.AnibndPath, File.GetLastWriteTimeUtc(v.AnibndPath)); // no-op
        var stamp = File.GetCreationTimeUtc(v.AnibndPath);
        loc.GameView("c5060", view);
        Assert.Equal(stamp, File.GetCreationTimeUtc(v.AnibndPath));

        // Game file changed: copy refreshed.
        File.WriteAllBytes(source, Enumerable.Repeat((byte)9, 12_000).ToArray());
        loc.GameView("c5060", view);
        Assert.Equal(12_000, new FileInfo(v.AnibndPath).Length);

        Assert.Null(loc.GameView("c9999", view));
    }

    [RequiresTestDataFact]
    public async Task Session_WithoutMod_ReadsGameScripts_ButNeverWrites()
    {
        var (work, game) = MakeGame(withScript: true);
        using var _ = work;
        var script = ModLayout.Script(Map, Samples.Owl + "_battle.lua");
        var project = SasProject.Create(Path.Combine(work.Path, "p", "p.sasproj"), "p", game, "", [script]);
        var session = new ProjectSession(project, decompilerExe: "");

        Assert.False(session.Catalog.HasMod);
        Assert.Equal(LuabndSource.Game, session.Catalog.Locate(Map));
        Assert.Contains(Map, session.Catalog.ListLuabnds());
        Assert.Null(session.Catalog.Status(Map).Unpacked);
        var doc = await session.OpenAsync(script); // plain text in the sample: no decompiler needed
        Assert.Contains("Goal.", doc.SavedText);

        string gameFile = Path.Combine(game, "script", Map + ".luabnd.dcx");
        byte[] gameBytes = File.ReadAllBytes(gameFile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(doc, doc.SavedText + "-- x\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RepackAsync(Map));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UnpackAsync(Map));
        Assert.Equal(gameBytes, File.ReadAllBytes(gameFile));
        Assert.False(Directory.Exists(Path.Combine(project.BackupDir, "original")));
    }
}
