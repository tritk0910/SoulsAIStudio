using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

public class WritePolicyTests
{
    /// <summary>Fake install: game/sekiro.exe + game/script + game/mods/MyMod.</summary>
    private static (TempWorkDir Work, string Game, string Mod) FakeInstall()
    {
        var w = TempWorkDir.Create("policy");
        string game = Path.Combine(w.Path, "Sekiro");
        string mod = Path.Combine(game, "mods", "MyMod");
        Directory.CreateDirectory(Path.Combine(game, "script"));
        Directory.CreateDirectory(Path.Combine(mod, "script"));
        File.WriteAllText(Path.Combine(game, "sekiro.exe"), "");
        return (w, game, mod);
    }

    [Fact]
    public void GameFolder_IsBlocked_ModsFolder_IsAllowed_WithoutConfiguration()
    {
        var (w, game, mod) = FakeInstall();
        using var _ = w;
        Assert.NotNull(WritePolicy.WhyBlocked(Path.Combine(game, "script", "m10_00_00_00.luabnd.dcx")));
        Assert.NotNull(WritePolicy.WhyBlocked(Path.Combine(game, "chr", "c5060.anibnd.dcx")));
        Assert.Null(WritePolicy.WhyBlocked(Path.Combine(mod, "script", "m10_00_00_00.luabnd.dcx")));
        Assert.Null(WritePolicy.WhyBlocked(Path.Combine(w.Path, "elsewhere", "x.dcx")));
        // "modsX" is not "mods"
        Assert.NotNull(WritePolicy.WhyBlocked(Path.Combine(game, "modsX", "a.dcx")));
    }

    [Fact]
    public void ExtraGameDir_IsProtected_EvenWithoutExe()
    {
        using var w = TempWorkDir.Create("policy2");
        string game = Path.Combine(w.Path, "G");
        Directory.CreateDirectory(game);
        Assert.NotNull(WritePolicy.WhyBlocked(Path.Combine(game, "script", "a.dcx"), [game]));
        Assert.Null(WritePolicy.WhyBlocked(Path.Combine(game, "mods", "M", "a.dcx"), [game]));
    }

    [Fact]
    public void ValidateFolders_RejectsGameAsMod_ParentMod_RelativePaths()
    {
        var (w, game, mod) = FakeInstall();
        using var _ = w;
        Assert.Empty(WritePolicy.ValidateFolders(game, mod));
        Assert.NotEmpty(WritePolicy.ValidateFolders(game, game));                  // mod == game
        Assert.NotEmpty(WritePolicy.ValidateFolders(game, w.Path));                // mod contains game
        Assert.NotEmpty(WritePolicy.ValidateFolders(game, "relative\\mod"));       // not rooted
        Assert.NotEmpty(WritePolicy.ValidateFolders(game, ""));                    // empty
        Assert.NotEmpty(WritePolicy.ValidateFolders(game, Path.Combine(game, "script"))); // inside game, not mods
    }

    [Fact]
    public void Project_Create_RefusesGameFolderAsMod()
    {
        var (w, game, _) = FakeInstall();
        using var __ = w;
        Assert.Throws<InvalidOperationException>(() =>
            SasProject.Create(Path.Combine(w.Path, "p", "x.sasproj"), "x", game, game, []));
    }
}
