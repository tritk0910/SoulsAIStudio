namespace SoulsAiStudio.Core.IO;

/// <summary>
/// The single rule for "may the app write here?" (review 12932e2 M2/M5): never into a game install's own files. Inside a
/// game install, writes are allowed only under its <c>mods\</c> folder or under the project's mod folder, which may be
/// anywhere (e.g. <c>Sekiro\MyMod</c>) as long as it is not the game root and not inside one of the game's data folders
/// (<see cref="GameDataFolders"/>). A folder is a game install when it contains <c>sekiro.exe</c> (or another
/// FromSoftware game exe / <c>Data*.bdt</c> archives), found by walking up from the target path — so it also
/// protects Steam libraries on other drives without any configuration.
/// </summary>
public static class WritePolicy
{
    private static readonly string[] GameExes = ["sekiro.exe", "DarkSoulsIII.exe", "eldenring.exe", "DarkSoulsRemastered.exe"];

    /// <summary>
    /// Top-level folders of an (UXM-unpacked) game install that hold the original game files, plus UXM's backups.
    /// A mod folder may not be (inside) one of them.
    /// </summary>
    public static readonly IReadOnlySet<string> GameDataFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "action", "asset", "chr", "cutscene", "event", "facegen", "font", "map", "material", "menu", "movie", "msg", "mtd",
        "obj", "other", "param", "parts", "script", "sfx", "shader", "sound", "_backup", "_unknown",
    };

    /// <summary>
    /// Why <paramref name="modDir"/> can't hold mod files of the game at <paramref name="gameRoot"/>, or null if it can.
    /// Outside the game: always fine. Inside: not the game root itself and not under a game data folder.
    /// </summary>
    public static string? WhyUnsafeModDir(string gameRoot, string modDir)
    {
        string game = WithSlash(Path.GetFullPath(gameRoot));
        string mod = WithSlash(Path.GetFullPath(modDir));
        if (string.Equals(mod, game, StringComparison.OrdinalIgnoreCase)) return "Mod folder and Sekiro folder are the same.";
        if (game.StartsWith(mod, StringComparison.OrdinalIgnoreCase)) return "The mod folder contains the Sekiro folder.";
        if (!mod.StartsWith(game, StringComparison.OrdinalIgnoreCase)) return null;
        string first = mod[game.Length..].Split(Path.DirectorySeparatorChar)[0];
        return GameDataFolders.Contains(first)
            ? $"The mod folder is inside the game's own \"{first}\" folder, which holds the original game files. Pick another folder."
            : null;
    }

    /// <summary>True when <paramref name="dir"/> looks like a game install root.</summary>
    public static bool IsGameInstall(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        if (GameExes.Any(e => File.Exists(Path.Combine(dir, e)))) return true;
        return Directory.EnumerateFiles(dir, "Data*.bdt").Any();
    }

    /// <summary>The game install containing <paramref name="path"/>, or null.</summary>
    public static string? FindGameInstall(string path)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
            if (IsGameInstall(d.FullName)) return d.FullName;
        return null;
    }

    /// <summary>
    /// Null when writing <paramref name="path"/> is allowed, otherwise a user-facing reason. <paramref name="modDir"/> is the
    /// project's mod folder: inside a game install, paths under it are allowed when it is a safe mod folder.
    /// </summary>
    public static string? WhyBlocked(string path, IEnumerable<string?>? extraGameDirs = null, string? modDir = null)
    {
        string full = Path.GetFullPath(path);
        var games = new List<string>();
        if (FindGameInstall(full) is { } found) games.Add(found);
        if (extraGameDirs != null)
            games.AddRange(extraGameDirs.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => Path.GetFullPath(g!)));

        foreach (string game in games)
        {
            string root = WithSlash(game);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.StartsWith(root + "mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(modDir) && Path.IsPathRooted(modDir) && WhyUnsafeModDir(game, modDir) == null
                && full.StartsWith(WithSlash(Path.GetFullPath(modDir)), StringComparison.OrdinalIgnoreCase)) continue;
            return $"Refusing to write into the game folder:\n{full}\n\n" +
                   "Work from your mod folder so the original game files stay untouched.";
        }
        return null;
    }

    /// <summary>
    /// Checks a game/mod folder pair for a project. Returns problems (empty = valid). An empty mod folder is allowed:
    /// the project is then read-only (game files can be read, nothing can be saved).
    /// </summary>
    public static IReadOnlyList<string> ValidateFolders(string gameDir, string modDir)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Path.IsPathRooted(gameDir)) problems.Add("Sekiro folder must be a full path.");
        else if (!Directory.Exists(gameDir)) problems.Add("Sekiro folder not found.");
        if (string.IsNullOrWhiteSpace(modDir)) return problems; // read-only project
        if (!Path.IsPathRooted(modDir)) { problems.Add("Mod folder must be a full path."); return problems; }
        if (!Directory.Exists(modDir)) problems.Add("Mod folder not found.");

        if (IsGameInstall(modDir)) problems.Add("The mod folder is a game install. Pick your Mod Engine mod folder.");
        if (!string.IsNullOrWhiteSpace(gameDir) && Path.IsPathRooted(gameDir) && WhyUnsafeModDir(gameDir, modDir) is { } why)
            problems.Add(why);
        // Another game install around the mod folder (e.g. a second Steam library).
        else if (FindGameInstall(modDir) is { } other && WhyUnsafeModDir(other, modDir) is { } why2)
            problems.Add(why2);
        return problems;
    }

    private static string WithSlash(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
