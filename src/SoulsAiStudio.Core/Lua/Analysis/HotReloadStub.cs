using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>Result of <see cref="HotReloadStub.Insert"/> / <see cref="HotReloadStub.Remove"/>.</summary>
public sealed record HotReloadEdit(string Text, int Line, string? Error = null)
{
    public bool Failed => Error != null;
}

/// <summary>
/// In-game Lua hot reload (CLAUDE.md §12; idea and <c>loadfile</c> trick: stanleydesu, credit f_wang). A marked block at
/// the top of <c>Goal.Parry</c> and <c>Goal.ShootReaction</c> re-runs the script from a plain .lua file on disk (<c>loadfile</c>) when the
/// version file next to it changes, so a Save in the app reaches a running fight without reloading the area:
/// <code>
/// -- [SAS hot reload] begin ...
/// do
///     local sasHotVer = loadfile("...\\540300_battle.version")
///     ...
///         local sasHotScript = loadfile("...\\540300_battle.lua")
///         if sasHotScript ~= nil then SAS_HOT_RELOAD_x = sasHotVer; sasHotScript() end
/// -- [SAS hot reload] end
/// </code>
/// The block holds absolute paths of this PC, so it is meant for development only: <see cref="Remove"/> takes it out.
/// </summary>
public static class HotReloadStub
{
    public const string BeginMarker = "-- [SAS hot reload] begin";
    public const string EndMarker = "-- [SAS hot reload] end";
    public static bool Has(string src) => src.Contains(BeginMarker, StringComparison.Ordinal);

    /// <summary>Lua global remembering the loaded version of this script (one per script file: the Lua state is shared).</summary>
    public static string GlobalName(string scriptFileName)
    {
        var sb = new System.Text.StringBuilder("SAS_HOT_RELOAD_");
        foreach (char c in Path.GetFileNameWithoutExtension(scriptFileName))
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    /// <summary>Text of the version file read by the stub (a Lua chunk returning a string).</summary>
    public static string VersionFileText(string version) => $"return \"{version}\"\n";

    /// <summary>Goal functions the block goes into (top of each one the script defines).</summary>
    public static readonly IReadOnlyList<string> HostGoals = ["Parry", "ShootReaction"];

    /// <summary>
    /// Puts the block at the top of <c>Goal.Parry</c> and <c>Goal.ShootReaction</c> (those the script has), replacing
    /// older blocks (e.g. after the paths changed). The block runs each time the function does and reloads once per Save.
    /// <paramref name="scriptPath"/> / <paramref name="versionPath"/>: absolute paths the game reads with <c>loadfile</c>.
    /// <see cref="HotReloadEdit.Line"/> is the first block's line.
    /// </summary>
    public static HotReloadEdit Insert(string src, string scriptPath, string versionPath, string globalName)
    {
        var removed = Remove(src);
        if (removed.Failed) return removed;
        src = removed.Text;

        var all = LuaLexer.Tokenize(src);
        var hosts = GoalOutline.Build(src, all).Where(g => HostGoals.Contains(g.Name)).ToList();
        if (hosts.Count == 0) return new HotReloadEdit(src, -1, "This script has no Goal.Parry or Goal.ShootReaction function to put the hot reload in.");

        string nl = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = src.Split(nl).ToList();
        var inserts = new List<(int At, List<string> Block)>();
        foreach (var g in hosts)
        {
            // The body starts after the line holding the ')' that closes the parameter list.
            var close = all.FirstOrDefault(x => x.Kind != LuaTokenKind.Comment && x.Line >= g.Line && x.Is(src, ")"));
            if (close.Text(src).Length == 0) return new HotReloadEdit(src, -1, $"Could not read the parameters of {g.QualifiedName}.");
            int at = close.Line + 1;
            if (at > g.EndLine) return new HotReloadEdit(src, -1, $"{g.QualifiedName} is written on one line; put its body on separate lines first.");
            inserts.Add((at, Block(BodyIndent(lines, at, g.EndLine, lines[g.Line]), scriptPath, versionPath, globalName)));
        }
        foreach (var (at, block) in inserts.OrderByDescending(i => i.At)) lines.InsertRange(at, block);
        return new HotReloadEdit(string.Join(nl, lines), inserts.Min(i => i.At));
    }

    private static List<string> Block(string ind, string scriptPath, string versionPath, string globalName)
    {
        string u = ind.Length > 0 && ind[0] == '\t' ? "\t" : "    ";
        string inner = ind + u;
        return
        [
            $"{ind}{BeginMarker}: added by Souls AI Studio, remove it before sharing the mod (Lua editor > Hot Reload).",
            $"{ind}do -- reloads this script once after each Save",
            $"{inner}local sasHotVer = loadfile({LuaString(versionPath)})",
            $"{inner}if sasHotVer ~= nil then sasHotVer = sasHotVer() end",
            $"{inner}if sasHotVer ~= nil and sasHotVer ~= {globalName} then",
            $"{inner}{u}local sasHotScript = loadfile({LuaString(scriptPath)})",
            $"{inner}{u}if sasHotScript ~= nil then",
            $"{inner}{u}{u}{globalName} = sasHotVer",
            $"{inner}{u}{u}sasHotScript()",
            $"{inner}{u}end",
            $"{inner}end",
            $"{ind}end",
            $"{ind}{EndMarker}",
        ];
    }

    /// <summary>Takes every block out (no change if there is none). <see cref="HotReloadEdit.Line"/>: where the first one was.</summary>
    public static HotReloadEdit Remove(string src)
    {
        if (!Has(src)) return new HotReloadEdit(src, -1);
        string nl = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = src.Split(nl).ToList();
        int first = -1;
        while (true)
        {
            var (b, e) = Range(lines.ToArray());
            if (b < 0)
            {
                if (lines.Any(l => l.TrimStart().StartsWith(BeginMarker, StringComparison.Ordinal)))
                    return new HotReloadEdit(src, -1, $"A hot reload block has no '{EndMarker}' line; remove it by hand.");
                break;
            }
            if (first < 0) first = b;
            lines.RemoveRange(b, e - b + 1);
        }
        return new HotReloadEdit(string.Join(nl, lines), first);
    }

    private static (int Begin, int End) Range(string[] lines)
    {
        int b = Array.FindIndex(lines, l => l.TrimStart().StartsWith(BeginMarker, StringComparison.Ordinal));
        if (b < 0) return (-1, -1);
        int e = Array.FindIndex(lines, b, l => l.TrimStart().StartsWith(EndMarker, StringComparison.Ordinal));
        return e < 0 ? (-1, -1) : (b, e);
    }

    /// <summary>Indent of the first non-empty body line, else the header's indent plus one step.</summary>
    private static string BodyIndent(List<string> lines, int from, int endLine, string header)
    {
        for (int i = from; i < endLine && i < lines.Count; i++)
            if (lines[i].Trim().Length > 0) return new string(lines[i].TakeWhile(c => c is ' ' or '\t').ToArray());
        string h = new(header.TakeWhile(c => c is ' ' or '\t').ToArray());
        return h + (h.StartsWith('\t') ? "\t" : "    ");
    }

    /// <summary>A Lua double-quoted string literal (Windows paths: backslashes escaped).</summary>
    public static string LuaString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
