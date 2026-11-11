using System.Diagnostics;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Lua.Decompile;
using SoulsAiStudio.Core.Lua.Syntax;
using SoulsAiStudio.Core.Tests.TestSupport;
using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.Lua;

/// <summary>[Fact] that skips unless DSLuaDecompiler.exe exists at the default path (or %SAS_DECOMPILER%).</summary>
public sealed class RequiresDecompilerFactAttribute : FactAttribute
{
    public static string ExePath => Environment.GetEnvironmentVariable("SAS_DECOMPILER") is { Length: > 0 } p ? p : LuaDecompiler.DefaultExePath;
    public RequiresDecompilerFactAttribute()
    {
        if (!TestPaths.HasTestData) Skip = TestPaths.MissingTestDataReason;
        else if (!File.Exists(ExePath)) Skip = $"DSLuaDecompiler.exe not found at '{ExePath}' (set %SAS_DECOMPILER%).";
    }
}

public class LuaDecompilerTests(ITestOutputHelper output)
{
    internal static LuabndEntry FirstBytecode(string luabnd, string? fileName = null)
    {
        OodleSupport.EnsureLoaded();
        var a = LuabndArchive.LoadPacked(Samples.LuabndPath(luabnd));
        return fileName is null ? a.LuaScripts.First(e => e.IsBytecode) : a.Find(fileName)!;
    }

    [Fact]
    public async Task MissingExe_ThrowsFileNotFound_WithPath()
    {
        using var work = TempWorkDir.Create("decomp-missing");
        string exe = work.Combine("nope/DSLuaDecompiler.exe");
        var d = new LuaDecompiler(exe, work.Combine("cache"));
        Assert.False(d.IsAvailable);
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => d.DecompileAsync([0x1B, (byte)'L', (byte)'u', (byte)'a', 0x50], "x.lua"));
        Assert.Equal(exe, ex.FileName);
        Assert.Contains("DSLuaDecompiler", ex.Message);
    }

    [RequiresDecompilerFact]
    public async Task Aicommon_Bytecode_DecompilesToLua_AndCacheIsFastAndIdentical()
    {
        using var work = TempWorkDir.Create("decomp");
        var entry = FirstBytecode("aicommon");
        var d = new LuaDecompiler(RequiresDecompilerFactAttribute.ExePath, work.Combine("cache"));

        var sw = Stopwatch.StartNew();
        string first = await d.DecompileAsync(entry.Bytes, entry.FileName);
        long cold = sw.ElapsedMilliseconds;
        sw.Restart();
        string second = await d.DecompileAsync(entry.Bytes, entry.FileName);
        long warm = sw.ElapsedMilliseconds;
        output.WriteLine($"{entry.RelativePath}: {entry.Bytes.Length} B bytecode -> {first.Length} chars; cold {cold} ms, cached {warm} ms");

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
        Assert.DoesNotContain('\r', first);
        Assert.True(warm < 200 && warm * 5 < Math.Max(cold, 1) + 50, $"cache not fast: cold {cold} ms, warm {warm} ms");
        Assert.Single(Directory.GetFiles(work.Combine("cache/decompiled")));

        // Output is lexable Lua with no gaps.
        AssertLexesWithoutGaps(first);
    }

    [RequiresDecompilerFact]
    public async Task CachedResult_IsReturned_EvenWhenExeLaterMissing()
    {
        using var work = TempWorkDir.Create("decomp-cache");
        var entry = FirstBytecode("aicommon");
        string cache = work.Combine("cache");
        string text = await new LuaDecompiler(RequiresDecompilerFactAttribute.ExePath, cache).DecompileAsync(entry.Bytes, entry.FileName);
        string again = await new LuaDecompiler(work.Combine("missing.exe"), cache).DecompileAsync(entry.Bytes, entry.FileName);
        Assert.Equal(text, again);
    }

    /// <summary>Garbage input must raise a clear error and must NOT be cached as "decompiled text".</summary>
    [RequiresDecompilerFact]
    public async Task GarbageInput_Throws_AndIsNotCached()
    {
        using var work = TempWorkDir.Create("decomp-bad");
        string cache = work.Combine("cache");
        var d = new LuaDecompiler(RequiresDecompilerFactAttribute.ExePath, cache);
        byte[] garbage = [0x1B, (byte)'L', (byte)'u', (byte)'a', 0x50, 1, 2, 3, 4, 5, 6, 7];
        string? result = null;
        Exception? error = null;
        try { result = await d.DecompileAsync(garbage, "garbage.lua"); }
        catch (Exception ex) { error = ex; }
        output.WriteLine(error?.ToString() ?? "returned: " + result);
        Assert.NotNull(error);
        Assert.IsType<InvalidOperationException>(error);
        Assert.False(Directory.Exists(Path.Combine(cache, "decompiled")) && Directory.EnumerateFiles(Path.Combine(cache, "decompiled")).Any(),
            "failed decompile was cached");
    }

    /// <summary>Decompiled battle scripts (bytecode in the corpus) go through the goal outline cleanly.</summary>
    [RequiresDecompilerFact]
    public async Task DecompiledBattleScripts_OutlineInvariantsHold()
    {
        using var work = TempWorkDir.Create("decomp-outline");
        var d = new LuaDecompiler(RequiresDecompilerFactAttribute.ExePath, work.Combine("cache"));
        // m10 (DFLT) battle bytecode: a few different authors' styles; 506001 is an Owl variant.
        var archive = LuabndArchive.LoadPacked(Samples.LuabndPath("m10_00_00_00"));
        var picks = archive.LuaScripts.Where(e => e.IsBytecode && e.FileName.EndsWith("_battle.lua")).Take(6).ToList();
        Assert.NotEmpty(picks);
        foreach (var e in picks)
        {
            string text = await d.DecompileAsync(e.Bytes, e.FileName);
            AssertLexesWithoutGaps(text);
            var goals = GoalOutline.Build(text);
            output.WriteLine($"{e.FileName}: {text.Split('\n').Length} lines, {goals.Count} goal functions");
            GoalOutlineTests.AssertOutlineInvariants(e.FileName, goals);
        }
    }

    internal static void AssertLexesWithoutGaps(string src)
    {
        var tokens = LuaLexer.Tokenize(src);
        int pos = 0;
        foreach (var t in tokens)
        {
            Assert.True(t.Start >= pos, $"token overlap at {t.Start}");
            Assert.True(string.IsNullOrWhiteSpace(src[pos..t.Start]), $"gap at {pos}");
            pos = t.Start + t.Length;
        }
        Assert.Equal(src.Length, pos);
    }
}
