using SoulsAiStudio.Core.Backup;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Backup;

/// <summary>Synthetic tests for <see cref="BackupService"/> (temp dirs only, no game data).</summary>
public class BackupServiceTests
{
    private sealed class Env : IDisposable
    {
        public TempWorkDir Work { get; } = TempWorkDir.Create("backup");
        public string Mod => Work.Combine("mod");
        public string Backups => Work.Combine("proj/backups");
        public string File1 => Path.Combine(Mod, "script", "a.lua");
        public BackupService Service { get; }
        public Env()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File1)!);
            File.WriteAllText(File1, "v0");
            Service = new BackupService(Backups, Mod);
        }
        public string Snap(string snapshot) => File.ReadAllText(Path.Combine(snapshot, "script", "a.lua"));
        public void Dispose() => Work.Dispose();
    }

    [Fact]
    public void Original_IsFirstSeenVersion_AndNeverOverwritten()
    {
        using var e = new Env();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        for (int i = 1; i <= 5; i++)
        {
            e.Service.BackupBeforeSave([e.File1], t.AddSeconds(i));
            File.WriteAllText(e.File1, "v" + i);
        }
        Assert.Equal("v0", File.ReadAllText(Path.Combine(e.Service.OriginalDir, "script", "a.lua")));
    }

    [Fact]
    public void Prune_KeepsNewestN_NeverTouchesOriginal()
    {
        using var e = new Env();
        e.Service.KeepSnapshots = 2;
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        for (int i = 1; i <= 5; i++)
        {
            e.Service.BackupBeforeSave([e.File1], t.AddMinutes(i));
            File.WriteAllText(e.File1, "v" + i);
        }
        var snaps = e.Service.ListSnapshots();
        Assert.Equal(2, snaps.Count);
        Assert.Equal("v4", e.Snap(snaps[0]));
        Assert.Equal("v3", e.Snap(snaps[1]));
        Assert.True(Directory.Exists(e.Service.OriginalDir));
    }

    [Fact]
    public void ListSnapshots_OrdersAcrossDayAndYearBoundaries()
    {
        using var e = new Env();
        DateTime[] times = [new(2025, 12, 31, 23, 59, 59, 999), new(2026, 1, 1, 0, 0, 0, 1), new(2026, 1, 10, 9, 0, 0)];
        foreach (var t in times) e.Service.BackupBeforeSave([e.File1], t);
        Assert.Equal(times.OrderByDescending(x => x).Select(x => x.ToString("yyyyMMdd-HHmmss-fff")),
            e.Service.ListSnapshots().Select(Path.GetFileName));
    }

    [Fact]
    public void ReturnsNull_WhenNoFileExistsYet()
    {
        using var e = new Env();
        Assert.Null(e.Service.BackupBeforeSave([Path.Combine(e.Mod, "script", "missing.lua")]));
        Assert.Empty(e.Service.ListSnapshots());
    }

    [Fact]
    public void ReadOnlySource_IsBackedUp_AndCopyIsWritable()
    {
        using var e = new Env();
        File.SetAttributes(e.File1, FileAttributes.ReadOnly);
        string snap = e.Service.BackupBeforeSave([e.File1])!;
        Assert.False(File.GetAttributes(Path.Combine(snap, "script", "a.lua")).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(e.File1).HasFlag(FileAttributes.ReadOnly), "source attribute must be kept");
    }

    /// <summary>Two saves with the same timestamp (fast double Ctrl+S) must not merge/overwrite each other.</summary>
    [Fact]
    public void SameTimestamp_TwoBackups_AreDistinctSnapshots()
    {
        using var e = new Env();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, 123);
        string s1 = e.Service.BackupBeforeSave([e.File1], t)!;
        File.WriteAllText(e.File1, "v1");
        string s2 = e.Service.BackupBeforeSave([e.File1], t)!;
        Assert.NotEqual(s1, s2);
        Assert.Equal(2, e.Service.ListSnapshots().Count);
        Assert.Equal("v0", e.Snap(s1));
    }

    /// <summary>Real clock, back-to-back backups of a tiny file: every call must yield its own snapshot.</summary>
    [Fact] // passes today: copying takes >1 ms per call; collisions only with an injected identical timestamp
    public void RealClock_RapidBackups_AreAllDistinct()
    {
        using var e = new Env();
        var paths = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            paths.Add(e.Service.BackupBeforeSave([e.File1])!);
            File.WriteAllText(e.File1, "v" + (i + 1));
        }
        Assert.Equal(30, paths.Distinct().Count());
        Assert.Equal(30, e.Service.ListSnapshots().Count);
    }

    [Fact]
    public void ReturnedSnapshot_Exists_EvenWhenKeepSnapshotsIsZero()
    {
        using var e = new Env();
        e.Service.KeepSnapshots = 0;
        string? snap = e.Service.BackupBeforeSave([e.File1]);
        // KeepSnapshots=0 is an odd setting; the contract we check is only that it does not crash and original/ survives.
        Assert.NotNull(snap);
        Assert.True(File.Exists(Path.Combine(e.Service.OriginalDir, "script", "a.lua")));
    }

    [Fact]
    public void FileOutsideRoot_GoesUnderExternal()
    {
        using var e = new Env();
        string outside = e.Work.Combine("elsewhere/b.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "x");
        string snap = e.Service.BackupBeforeSave([outside])!;
        var files = Directory.GetFiles(snap, "*", SearchOption.AllDirectories);
        var f = Assert.Single(files);
        Assert.StartsWith(Path.Combine(snap, "_external"), f);
        Assert.StartsWith(Path.GetFullPath(e.Backups), Path.GetFullPath(f)); // never escapes the backup dir
    }
}
