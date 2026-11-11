using SoulsFormats;

namespace SoulsAiStudio.Core.Formats;

/// <summary>
/// Loads Oodle (needed for DCX_KRAK, e.g. <c>m25_00_00_00.luabnd.dcx</c>) from the user's Sekiro install.
/// Oodle is proprietary: it is never shipped with the app, only loaded in place from the game folder.
/// </summary>
public static class OodleLoader
{
    public const string DllName = "oo2core_6_win64.dll";
    private static readonly object Gate = new();

    public static string? LoadedFrom { get; private set; }
    public static bool IsLoaded => LoadedFrom is not null;

    /// <summary>Tries each directory in order; returns true if Oodle is (already) loaded.</summary>
    public static bool TryLoad(params string?[] searchDirs)
    {
        lock (Gate)
        {
            if (IsLoaded) return true;
            if (!OperatingSystem.IsWindows()) return false;
            foreach (string? dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string path = Path.Combine(dir, DllName);
                if (File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out IntPtr handle))
                {
                    Oodle.Oodle6Ptr = handle;
                    LoadedFrom = path;
                    return true;
                }
            }
            return false;
        }
    }
}
