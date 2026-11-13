using System.Text.Json;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

public class CatalogAndProjectFileTests
{
    // ---------- ScriptCatalog ----------

    [Fact]
    public void Scan_MissingAndEmptyScriptFolders_ReturnsEmpty_NoErrors()
    {
        using var work = TempWorkDir.Create("cat-empty");
        var missing = new ScriptCatalog(work.Combine("nogame"), work.Combine("nomod"));
        Assert.Empty(missing.ListLuabnds());
        var (s1, e1) = missing.Scan();
        Assert.Empty(s1); Assert.Empty(e1);

        Directory.CreateDirectory(work.Combine("g/script"));
        Directory.CreateDirectory(work.Combine("m/script"));
        var empty = new ScriptCatalog(work.Combine("g"), work.Combine("m"));
        var (s2, e2) = empty.Scan();
        Assert.Empty(s2); Assert.Empty(e2);
        Assert.Null(empty.Locate("m10_00_00_00"));
        Assert.Throws<FileNotFoundException>(() => empty.Open("m10_00_00_00"));
    }

    [RequiresTestDataFact]
    public void Scan_CorruptArchives_AreReported_OthersStillListed()
    {
        using var l = ModLayout.Create("cat-corrupt").GamePacked("aicommon").ModPacked("m10_00_00_00");
        File.WriteAllBytes(Path.Combine(l.ModScript, "m98_00_00_00.luabnd.dcx"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(l.ModScript, "m97_00_00_00.luabnd.dcx"), []);
        byte[] truncated = File.ReadAllBytes(Samples.LuabndPath("m10_00_00_00"))[..5000];
        File.WriteAllBytes(Path.Combine(l.GameScript, "m96_00_00_00.luabnd.dcx"), truncated);
        // Unpacked folder whose manifest is broken / references a missing file.
        Directory.CreateDirectory(Path.Combine(l.ModScript, "m95_00_00_00-luabnd-dcx"));
        File.WriteAllText(Path.Combine(l.ModScript, "m95_00_00_00-luabnd-dcx", "_witchy-bnd4.xml"), "<bnd4><files>");
        l.ModUnpacked("m11_02_00_00");
        File.Delete(l.ModUnpackedLua("m11_02_00_00", "540300_battle.lua"));

        var (scripts, errors) = new ScriptCatalog(l.Game, l.Mod).Scan();

        Assert.Contains(scripts, s => s.Luabnd == "m10_00_00_00" && s.FileName == "506300_battle.lua");
        Assert.Contains(scripts, s => s.Luabnd == "aicommon");
        foreach (string bad in new[] { "m95_00_00_00", "m96_00_00_00", "m97_00_00_00", "m98_00_00_00", "m11_02_00_00" })
            Assert.Single(errors, e => e.StartsWith(bad + ":"));
        Assert.Equal(5, errors.Count);
    }

    /// <summary>
    /// Oodle cannot be unloaded in-process (SoulsFormats keeps a static pointer and Windows LoadLibrary would return
    /// the already-loaded module), so "Oodle missing" is only testable when this machine really has no Oodle.
    /// </summary>
    [RequiresTestDataFact]
    public void Scan_KrakWithoutOodle_ReportsError_OthersListed()
    {
        if (OodleSupport.Available)
        {
            // Can't simulate; still check KRAK packed-only is listed when Oodle is present.
            using var l2 = ModLayout.Create("cat-krak").ModPacked("m25_00_00_00").ModPacked("m10_00_00_00");
            var (s, e) = new ScriptCatalog(l2.Game, l2.Mod).Scan();
            Assert.Empty(e);
            Assert.Contains(s, x => x.Luabnd == "m25_00_00_00" && x.FileName == "500000_battle.lua" && !x.IsBytecode);
            return;
        }
        using var l = ModLayout.Create("cat-nooodle").ModPacked("m25_00_00_00").ModPacked("m10_00_00_00");
        var (scripts, errors) = new ScriptCatalog(l.Game, l.Mod).Scan();
        Assert.Single(errors, x => x.StartsWith("m25_00_00_00:"));
        Assert.Contains(scripts, x => x.Luabnd == "m10_00_00_00");
    }

    [RequiresTestDataFact]
    public void Scan_ModUnpackedKrak_DoesNotNeedOodle_AndWinsOverPacked()
    {
        using var l = ModLayout.Create("cat-krak-unp").ModUnpacked("m25_00_00_00").GamePacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var cat = new ScriptCatalog(l.Game, l.Mod);
        Assert.Equal(LuabndSource.ModUnpacked, cat.Locate("m25_00_00_00"));
        var (scripts, errors) = cat.Scan();
        Assert.Empty(errors);
        Assert.Equal(67, scripts.Count(s => s.Luabnd == "m25_00_00_00"));
        Assert.All(scripts.Where(s => s.Luabnd == "m10_00_00_00"), s => Assert.Equal(LuabndSource.ModUnpacked, s.Source));
        // third script kind
        var platoon = Assert.Single(scripts, s => s.FileName == "000120_platoon.lua");
        Assert.Equal("other", platoon.Kind);
        Assert.Equal("000120", platoon.NpcId);
    }

    [Fact]
    public void Catalog_FolderWithoutManifest_IsIgnored()
    {
        using var work = TempWorkDir.Create("cat-nomanifest");
        Directory.CreateDirectory(work.Combine("m/script/m10_00_00_00-luabnd-dcx/out/bin"));
        var cat = new ScriptCatalog(work.Combine("g"), work.Combine("m"));
        Assert.Empty(cat.ListLuabnds());
        Assert.Null(cat.Locate("m10_00_00_00"));
    }

    // ---------- SasProject ----------

    /// <summary>JSON fragment with valid absolute, separate game/mod folders (Load validates them).</summary>
    private static string Folders(TempWorkDir work)
    {
        Directory.CreateDirectory(work.Combine("game"));
        Directory.CreateDirectory(work.Combine("mod"));
        return $"\"gameDir\": {JsonSerializer.Serialize(work.Combine("game"))}, \"modDir\": {JsonSerializer.Serialize(work.Combine("mod"))}";
    }

    [Fact]
    public void Load_InvalidFolders_ThrowsInvalidOperation()
    {
        using var work = TempWorkDir.Create("proj-badfolders");
        string path = work.Combine("p.sasproj");
        File.WriteAllText(path, """{ "formatVersion": 1, "name": "x", "gameDir": "", "modDir": "" }""");
        Assert.Throws<InvalidOperationException>(() => SasProject.Load(path));
    }

    [Fact]
    public void Load_NewerFormatVersion_ThrowsNotSupported()
    {
        using var work = TempWorkDir.Create("proj-newer");
        string path = work.Combine("p.sasproj");
        File.WriteAllText(path, $$"""{ "formatVersion": {{SasProject.CurrentFormatVersion + 1}}, "name": "x" }""");
        var ex = Assert.Throws<NotSupportedException>(() => SasProject.Load(path));
        Assert.Contains("newer", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{ \"formatVersion\": \"one\" }")]
    [InlineData("null")]
    public void Load_CorruptJson_ThrowsClearError(string content)
    {
        using var work = TempWorkDir.Create("proj-corrupt");
        string path = work.Combine("p.sasproj");
        File.WriteAllText(path, content);
        var ex = Record.Exception(() => SasProject.Load(path));
        Assert.NotNull(ex);
        Assert.True(ex is JsonException or FormatException, $"unexpected {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>The error for a corrupt project should name the file so the start screen can show which one is broken.</summary>
    [Fact]
    public void Load_CorruptJson_MessageNamesTheFile()
    {
        using var work = TempWorkDir.Create("proj-corrupt-msg");
        string path = work.Combine("broken.sasproj");
        File.WriteAllText(path, "not json");
        var ex = Record.Exception(() => SasProject.Load(path));
        Assert.Contains("broken.sasproj", ex!.Message);
    }

    [Fact]
    public void Load_NullCollections_AreNormalized()
    {
        using var work = TempWorkDir.Create("proj-nulls");
        string path = work.Combine("p.sasproj");
        File.WriteAllText(path, $$"""{ "formatVersion": 1, "name": "x", {{Folders(work)}}, "scripts": null, "ui": null }""");
        var p = SasProject.Load(path);
        Assert.NotNull(p.Scripts);
        Assert.NotNull(p.Ui);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        using var work = TempWorkDir.Create("proj-missing");
        Assert.Throws<FileNotFoundException>(() => SasProject.Load(work.Combine("nope.sasproj")));
    }

    [Fact]
    public void Load_OlderOrMissingFormatVersion_Accepted_UnknownFieldsIgnored()
    {
        using var work = TempWorkDir.Create("proj-old");
        string path = work.Combine("p.sasproj");
        File.WriteAllText(path, $$"""{ "name": "old", {{Folders(work)}}, "futureField": [1,2], "scripts": [ { "luabnd": "m10_00_00_00", "entryPath": "out\\bin\\506300_battle.lua" } ] }""");
        var p = SasProject.Load(path);
        Assert.Equal("old", p.Name);
        Assert.Equal("506300_battle.lua", Assert.Single(p.Scripts).FileName);
        Assert.Equal(Path.GetFullPath(path), p.FilePath);
    }

    [Fact]
    public void SaveAs_ReadOnlyProjectFile_ThrowsReadOnly_FileUnchanged()
    {
        using var work = TempWorkDir.Create("proj-ro");
        string path = work.Combine("p.sasproj");
        _ = Folders(work);
        var p = SasProject.Create(path, "x", work.Combine("game"), work.Combine("mod"), []);
        string before = File.ReadAllText(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        p.Name = "changed";
        Assert.Throws<SoulsAiStudio.Core.IO.ReadOnlyFileException>(() => p.Save());
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void RecentProjects_CorruptStore_DoesNotThrow_AndIsRepaired()
    {
        using var work = TempWorkDir.Create("recent-corrupt");
        string store = work.Combine("recent.json");
        File.WriteAllText(store, "{ this is not a list");
        var r = new RecentProjects(store);
        Assert.Empty(r.Paths);
        r.Add(work.Combine("a.sasproj"));
        Assert.Single(new RecentProjects(store).Paths);
    }

    [Fact]
    public void RecentProjects_Clear_PersistsEmptyList()
    {
        using var work = TempWorkDir.Create("recent-clear");
        string store = work.Combine("recent.json");
        var r = new RecentProjects(store);
        r.Add(work.Combine("a.sasproj"));
        r.Add(work.Combine("b.sasproj"));
        r.Clear();
        Assert.Empty(r.Paths);
        Assert.Empty(new RecentProjects(store).Paths);
    }
}
