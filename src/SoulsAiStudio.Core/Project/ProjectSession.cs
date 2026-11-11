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
    public string SavedText { get; private set; }
    /// <summary>The script was compiled bytecode and was decompiled for editing; saving writes plain text.</summary>
    public bool WasBytecode { get; }
    public LuabndSource LoadedFrom { get; private set; }

    /// <summary>Records a successful save (call on the UI thread).</summary>
    public void MarkSaved(string text)
    {
        SavedText = text;
        if (LoadedFrom == LuabndSource.Game) LoadedFrom = LuabndSource.ModPacked; // first save created the mod copy
    }
}

/// <param name="Warning">Non-null when the save was partial (game file written, unpacked copy not).</param>
public sealed record SaveResult(IReadOnlyList<string> WrittenFiles, string? Warning);

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

    private readonly Dictionary<string, SemaphoreSlim> _luabndLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _locksGate = new();

    private SemaphoreSlim LockFor(string luabnd)
    {
        lock (_locksGate)
        {
            if (!_luabndLocks.TryGetValue(luabnd, out var s)) _luabndLocks[luabnd] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }

    /// <summary>
    /// Saves <paramref name="text"/> into the mod folder: repacked <c>.luabnd.dcx</c> first (what the game loads),
    /// then the unpacked <c>.lua</c> if the mod has a WitchyBND folder. If the luabnd only exists in the game, a mod
    /// copy is created. Every overwritten file is backed up beforehand. Saves of the same luabnd are serialized, so
    /// two scripts of one map (e.g. <c>_battle</c> + <c>_logic</c>) never overwrite each other's edits.
    /// <para>Does not mutate <paramref name="doc"/>; call <see cref="ScriptDocument.MarkSaved"/> on the UI thread.</para>
    /// </summary>
    public async Task<SaveResult> SaveAsync(ScriptDocument doc, string text, CancellationToken ct = default)
    {
        string luabnd = doc.Script.Luabnd;
        SemaphoreSlim gate = LockFor(luabnd);
        await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => SaveLocked(doc.Script, text), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private SaveResult SaveLocked(ProjectScript script, string text)
    {
        string luabnd = script.Luabnd;
        string unpackedDir = Catalog.ModUnpackedDir(luabnd);
        string packedPath = Catalog.ModPackedPath(luabnd);
        string unpackedFile = Path.Combine(unpackedDir, script.EntryPath.Replace('\\', Path.DirectorySeparatorChar));

        bool hasUnpacked = File.Exists(Path.Combine(unpackedDir, WitchyBnd4Manifest.FileName));
        LuabndArchive archive = hasUnpacked ? Catalog.OpenModUnpackedMerged(luabnd)
            : File.Exists(packedPath) ? LuabndArchive.LoadPacked(packedPath)
            : LuabndArchive.LoadPacked(Catalog.GamePackedPath(luabnd)); // first save: copy from game
        LuabndEntry entry = archive.Find(script.EntryPath)
            ?? throw new FileNotFoundException($"'{script.EntryPath}' is not in {luabnd}.");

        var targets = hasUnpacked ? new List<string> { packedPath, unpackedFile } : new List<string> { packedPath };
        EnsureWritable(targets);
        Backups.BackupBeforeSave(targets);

        entry.SetText(text);
        byte[] packed = archive.ToPackedBytes(); // build before touching disk
        var written = new List<string>();
        AtomicFile.WriteAllBytes(packedPath, packed);
        written.Add(packedPath);
        try
        {
            if (hasUnpacked) written.AddRange(archive.SaveUnpackedChanges(unpackedDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveResult(written, $"The game file was saved, but the unpacked copy could not be updated: {ex.Message}");
        }
        return new SaveResult(written, null);
    }

    private void EnsureWritable(IEnumerable<string> paths)
    {
        string mod = Path.GetFullPath(Project.ModDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string p in paths)
        {
            if (!Path.GetFullPath(p).StartsWith(mod, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to write outside the mod folder: {p}");
            if (WritePolicy.WhyBlocked(p, [Project.GameDir]) is { } why)
                throw new InvalidOperationException(why);
        }
    }
}
