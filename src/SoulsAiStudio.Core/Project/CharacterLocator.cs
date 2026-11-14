using System.Text.RegularExpressions;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Project;

/// <summary>Where a character's anibnd/chrbnd were found (mod first, then game).</summary>
public sealed record CharacterFiles(string ChrId, string AnibndPath, string? ChrbndPath)
{
    public bool AnibndFromMod { get; init; }
}

/// <summary>A character found in the chr folders (mod first, then game).</summary>
/// <param name="IsStub">The anibnd is tiny (no animations of its own, e.g. variant chrs like c5063).</param>
public sealed record CharacterCandidate(string ChrId, bool InMod, bool InGame, bool HasChrbnd, bool IsStub);

/// <summary>
/// Maps an AI script to the character (<c>cXXXX</c>) whose model and animations it drives, and finds the files.
/// Sekiro think ids are <c>CCCCVV</c> (e.g. 506300 → c5063); variant chrs like c5063/c5403 are tiny stub anibnds,
/// so we fall back to the base chr (c5060/c5400) when the variant has no real animation data.
/// TODO(params): resolve exactly via NpcThinkParam → NpcParam once the param loader exists.
/// </summary>
public sealed class CharacterLocator(string gameDir, string modDir)
{
    /// <summary>Anibnds smaller than this are stubs (no animations of their own).</summary>
    public const long StubAnibndBytes = 4096;

    public string? AnibndPath(string chrId) => FirstExisting(
        Path.Combine(modDir, "chr", chrId + ".anibnd.dcx"),
        Path.Combine(gameDir, "chr", chrId + ".anibnd.dcx"));

    public string? ChrbndPath(string chrId) => FirstExisting(
        Path.Combine(modDir, "chr", chrId + ".chrbnd.dcx"),
        Path.Combine(gameDir, "chr", chrId + ".chrbnd.dcx"));

    /// <summary>Candidate chr ids for a think/npc id like <c>506300</c>, best first.</summary>
    public static IEnumerable<string> Candidates(string npcId)
    {
        if (npcId.Length < 4 || !npcId.All(char.IsDigit)) yield break;
        yield return "c" + npcId[..4];                    // c5063
        if (npcId[3] != '0') yield return "c" + npcId[..3] + "0"; // c5060
    }

    /// <summary>Resolves an explicit chr id (if given) or guesses from the script file name.</summary>
    public CharacterFiles? Resolve(ProjectScript script)
    {
        IEnumerable<string> ids = !string.IsNullOrWhiteSpace(script.Character)
            ? [script.Character.Trim()]
            : Candidates(script.FileName.Split('_')[0]);

        CharacterFiles? stubFallback = null;
        foreach (string id in ids)
        {
            string? anibnd = AnibndPath(id);
            if (anibnd == null) continue;
            var files = new CharacterFiles(id, anibnd, ChrbndPath(id))
            {
                AnibndFromMod = anibnd.StartsWith(Path.GetFullPath(modDir), StringComparison.OrdinalIgnoreCase),
            };
            if (new FileInfo(anibnd).Length >= StubAnibndBytes && files.ChrbndPath != null) return files;
            stubFallback ??= files;
        }
        return stubFallback;
    }

    private static readonly Regex ChrIdPattern = new(@"^c\d{4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Normalized chr id (<c>"C5060 "</c> → <c>c5060</c>), or null if it is not <c>c</c> + 4 digits.</summary>
    public static string? NormalizeChrId(string? chrId)
    {
        string id = (chrId ?? "").Trim().ToLowerInvariant();
        if (id.Length == 4 && id.All(char.IsDigit)) id = "c" + id;
        return ChrIdPattern.IsMatch(id) ? id : null;
    }

    /// <summary>Files of an explicit character id (no guessing). Null if it has no anibnd in the mod or the game.</summary>
    public CharacterFiles? ResolveId(string chrId)
    {
        string? id = NormalizeChrId(chrId);
        if (id == null || AnibndPath(id) is not { } anibnd) return null;
        return new CharacterFiles(id, anibnd, ChrbndPath(id))
        {
            AnibndFromMod = anibnd.StartsWith(Path.GetFullPath(modDir), StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Every <c>cXXXX.anibnd.dcx</c> in the mod's and the game's <c>chr</c> folders, sorted by id.</summary>
    public IReadOnlyList<CharacterCandidate> Scan()
    {
        var mod = AnibndIds(modDir);
        var game = AnibndIds(gameDir);
        var result = new List<CharacterCandidate>();
        foreach (string id in mod.Keys.Union(game.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            long size = mod.TryGetValue(id, out long m) ? m : game[id];
            result.Add(new CharacterCandidate(id, mod.ContainsKey(id), game.ContainsKey(id), ChrbndPath(id) != null, size < StubAnibndBytes));
        }
        return result;
    }

    private static Dictionary<string, long> AnibndIds(string root)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        string dir = Path.Combine(root, "chr");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(dir)) return ids;
        foreach (string f in Directory.EnumerateFiles(dir, "c*.anibnd.dcx"))
        {
            string name = Path.GetFileName(f);
            if (NormalizeChrId(name[..^".anibnd.dcx".Length]) is { } id && name.Length == id.Length + ".anibnd.dcx".Length)
                ids[id] = new FileInfo(f).Length;
        }
        return ids;
    }

    /// <summary>
    /// Copies the game's anibnd of <paramref name="chrId"/> into <c>&lt;mod&gt;\chr\</c> so its animations can be edited and
    /// saved (saves never go to the game folder). Refuses to overwrite an existing mod file. Returns the new path.
    /// </summary>
    public string CopyAnibndToMod(string chrId)
    {
        string id = NormalizeChrId(chrId) ?? throw new ArgumentException($"'{chrId}' is not a character id like c5060.", nameof(chrId));
        string source = Path.Combine(gameDir, "chr", id + ".anibnd.dcx");
        string target = Path.Combine(modDir, "chr", id + ".anibnd.dcx");
        if (File.Exists(target)) throw new IOException($"{id}.anibnd.dcx already exists in the mod folder.");
        if (!File.Exists(source)) throw new FileNotFoundException($"{id}.anibnd.dcx is not in the game's chr folder.", source);
        if (WritePolicy.WhyBlocked(target, [gameDir]) is { } why) throw new InvalidOperationException(why);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.WriteAllBytes(target, File.ReadAllBytes(source));
        return target;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}
