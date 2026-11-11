using SoulsAiStudio.Core.Backup;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Lua.Decompile;

namespace SoulsAiStudio.Core.Project;

/// <summary>An AI script opened for editing.</summary>
public sealed class ScriptDocument
{
    internal ScriptDocument(ProjectScript script, string text, bool wasBytecode, LuabndSource source)
    {
        Script = script;
        SavedText = text;
        WasBytecode = wasBytecode;
        LoadedFrom = source;
    }

    public ProjectScript Script { get; }
    /// <summary>Text as last loaded/saved. The editor owns the live buffer.</summary>
    public string SavedText { get; internal set; }
    /// <summary>The script was compiled bytecode and was decompiled for editing; saving writes plain text.</summary>
    public bool WasBytecode { get; }
    public LuabndSource LoadedFrom { get; internal set; }
}

/// <summary>
/// The open project: resolves files, opens scripts (decompiling bytecode on first open) and saves them back
/// into the mod folder with backups. Never writes into the game folder.
/// </summary>
public sealed class ProjectSession
{
    private readonly LuaDecompiler _decompiler;

    public ProjectSession(SasProject project, string decompilerExe)
    {
        Project = project;
        Catalog = new ScriptCatalog(project.GameDir, project.ModDir);
        Backups = new BackupService(project.BackupDir, project.ModDir);
        _decompiler = new LuaDecompiler(
            string.IsNullOrEmpty(project.DecompilerPath) ? decompilerExe : project.DecompilerPath, project.CacheDir);
    }

    public SasProject Project { get; }
    public ScriptCatalog Catalog { get; }
    public BackupService Backups { get; }

    public async Task<ScriptDocument> OpenAsync(ProjectScript script, CancellationToken ct = default)
    {
        LuabndSource source = Catalog.Locate(script.Luabnd)
            ?? throw new FileNotFoundException($"Luabnd '{script.Luabnd}' not found in mod or game.");
        LuabndArchive archive = await Task.Run(() => Catalog.Open(script.Luabnd), ct);
        LuabndEntry entry = archive.Find(script.EntryPath)
            ?? throw new FileNotFoundException($"'{script.EntryPath}' is not in {script.Luabnd}.");

        if (!entry.IsBytecode)
            return new ScriptDocument(script, entry.GetText(), wasBytecode: false, source);

        string text = await _decompiler.DecompileAsync(entry.Bytes, entry.FileName, ct);
        return new ScriptDocument(script, text, wasBytecode: true, source);
    }

    /// <summary>
    /// Saves <paramref name="text"/> into the mod folder:
    /// unpacked folder (if the mod has one) + repacked <c>.luabnd.dcx</c>. If the luabnd only exists in the game,
    /// a mod copy is created first. Every overwritten file is backed up beforehand.
    /// </summary>
    /// <returns>Files written.</returns>
    public async Task<IReadOnlyList<string>> SaveAsync(ScriptDocument doc, string text, CancellationToken ct = default)
    {
        string luabnd = doc.Script.Luabnd;
        string unpackedDir = Catalog.ModUnpackedDir(luabnd);
        string packedPath = Catalog.ModPackedPath(luabnd);
        var written = new List<string>();

        await Task.Run(() =>
        {
            bool hasUnpacked = File.Exists(Path.Combine(unpackedDir, WitchyBnd4Manifest.FileName));
            LuabndArchive archive = hasUnpacked ? LuabndArchive.LoadUnpacked(unpackedDir)
                : File.Exists(packedPath) ? LuabndArchive.LoadPacked(packedPath)
                : LuabndArchive.LoadPacked(Catalog.GamePackedPath(luabnd)); // first save: copy from game
            LuabndEntry entry = archive.Find(doc.Script.EntryPath)
                ?? throw new FileNotFoundException($"'{doc.Script.EntryPath}' is not in {luabnd}.");

            var targets = new List<string> { packedPath };
            if (hasUnpacked) targets.Add(Path.Combine(unpackedDir, doc.Script.EntryPath.Replace('\\', Path.DirectorySeparatorChar)));
            EnsureInsideMod(targets);
            Backups.BackupBeforeSave(targets);

            entry.SetText(text);
            if (hasUnpacked) written.AddRange(archive.SaveUnpackedChanges(unpackedDir));
            archive.SavePacked(packedPath);
            written.Add(packedPath);
        }, ct);

        doc.SavedText = text;
        doc.LoadedFrom = Catalog.Locate(luabnd) ?? doc.LoadedFrom;
        return written;
    }

    private void EnsureInsideMod(IEnumerable<string> paths)
    {
        string mod = Path.GetFullPath(Project.ModDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string p in paths)
            if (!Path.GetFullPath(p).StartsWith(mod, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to write outside the mod folder: {p}");
    }
}
