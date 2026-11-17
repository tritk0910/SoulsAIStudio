using SoulsAiStudio.Core.Watch;

namespace SoulsAiStudio.Core.Tests.Watch;

public class ChangeDebouncerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Burst_IsReportedOnce_AfterQuietPeriod()
    {
        var d = new ChangeDebouncer(TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < 60; i++) d.Note("a", T0.AddMilliseconds(i * 20)); // 60 writes over 1.2 s
        DateTime last = T0.AddMilliseconds(59 * 20);
        Assert.Empty(d.TakeDue(last.AddMilliseconds(499)));
        Assert.Equal(["a"], d.TakeDue(last.AddMilliseconds(500)));
        Assert.Empty(d.TakeDue(last.AddSeconds(10)));
        Assert.False(d.IsPending);
    }

    [Fact]
    public void Keys_AreIndependent_AndNotingAgainRestartsTheWait()
    {
        var d = new ChangeDebouncer(TimeSpan.FromMilliseconds(500));
        d.Note("a", T0);
        d.Note("b", T0.AddMilliseconds(400));
        Assert.Equal(["a"], d.TakeDue(T0.AddMilliseconds(600)));
        d.Note("b", T0.AddMilliseconds(800));
        Assert.Empty(d.TakeDue(T0.AddMilliseconds(1000)));
        Assert.Equal(["b"], d.TakeDue(T0.AddMilliseconds(1300)));
    }
}

public class LineDiffTests
{
    [Fact]
    public void Identical_AllSame()
    {
        var diff = LineDiff.Compute("a\nb\nc\n", "a\nb\nc\n");
        Assert.All(diff, l => Assert.Equal(DiffKind.Same, l.Kind));
        Assert.Equal(3, diff.Count);
        Assert.Empty(LineDiff.Hunks(diff));
    }

    [Fact]
    public void InsertDeleteReplace_AreMinimal()
    {
        var diff = LineDiff.Compute("a\nb\nc\nd\n", "a\nX\nc\nd\ne\n");
        Assert.Equal(new[] { (DiffKind.Same, "a"), (DiffKind.Removed, "b"), (DiffKind.Added, "X"), (DiffKind.Same, "c"), (DiffKind.Same, "d"), (DiffKind.Added, "e") },
            diff.Select(l => (l.Kind, l.Text)).ToArray());
        Assert.Equal(1, diff[1].OldLine);
        Assert.Equal(1, diff[2].NewLine);
        Assert.Equal(4, diff[5].NewLine);
    }

    [Fact]
    public void CrLf_And_FinalNewline_AreIgnored()
    {
        Assert.All(LineDiff.Compute("a\r\nb\r\n", "a\nb"), l => Assert.Equal(DiffKind.Same, l.Kind));
        Assert.Empty(LineDiff.SplitLines(""));
    }

    [Fact]
    public void EmptySides()
    {
        Assert.All(LineDiff.Compute("", "x\ny\n"), l => Assert.Equal(DiffKind.Added, l.Kind));
        Assert.All(LineDiff.Compute("x\ny\n", ""), l => Assert.Equal(DiffKind.Removed, l.Kind));
    }

    [Fact]
    public void Random_ReconstructsBothSides_WithLcsMinimalEdits()
    {
        var rng = new Random(1234);
        for (int iter = 0; iter < 400; iter++)
        {
            string[] a = RandomLines(rng), b = RandomLines(rng);
            var diff = LineDiff.Compute(a, b);
            Assert.Equal(a, diff.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text).ToArray());
            Assert.Equal(b, diff.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text).ToArray());
            Assert.Equal(Lcs(a, b), diff.Count(l => l.Kind == DiffKind.Same)); // shortest edit script
            // Line numbers point at the right lines.
            foreach (var l in diff)
            {
                if (l.OldLine >= 0) Assert.Equal(a[l.OldLine], l.Text);
                if (l.NewLine >= 0) Assert.Equal(b[l.NewLine], l.Text);
            }
        }
    }

    [Fact]
    public void Hunks_KeepContext_AndMergeCloseChanges()
    {
        var a = Enumerable.Range(0, 40).Select(i => "line" + i).ToArray();
        var b = a.ToArray();
        b[5] = "changed5";
        b[8] = "changed8";   // close to 5 → same hunk
        b[30] = "changed30"; // far → own hunk
        var hunks = LineDiff.Hunks(LineDiff.Compute(a, b), context: 2);
        Assert.Equal(2, hunks.Count);
        Assert.Equal(2, hunks[0].Removed);
        Assert.Equal(2, hunks[0].Added);
        Assert.Equal("line3", hunks[0].Lines[0].Text);
        Assert.Equal("line10", hunks[0].Lines[^1].Text);
        Assert.Equal("line28", hunks[1].Lines[0].Text);
        Assert.Equal("line32", hunks[1].Lines[^1].Text);
    }

    [Fact]
    public void LargeFile_SmallEdit_IsFast()
    {
        var a = Enumerable.Range(0, 20000).Select(i => $"local x{i} = {i % 97}").ToArray();
        var b = a.ToList();
        b.Insert(10000, "-- new");
        b.RemoveAt(15000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diff = LineDiff.Compute(a, b);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, diff.Count(l => l.Kind != DiffKind.Same));
    }

    [Fact]
    public void TotallyDifferent_HugeInputs_FallBackButStayCorrect()
    {
        var a = Enumerable.Range(0, 5000).Select(i => "a" + i).ToArray();
        var b = Enumerable.Range(0, 5000).Select(i => "b" + i).ToArray();
        var diff = LineDiff.Compute(a, b);
        Assert.Equal(a, diff.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text).ToArray());
        Assert.Equal(b, diff.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text).ToArray());
    }

    private static string[] RandomLines(Random rng) =>
        Enumerable.Range(0, rng.Next(0, 14)).Select(_ => ((char)('a' + rng.Next(4))).ToString()).ToArray();

    private static int Lcs(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Length, b.Length];
    }
}
