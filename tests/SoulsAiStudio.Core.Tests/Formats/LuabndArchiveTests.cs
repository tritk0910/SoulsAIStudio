using SoulsAiStudio.Core.Formats;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsFormats;

namespace SoulsAiStudio.Core.Tests.Formats;

public class LuabndArchiveTests
{
    private static void EnsureOodle() => OodleLoader.TryLoad(TestPaths.SekiroInstallDir);

    private static bool NeedsOodleButMissing(string name)
    {
        EnsureOodle();
        return name == "m25_00_00_00" && !OodleLoader.IsLoaded;
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(Samples.UnpackedMapNames), MemberType = typeof(Samples))]
    public void PackedAndUnpacked_LoadToSameEntries(string map)
    {
        if (NeedsOodleButMissing(map)) return;
        var packed = LuabndArchive.LoadPacked(Samples.LuabndPath(map));
        var unpacked = LuabndArchive.LoadUnpacked(Samples.UnpackedDir(map));

        Assert.Equal(unpacked.Header.Root, packed.Header.Root);
        Assert.Equal(unpacked.Header.Compression.Type, packed.Header.Compression.Type);
        Assert.Equal(unpacked.Entries.Select(e => (e.Id, e.Flags, e.RelativePath)),
                     packed.Entries.Select(e => (e.Id, e.Flags, e.RelativePath)));
        for (int i = 0; i < packed.Entries.Count; i++)
            Assert.True(packed.Entries[i].Bytes.AsSpan().SequenceEqual(unpacked.Entries[i].Bytes), packed.Entries[i].RelativePath);
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(Samples.UnpackedMapNames), MemberType = typeof(Samples))]
    public void EditOneScript_SavePacked_RoundTripsWithSameCompression(string map)
    {
        if (NeedsOodleButMissing(map)) return;
        using var work = TempWorkDir.Create("luabnd-save");
        string copy = work.CopyScript(map + ".luabnd.dcx");

        var archive = LuabndArchive.LoadPacked(copy);
        var original = BND4.Read(copy);
        LuabndEntry target = archive.LuaScripts.First(e => !e.IsBytecode);
        string edited = target.GetText() + "-- edited by test\n";
        target.SetText(edited);
        Assert.True(archive.IsModified);

        archive.SavePacked(copy);
        Assert.False(archive.IsModified);

        var reread = LuabndArchive.LoadPacked(copy);
        Assert.Equal(original.Compression.Type, reread.Header.Compression.Type);
        Assert.Equal(archive.Entries.Count, reread.Entries.Count);
        Assert.Equal(edited, reread.Find(target.RelativePath)!.GetText());
        // Every other entry (incl. bytecode) is untouched, byte for byte.
        foreach (var e in reread.Entries.Where(e => e.RelativePath != target.RelativePath))
            Assert.True(e.Bytes.AsSpan().SequenceEqual(original.Files.First(f => f.ID == e.Id).Bytes), e.RelativePath);
        // Full BND entry names are preserved (root + relative path).
        Assert.Equal(original.Files.Select(f => f.Name), BND4.Read(copy).Files.Select(f => f.Name));
    }

    [RequiresTestDataFact]
    public void SaveUnpackedChanges_WritesOnlyModifiedFiles()
    {
        using var work = TempWorkDir.Create("luabnd-unpacked");
        string dir = work.CopyScript("m10_00_00_00-luabnd-dcx");
        var archive = LuabndArchive.LoadUnpacked(dir);
        var owl = archive.Find(Samples.Owl + "_battle.lua")!;
        Assert.False(owl.IsBytecode);

        owl.SetText(owl.GetText().Replace("\n", "\n", StringComparison.Ordinal)); // no-op edit
        Assert.False(archive.IsModified);

        owl.SetText(owl.GetText() + "-- x\n");
        var written = archive.SaveUnpackedChanges(dir);
        Assert.Single(written);
        Assert.EndsWith("506300_battle.lua", written[0]);
        Assert.EndsWith("-- x\n", File.ReadAllText(written[0]));
    }

    [RequiresTestDataFact]
    public void Find_AcceptsFileNameOrPathWithEitherSlash()
    {
        var a = LuabndArchive.LoadUnpacked(Samples.UnpackedDir("m10_00_00_00"));
        Assert.NotNull(a.Find("506300_battle.lua"));
        Assert.NotNull(a.Find("out/bin/506300_battle.lua"));
        Assert.NotNull(a.Find(@"OUT\BIN\506300_BATTLE.LUA"));
        Assert.Null(a.Find("does_not_exist.lua"));
    }

    [RequiresTestDataFact]
    public void Bytecode_GetText_Throws()
    {
        var a = LuabndArchive.LoadUnpacked(Samples.UnpackedDir("m10_00_00_00"));
        var bc = a.LuaScripts.First(e => e.IsBytecode);
        Assert.Throws<InvalidOperationException>(() => bc.GetText());
    }
}

public class AtomicFileTests
{
    [Fact]
    public void ReadOnlyTarget_IsRefused_AndLeftIntact()
    {
        using var work = TempWorkDir.Create("atomic");
        string f = work.Combine("ro.lua");
        File.WriteAllText(f, "old");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<SoulsAiStudio.Core.IO.ReadOnlyFileException>(() => SoulsAiStudio.Core.IO.AtomicFile.WriteAllText(f, "new"));
            Assert.Equal("old", File.ReadAllText(f));
            Assert.Single(Directory.GetFiles(work.Path)); // no temp file left behind
        }
        finally { File.SetAttributes(f, FileAttributes.Normal); }
    }

    [Fact]
    public void Write_ReplacesExistingFile()
    {
        using var work = TempWorkDir.Create("atomic");
        string f = work.Combine("a.lua");
        File.WriteAllText(f, "old");
        SoulsAiStudio.Core.IO.AtomicFile.WriteAllText(f, "new");
        Assert.Equal("new", File.ReadAllText(f));
        Assert.Single(Directory.GetFiles(work.Path));
    }
}
