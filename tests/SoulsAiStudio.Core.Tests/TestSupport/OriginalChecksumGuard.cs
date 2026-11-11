using System.Security.Cryptography;

namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// Verifies <c>test-data/original</c> against <c>SHA256SUMS.txt</c> (sha256sum format: <c>&lt;hex&gt; *./path</c>).
/// NOTE: the paths in SHA256SUMS.txt are written as <c>./script/...</c>, i.e. relative to
/// <c>test-data/original/Severance-dev/</c> (not to <c>test-data/original/</c> as CLAUDE.md says).
/// The guard auto-detects which base directory the listed paths resolve against.
/// </summary>
public static class OriginalChecksumGuard
{
    public const string SumsFileName = "SHA256SUMS.txt";

    public sealed record Result(
        string BaseDir,
        int Listed,
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> Mismatched,
        IReadOnlyList<string> Unlisted)
    {
        public bool Ok => Missing.Count == 0 && Mismatched.Count == 0 && Unlisted.Count == 0;

        public override string ToString() =>
            $"base={BaseDir} listed={Listed} missing={Missing.Count} mismatched={Mismatched.Count} unlisted={Unlisted.Count}" +
            (Missing.Count > 0 ? "\n  missing: " + string.Join(", ", Missing) : "") +
            (Mismatched.Count > 0 ? "\n  CHANGED: " + string.Join(", ", Mismatched) : "") +
            (Unlisted.Count > 0 ? "\n  unlisted (new files in read-only backup?): " + string.Join(", ", Unlisted) : "");
    }

    public static IReadOnlyList<(string Hash, string RelPath)> ParseSums(string sumsPath)
    {
        var list = new List<(string, string)>();
        foreach (string raw in File.ReadLines(sumsPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            if (sp != 64) throw new FormatException($"Bad SHA256SUMS line: {raw}");
            string hash = line[..64].ToLowerInvariant();
            string path = line[65..].TrimStart(' ', '*');
            if (path.StartsWith("./")) path = path[2..];
            list.Add((hash, path));
        }
        return list;
    }

    /// <summary>Hash every listed file and list any extra files. Pure read-only.</summary>
    public static Result Verify(string originalDir)
    {
        string sums = Path.Combine(originalDir, SumsFileName);
        var entries = ParseSums(sums);

        // Detect base dir: the listed paths may be relative to original/ or original/Severance-dev/.
        string[] candidates = new[] { originalDir }
            .Concat(Directory.EnumerateDirectories(originalDir))
            .ToArray();
        string baseDir = candidates
            .OrderByDescending(c => entries.Count(e => File.Exists(Path.Combine(c, e.RelPath))))
            .First();

        var missing = new List<string>();
        var mismatched = new List<string>();
        var listedFull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hash, rel) in entries)
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));
            listedFull.Add(full);
            if (!File.Exists(full)) { missing.Add(rel); continue; }
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (actual != hash) mismatched.Add(rel);
        }

        var unlisted = Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(f => !listedFull.Contains(f) && !string.Equals(Path.GetFileName(f), SumsFileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(baseDir, f))
            .ToList();

        return new Result(baseDir, entries.Count, missing, mismatched, unlisted);
    }
}
