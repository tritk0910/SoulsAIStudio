namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// Locates the repo root and the read-only test data backup (<c>test-data/original</c>).
/// test-data is gitignored (it is the user's mod), so every test that needs it must
/// use <see cref="RequiresTestDataFactAttribute"/> / <see cref="RequiresTestDataTheoryAttribute"/>
/// so a fresh clone skips instead of failing.
/// Override location with env var <c>SAS_TEST_DATA</c> (path to a folder laid out like <c>test-data/original</c>).
/// </summary>
public static class TestPaths
{
    public const string TestDataEnvVar = "SAS_TEST_DATA";

    /// <summary>Real Sekiro install. Tests may READ here, never write.</summary>
    public const string SekiroInstallDir = @"C:\Program Files (x86)\Steam\steamapps\common\Sekiro";

    private static readonly Lazy<string?> _repoRoot = new(FindRepoRoot);
    private static readonly Lazy<string?> _original = new(FindOriginal);

    /// <summary>Repo root (folder containing tests/SoulsAiStudio.Core.Tests or SoulsAIStudio.sln), or null if not found.</summary>
    public static string? RepoRoot => _repoRoot.Value;

    /// <summary><c>test-data/original</c> (READ-ONLY backup), or null if missing.</summary>
    public static string? OriginalDir => _original.Value;

    /// <summary><c>test-data/original/Severance-dev</c> (the mod root inside the backup).</summary>
    public static string? ModDir => OriginalDir is null ? null : Path.Combine(OriginalDir, "Severance-dev");

    /// <summary><c>test-data/original/Severance-dev/script</c>.</summary>
    public static string? ScriptDir => ModDir is null ? null : Path.Combine(ModDir, "script");

    public static bool HasTestData => ScriptDir is not null && Directory.Exists(ScriptDir);

    public static string MissingTestDataReason =>
        $"test-data/original not found (gitignored; expected at <repo>/test-data/original/Severance-dev/script or via %{TestDataEnvVar}%). " +
        $"Searched upward from '{AppContext.BaseDirectory}'.";

    /// <summary>Script dir; throws a clear message if missing (use only inside data-gated tests).</summary>
    public static string RequireScriptDir() => ScriptDir is { } s && Directory.Exists(s)
        ? s
        : throw new InvalidOperationException(MissingTestDataReason);

    public static string RequireOriginalDir() => OriginalDir ?? throw new InvalidOperationException(MissingTestDataReason);

    public static bool HasSekiroInstall => Directory.Exists(Path.Combine(SekiroInstallDir, "chr"));

    /// <summary>True if <paramref name="path"/> is inside a location tests must never write to.</summary>
    public static bool IsProtectedPath(string path)
    {
        string full = Normalize(path);
        if (OriginalDir is { } o && IsUnder(full, Normalize(o))) return true;
        if (IsUnder(full, Normalize(SekiroInstallDir))) return true;
        return false;
    }

    /// <summary>Throw if a test is about to write into a protected location.</summary>
    public static void AssertWritable(string path)
    {
        if (IsProtectedPath(path))
            throw new InvalidOperationException($"Refusing to write to protected path '{path}' (test-data/original or Sekiro install are read-only).");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "SoulsAiStudio.Core.Tests"))
                || File.Exists(Path.Combine(dir.FullName, "SoulsAIStudio.sln")))
                return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, "test-data", "original")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindOriginal()
    {
        string? env = Environment.GetEnvironmentVariable(TestDataEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env);
        if (RepoRoot is null) return null;
        string candidate = Path.Combine(RepoRoot, "test-data", "original");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string Normalize(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsUnder(string fullWithSlash, string rootWithSlash) =>
        fullWithSlash.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>[Fact] that auto-skips (with reason) when test-data/original is missing.</summary>
public sealed class RequiresTestDataFactAttribute : FactAttribute
{
    public RequiresTestDataFactAttribute()
    {
        if (!TestPaths.HasTestData) Skip = TestPaths.MissingTestDataReason;
    }
}

/// <summary>[Theory] that auto-skips (with reason) when test-data/original is missing.</summary>
public sealed class RequiresTestDataTheoryAttribute : TheoryAttribute
{
    public RequiresTestDataTheoryAttribute()
    {
        if (!TestPaths.HasTestData) Skip = TestPaths.MissingTestDataReason;
    }
}

/// <summary>[Fact] that auto-skips unless the real Sekiro install (unpacked, with chr/) is present. Read-only use.</summary>
public sealed class RequiresSekiroInstallFactAttribute : FactAttribute
{
    public RequiresSekiroInstallFactAttribute()
    {
        if (!TestPaths.HasSekiroInstall) Skip = $"Sekiro install not found at '{TestPaths.SekiroInstallDir}' (needs unpacked chr/, param/).";
    }
}
