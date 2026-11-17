namespace SoulsAiStudio.Core.Watch;

public enum DiffKind { Same, Removed, Added }

/// <param name="OldLine">0-based line in the old text, -1 for an added line.</param>
/// <param name="NewLine">0-based line in the new text, -1 for a removed line.</param>
public readonly record struct DiffLine(DiffKind Kind, int OldLine, int NewLine, string Text);

/// <summary>A group of changes with a few unchanged lines around them.</summary>
public sealed record DiffHunk(IReadOnlyList<DiffLine> Lines)
{
    public int Removed => Lines.Count(l => l.Kind == DiffKind.Removed);
    public int Added => Lines.Count(l => l.Kind == DiffKind.Added);
}

/// <summary>
/// Line diff (Myers' O(ND) algorithm, common prefix/suffix trimmed first). Used for "Show diff" when a script with
/// unsaved edits changed on disk. When the texts differ in more than <see cref="MaxEdits"/> lines the result is still
/// correct but not minimal (everything in the differing middle is removed, then added).
/// </summary>
public static class LineDiff
{
    public const int MaxEdits = 3000;

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines; // a final newline doesn't start another line
    }

    public static List<DiffLine> Compute(string oldText, string newText) => Compute(SplitLines(oldText), SplitLines(newText));

    public static List<DiffLine> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;

        var result = new List<DiffLine>(Math.Max(a.Count, b.Count) + 8);
        for (int i = 0; i < prefix; i++) result.Add(new DiffLine(DiffKind.Same, i, i, a[i]));

        // Lines → ids so the inner loop compares ints.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] A = Ids(a, prefix, a.Count - suffix, ids), B = Ids(b, prefix, b.Count - suffix, ids);
        var middle = Myers(A, B) ?? Fallback(A.Length, B.Length);
        foreach (var (kind, x, y) in middle)
            result.Add(kind switch
            {
                DiffKind.Same => new DiffLine(kind, prefix + x, prefix + y, a[prefix + x]),
                DiffKind.Removed => new DiffLine(kind, prefix + x, -1, a[prefix + x]),
                _ => new DiffLine(kind, -1, prefix + y, b[prefix + y]),
            });

        for (int i = 0; i < suffix; i++)
            result.Add(new DiffLine(DiffKind.Same, a.Count - suffix + i, b.Count - suffix + i, a[a.Count - suffix + i]));
        return result;
    }

    /// <summary>Changed regions with <paramref name="context"/> unchanged lines before/after; unchanged runs between are dropped.</summary>
    public static List<DiffHunk> Hunks(IReadOnlyList<DiffLine> diff, int context = 3)
    {
        var hunks = new List<DiffHunk>();
        int i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == DiffKind.Same) { i++; continue; }
            int start = Math.Max(0, i - context);
            int lastChange = i;
            // Extend while the next change is close enough that the context of both would touch.
            for (int j = i + 1; j < diff.Count && j - lastChange <= 2 * context + 1; j++)
                if (diff[j].Kind != DiffKind.Same) lastChange = j;
            int end = Math.Min(diff.Count, lastChange + 1 + context);
            hunks.Add(new DiffHunk(diff.Skip(start).Take(end - start).ToList()));
            i = end;
        }
        return hunks;
    }

    private static int[] Ids(IReadOnlyList<string> lines, int from, int to, Dictionary<string, int> ids)
    {
        var r = new int[Math.Max(0, to - from)];
        for (int i = from; i < to; i++)
        {
            if (!ids.TryGetValue(lines[i], out int id)) ids[lines[i]] = id = ids.Count;
            r[i - from] = id;
        }
        return r;
    }

    private static List<(DiffKind, int, int)> Fallback(int n, int m)
    {
        var r = new List<(DiffKind, int, int)>(n + m);
        for (int x = 0; x < n; x++) r.Add((DiffKind.Removed, x, -1));
        for (int y = 0; y < m; y++) r.Add((DiffKind.Added, -1, y));
        return r;
    }

    /// <summary>Shortest edit script, or null when it needs more than <see cref="MaxEdits"/> edits.</summary>
    private static List<(DiffKind, int, int)>? Myers(int[] A, int[] B)
    {
        int n = A.Length, m = B.Length;
        if (n == 0 || m == 0) return Fallback(n, m);
        int max = Math.Min(n + m, MaxEdits);
        int off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>(); // trace[d] = v[-d-1 .. d+1] before round d
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            var snap = new int[2 * d + 3];
            Array.Copy(v, off - d - 1, snap, 0, snap.Length);
            trace.Add(snap);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && A[x] == B[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return null;

        var edits = new List<(DiffKind, int, int)>(n + m);
        int cx = n, cy = m;
        for (int d = found; d >= 0; d--)
        {
            int[] t = trace[d];
            int dd = d;
            int V(int k) => t[k + dd + 1];
            int kk = cx - cy;
            int prevK = kk == -d || (kk != d && V(kk - 1) < V(kk + 1)) ? kk + 1 : kk - 1;
            int prevX = V(prevK), prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { cx--; cy--; edits.Add((DiffKind.Same, cx, cy)); }
            if (d > 0)
            {
                if (cx == prevX) { cy--; edits.Add((DiffKind.Added, -1, cy)); }
                else { cx--; edits.Add((DiffKind.Removed, cx, -1)); }
            }
        }
        edits.Reverse();
        return edits;
    }
}
