using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>Tests for the test infrastructure itself + the read-only guard on test-data/original.</summary>
public class TestDataGuardTests(ITestOutputHelper output)
{
    [Fact]
    public void RepoRoot_IsFound()
    {
        Assert.NotNull(TestPaths.RepoRoot);
        Assert.True(Directory.Exists(Path.Combine(TestPaths.RepoRoot!, "tests", "SoulsAiStudio.Core.Tests")));
    }

    /// <summary>
    /// CLAUDE.md 7.1: test-data/original must never change. Hashes all 192 listed files (~4 MB, &lt;1 s)
    /// and fails on any changed, missing or NEW file inside the backup.
    /// </summary>
    [RequiresTestDataFact]
    public void Original_ChecksumsUnchanged()
    {
        var result = OriginalChecksumGuard.Verify(TestPaths.RequireOriginalDir());
        output.WriteLine(result.ToString());
        Assert.True(result.Listed > 0, "SHA256SUMS.txt is empty");
        Assert.True(result.Ok, "test-data/original was modified!\n" + result);
    }

    [RequiresTestDataFact]
    public void ChecksumPaths_AreRelativeToSeveranceDev()
    {
        // Documents a discrepancy with CLAUDE.md ("path relative to test-data/original/"):
        // the file actually lists ./script/... which resolves under original/Severance-dev/.
        var result = OriginalChecksumGuard.Verify(TestPaths.RequireOriginalDir());
        Assert.Equal(TestPaths.ModDir, result.BaseDir.TrimEnd(Path.DirectorySeparatorChar));
    }

    [RequiresTestDataFact]
    public void TempWorkDir_CopiesSubset_AndCleansUp()
    {
        string path;
        using (var work = TempWorkDir.Create("guard"))
        {
            path = work.Path;
            Assert.False(TestPaths.IsProtectedPath(work.Path));
            string dcx = work.CopyScript("m10_00_00_00.luabnd.dcx");
            string dir = work.CopyScript("m10_00_00_00-luabnd-dcx");
            Assert.True(File.Exists(dcx));
            Assert.True(File.Exists(Path.Combine(dir, "_witchy-bnd4.xml")));
            Assert.Equal(
                Directory.GetFiles(Samples.UnpackedDir("m10_00_00_00"), "*", SearchOption.AllDirectories).Length,
                Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length);

            // Copy is writable and independent of the original.
            File.AppendAllText(Path.Combine(dir, "out", "bin", "506300_battle.lua"), "\n-- test edit\n");
            Assert.NotEqual(new FileInfo(Samples.OwlBattle).Length, new FileInfo(Path.Combine(dir, "out", "bin", "506300_battle.lua")).Length);
        }
        Assert.False(Directory.Exists(path), "temp work dir not cleaned up");
    }

    [RequiresTestDataFact]
    public void ProtectedPaths_RefuseWrites()
    {
        Assert.True(TestPaths.IsProtectedPath(Samples.OwlBattle));
        Assert.True(TestPaths.IsProtectedPath(Path.Combine(TestPaths.SekiroInstallDir, "mods", "Severance-dev", "script", "x.luabnd.dcx")));
        Assert.Throws<InvalidOperationException>(() => TestPaths.AssertWritable(Path.Combine(TestPaths.RequireScriptDir(), "new.txt")));
    }
}
