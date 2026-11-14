using System.Xml.Linq;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsFormats;

namespace SoulsAiStudio.Core.Tests.Formats;

/// <summary>In-app unpack/repack of luabnds, for WitchyBND and Yabber folders.</summary>
public class UnpackRepackTests
{
    private const string Map = "m10_00_00_00"; // DCX_DFLT, no Oodle needed

    private static void AssertSameBnd(BND4 expected, BND4 actual)
    {
        Assert.Equal(expected.Files.Select(f => (f.ID, f.Flags, f.Name)), actual.Files.Select(f => (f.ID, f.Flags, f.Name)));
        for (int i = 0; i < expected.Files.Count; i++)
            Assert.True(expected.Files[i].Bytes.AsSpan().SequenceEqual(actual.Files[i].Bytes), expected.Files[i].Name);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.Extended, actual.Extended);
        var e = Assert.IsType<DCX.DcxDfltCompressionInfo>(expected.Compression);
        var a = Assert.IsType<DCX.DcxDfltCompressionInfo>(actual.Compression);
        Assert.Equal((e.Unk04, e.Unk10, e.Unk14, e.Unk30, e.Unk38), (a.Unk04, a.Unk10, a.Unk14, a.Unk30, a.Unk38));
    }

    [RequiresTestDataFact]
    public void UnpackTo_WritesWitchyFolder_SameAsWitchyBnd()
    {
        using var work = TempWorkDir.Create("unpack");
        string dir = Path.Combine(work.Path, Map + "-luabnd-dcx");
        LuabndArchive.LoadPacked(Samples.LuabndPath(Map)).UnpackTo(dir, Map + ".luabnd.dcx");

        // Manifest matches the one WitchyBND wrote (independent test-side reader).
        var ours = WitchyBnd4Xml.Load(Path.Combine(dir, WitchyBnd4Manifest.FileName));
        var witchy = WitchyBnd4Xml.Load(Samples.WitchyXmlPath(Map));
        Assert.Equal(witchy with { Files = [] }, ours with { Files = [] });
        Assert.Equal(witchy.Files, ours.Files);
        // Same files with the same bytes.
        foreach (var f in witchy.Files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(Samples.UnpackedDir(Map), f.Path)), File.ReadAllBytes(Path.Combine(dir, f.Path)));
        // And it repacks to the original archive.
        AssertSameBnd(BND4.Read(Samples.LuabndPath(Map)), BND4.Read(LuabndArchive.LoadUnpacked(dir).ToPackedBytes()));
    }

    /// <summary>Builds a folder laid out like Yabber 1.3: per-file &lt;root&gt; (drive), the rest of the name as path, legacy DCX name.</summary>
    private static string MakeYabberFolder(string parent)
    {
        string dir = Path.Combine(parent, Map + "-luabnd-dcx");
        var bnd = BND4.Read(Samples.LuabndPath(Map));
        var files = new XElement("files");
        foreach (var f in bnd.Files)
        {
            string root = f.Name[..3];           // "N:\"
            string path = f.Name[3..];           // "NTC\data\Target\INTERROOT_win64\script\ai\out\bin\x.lua"
            string disk = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            File.WriteAllBytes(disk, f.Bytes);
            files.Add(new XElement("file", new XElement("flags", f.Flags.ToString()), new XElement("id", f.ID),
                new XElement("root", root), new XElement("path", path)));
        }
        new XDocument(new XElement("bnd4",
            new XElement("filename", Map + ".luabnd.dcx"),
            new XElement("compression", "DCX_DFLT_11000_44_9"),
            new XElement("version", bnd.Version),
            new XElement("format", bnd.Format.ToString()),
            new XElement("bigendian", "False"), new XElement("bitbigendian", "False"), new XElement("unicode", "True"),
            new XElement("extended", $"0x{bnd.Extended:X2}"), new XElement("unk04", "False"), new XElement("unk05", "False"),
            files)).Save(Path.Combine(dir, WitchyBnd4Manifest.YabberFileName));
        return dir;
    }

    [RequiresTestDataFact]
    public void YabberFolder_LoadsAndRepacksToOriginal()
    {
        using var work = TempWorkDir.Create("yabber");
        string dir = MakeYabberFolder(work.Path);
        var archive = LuabndArchive.LoadUnpacked(dir);
        Assert.Equal(UnpackFormat.Yabber, archive.UnpackedFormat);
        var packed = LuabndArchive.LoadPacked(Samples.LuabndPath(Map));
        Assert.Equal(packed.Entries.Select(e => e.RelativePath), archive.Entries.Select(e => e.RelativePath)); // out\bin\x.lua
        Assert.StartsWith(@"NTC\", archive.Find("506300_battle.lua")!.DiskPath);
        AssertSameBnd(BND4.Read(Samples.LuabndPath(Map)), BND4.Read(archive.ToPackedBytes()));
    }

    [Fact]
    public void Manifest_RejectsPathsOutsideFolder()
    {
        using var work = TempWorkDir.Create("badmanifest");
        File.WriteAllText(Path.Combine(work.Path, WitchyBnd4Manifest.FileName), """
            <bnd4><filename>x.luabnd.dcx</filename><compression>None</compression><version>07D7R6</version>
            <format>IDs, Names1, Names2, Compression</format><files><file><flags>Flag1</flags><id>1</id><path>..\..\evil.lua</path></file></files></bnd4>
            """);
        Assert.Throws<FormatException>(() => LuabndArchive.LoadUnpacked(work.Path));
    }

    [RequiresTestDataFact]
    public async Task Session_RepackFromEditedFolder_BacksUpAndWrites()
    {
        using var l = ModLayout.Create("repack").ModPacked(Map).ModUnpacked(Map);
        var session = l.Session();
        string lua = l.ModUnpackedLua(Map, Samples.Owl + "_battle.lua");
        string edited = File.ReadAllText(lua) + "-- edited in VSCode\n";
        File.WriteAllText(lua, edited);
        File.SetLastWriteTimeUtc(lua, DateTime.UtcNow.AddMinutes(1));
        Assert.True(session.Catalog.Status(Map).NeedsRepack);

        string written = await session.RepackAsync(Map);
        Assert.Equal(l.ModPackedPath(Map), written);
        Assert.Equal(edited, LuabndArchive.LoadPacked(written).Find(Samples.Owl + "_battle.lua")!.GetText());
        Assert.True(File.Exists(Path.Combine(session.Project.BackupDir, "original", "script", Map + ".luabnd.dcx")));
        var st = session.Catalog.Status(Map);
        Assert.Equal(UnpackFormat.Witchy, st.Unpacked);
        Assert.False(st.NeedsRepack);
        Assert.False(st.PackedIsNewer); // "in sync" right after a repack

        // Read-only .dcx: refused before any backup is taken.
        int backups = Directory.GetDirectories(session.Project.BackupDir).Length;
        File.SetAttributes(written, FileAttributes.ReadOnly);
        try { await Assert.ThrowsAsync<SoulsAiStudio.Core.IO.ReadOnlyFileException>(() => session.RepackAsync(Map)); }
        finally { File.SetAttributes(written, FileAttributes.Normal); }
        Assert.Equal(backups, Directory.GetDirectories(session.Project.BackupDir).Length);
    }

    [RequiresTestDataFact]
    public async Task Session_UnpackFromGame_ThenRefusesToOverwrite()
    {
        using var l = ModLayout.Create("unpack-session").GamePacked(Map);
        var session = l.Session();
        Assert.Null(session.Catalog.Status(Map).Unpacked);
        string dir = await session.UnpackAsync(Map);
        Assert.Equal(l.ModUnpackedDir(Map), dir);
        Assert.Equal(LuabndSource.ModUnpacked, session.Catalog.Locate(Map));
        Assert.True(File.Exists(Path.Combine(l.Game, "script", Map + ".luabnd.dcx"))); // game untouched
        Assert.True(session.Catalog.Status(Map).NeedsRepack); // no mod .dcx yet
        await Assert.ThrowsAsync<IOException>(() => session.UnpackAsync(Map));
    }

    [RequiresTestDataFact]
    public async Task Session_UnpackModDcx_IsInSync()
    {
        using var l = ModLayout.Create("unpack-mod").ModPacked(Map);
        var session = l.Session();
        await session.UnpackAsync(Map);
        var st = session.Catalog.Status(Map);
        Assert.Equal(UnpackFormat.Witchy, st.Unpacked);
        Assert.False(st.NeedsRepack);
        Assert.False(st.PackedIsNewer);
    }

    [RequiresTestDataFact]
    public async Task Session_SaveScript_UpdatesYabberFolderFile()
    {
        using var l = ModLayout.Create("yabber-save").ModPacked(Map);
        MakeYabberFolder(l.ModScript);
        var script = ModLayout.Script(Map, Samples.Owl + "_battle.lua");
        var session = l.Session(script);
        var doc = await session.OpenAsync(script);
        string text = doc.SavedText + "-- saved in app\n";
        var result = await session.SaveAsync(doc, text);
        Assert.Null(result.Warning);
        string yabberFile = Path.Combine(l.ModUnpackedDir(Map), @"NTC\data\Target\INTERROOT_win64\script\ai\out\bin", Samples.Owl + "_battle.lua");
        Assert.Equal(text, File.ReadAllText(yabberFile).ReplaceLineEndings("\n"));
        Assert.Equal(text, LuabndArchive.LoadPacked(l.ModPackedPath(Map)).Find(Samples.Owl + "_battle.lua")!.GetText());
    }
}
