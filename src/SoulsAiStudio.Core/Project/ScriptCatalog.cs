using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Project;

/// <summary>Where a luabnd's current content lives. Mod sources win over the game.</summary>
public enum LuabndSource
{
    /// <summary><c>mods\X\script\&lt;name&gt;-luabnd-dcx\</c> (WitchyBND- or Yabber-unpacked; the user's working copy).</summary>
    ModUnpacked,
    /// <summary><c>mods\X\script\&lt;name&gt;.luabnd.dcx</c>.</summary>
    ModPacked,
    /// <summary><c>Sekiro\script\&lt;name&gt;.luabnd.dcx</c> (read-only for us).</summary>
    Game,
}

/// <summary>An AI script found while scanning, as offered by the New Project wizard.</summary>
public sealed record ScriptCandidate(string Luabnd, string EntryPath, LuabndSource Source, bool IsBytecode, long Size)
{
    public string FileName => EntryPath[(EntryPath.LastIndexOf('\\') + 1)..];

    /// <summary>NPC think id prefix, e.g. <c>506300</c> for <c>506300_battle.lua</c>; null if not numeric.</summary>
    public string? NpcId => FileName.Split('_')[0] is { Length: 6 } id && id.All(char.IsDigit) ? id : null;

    public string Kind => FileName.Contains("_battle", StringComparison.OrdinalIgnoreCase) ? "battle"
        : FileName.Contains("_logic", StringComparison.OrdinalIgnoreCase) ? "logic" : "other";

    public ProjectScript ToProjectScript(string displayName = "") =>
        new() { Luabnd = Luabnd, EntryPath = EntryPath, DisplayName = displayName };
}

/// <summary>
/// Resolves luabnd locations for a game + mod pair and lists the Lua scripts inside them.
/// Lookup order for each luabnd: mod unpacked folder → mod packed file → game packed file.
/// </summary>
public sealed class ScriptCatalog(string gameDir, string modDir)
{
    public string GameScriptDir => Path.Combine(gameDir, "script");
    public string ModScriptDir => Path.Combine(modDir, "script");

    public string ModUnpackedDir(string luabnd) => Path.Combine(ModScriptDir, luabnd + "-luabnd-dcx");
    public string ModPackedPath(string luabnd) => Path.Combine(ModScriptDir, luabnd + ".luabnd.dcx");
    public string GamePackedPath(string luabnd) => Path.Combine(GameScriptDir, luabnd + ".luabnd.dcx");

    public LuabndSource? Locate(string luabnd)
    {
        if (WitchyBnd4Manifest.FindIn(ModUnpackedDir(luabnd)) != null) return LuabndSource.ModUnpacked;
        if (File.Exists(ModPackedPath(luabnd))) return LuabndSource.ModPacked;
        if (File.Exists(GamePackedPath(luabnd))) return LuabndSource.Game;
        return null;
    }

    /// <summary>All luabnd base names known in mod or game (e.g. <c>m10_00_00_00</c>, <c>aicommon</c>).</summary>
    public IReadOnlyList<string> ListLuabnds()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { GameScriptDir, ModScriptDir }.Where(Directory.Exists))
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.luabnd.dcx"))
                names.Add(Path.GetFileName(f)[..^".luabnd.dcx".Length]);
            foreach (string d in Directory.EnumerateDirectories(dir, "*-luabnd-dcx"))
                if (WitchyBnd4Manifest.FindIn(d) != null)
                    names.Add(Path.GetFileName(d)[..^"-luabnd-dcx".Length]);
        }
        return names.ToList();
    }

    /// <summary>Where a luabnd exists and whether the mod's unpacked folder has changes the .dcx doesn't (yet).</summary>
    public LuabndStatus Status(string luabnd)
    {
        string packed = ModPackedPath(luabnd), dir = ModUnpackedDir(luabnd);
        DateTime? packedTime = File.Exists(packed) ? File.GetLastWriteTimeUtc(packed) : null;
        UnpackFormat? format = null;
        DateTime? newest = null;
        if (WitchyBnd4Manifest.FindIn(dir) is { } manifest)
        {
            format = manifest.EndsWith(WitchyBnd4Manifest.YabberFileName, StringComparison.OrdinalIgnoreCase) ? UnpackFormat.Yabber : UnpackFormat.Witchy;
            try
            {
                newest = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(File.GetLastWriteTimeUtc(manifest)).Max();
            }
            catch (IOException) { }
        }
        return new LuabndStatus(luabnd, packedTime, format, newest, File.Exists(GamePackedPath(luabnd)));
    }

    public LuabndArchive Open(string luabnd) => Locate(luabnd) switch
    {
        LuabndSource.ModUnpacked => OpenModUnpackedMerged(luabnd),
        LuabndSource.ModPacked => LuabndArchive.LoadPacked(ModPackedPath(luabnd)),
        LuabndSource.Game => LuabndArchive.LoadPacked(GamePackedPath(luabnd)),
        _ => throw new FileNotFoundException($"Luabnd '{luabnd}' not found in mod or game script folders."),
    };

    /// <summary>
    /// Loads the mod's unpacked folder, but for any entry whose content differs from the mod's packed
    /// <c>.luabnd.dcx</c>, keeps whichever copy was written last (unpacked file mtime vs .dcx mtime). This protects
    /// both directions: an edit in VSCode (unpacked newer) and a partial save where only the .dcx was updated
    /// (packed newer) — the stale side never overwrites the newer one on the next repack.
    /// </summary>
    public LuabndArchive OpenModUnpackedMerged(string luabnd)
    {
        string dir = ModUnpackedDir(luabnd);
        var archive = LuabndArchive.LoadUnpacked(dir);
        string packedPath = ModPackedPath(luabnd);
        if (!File.Exists(packedPath)) return archive;

        LuabndArchive packed;
        try { packed = LuabndArchive.LoadPacked(packedPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException) { return archive; }

        DateTime packedTime = File.GetLastWriteTimeUtc(packedPath);
        foreach (LuabndEntry e in archive.Entries)
        {
            LuabndEntry? p = packed.Find(e.RelativePath);
            if (p == null || p.Bytes.AsSpan().SequenceEqual(e.Bytes)) continue;
            string file = Path.Combine(dir, e.DiskPath.Replace('\\', Path.DirectorySeparatorChar));
            if (File.GetLastWriteTimeUtc(file) < packedTime) e.AdoptNewerBytes(p.Bytes);
        }
        return archive;
    }

    /// <summary>Lists every .lua in every luabnd. Unreadable archives (e.g. Oodle missing) are reported, not thrown.</summary>
    public (List<ScriptCandidate> Scripts, List<string> Errors) Scan(CancellationToken ct = default)
    {
        var scripts = new List<ScriptCandidate>();
        var errors = new List<string>();
        foreach (string name in ListLuabnds())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                LuabndSource source = Locate(name)!.Value;
                LuabndArchive a = Open(name);
                scripts.AddRange(a.LuaScripts.Select(e =>
                    new ScriptCandidate(name, e.RelativePath, source, LuaText.IsBytecode(e.Bytes), e.Bytes.Length)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        return (scripts, errors);
    }
}

/// <param name="ModPackedTime">Last write of the mod's <c>.luabnd.dcx</c> (UTC), null if the mod has none.</param>
/// <param name="Unpacked">Format of the mod's unpacked folder, null if there is none.</param>
/// <param name="UnpackedNewest">Newest file in the unpacked folder (UTC).</param>
public sealed record LuabndStatus(string Luabnd, DateTime? ModPackedTime, UnpackFormat? Unpacked, DateTime? UnpackedNewest, bool InGame)
{
    /// <summary>The unpacked folder was edited after the .dcx was written (or there is no .dcx yet): Repack needed.</summary>
    public bool NeedsRepack => Unpacked != null && (ModPackedTime == null || UnpackedNewest > ModPackedTime.Value.AddSeconds(1));

    /// <summary>The .dcx is newer than every unpacked file: repacking would undo those newer changes.</summary>
    public bool PackedIsNewer => Unpacked != null && ModPackedTime != null && UnpackedNewest < ModPackedTime.Value.AddSeconds(-1);
}
