namespace SoulsAiStudio.Core.Project;

/// <summary>Where a character's anibnd/chrbnd were found (mod first, then game).</summary>
public sealed record CharacterFiles(string ChrId, string AnibndPath, string? ChrbndPath)
{
    public bool AnibndFromMod { get; init; }
}

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

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}
