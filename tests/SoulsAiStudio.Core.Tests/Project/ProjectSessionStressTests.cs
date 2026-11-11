using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsFormats;

namespace SoulsAiStudio.Core.Tests.Project;

/// <summary>Adversarial tests for <see cref="ProjectSession"/> open/save (all on temp copies).</summary>
public class ProjectSessionStressTests
{
    private const string Owl = "506300_battle.lua";
    private const string OwlOther = "506000_battle.lua";

    // ---------- DCX_KRAK (m25, True Monk) ----------

    [RequiresOodleFact]
    public async Task Save_KrakLuabnd_Unpacked_PreservesKrakAndContent()
    {
        using var l = ModLayout.Create("krak-unpacked").ModPacked("m25_00_00_00").ModUnpacked("m25_00_00_00");
        var s = l.Session(ModLayout.Script("m25_00_00_00", "500000_battle.lua"));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        Assert.Equal(LuabndSource.ModUnpacked, doc.LoadedFrom);

        string edited = doc.SavedText + "-- true monk edit\n";
        await s.SaveAsync(doc, edited);

        AssertPackedKrak(l.ModPackedPath("m25_00_00_00"), "500000_battle.lua", edited);
        Assert.Equal(edited, File.ReadAllText(l.ModUnpackedLua("m25_00_00_00", "500000_battle.lua")));
    }

    [RequiresOodleFact]
    public async Task Save_KrakLuabnd_PackedOnly_PreservesKrakAndContent()
    {
        using var l = ModLayout.Create("krak-packed").ModPacked("m25_00_00_00");
        var s = l.Session(ModLayout.Script("m25_00_00_00", "500000_battle.lua"));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        Assert.Equal(LuabndSource.ModPacked, doc.LoadedFrom);

        string edited = doc.SavedText.Replace("Goal.Activate", "Goal.Activate --[[x]]", StringComparison.Ordinal);
        Assert.NotEqual(doc.SavedText, edited);
        await s.SaveAsync(doc, edited);
        AssertPackedKrak(l.ModPackedPath("m25_00_00_00"), "500000_battle.lua", edited);
    }

    private static void AssertPackedKrak(string packed, string file, string expectedText)
    {
        var bnd = BND4.Read(File.ReadAllBytes(packed));
        var krak = Assert.IsType<DCX.DcxKrakCompressionInfo>(bnd.Compression);
        Assert.Equal(6, krak.CompressionLevel);
        Assert.Equal(69, bnd.Files.Count);
        var original = BND4.Read(File.ReadAllBytes(Samples.LuabndPath("m25_00_00_00")));
        for (int i = 0; i < bnd.Files.Count; i++)
        {
            Assert.Equal(original.Files[i].ID, bnd.Files[i].ID);
            Assert.Equal(original.Files[i].Name, bnd.Files[i].Name);
            if (!bnd.Files[i].Name.EndsWith(@"\" + file))
                Assert.True(original.Files[i].Bytes.AsSpan().SequenceEqual(bnd.Files[i].Bytes), bnd.Files[i].Name);
        }
        Assert.Equal(expectedText, LuabndArchive.LoadPacked(packed).Find(file)!.GetText());
    }

    // ---------- read-only targets ----------

    /// <summary>.dcx read-only: written FIRST, so the save fails before anything in the mod changes.</summary>
    [RequiresTestDataFact]
    public async Task Save_ReadOnlyPackedDcx_ThrowsReadOnly_AndChangesNothingInMod()
    {
        using var l = ModLayout.Create("ro-dcx").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        string dcx = l.ModPackedPath("m10_00_00_00");
        File.SetAttributes(dcx, FileAttributes.ReadOnly);
        var before = l.HashMod();

        var ex = await Assert.ThrowsAsync<ReadOnlyFileException>(() => s.SaveAsync(doc, doc.SavedText + "--x\n"));
        Assert.Equal(Path.GetFullPath(dcx), ex.FilePath);
        Assert.Equal(before, l.HashMod()); // unpacked .lua untouched, no stray .tmp
        Assert.True(File.GetAttributes(dcx).HasFlag(FileAttributes.ReadOnly), "read-only flag must not be cleared silently");
        AssertBackupsMatch(s, l, before);
    }

    /// <summary>
    /// Unpacked .lua read-only: current design writes the .dcx, then reports a Warning (partial save).
    /// </summary>
    [RequiresTestDataFact]
    public async Task Save_ReadOnlyUnpackedLua_PartialSave_WarnsAndDcxHasEdit()
    {
        using var l = ModLayout.Create("ro-lua").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        string lua = l.ModUnpackedLua("m10_00_00_00", Owl);
        File.SetAttributes(lua, FileAttributes.ReadOnly);
        string luaHash = ModLayout.Hash(lua);
        string edited = doc.SavedText + "--x\n";

        var result = await s.SaveAsync(doc, edited);
        Assert.NotNull(result.Warning);
        Assert.Equal([l.ModPackedPath("m10_00_00_00")], result.WrittenFiles);
        Assert.Equal(edited, LuabndArchive.LoadPacked(l.ModPackedPath("m10_00_00_00")).Find(Owl)!.GetText());
        Assert.Equal(luaHash, ModLayout.Hash(lua));
        Assert.True(File.GetAttributes(lua).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.EnumerateFiles(l.ModScript, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Follow-up of the partial save above: the unpacked folder is stale for Owl, and the next save of ANY script in
    /// the same map repacks from the unpacked folder — silently reverting the Owl edit inside the .dcx (data loss).
    /// </summary>
    [RequiresTestDataFact]
    public async Task PartialSave_ThenSaveOtherScriptInSameMap_DoesNotRevertFirstEdit()
    {
        using var l = ModLayout.Create("ro-lua-loss").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl), ModLayout.Script("m10_00_00_00", OwlOther));
        var owl = await s.OpenAsync(s.Project.Scripts[0]);
        var other = await s.OpenAsync(s.Project.Scripts[1]);
        File.SetAttributes(l.ModUnpackedLua("m10_00_00_00", Owl), FileAttributes.ReadOnly);

        string owlEdited = owl.SavedText + "-- owl edit\n";
        Assert.NotNull((await s.SaveAsync(owl, owlEdited)).Warning);
        owl.MarkSaved(owlEdited);
        await s.SaveAsync(other, other.SavedText + "-- other edit\n");

        var packed = LuabndArchive.LoadPacked(l.ModPackedPath("m10_00_00_00"));
        Assert.EndsWith("-- other edit\n", packed.Find(OwlOther)!.GetText());
        Assert.Equal(owlEdited, packed.Find(Owl)!.GetText());
    }

    /// <summary>_battle + _logic style: two scripts of the same map saved concurrently must both survive.</summary>
    [RequiresTestDataFact]
    public async Task ConcurrentSaves_SameLuabnd_BothEditsSurvive()
    {
        using var l = ModLayout.Create("concurrent").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl), ModLayout.Script("m10_00_00_00", OwlOther));
        var a = await s.OpenAsync(s.Project.Scripts[0]);
        var b = await s.OpenAsync(s.Project.Scripts[1]);
        for (int round = 0; round < 3; round++)
        {
            string ta = a.SavedText + $"-- A{round}\n", tb = b.SavedText + $"-- B{round}\n";
            var results = await Task.WhenAll(Task.Run(() => s.SaveAsync(a, ta)), Task.Run(() => s.SaveAsync(b, tb)));
            Assert.All(results, r => Assert.Null(r.Warning));
            a.MarkSaved(ta); b.MarkSaved(tb);
            var packed = LuabndArchive.LoadPacked(l.ModPackedPath("m10_00_00_00"));
            Assert.Equal(ta, packed.Find(Owl)!.GetText());
            Assert.Equal(tb, packed.Find(OwlOther)!.GetText());
            Assert.Equal(ta, File.ReadAllText(l.ModUnpackedLua("m10_00_00_00", Owl)));
            Assert.Equal(tb, File.ReadAllText(l.ModUnpackedLua("m10_00_00_00", OwlOther)));
        }
        Assert.Empty(Directory.EnumerateFiles(l.ModScript, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>Same as above but two independent ProjectSession instances (e.g. two app windows) — the lock is per session.</summary>
    [RequiresTestDataFact]
    public async Task ConcurrentSaves_DifferentLuabnds_Independent()
    {
        using var l = ModLayout.Create("concurrent2").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00")
            .ModPacked("m11_02_00_00").ModUnpacked("m11_02_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl), ModLayout.Script("m11_02_00_00", "540300_battle.lua"));
        var a = await s.OpenAsync(s.Project.Scripts[0]);
        var b = await s.OpenAsync(s.Project.Scripts[1]);
        await Task.WhenAll(s.SaveAsync(a, a.SavedText + "--a\n"), s.SaveAsync(b, b.SavedText + "--b\n"));
        Assert.EndsWith("--a\n", LuabndArchive.LoadPacked(l.ModPackedPath("m10_00_00_00")).Find(Owl)!.GetText());
        Assert.EndsWith("--b\n", LuabndArchive.LoadPacked(l.ModPackedPath("m11_02_00_00")).Find("540300_battle.lua")!.GetText());
    }

    /// <summary>Whatever backups exist after a failed save must hold the (unchanged) current content.</summary>
    private static void AssertBackupsMatch(ProjectSession s, ModLayout l, Dictionary<string, string> modHashes)
    {
        foreach (string dir in s.Backups.ListSnapshots().Append(s.Backups.OriginalDir).Where(Directory.Exists))
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, f);
                Assert.True(modHashes.TryGetValue(rel, out string? h), $"backup of unknown file {rel}");
                Assert.Equal(h, ModLayout.Hash(f));
            }
    }

    // ---------- repeated saves ----------

    [RequiresTestDataFact]
    public async Task SaveManyTimes_OriginalUntouched_PrunedToKeepSnapshots_EachSnapshotHoldsPreviousVersion()
    {
        using var l = ModLayout.Create("many").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl));
        s.Backups.KeepSnapshots = 3;
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        string pristine = doc.SavedText;
        string origPackedHash = ModLayout.Hash(l.ModPackedPath("m10_00_00_00"));

        const int saves = 6;
        for (int i = 1; i <= saves; i++)
            await s.SaveAsync(doc, pristine + $"-- save {i}\n");

        var snaps = s.Backups.ListSnapshots();
        Assert.Equal(3, snaps.Count);
        Assert.Equal(snaps.Count, snaps.Select(Path.GetFileName).Distinct().Count());

        string Rel(string root) => Path.Combine(root, "script", "m10_00_00_00-luabnd-dcx", "out", "bin", Owl);
        Assert.Equal(pristine, File.ReadAllText(Rel(s.Backups.OriginalDir)));
        Assert.Equal(origPackedHash, ModLayout.Hash(Path.Combine(s.Backups.OriginalDir, "script", "m10_00_00_00.luabnd.dcx")));
        // Newest snapshot = state before save 6 = text of save 5, and so on.
        for (int k = 0; k < 3; k++)
            Assert.Equal(pristine + $"-- save {saves - 1 - k}\n", File.ReadAllText(Rel(snaps[k])));

        Assert.Equal(pristine + $"-- save {saves}\n", File.ReadAllText(l.ModUnpackedLua("m10_00_00_00", Owl)));
        Assert.Equal(pristine + $"-- save {saves}\n", LuabndArchive.LoadPacked(l.ModPackedPath("m10_00_00_00")).Find(Owl)!.GetText());
    }

    // ---------- unpacked folder newer than .dcx (user edited in VSCode) ----------

    [RequiresTestDataFact]
    public async Task UnpackedNewerThanDcx_OpenShowsUnpacked_SaveKeepsOtherExternalEdits()
    {
        using var l = ModLayout.Create("newer").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        string owlLua = l.ModUnpackedLua("m10_00_00_00", Owl);
        string otherLua = l.ModUnpackedLua("m10_00_00_00", OwlOther);
        File.AppendAllText(owlLua, "-- vscode edit owl\n");
        File.AppendAllText(otherLua, "-- vscode edit 506000\n");
        File.SetLastWriteTimeUtc(l.ModPackedPath("m10_00_00_00"), DateTime.UtcNow.AddHours(-1));

        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl), ModLayout.Script("m10_00_00_00", OwlOther));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        Assert.Equal(LuabndSource.ModUnpacked, doc.LoadedFrom);
        Assert.EndsWith("-- vscode edit owl\n", doc.SavedText);
        Assert.EndsWith("-- vscode edit 506000\n", (await s.OpenAsync(s.Project.Scripts[1])).SavedText);

        await s.SaveAsync(doc, doc.SavedText + "-- app edit\n");

        var packed = LuabndArchive.LoadPacked(l.ModPackedPath("m10_00_00_00"));
        Assert.EndsWith("-- vscode edit owl\n-- app edit\n", packed.Find(Owl)!.GetText());
        Assert.EndsWith("-- vscode edit 506000\n", packed.Find(OwlOther)!.GetText()); // not lost
        Assert.EndsWith("-- vscode edit 506000\n", File.ReadAllText(otherLua));
    }

    [RequiresTestDataFact]
    public async Task Save_WithStaleDocument_UsesNewText_NotFileOnDisk()
    {
        // The editor buffer wins over a concurrent external edit of the SAME file (conflict UI is the app's job);
        // documents the current behavior so a change is noticed.
        using var l = ModLayout.Create("stale").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        File.AppendAllText(l.ModUnpackedLua("m10_00_00_00", Owl), "-- external\n");
        await s.SaveAsync(doc, doc.SavedText + "-- mine\n");
        string onDisk = File.ReadAllText(l.ModUnpackedLua("m10_00_00_00", Owl));
        Assert.EndsWith("-- mine\n", onDisk);
        Assert.DoesNotContain("-- external", onDisk);
        // ...but the external version is recoverable from the snapshot.
        string snap = Path.Combine(s.Backups.ListSnapshots()[0], "script", "m10_00_00_00-luabnd-dcx", "out", "bin", Owl);
        Assert.EndsWith("-- external\n", File.ReadAllText(snap));
    }

    [RequiresTestDataFact]
    public async Task Save_NeverWritesGameFolder()
    {
        using var l = ModLayout.Create("game-ro").GamePacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl));
        string gameHash = ModLayout.Hash(l.GamePackedPath("m10_00_00_00"));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        var written = (await s.SaveAsync(doc, doc.SavedText + "--g\n")).WrittenFiles;
        Assert.All(written, w => Assert.StartsWith(l.Mod, w));
        Assert.Equal(gameHash, ModLayout.Hash(l.GamePackedPath("m10_00_00_00")));
        Assert.Equal(new[] { "test.sasproj" }, Directory.EnumerateFiles(Path.Combine(l.Work.Path, "proj")).Select(Path.GetFileName));
        Assert.Single(Directory.EnumerateFiles(l.GameScript));
    }

    [RequiresTestDataFact]
    public async Task Save_TextWithCrlfAndUnicode_RoundTripsExactly()
    {
        using var l = ModLayout.Create("enc").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", Owl));
        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        string edited = doc.SavedText + "-- 危 perilous\r\n";
        await s.SaveAsync(doc, edited);
        var reopened = await l.Session(ModLayout.Script("m10_00_00_00", Owl)).OpenAsync(new ProjectScript { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\" + Owl });
        Assert.Equal(edited, reopened.SavedText);
        byte[] raw = File.ReadAllBytes(l.ModUnpackedLua("m10_00_00_00", Owl));
        Assert.False(raw[0] == 0xEF, "no BOM written");
    }
}
