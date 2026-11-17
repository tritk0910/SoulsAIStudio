namespace SoulsAiStudio.Core.Project;

/// <summary>A script that drives the same character as the loaded anibnd.</summary>
/// <param name="Script">The project's own entry when the script is in the project (keeps its Character / AnimCategory), else a new one.</param>
public sealed record SharedScript(ProjectScript Script, ScriptCandidate Candidate, bool InProject);

/// <summary>
/// Finds every AI script (in the mod's and the game's luabnds) whose character resolves to a given anibnd, e.g. all of
/// Isshin's scripts (540000, 540010, 540300, 540310, 540400 … in m11_01 and m11_02) for <c>c5400.anibnd.dcx</c>.
/// The character comes from the project entry when there is one, else from the script id (see <see cref="CharacterLocator"/>).
/// </summary>
public static class SharedCharacterScripts
{
    public static List<SharedScript> Find(IEnumerable<ScriptCandidate> candidates, IReadOnlyList<ProjectScript> projectScripts,
        CharacterLocator locator, string anibndPath)
    {
        string target = Path.GetFullPath(anibndPath);
        var inProject = projectScripts.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // chr guess -> anibnd (cache)
        var result = new List<SharedScript>();
        foreach (var c in candidates)
        {
            bool known = inProject.TryGetValue(c.Luabnd + "|" + c.EntryPath, out var ps);
            ps ??= c.ToProjectScript();
            string cacheKey = string.IsNullOrWhiteSpace(ps.Character) ? "#" + ps.FileName.Split('_')[0] : ps.Character.Trim();
            if (!resolved.TryGetValue(cacheKey, out string? path))
                resolved[cacheKey] = path = locator.Resolve(ps)?.AnibndPath is { } p ? Path.GetFullPath(p) : null;
            if (path != null && string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                result.Add(new SharedScript(ps, c, known));
        }
        return result;
    }
}
