using System.Diagnostics;
using System.Numerics;
using SoulsAiStudio.Editor.Input;
using SoulsAiStudio.Editor.Rendering;
using SoulsAiStudio.Editor.Syntax;
using SoulsAiStudio.Editor.Text;
using Xunit.Abstractions;

namespace SoulsAiStudio.Editor.Tests;

/// <summary>Tests on the real Owl (Father) battle script. The file is only READ (test-data/original is read-only).</summary>
public class OwlFileTests
{
    readonly ITestOutputHelper _out;

    public OwlFileTests(ITestOutputHelper output) => _out = output;

    static string? FindOwl()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "test-data", "original", "Severance-dev", "script",
                "m10_00_00_00-luabnd-dcx", "out", "bin", "506300_battle.lua");
            if (File.Exists(p))
                return p;
            dir = dir.Parent;
        }
        return null;
    }

    static string? LoadOwl()
    {
        var p = FindOwl();
        return p == null ? null : File.ReadAllText(p); // read-only access
    }

    [Fact]
    public void Tokenizer_CoversRealFile()
    {
        var text = LoadOwl();
        if (text == null)
            return; // test data not present on this machine

        var buffer = new TextBuffer();
        buffer.SetText(text);
        var colorizer = new Colorizer(buffer);
        Assert.True(colorizer.Run(int.MaxValue));
        Assert.True(colorizer.EndState(buffer.LineCount - 1).IsNormal);

        var counts = new int[(int)LuaTokenKind.Count];
        for (int i = 0; i < buffer.LineCount; i++)
        {
            var line = buffer[i];
            Assert.Equal(line.Length, line.ColorSpan.Length);
            foreach (var k in line.ColorSpan)
                counts[k]++;
        }
        for (int k = 0; k < counts.Length; k++)
            _out.WriteLine($"{(LuaTokenKind)k,-12} {counts[k],8} chars");

        Assert.True(counts[(int)LuaTokenKind.Keyword] > 1000);
        Assert.True(counts[(int)LuaTokenKind.Constant] > 1000);
        Assert.True(counts[(int)LuaTokenKind.Number] > 1000);
        Assert.True(counts[(int)LuaTokenKind.Function] > 1000);

        // Specific spots (0-based lines) from the file
        int kengeki03 = FindLine(buffer, "Goal.Kengeki03 = function");
        Assert.Equal(2523, kengeki03);
        AssertWordKind(buffer, kengeki03, "function", LuaTokenKind.Keyword);
        int combo = FindLine(buffer, "f59_arg1:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002");
        AssertWordKind(buffer, combo, "GOAL_COMMON_ComboRepeat", LuaTokenKind.Constant);
        AssertWordKind(buffer, combo, "AddSubGoal", LuaTokenKind.Function);
        AssertWordKind(buffer, combo, "3002", LuaTokenKind.Number);
        AssertWordKind(buffer, combo, "TARGET_ENE_0", LuaTokenKind.Constant);
        int interrupt = FindLine(buffer, "elseif interruptSpeffect == 5060040 then");
        AssertWordKind(buffer, interrupt, "elseif", LuaTokenKind.Keyword);
        AssertWordKind(buffer, interrupt, "interruptSpeffect", LuaTokenKind.Identifier);
        int commented = FindLine(buffer, "-- elseif interruptSpeffect == 5060051");
        AssertWordKind(buffer, commented, "elseif", LuaTokenKind.Comment);
    }

    static int FindLine(TextBuffer b, string needle)
    {
        for (int i = 0; i < b.LineCount; i++)
            if (b.GetLineSpan(i).IndexOf(needle, StringComparison.Ordinal) >= 0)
                return i;
        Assert.Fail("not found: " + needle);
        return -1;
    }

    static void AssertWordKind(TextBuffer b, int line, string word, LuaTokenKind kind)
    {
        var span = b.GetLineSpan(line);
        int i = span.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, word);
        Assert.Equal(kind, (LuaTokenKind)b[line].ColorSpan[i]);
    }

    [Fact]
    public void EditUndoRoundTrip_ReturnsIdenticalText()
    {
        var original = LoadOwl();
        if (original == null)
            return;

        var editor = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        editor.Text = original;
        Assert.Equal(original, editor.Text);
        Assert.False(editor.IsDirty);

        var core = editor.Core;
        var rnd = new Random(1234);
        int edits = 0;
        for (int i = 0; i < 300; i++)
        {
            int line = rnd.Next(core.Buffer.LineCount);
            int col = rnd.Next(core.Buffer.GetLineLength(line) + 1);
            core.SetCursor((line, col));
            switch (rnd.Next(8))
            {
                case 0:
                    foreach (var ch in "-- sửa: ơ ư đ")
                        core.TypeChar(ch);
                    break;
                case 1:
                    core.TypeChar('\n');
                    break;
                case 2:
                    core.Backspace(rnd.Next(2) == 0);
                    break;
                case 3:
                    core.Delete(false);
                    break;
                case 4:
                    core.Select((line, 0), (Math.Min(core.Buffer.LineCount - 1, line + rnd.Next(5)), 3));
                    core.Tab(rnd.Next(2) == 0);
                    break;
                case 5:
                    ((MemoryClipboard)core.Clipboard).Text = "if x then\r\n    arg2:AddSubGoal(GOAL_COMMON_ComboFinal, 10, 3044)\r\nend";
                    core.Paste();
                    break;
                case 6:
                    core.Select((line, 0), (Math.Min(core.Buffer.LineCount - 1, line + rnd.Next(3)), 2));
                    core.Cut();
                    break;
                case 7:
                    core.Select((line, col), (line, col));
                    core.TypeChar('x');
                    break;
            }
            edits++;
        }

        var edited = editor.Text;
        Assert.NotEqual(original, edited);
        Assert.True(editor.IsDirty);

        while (core.Undo.CanUndo)
            core.DoUndo();
        Assert.Equal(original, editor.Text);
        Assert.False(editor.IsDirty);

        while (core.Undo.CanRedo)
            core.DoRedo();
        Assert.Equal(edited, editor.Text);
        _out.WriteLine($"{edits} edit ops -> {core.Undo.Count} undo records, round trip OK");
    }

    [Fact]
    public void Frame_VisibleLinesOnly_ZeroAllocs_UnderOneMs()
    {
        var text = LoadOwl();
        if (text == null)
            return;

        var editor = new LuaCodeEditor { Clipboard = new MemoryClipboard() };
        editor.Text = text;
        Assert.True(editor.ColorizeAll());

        // realistic state: selection, find query, diagnostics, highlight line
        editor.Core.Select((1700, 4), (1712, 10));
        editor.Find.SetQuery("AddSubGoal", false);
        var diags = new List<(int, int, int, bool, string)>();
        for (int i = 0; i < 2700; i += 7)
            diags.Add((i, 4, 20, i % 2 == 0, "Info: example diagnostic"));
        editor.SetDiagnostics(diags);
        editor.SetHighlightLine(1590);

        var sink = new NullDrawSink();
        var metrics = new FixedGlyphMetrics { LineHeight = 17, CharWidth = 8 };
        var size = new Vector2(1100, 1000); // ~60 visible lines at 1080p
        float[] scrolls = { 0, 1589 * 17f, 1695 * 17f, 2650 * 17f };

        // warm up (JIT, list growth, stackalloc paths)
        foreach (var s in scrolls)
            editor.DrawHeadless(sink, metrics, s, size, showFind: true);

        const int frames = 400;
        var sw = new Stopwatch();
        long before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        for (int i = 0; i < frames; i++)
            editor.DrawHeadless(sink, metrics, scrolls[i % scrolls.Length], size, showFind: true);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
        _out.WriteLine($"managed frame: {msPerFrame * 1000:F1} us/frame, {allocated} bytes allocated over {frames} frames, {sink.Calls / (frames + scrolls.Length)} draw calls/frame");
        Assert.Equal(0, allocated);
        Assert.True(msPerFrame < 1.0, $"{msPerFrame} ms/frame");
    }
}
