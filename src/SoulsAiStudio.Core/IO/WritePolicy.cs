namespace SoulsAiStudio.Core.IO;

/// <summary>
/// The single rule for "may the app write here?" (review 12932e2 M2/M5): never inside a game install, except under
/// that install's <c>mods\</c> folder. A folder is a game install when it contains <c>sekiro.exe</c> (or another
/// FromSoftware game exe / <c>Data*.bdt</c> archives), found by walking up from the target path — so it also
/// protects Steam libraries on other drives without any configuration.
/// </summary>
public static class WritePolicy
{
    private static readonly string[] GameExes = ["sekiro.exe", "DarkSoulsIII.exe", "eldenring.exe", "DarkSoulsRemastered.exe"];

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

    /// <summary>Null when writing <paramref name="path"/> is allowed, otherwise a user-facing reason.</summary>
    public static string? WhyBlocked(string path, IEnumerable<string?>? extraGameDirs = null)
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
            return $"Refusing to write into the game folder:\n{full}\n\n" +
                   "Work from your mod folder (…\\mods\\<your mod>) so the original game files stay untouched.";
        }
        return null;
    }

    /// <summary>Checks a game/mod folder pair for a project. Returns problems (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateFolders(string gameDir, string modDir)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Path.IsPathRooted(gameDir)) problems.Add("Sekiro folder must be a full path.");
        else if (!Directory.Exists(gameDir)) problems.Add("Sekiro folder not found.");
        if (string.IsNullOrWhiteSpace(modDir) || !Path.IsPathRooted(modDir)) { problems.Add("Mod folder must be a full path."); return problems; }
        if (!Directory.Exists(modDir)) problems.Add("Mod folder not found.");

        string mod = WithSlash(Path.GetFullPath(modDir));
        if (IsGameInstall(modDir)) problems.Add("The mod folder is a game install. Pick your Mod Engine mod (…\\mods\\<your mod>).");
        if (!string.IsNullOrWhiteSpace(gameDir) && Path.IsPathRooted(gameDir))
        {
            string game = WithSlash(Path.GetFullPath(gameDir));
            if (string.Equals(mod, game, StringComparison.OrdinalIgnoreCase)) problems.Add("Mod folder and Sekiro folder are the same.");
            else if (game.StartsWith(mod, StringComparison.OrdinalIgnoreCase)) problems.Add("The mod folder contains the Sekiro folder.");
        }
        if (WhyBlocked(Path.Combine(modDir, "script", "x.luabnd.dcx"), [gameDir]) != null)
            problems.Add("The mod folder is inside the game folder but not under mods\\.");
        return problems;
    }

    private static string WithSlash(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
