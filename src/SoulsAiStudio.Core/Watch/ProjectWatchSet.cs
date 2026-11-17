using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.Core.Watch;

/// <summary>What to watch for an open project (CLAUDE.md §5 P0 item 6).</summary>
public static class ProjectWatchSet
{
    public const string GameParamRelative = @"param\gameparam\gameparam.parambnd.dcx";

    /// <param name="loadedAnibnd">Anibnd open in DSAS (null = none). A read-only view copy in the project cache is skipped.</param>
    /// <param name="loadedChrbnd">Its chrbnd (mod or game), or null.</param>
    public static List<WatchSpec> Build(SasProject project, ScriptCatalog catalog, string? loadedAnibnd, string? loadedChrbnd)
    {
        var specs = new List<WatchSpec>();
        bool hasGame = !string.IsNullOrWhiteSpace(project.GameDir);
        foreach (string luabnd in project.Scripts.Select(s => s.Luabnd).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.HasMod)
            {
                specs.Add(new WatchSpec(WatchKind.Luabnd, luabnd, catalog.ModPackedPath(luabnd)));
                specs.Add(new WatchSpec(WatchKind.Luabnd, luabnd, catalog.ModUnpackedDir(luabnd), IsDirectory: true));
            }
            if (hasGame) specs.Add(new WatchSpec(WatchKind.Luabnd, luabnd, catalog.GamePackedPath(luabnd)));
        }

        string cache = string.IsNullOrEmpty(project.FilePath) ? "" : Path.GetFullPath(project.CacheDir);
        if (!string.IsNullOrWhiteSpace(loadedAnibnd) && !IsUnder(loadedAnibnd, cache))
            specs.Add(new WatchSpec(WatchKind.Anibnd, ChrId(loadedAnibnd), loadedAnibnd));
        if (!string.IsNullOrWhiteSpace(loadedChrbnd) && !IsUnder(loadedChrbnd, cache))
            specs.Add(new WatchSpec(WatchKind.Chrbnd, ChrId(loadedChrbnd), loadedChrbnd));

        if (!project.IsReadOnly) specs.Add(new WatchSpec(WatchKind.GameParam, "gameparam", Path.Combine(project.ModDir, GameParamRelative)));
        if (hasGame) specs.Add(new WatchSpec(WatchKind.GameParam, "gameparam", Path.Combine(project.GameDir, GameParamRelative)));
        return specs;
    }

    /// <summary><c>c5060</c> for <c>...\chr\c5060.anibnd.dcx</c>.</summary>
    public static string ChrId(string path) => Path.GetFileName(path).Split('.')[0];

    private static bool IsUnder(string path, string dir)
    {
        if (dir.Length == 0) return false;
        try { return Path.GetFullPath(path).StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
