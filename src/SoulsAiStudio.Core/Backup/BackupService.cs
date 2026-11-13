namespace SoulsAiStudio.Core.Backup;

/// <summary>
/// Project backups (CLAUDE.md P0 #5):
/// <list type="bullet">
/// <item><c>backups/original/</c> — first-seen copy of every file the app ever writes; never pruned.</item>
/// <item><c>backups/yyyyMMdd-HHmmss-fff/</c> — copy taken right before each save; the newest
/// <see cref="KeepSnapshots"/> are kept.</item>
/// </list>
/// Files are stored under a path relative to <see cref="_rootDir"/> (the mod folder) so restores are unambiguous.
/// </summary>
public sealed class BackupService(string backupDir, string rootDir)
{
    private readonly string _rootDir = Path.GetFullPath(rootDir);

    public int KeepSnapshots { get; set; } = 50;
    public string OriginalDir => Path.Combine(backupDir, "original");

    /// <summary>Backs up the current on-disk state of <paramref name="files"/> before they are overwritten.</summary>
    /// <returns>The snapshot folder, or null when none of the files existed yet.</returns>
    public string? BackupBeforeSave(IEnumerable<string> files, DateTime? now = null)
    {
        var existing = files.Select(Path.GetFullPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return null;

        string stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss-fff");
        string snapshot = Path.Combine(backupDir, stamp);
        for (int n = 1; Directory.Exists(snapshot); n++) // same millisecond (or injected clock): never reuse a snapshot
            snapshot = Path.Combine(backupDir, $"{stamp}-{n:D2}");
        foreach (string file in existing)
        {
            string rel = Relative(file);
            string original = Path.Combine(OriginalDir, rel);
            if (!File.Exists(original)) CopyFile(file, original);
            CopyFile(file, Path.Combine(snapshot, rel));
        }
        Prune();
        return snapshot;
    }

    /// <summary>Snapshot folders, newest first (excluding <c>original</c>).</summary>
    public IReadOnlyList<string> ListSnapshots() =>
        Directory.Exists(backupDir)
            ? Directory.GetDirectories(backupDir)
                .Where(d => !Path.GetFileName(d).Equals("original", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList()
            : [];

    private void Prune()
    {
        foreach (string old in ListSnapshots().Skip(KeepSnapshots))
            Directory.Delete(old, recursive: true);
    }

    private string Relative(string file)
    {
        string rel = Path.GetRelativePath(_rootDir, file);
        // Files outside the root (should not happen for mod writes) keep a sanitized absolute path.
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
            ? Path.Combine("_external", file.Replace(":", "", StringComparison.Ordinal).TrimStart('\\', '/'))
            : rel;
    }

    private static void CopyFile(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        File.SetAttributes(dst, FileAttributes.Normal);
    }
}
