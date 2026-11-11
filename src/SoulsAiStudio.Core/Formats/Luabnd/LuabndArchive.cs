using SoulsAiStudio.Core.IO;
using SoulsFormats;

namespace SoulsAiStudio.Core.Formats.Luabnd;

/// <summary>One file inside a luabnd (a Lua script, or the <c>.luagnl</c>/<c>.luainfo</c> tables).</summary>
public sealed class LuabndEntry
{
    internal LuabndEntry(int id, Binder.FileFlags flags, string relativePath, byte[] bytes)
    {
        Id = id;
        Flags = flags;
        RelativePath = relativePath;
        Bytes = bytes;
    }

    public int Id { get; }
    public Binder.FileFlags Flags { get; }

    /// <summary>Path relative to the archive root, backslash-separated, e.g. <c>out\bin\506300_battle.lua</c>.</summary>
    public string RelativePath { get; }

    public string FileName => RelativePath[(RelativePath.LastIndexOf('\\') + 1)..];
    public byte[] Bytes { get; private set; }
    public bool IsLua => RelativePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
    public bool IsBytecode => LuaText.IsBytecode(Bytes);

    /// <summary>True once <see cref="SetText"/> changed this entry since it was loaded.</summary>
    public bool IsModified { get; private set; }

    public string GetText() => LuaText.Decode(Bytes);

    public void SetText(string text)
    {
        byte[] data = LuaText.Encode(text);
        if (data.AsSpan().SequenceEqual(Bytes)) return;
        Bytes = data;
        IsModified = true;
    }

    internal void MarkSaved() => IsModified = false;

    /// <summary>Takes newer content from another copy of the same archive without marking the entry modified.</summary>
    internal void AdoptNewerBytes(byte[] bytes) => Bytes = bytes;
}

/// <summary>
/// A Sekiro luabnd (BND4, usually DCX-compressed) holding the AI scripts for one map, e.g.
/// <c>m10_00_00_00.luabnd.dcx</c>. Loads either the packed file or a WitchyBND-unpacked folder and writes back
/// with the original header and compression, so the result stays compatible with WitchyBND and the game.
/// </summary>
public sealed class LuabndArchive
{
    private readonly List<LuabndEntry> _entries;

    private LuabndArchive(LuabndHeader header, List<LuabndEntry> entries)
    {
        Header = header;
        _entries = entries;
    }

    public LuabndHeader Header { get; }
    public IReadOnlyList<LuabndEntry> Entries => _entries;
    public IEnumerable<LuabndEntry> LuaScripts => _entries.Where(e => e.IsLua);
    public bool IsModified => _entries.Any(e => e.IsModified);

    /// <summary>Finds an entry by relative path (case-insensitive, either slash direction) or by bare file name.</summary>
    public LuabndEntry? Find(string pathOrFileName)
    {
        string key = pathOrFileName.Replace('/', '\\');
        return _entries.FirstOrDefault(e => e.RelativePath.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? _entries.FirstOrDefault(e => e.FileName.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a packed <c>.luabnd.dcx</c>. DCX_KRAK archives need Oodle loaded (see <see cref="OodleLoader"/>).</summary>
    public static LuabndArchive LoadPacked(string path)
    {
        BND4 bnd = BND4.Read(path);
        string root = CommonRoot(bnd.Files.Select(f => f.Name ?? ""));
        var header = new LuabndHeader(bnd.Version, bnd.Format, bnd.BigEndian, bnd.BitBigEndian, bnd.Unicode,
            bnd.Extended, bnd.Unk04, bnd.Unk05, bnd.Compression, root);
        var entries = bnd.Files.Select(f => new LuabndEntry(f.ID, f.Flags, StripRoot(f.Name ?? "", root), f.Bytes)).ToList();
        return new LuabndArchive(header, entries);
    }

    /// <summary>Reads a WitchyBND-unpacked folder (the one containing <c>_witchy-bnd4.xml</c>).</summary>
    public static LuabndArchive LoadUnpacked(string folder)
    {
        var m = WitchyBnd4Manifest.Load(Path.Combine(folder, WitchyBnd4Manifest.FileName));
        var header = new LuabndHeader(m.Version, m.Format, m.BigEndian, m.BitBigEndian, m.Unicode,
            m.Extended, m.Unk04, m.Unk05, m.Compression, m.Root);
        var entries = m.Files.Select(e => new LuabndEntry(e.Id, e.Flags, e.Path,
            File.ReadAllBytes(Path.Combine(folder, ToDiskPath(e.Path))))).ToList();
        return new LuabndArchive(header, entries);
    }

    /// <summary>Serializes to packed bytes using the archive's own compression.</summary>
    public byte[] ToPackedBytes()
    {
        var bnd = new BND4
        {
            Version = Header.Version,
            Format = Header.Format,
            BigEndian = Header.BigEndian,
            BitBigEndian = Header.BitBigEndian,
            Unicode = Header.Unicode,
            Extended = Header.Extended,
            Unk04 = Header.Unk04,
            Unk05 = Header.Unk05,
            Compression = Header.Compression,
        };
        foreach (LuabndEntry e in _entries)
            bnd.Files.Add(new BinderFile(e.Flags, e.Id, Header.BndName(e.RelativePath), e.Bytes));
        return bnd.Write();
    }

    /// <summary>Atomically writes the packed archive. Callers are responsible for backups.</summary>
    public void SavePacked(string path)
    {
        AtomicFile.WriteAllBytes(path, ToPackedBytes());
        foreach (LuabndEntry e in _entries) e.MarkSaved();
    }

    /// <summary>
    /// Writes modified entries back into an unpacked folder (unchanged files and the manifest are left alone).
    /// Returns the paths written.
    /// </summary>
    public IReadOnlyList<string> SaveUnpackedChanges(string folder)
    {
        var written = new List<string>();
        foreach (LuabndEntry e in _entries.Where(e => e.IsModified))
        {
            string disk = Path.Combine(folder, ToDiskPath(e.RelativePath));
            AtomicFile.WriteAllBytes(disk, e.Bytes);
            written.Add(disk);
            e.MarkSaved();
        }
        return written;
    }

    private static string ToDiskPath(string relative) => relative.Replace('\\', Path.DirectorySeparatorChar);

    private static string StripRoot(string name, string root) =>
        root.Length > 0 && name.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? name[(root.Length + 1)..] : name;

    /// <summary>
    /// Root directory shared by all entry names. WitchyBND stores it separately; for packed files we derive it
    /// from the <c>script\ai</c> prefix (entries look like <c>N:\...\script\ai\out\bin\x.lua</c>) and fall back
    /// to the longest common directory.
    /// </summary>
    private static string CommonRoot(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count == 0) return "";
        const string marker = "\\script\\ai";
        int idx = list[0].IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            string candidate = list[0][..(idx + marker.Length)];
            if (list.All(n => n.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        string prefix = list[0][..Math.Max(0, list[0].LastIndexOf('\\'))];
        foreach (string n in list.Skip(1))
            while (prefix.Length > 0 && !n.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                prefix = prefix[..Math.Max(0, prefix.LastIndexOf('\\'))];
        return prefix;
    }
}

/// <summary>BND4 header fields that must survive a load/save round trip.</summary>
public sealed record LuabndHeader(
    string Version,
    Binder.Format Format,
    bool BigEndian,
    bool BitBigEndian,
    bool Unicode,
    byte Extended,
    bool Unk04,
    bool Unk05,
    DCX.CompressionInfo Compression,
    string Root)
{
    public string BndName(string relativePath) =>
        string.IsNullOrEmpty(Root) ? relativePath : Root.TrimEnd('\\') + "\\" + relativePath;
}
