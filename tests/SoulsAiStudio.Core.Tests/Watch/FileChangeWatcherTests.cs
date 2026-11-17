using SoulsAiStudio.Core.IO;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsAiStudio.Core.Watch;

namespace SoulsAiStudio.Core.Tests.Watch;

/// <summary>Real FileSystemWatcher tests on temp folders (short debounce).</summary>
public class FileChangeWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private static List<WatchChange> Collect(FileChangeWatcher w, int expected, int timeoutMs = 5000)
    {
        var all = new List<WatchChange>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && all.Count < expected)
        {
            all.AddRange(w.Drain());
            Thread.Sleep(25);
        }
        // Give stragglers (duplicate reports) a chance to show up.
        Thread.Sleep(600);
        all.AddRange(w.Drain());
        return all;
    }

    private static void AssertNothing(FileChangeWatcher w) => Assert.Empty(Collect(w, expected: 1, timeoutMs: 900));

    private static void Touch(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ExternalWrite_ToWatchedFile_IsReportedOnce()
    {
        using var work = TempWorkDir.Create("watch-file");
        string file = work.Combine("mod/chr/c5060.anibnd.dcx");
        Touch(file, "v1");
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Anibnd, "c5060", file)]);

        for (int i = 0; i < 5; i++) { File.WriteAllText(file, "v2 " + i); Thread.Sleep(10); } // burst
        var changes = Collect(w, 1);
        var c = Assert.Single(changes);
        Assert.Equal(WatchKind.Anibnd, c.Spec.Kind);
        Assert.Equal(Path.GetFullPath(file), Assert.Single(c.Files));
    }

    [Fact]
    public void UnrelatedFiles_InSameFolder_AreIgnored()
    {
        using var work = TempWorkDir.Create("watch-unrelated");
        string file = work.Combine("mod/chr/c5060.anibnd.dcx");
        Touch(file, "v1");
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Anibnd, "c5060", file)]);
        Touch(work.Combine("mod/chr/c5060.anibnd.dcx.dsasbak"), "x");
        Touch(work.Combine("mod/chr/_DSAS_WORKSPACE.json"), "x");
        Touch(work.Combine("mod/chr/c9999.anibnd.dcx"), "x");
        AssertNothing(w);
    }

    [Fact]
    public void OwnAtomicWrite_IsNotReported()
    {
        using var work = TempWorkDir.Create("watch-own");
        string file = work.Combine("mod/script/m10_00_00_00.luabnd.dcx");
        Touch(file, "v1");
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Luabnd, "m10_00_00_00", file)]);

        using (w.Track(file))
        {
            AtomicFile.WriteAllBytes(file, "ours"u8);
            Thread.Sleep(400); // longer than the debounce: still held back while the scope is open
        }
        AssertNothing(w);

        // A later external write is still reported.
        File.WriteAllText(file, "theirs");
        Assert.Single(Collect(w, 1));
    }

    [Fact]
    public void ExternalAtomicTempAndRename_IsReportedOnce_TempFilesIgnored()
    {
        using var work = TempWorkDir.Create("watch-atomic");
        string dir = work.Combine("mod/script/m10_00_00_00-luabnd-dcx");
        string lua = Path.Combine(dir, "out", "bin", "506300_battle.lua");
        Touch(lua, "-- v1\n");
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Luabnd, "m10_00_00_00", dir, IsDirectory: true)]);

        AtomicFile.WriteAllText(lua, "-- v2 from another tool\n"); // temp file + rename, untracked = external
        var c = Assert.Single(Collect(w, 1));
        Assert.Equal(Path.GetFullPath(lua), Assert.Single(c.Files));
    }

    [Fact]
    public void WitchyBndStyleUnpack_ManyFiles_IsOneChange()
    {
        using var work = TempWorkDir.Create("watch-witchy");
        string script = work.Combine("mod/script");
        Directory.CreateDirectory(script);
        string dir = Path.Combine(script, "m10_00_00_00-luabnd-dcx"); // does not exist yet
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Luabnd, "m10_00_00_00", dir, IsDirectory: true)]);

        Touch(Path.Combine(dir, "_witchy-bnd4.xml"), "<bnd4/>");
        for (int i = 0; i < 60; i++) Touch(Path.Combine(dir, "out", "bin", $"5{i:D5}_battle.lua"), "-- " + i);
        Touch(Path.Combine(dir, "m10_00_00_00.luabnd.dcx.bak"), "backup"); // ignored

        var changes = Collect(w, 1);
        var c = Assert.Single(changes);
        Assert.Equal(61, c.Files.Count);

        // After the folder appeared the watcher moved onto it: edits inside are still seen.
        File.WriteAllText(Path.Combine(dir, "out", "bin", "500000_battle.lua"), "-- edited");
        Assert.Single(Assert.Single(Collect(w, 1)).Files);
    }

    [Fact]
    public void MissingFolder_CreatedLater_IsPickedUp()
    {
        using var work = TempWorkDir.Create("watch-missing");
        string param = work.Combine("mod/param/gameparam/gameparam.parambnd.dcx"); // mod/ exists, param/ doesn't
        Directory.CreateDirectory(work.Combine("mod"));
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.GameParam, "gameparam", param)]);

        Touch(param, "params");
        var c = Assert.Single(Collect(w, 1));
        Assert.Equal(WatchKind.GameParam, c.Spec.Kind);
    }

    [Fact]
    public void Deletion_IsReported()
    {
        using var work = TempWorkDir.Create("watch-delete");
        string file = work.Combine("mod/chr/c5060.chrbnd.dcx");
        Touch(file, "v1");
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Chrbnd, "c5060", file)]);
        File.Delete(file);
        Assert.Single(Collect(w, 1));
    }

    [Fact]
    public void Overflow_Rescans_ReportsOnlyRealChanges()
    {
        using var work = TempWorkDir.Create("watch-overflow");
        string a = work.Combine("mod/a.dcx"), b = work.Combine("mod/b.dcx");
        Touch(a, "a1");
        Touch(b, "b1");
        using var w = new FileChangeWatcher(Debounce, autoPump: false);
        w.SetSpecs([new WatchSpec(WatchKind.Anibnd, "a", a), new WatchSpec(WatchKind.Chrbnd, "b", b)]);

        w.SimulateOverflow(); // nothing changed: re-scan finds nothing
        PumpTwice(w);
        Assert.Empty(w.Drain());

        w.Dispose(); // no OS events from here on: only the re-scan can find the change
        using var w2 = new FileChangeWatcher(Debounce, autoPump: false);
        w2.SetSpecs([new WatchSpec(WatchKind.Anibnd, "a", a), new WatchSpec(WatchKind.Chrbnd, "b", b)]);
        File.WriteAllText(b, "b2 longer");
        Thread.Sleep(300);
        w2.Drain();
        w2.SimulateOverflow();
        PumpTwice(w2);
        var c = Assert.Single(w2.Drain());
        Assert.Equal(WatchKind.Chrbnd, c.Spec.Kind);
        Assert.Equal(1, w2.ErrorCount);
    }

    /// <summary>First pump rebuilds the watchers (and restarts the quiet period), the second evaluates.</summary>
    private static void PumpTwice(FileChangeWatcher w)
    {
        w.Pump(DateTime.UtcNow.AddSeconds(1));
        w.Pump(DateTime.UtcNow.AddSeconds(3));
    }

    [Fact]
    public void LockedFile_IsRetried_UntilTheWriterCloses()
    {
        using var work = TempWorkDir.Create("watch-locked");
        string file = work.Combine("mod/chr/c5060.anibnd.dcx");
        Touch(file, "v1");
        using var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Anibnd, "c5060", file)]);

        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Write("still writing..."u8);
            fs.Flush(flushToDisk: true);
            Thread.Sleep(900); // several debounce periods: not reported while locked
            Assert.Empty(w.Drain());
        }
        Assert.Single(Collect(w, 1));
    }

    [Fact]
    public void SetSpecs_KeepsKnownState_AndDispose_StopsReports()
    {
        using var work = TempWorkDir.Create("watch-specs");
        string a = work.Combine("mod/a.dcx"), b = work.Combine("mod/b.dcx");
        Touch(a, "a1");
        Touch(b, "b1");
        var w = new FileChangeWatcher(Debounce);
        w.SetSpecs([new WatchSpec(WatchKind.Anibnd, "a", a)]);
        w.SetSpecs([new WatchSpec(WatchKind.Anibnd, "a", a), new WatchSpec(WatchKind.Chrbnd, "b", b)]);
        AssertNothing(w); // adding a spec doesn't report its existing file

        w.Dispose();
        File.WriteAllText(a, "a2");
        Thread.Sleep(500);
        Assert.Empty(w.Drain());
    }

    // ---------- with ProjectSession (real luabnds, temp copies) ----------

    [RequiresTestDataFact]
    public async Task ProjectSessionSave_AndRepack_AreNotReported_ButVsCodeEditIs()
    {
        using var l = ModLayout.Create("watch-session").ModPacked("m10_00_00_00").ModUnpacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", "506300_battle.lua"));
        using var w = new FileChangeWatcher(Debounce);
        s.WriteTracker = w;
        w.SetSpecs(ProjectWatchSet.Build(s.Project, s.Catalog, null, null));
        Assert.Contains(w.Specs, x => x.IsDirectory && x.Tag == "m10_00_00_00");

        var doc = await s.OpenAsync(s.Project.Scripts[0]);
        await s.SaveAsync(doc, doc.SavedText + "-- ours\n");
        await s.RepackAsync("m10_00_00_00");
        AssertNothing(w);

        // An edit in VSCode to the unpacked .lua is external.
        string lua = l.ModUnpackedLua("m10_00_00_00", "506300_battle.lua");
        File.WriteAllText(lua, File.ReadAllText(lua) + "-- theirs\n");
        var c = Assert.Single(Collect(w, 1));
        Assert.Equal(WatchKind.Luabnd, c.Spec.Kind);
        Assert.Equal("m10_00_00_00", c.Spec.Tag);
        Assert.Equal(Path.GetFullPath(lua), Assert.Single(c.Files));
    }

    [RequiresTestDataFact]
    public async Task ProjectSessionUnpack_IsNotReported()
    {
        using var l = ModLayout.Create("watch-unpack").ModPacked("m10_00_00_00");
        var s = l.Session(ModLayout.Script("m10_00_00_00", "506300_battle.lua"));
        using var w = new FileChangeWatcher(Debounce);
        s.WriteTracker = w;
        w.SetSpecs(ProjectWatchSet.Build(s.Project, s.Catalog, null, null));
        await s.UnpackAsync("m10_00_00_00");
        Assert.True(Directory.Exists(l.ModUnpackedDir("m10_00_00_00")));
        AssertNothing(w);
    }

    [Fact]
    public void ProjectWatchSet_CoversLuabnds_Character_AndGameParam()
    {
        using var work = TempWorkDir.Create("watch-set");
        string game = work.Combine("game"), mod = work.Combine("mod");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mod);
        var p = SasProject.Create(work.Combine("proj/p.sasproj"), "p", game, mod,
            [new ProjectScript { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_battle.lua" },
             new ProjectScript { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_logic.lua" }]);
        var specs = ProjectWatchSet.Build(p, new ScriptCatalog(game, mod), Path.Combine(mod, "chr", "c5060.anibnd.dcx"), Path.Combine(game, "chr", "c5060.chrbnd.dcx"));
        Assert.Equal(3, specs.Count(s => s.Kind == WatchKind.Luabnd)); // mod packed, mod unpacked, game packed (no duplicates)
        Assert.Contains(specs, s => s.Kind == WatchKind.Anibnd && s.Tag == "c5060");
        Assert.Contains(specs, s => s.Kind == WatchKind.Chrbnd && s.Tag == "c5060");
        Assert.Contains(specs, s => s.Kind == WatchKind.GameParam && s.FullPath.StartsWith(Path.GetFullPath(mod)));

        // A read-only view copy in the project cache is never watched.
        var view = ProjectWatchSet.Build(p, new ScriptCatalog(game, mod), Path.Combine(p.GameViewDir, "chr", "c5060.anibnd.dcx"), null);
        Assert.DoesNotContain(view, s => s.Kind == WatchKind.Anibnd);
    }
}
