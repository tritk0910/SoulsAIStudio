using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsFormats;
using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.Formats;

/// <summary>
/// Characterization of Sekiro luabnd as read/written by SoulsFormats directly.
/// These pin down the behavior the Core luabnd reader/writer (CLAUDE.md P0 #4, 7.1) must preserve.
/// Facts verified on the test-data backup (2026-11):
///  - all 4 luabnds: BND4, version "07D7R6", format IDs|Names1|Names2|Compression, unicode, extended=4, every entry Flag1;
///  - entry names = "N:\NTC\data\Target\INTERROOT_win64\script\ai\" + relative path (xml &lt;root&gt; + &lt;path&gt;);
///  - compression: m10/m11/aicommon = DCX_DFLT (0x11000,0x44,0x4C,9,0); m25 = DCX_KRAK level 6 (needs Oodle);
///  - mostly bytecode (ESC "Lua" 0x50); only the modded scripts are plain text.
/// </summary>
public class LuabndBaselineTests(ITestOutputHelper output)
{
    public sealed record Expected(int Entries, int LuaEntries, string[] PlainText, DCX.Type Compression, int GnlId);

    public static readonly IReadOnlyDictionary<string, Expected> Baseline = new Dictionary<string, Expected>
    {
        ["m10_00_00_00"] = new(58, 56, ["506000_battle.lua", "506300_battle.lua"], DCX.Type.DCX_DFLT, 1000000),
        ["m11_02_00_00"] = new(58, 56, ["540000_battle.lua", "540010_battle.lua", "540300_battle.lua", "540310_battle.lua",
                                         "540400_battle.lua", "540410_battle.lua", "540420_battle.lua"], DCX.Type.DCX_DFLT, 10000000),
        ["m25_00_00_00"] = new(69, 67, ["500000_battle.lua"], DCX.Type.DCX_KRAK, 1000000),
        ["aicommon"] = new(107, 105, ["147001_battle.lua", "goal_list.lua"], DCX.Type.DCX_DFLT, 1000000),
    };

    public const string Root = @"N:\NTC\data\Target\INTERROOT_win64\script\ai";

    // ---- data sources (KRAK rows only when Oodle is loadable; see Krak_OodleAvailable) ----
    public static TheoryData<string> Luabnds() => Filter(Samples.AllLuabnds);
    public static TheoryData<string> UnpackedMaps() => Filter(Samples.UnpackedMaps);

    private static TheoryData<string> Filter(IEnumerable<string> names)
    {
        var td = new TheoryData<string>();
        foreach (var n in names)
            if (Baseline[n].Compression != DCX.Type.DCX_KRAK || OodleSupport.Available) td.Add(n);
        return td;
    }

    internal static BND4 ReadLuabnd(string name)
    {
        OodleSupport.EnsureLoaded();
        // Read via bytes: never hold a handle on the original longer than needed.
        return BND4.Read(File.ReadAllBytes(Samples.LuabndPath(name)));
    }

    /// <summary>
    /// Visible notice: when Oodle is missing, m25 (DCX_KRAK) rows are dropped from the theories below
    /// and this test is skipped with the reason.
    /// </summary>
    [RequiresOodleFact]
    public void Krak_OodleAvailable_m25IsReadable()
    {
        output.WriteLine("Oodle loaded from: " + OodleSupport.LoadedFrom);
        var bnd = ReadLuabnd("m25_00_00_00");
        var krak = Assert.IsType<DCX.DcxKrakCompressionInfo>(bnd.Compression);
        Assert.Equal(6, krak.CompressionLevel);
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(Luabnds))]
    public void Read_HeaderAndEntries_MatchBaseline(string name)
    {
        var exp = Baseline[name];
        var bnd = ReadLuabnd(name);

        Assert.Equal(exp.Compression, bnd.Compression.Type);
        if (bnd.Compression is DCX.DcxDfltCompressionInfo d)
            Assert.Equal((0x11000, 0x44, 0x4C, (byte)9, (byte)0), (d.Unk04, d.Unk10, d.Unk14, d.Unk30, d.Unk38));

        Assert.Equal("07D7R6", bnd.Version);
        Assert.Equal(Binder.Format.IDs | Binder.Format.Names1 | Binder.Format.Names2 | Binder.Format.Compression, bnd.Format);
        Assert.False(bnd.BigEndian);
        Assert.False(bnd.BitBigEndian);
        Assert.True(bnd.Unicode);
        Assert.Equal(4, bnd.Extended);
        Assert.False(bnd.Unk04);
        Assert.False(bnd.Unk05);

        Assert.Equal(exp.Entries, bnd.Files.Count);
        Assert.All(bnd.Files, f => Assert.Equal(Binder.FileFlags.Flag1, f.Flags));
        Assert.All(bnd.Files, f => Assert.StartsWith(Root + @"\", f.Name));
        Assert.Equal(bnd.Files.Count, bnd.Files.Select(f => f.ID).Distinct().Count());
        Assert.Equal(bnd.Files.Count, bnd.Files.Select(f => f.Name.ToLowerInvariant()).Distinct().Count());
        Assert.True(bnd.Files.Select(f => f.ID).SequenceEqual(bnd.Files.Select(f => f.ID).Order()), "entry IDs not ascending in BND order");

        var lua = bnd.Files.Where(f => f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(exp.LuaEntries, lua.Count);
        Assert.All(lua, f => Assert.StartsWith(Root + @"\out\bin\", f.Name));

        // .luagnl / .luainfo are the last two entries, at <GnlId>, <GnlId+1>, directly under root.
        var gnl = bnd.Files[^2];
        var info = bnd.Files[^1];
        Assert.EndsWith(".luagnl", gnl.Name, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".luainfo", info.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(exp.GnlId, gnl.ID);
        Assert.Equal(exp.GnlId + 1, info.ID);

        output.WriteLine($"{name}: {bnd.Files.Count} entries, IDs {bnd.Files[0].ID}..{lua[^1].ID} (+{gnl.ID},{info.ID}), {bnd.Compression.Type}");
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(Luabnds))]
    public void LuaEntries_PlainTextVsBytecode(string name)
    {
        var exp = Baseline[name];
        var bnd = ReadLuabnd(name);
        var lua = bnd.Files.Where(f => f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();

        var plain = lua.Where(f => !Samples.IsLuaBytecode(f.Bytes)).Select(f => Path.GetFileName(f.Name.Replace('\\', '/'))).Order(StringComparer.Ordinal).ToArray();
        var bytecode = lua.Where(f => Samples.IsLuaBytecode(f.Bytes)).ToList();

        output.WriteLine($"{name}: {lua.Count} lua = {plain.Length} plain text + {bytecode.Count} bytecode; plain: {string.Join(", ", plain)}");
        Assert.Equal(exp.PlainText.Order(StringComparer.Ordinal), plain);
        // HavokScript header: ESC 'L' 'u' 'a' 0x50 for every bytecode entry.
        Assert.All(bytecode, f => Assert.Equal(0x50, f.Bytes[4]));
        // Plain text entries: ASCII only, LF line endings, no BOM, trailing newline.
        Assert.All(lua.Where(f => !Samples.IsLuaBytecode(f.Bytes)), f =>
        {
            Assert.DoesNotContain(f.Bytes, b => b >= 0x80);
            Assert.DoesNotContain((byte)'\r', f.Bytes);
            Assert.Equal((byte)'\n', f.Bytes[^1]);
        });
    }

    /// <summary>
    /// Compares each BND entry with the WitchyBND-unpacked file. In the backup they are identical
    /// (the user repacked after editing). In a live mod folder the unpacked folder may be NEWER —
    /// the Core must decide which is the source of truth; this test only pins the backup state.
    /// </summary>
    [RequiresTestDataTheory]
    [MemberData(nameof(UnpackedMaps))]
    public void UnpackedFolder_MatchesDcxEntries(string map)
    {
        var bnd = ReadLuabnd(map);
        string dir = Samples.UnpackedDir(map);
        var mismatches = new List<string>();
        foreach (var f in bnd.Files)
        {
            string rel = f.Name[(Root.Length + 1)..];
            string disk = Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(disk)) mismatches.Add($"missing on disk: {rel}");
            else if (!File.ReadAllBytes(disk).AsSpan().SequenceEqual(f.Bytes)) mismatches.Add($"content differs: {rel}");
        }
        var expectedOnDisk = bnd.Files.Select(f => f.Name[(Root.Length + 1)..].ToLowerInvariant()).Append("_witchy-bnd4.xml").ToHashSet();
        var extra = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p).Replace('/', '\\').ToLowerInvariant())
            .Where(p => !expectedOnDisk.Contains(p)).ToList();
        mismatches.AddRange(extra.Select(e => $"extra on disk: {e}"));

        output.WriteLine($"{map}: {bnd.Files.Count} entries checked, {mismatches.Count} mismatches");
        foreach (var m in mismatches) output.WriteLine("  " + m);
        Assert.Empty(mismatches);
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(UnpackedMaps))]
    public void WitchyXml_AgreesWithBnd(string map)
    {
        var bnd = ReadLuabnd(map);
        var xml = WitchyBnd4Xml.Load(Samples.WitchyXmlPath(map));

        Assert.Equal(map + ".luabnd.dcx", xml.Filename);
        Assert.Equal(Root, xml.Root);
        Assert.Equal(bnd.Version, xml.Version);
        Assert.Equal(bnd.Format, xml.Format);
        Assert.Equal(bnd.BigEndian, xml.BigEndian);
        Assert.Equal(bnd.BitBigEndian, xml.BitBigEndian);
        Assert.Equal(bnd.Unicode, xml.Unicode);
        Assert.Equal(bnd.Extended, xml.Extended);
        Assert.Equal(bnd.Unk04, xml.Unk04);
        Assert.Equal(bnd.Unk05, xml.Unk05);

        Assert.Equal(bnd.Compression.Type.ToString(), xml.Compression);
        switch (bnd.Compression)
        {
            case DCX.DcxDfltCompressionInfo d:
                Assert.Equal((d.Unk04, d.Unk10, d.Unk14, d.Unk30, d.Unk38), (xml.DfltUnk04, xml.DfltUnk10, xml.DfltUnk14, xml.DfltUnk30, xml.DfltUnk38));
                break;
            case DCX.DcxKrakCompressionInfo k:
                Assert.Equal(k.CompressionLevel, xml.CompressionLevel);
                break;
        }

        // Same entries, same ORDER (xml order == BND order), same ID/flags/name.
        Assert.Equal(bnd.Files.Count, xml.Files.Count);
        for (int i = 0; i < bnd.Files.Count; i++)
        {
            Assert.Equal(bnd.Files[i].ID, xml.Files[i].Id);
            Assert.Equal(bnd.Files[i].Flags, xml.Files[i].Flags);
            Assert.Equal(bnd.Files[i].Name, xml.Files[i].BndName(xml.Root));
        }
    }

    /// <summary>read → write (same compression) to temp → read: identical entries; uncompressed BND4 byte-identical.</summary>
    [RequiresTestDataTheory]
    [MemberData(nameof(Luabnds))]
    public void RoundTrip_ReadWriteRead_PreservesEntries(string name)
    {
        byte[] raw = File.ReadAllBytes(Samples.LuabndPath(name));
        var bnd = ReadLuabnd(name);

        using var work = TempWorkDir.Create("rt-" + name);
        string outPath = work.Combine(name + ".luabnd.dcx");
        TestPaths.AssertWritable(outPath);
        bnd.Write(outPath, bnd.Compression);

        byte[] written = File.ReadAllBytes(outPath);
        var back = BND4.Read(written);
        AssertSameEntries(bnd, back);
        Assert.Equal(bnd.Compression.Type, back.Compression.Type);

        bool decompressedIdentical = DCX.Decompress(raw).AsSpan().SequenceEqual(DCX.Decompress(written));
        bool dcxIdentical = raw.AsSpan().SequenceEqual(written);
        output.WriteLine($"{name}: {bnd.Compression.Type} decompressed BND4 identical={decompressedIdentical}, .dcx byte-identical={dcxIdentical} ({raw.Length} -> {written.Length} bytes)");
        Assert.True(decompressedIdentical, "SoulsFormats BND4 writer must reproduce the uncompressed BND4 exactly");
        // NOTE (characterization, not asserted): DCX_DFLT output is NOT byte-identical to WitchyBND's
        // (different zlib stream); DCX_KRAK (Oodle level 6) was byte-identical on this machine.
    }

    /// <summary>
    /// The "repack" the app needs: build a BND4 purely from the unpacked folder + _witchy-bnd4.xml
    /// (in a temp copy), write it, and read it back → same entries as the original .luabnd.dcx.
    /// </summary>
    [RequiresTestDataTheory]
    [MemberData(nameof(UnpackedMaps))]
    public void Repack_FromUnpackedFolderAndXml_MatchesOriginal(string map)
    {
        using var work = TempWorkDir.Create("repack-" + map);
        string dir = work.CopyScript(map + "-luabnd-dcx");
        var xml = WitchyBnd4Xml.Load(Path.Combine(dir, "_witchy-bnd4.xml"));

        var rebuilt = xml.BuildBnd4(dir);
        string outPath = work.Combine(xml.Filename);
        TestPaths.AssertWritable(outPath);
        rebuilt.Write(outPath);

        var original = ReadLuabnd(map);
        byte[] origRaw = File.ReadAllBytes(Samples.LuabndPath(map));
        byte[] outRaw = File.ReadAllBytes(outPath);
        var back = BND4.Read(outRaw);

        AssertSameEntries(original, back);
        Assert.Equal(original.Compression.Type, back.Compression.Type);
        Assert.Equal(original.Version, back.Version);
        Assert.Equal(original.Format, back.Format);

        bool decompressedIdentical = DCX.Decompress(origRaw).AsSpan().SequenceEqual(DCX.Decompress(outRaw));
        output.WriteLine($"{map}: repack {xml.Compression}: decompressed identical={decompressedIdentical}, .dcx byte-identical={origRaw.AsSpan().SequenceEqual(outRaw)}");
        Assert.True(decompressedIdentical, "repack from unpacked+xml should reproduce the uncompressed BND4 exactly");
    }

    [RequiresTestDataFact]
    public void KeyBattleScripts_ArePlainTextInBndAndOnDisk()
    {
        foreach (var (map, npc) in new[] { ("m10_00_00_00", Samples.Owl), ("m11_02_00_00", Samples.Isshin), ("m25_00_00_00", Samples.TrueMonk) })
        {
            Assert.False(Samples.IsLuaBytecode(File.ReadAllBytes(Samples.BattleLua(map, npc))), $"{npc}_battle.lua on disk is bytecode");
            if (Baseline[map].Compression == DCX.Type.DCX_KRAK && !OodleSupport.Available) continue;
            var entry = ReadLuabnd(map).Files.Single(f => f.Name.EndsWith($@"\{npc}_battle.lua"));
            Assert.False(Samples.IsLuaBytecode(entry.Bytes));
        }
    }

    internal static void AssertSameEntries(BND4 expected, BND4 actual)
    {
        Assert.Equal(expected.Files.Count, actual.Files.Count);
        for (int i = 0; i < expected.Files.Count; i++)
        {
            var e = expected.Files[i];
            var a = actual.Files[i];
            Assert.Equal(e.ID, a.ID);
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Flags, a.Flags);
            Assert.True(e.Bytes.AsSpan().SequenceEqual(a.Bytes), $"bytes differ for entry {e.ID} {e.Name}");
        }
    }
}
