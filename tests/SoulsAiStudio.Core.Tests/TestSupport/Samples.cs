namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>Known sample files in test-data/original (see CLAUDE.md 7.1).</summary>
public static class Samples
{
    public const string Owl = "506300";        // Inner Owl (Father), m10_00_00_00
    public const string Isshin = "540300";     // Inner Isshin, m11_02_00_00
    public const string TrueMonk = "500000";   // True Monk, m25_00_00_00

    /// <summary>Maps with a .luabnd.dcx AND an unpacked WitchyBND folder.</summary>
    public static readonly string[] UnpackedMaps = ["m10_00_00_00", "m11_02_00_00", "m25_00_00_00"];

    /// <summary>All .luabnd.dcx files (file name without extension).</summary>
    public static readonly string[] AllLuabnds = ["m10_00_00_00", "m11_02_00_00", "m25_00_00_00", "aicommon"];

    public static string LuabndPath(string name) => Path.Combine(TestPaths.RequireScriptDir(), name + ".luabnd.dcx");
    public static string UnpackedDir(string map) => Path.Combine(TestPaths.RequireScriptDir(), map + "-luabnd-dcx");
    public static string WitchyXmlPath(string map) => Path.Combine(UnpackedDir(map), "_witchy-bnd4.xml");

    public static string BattleLua(string map, string npcId) => Path.Combine(UnpackedDir(map), "out", "bin", npcId + "_battle.lua");
    public static string OwlBattle => BattleLua("m10_00_00_00", Owl);
    public static string IsshinBattle => BattleLua("m11_02_00_00", Isshin);
    public static string TrueMonkBattle => BattleLua("m25_00_00_00", TrueMonk);

    public static TheoryData<string> LuabndNames() => ToTheory(AllLuabnds);
    public static TheoryData<string> UnpackedMapNames() => ToTheory(UnpackedMaps);

    private static TheoryData<string> ToTheory(IEnumerable<string> items)
    {
        var td = new TheoryData<string>();
        foreach (var i in items) td.Add(i);
        return td;
    }

    /// <summary>Lua/HavokScript bytecode signature: ESC 'L' 'u' 'a'.</summary>
    public static bool IsLuaBytecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a';
}
