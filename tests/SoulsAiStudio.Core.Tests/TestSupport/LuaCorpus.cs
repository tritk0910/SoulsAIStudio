using System.Text;

namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// Every <c>out/bin/*.lua</c> in the three unpacked luabnd folders.
/// Plug into future parser round-trip tests:
/// <code>
/// [RequiresTestDataTheory]
/// [MemberData(nameof(LuaCorpus.PlainText), MemberType = typeof(LuaCorpus))]
/// public void Parser_RoundTrips(string relPath) { var text = LuaCorpus.ReadText(relPath); ... }
/// </code>
/// Rows are the path relative to the script dir (e.g. <c>m10_00_00_00-luabnd-dcx/out/bin/506300_battle.lua</c>),
/// which keeps test names readable and serializable.
/// </summary>
public static class LuaCorpus
{
    public const string NoDataSentinel = "<no-test-data>";

    public sealed record Entry(string RelPath, string Map, string FileName, long Size, bool IsBytecode, int LineCount)
    {
        public string FullPath => Path.Combine(TestPaths.RequireScriptDir(), RelPath);
    }

    private static readonly Lazy<IReadOnlyList<Entry>> _entries = new(Load);

    public static IReadOnlyList<Entry> Entries => _entries.Value;

    private static IReadOnlyList<Entry> Load()
    {
        if (!TestPaths.HasTestData) return [];
        string script = TestPaths.RequireScriptDir();
        var list = new List<Entry>();
        foreach (string map in Samples.UnpackedMaps)
        {
            string bin = Path.Combine(script, map + "-luabnd-dcx", "out", "bin");
            if (!Directory.Exists(bin)) continue;
            foreach (string f in Directory.EnumerateFiles(bin, "*").Where(f => string.Equals(Path.GetExtension(f), ".lua", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(f);
                bool bc = Samples.IsLuaBytecode(bytes);
                int lines = bc ? 0 : CountLines(bytes);
                list.Add(new Entry(Path.GetRelativePath(script, f).Replace('\\', '/'), map, Path.GetFileName(f), bytes.LongLength, bc, lines));
            }
        }
        return list;
    }

    private static int CountLines(byte[] b)
    {
        if (b.Length == 0) return 0;
        int n = 1;
        foreach (byte c in b) if (c == (byte)'\n') n++;
        if (b[^1] == (byte)'\n') n--;
        return n;
    }

    /// <summary>Read a corpus file as text. Encoding: UTF-8 (no BOM handling beyond detection) — see LuaCorpusTests for what the data actually is.</summary>
    public static string ReadText(string relPath) => File.ReadAllText(Path.Combine(TestPaths.RequireScriptDir(), relPath), Encoding.UTF8);

    public static byte[] ReadBytes(string relPath) => File.ReadAllBytes(Path.Combine(TestPaths.RequireScriptDir(), relPath));

    /// <summary>All corpus files (plain text + bytecode).</summary>
    public static TheoryData<string> All() => ToTheory(Entries);

    /// <summary>Plain-text Lua only (what a Lua parser must round-trip).</summary>
    public static TheoryData<string> PlainText() => ToTheory(Entries.Where(e => !e.IsBytecode));

    /// <summary>Bytecode only (needs DSLuaDecompiler before parsing).</summary>
    public static TheoryData<string> Bytecode() => ToTheory(Entries.Where(e => e.IsBytecode));

    /// <summary>The three required battle scripts (Owl, Isshin, True Monk).</summary>
    public static TheoryData<string> KeyBattleScripts()
    {
        var td = new TheoryData<string>();
        td.Add($"m10_00_00_00-luabnd-dcx/out/bin/{Samples.Owl}_battle.lua");
        td.Add($"m11_02_00_00-luabnd-dcx/out/bin/{Samples.Isshin}_battle.lua");
        td.Add($"m25_00_00_00-luabnd-dcx/out/bin/{Samples.TrueMonk}_battle.lua");
        return td;
    }

    private static TheoryData<string> ToTheory(IEnumerable<Entry> entries)
    {
        var td = new TheoryData<string>();
        foreach (var e in entries) td.Add(e.RelPath);
        // xUnit 2 fails a theory with zero rows; the RequiresTestData* attribute skips it anyway.
        if (td.Count == 0) td.Add(NoDataSentinel);
        return td;
    }
}
