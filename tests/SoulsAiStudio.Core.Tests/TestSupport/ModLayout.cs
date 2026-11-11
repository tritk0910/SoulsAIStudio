using System.Security.Cryptography;
using SoulsAiStudio.Core.Formats;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// Fake "Sekiro install + Mod Engine mod" pair inside a <see cref="TempWorkDir"/>, populated from test-data/original.
/// <code>
/// using var l = ModLayout.Create().GamePacked("aicommon").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
/// var session = l.Session(new ProjectScript { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_battle.lua" });
/// </code>
/// </summary>
public sealed class ModLayout : IDisposable
{
    public TempWorkDir Work { get; }
    public string Game { get; }
    public string Mod { get; }
    public string GameScript => Path.Combine(Game, "script");
    public string ModScript => Path.Combine(Mod, "script");

    private ModLayout(TempWorkDir work)
    {
        Work = work;
        Game = Path.Combine(work.Path, "game");
        Mod = Path.Combine(work.Path, "mod");
        Directory.CreateDirectory(GameScript);
        Directory.CreateDirectory(ModScript);
    }

    public static ModLayout Create(string label = "layout")
    {
        // Core reads DCX_KRAK through SoulsFormats' global Oodle pointer.
        OodleSupport.EnsureLoaded();
        OodleLoader.TryLoad(TestPaths.SekiroInstallDir);
        return new ModLayout(TempWorkDir.Create(label));
    }

    public ModLayout GamePacked(string luabnd) { CopyFile(Samples.LuabndPath(luabnd), GamePackedPath(luabnd)); return this; }
    public ModLayout ModPacked(string luabnd) { CopyFile(Samples.LuabndPath(luabnd), ModPackedPath(luabnd)); return this; }

    public ModLayout ModUnpacked(string map)
    {
        string src = Samples.UnpackedDir(map);
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            CopyFile(f, Path.Combine(ModUnpackedDir(map), Path.GetRelativePath(src, f)));
        return this;
    }

    public string GamePackedPath(string luabnd) => Path.Combine(GameScript, luabnd + ".luabnd.dcx");
    public string ModPackedPath(string luabnd) => Path.Combine(ModScript, luabnd + ".luabnd.dcx");
    public string ModUnpackedDir(string map) => Path.Combine(ModScript, map + "-luabnd-dcx");
    public string ModUnpackedLua(string map, string fileName) => Path.Combine(ModUnpackedDir(map), "out", "bin", fileName);

    public SasProject Project(params ProjectScript[] scripts) =>
        SasProject.Create(Path.Combine(Work.Path, "proj", "test.sasproj"), "test", Game, Mod, scripts);

    public ProjectSession Session(params ProjectScript[] scripts) => new(Project(scripts), decompilerExe: "");

    public static ProjectScript Script(string luabnd, string fileName) =>
        new() { Luabnd = luabnd, EntryPath = @"out\bin\" + fileName };

    /// <summary>sha256 of every file under the mod folder (relative path → hash), for "nothing changed" checks.</summary>
    public Dictionary<string, string> HashMod() =>
        Directory.EnumerateFiles(Mod, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Mod, f), Hash, StringComparer.OrdinalIgnoreCase);

    public static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static void CopyFile(string src, string dst)
    {
        TestPaths.AssertWritable(dst);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        File.SetAttributes(dst, FileAttributes.Normal);
    }

    public void Dispose() => Work.Dispose();
}
