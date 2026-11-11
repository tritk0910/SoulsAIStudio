using System.Runtime.InteropServices;
using SoulsFormats;

namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// DCX_KRAK (Oodle) is used by some luabnds (e.g. m25_00_00_00.luabnd.dcx, written by WitchyBND with
/// compressionLevel 6). SoulsFormats needs oo2core_6_win64.dll; it ships with Sekiro and is NOT redistributable,
/// so tests load it in place (read-only) from: %SAS_OODLE_DLL%, the test bin dir, or the Sekiro install dir.
/// </summary>
public static class OodleSupport
{
    public const string EnvVar = "SAS_OODLE_DLL";
    private static readonly Lazy<string?> _loadedFrom = new(TryLoad);

    /// <summary>Path the Oodle dll was loaded from, or null if unavailable.</summary>
    public static string? LoadedFrom => _loadedFrom.Value;
    public static bool Available => LoadedFrom is not null;

    public static string MissingReason =>
        $"Oodle (oo2core_6_win64.dll) not found via %{EnvVar}%, test bin dir or '{TestPaths.SekiroInstallDir}'; DCX_KRAK samples cannot be read.";

    public static bool EnsureLoaded() => Available;

    private static string? TryLoad()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "oo2core_6_win64.dll"));
        candidates.Add(Path.Combine(TestPaths.SekiroInstallDir, "oo2core_6_win64.dll"));
        foreach (string c in candidates)
        {
            if (File.Exists(c) && System.Runtime.InteropServices.NativeLibrary.TryLoad(c, out IntPtr h))
            {
                Oodle.Oodle6Ptr = h;
                return c;
            }
        }
        return null;
    }
}

/// <summary>[Fact]/[Theory] that skips when test data OR Oodle is missing.</summary>
public sealed class RequiresOodleTheoryAttribute : TheoryAttribute
{
    public RequiresOodleTheoryAttribute()
    {
        if (!TestPaths.HasTestData) Skip = TestPaths.MissingTestDataReason;
        else if (!OodleSupport.Available) Skip = OodleSupport.MissingReason;
    }
}

public sealed class RequiresOodleFactAttribute : FactAttribute
{
    public RequiresOodleFactAttribute()
    {
        if (!TestPaths.HasTestData) Skip = TestPaths.MissingTestDataReason;
        else if (!OodleSupport.Available) Skip = OodleSupport.MissingReason;
    }
}
