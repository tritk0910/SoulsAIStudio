namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// Unique temp working directory for tests that write. Copies a chosen subset of
/// <c>test-data/original</c> into it so the original backup is never touched.
/// Deleted on Dispose (set env <c>SAS_KEEP_TEMP=1</c> to keep it for debugging).
/// <code>
/// using var work = TempWorkDir.Create();
/// string dcx = work.CopyFromOriginal("Severance-dev/script/m10_00_00_00.luabnd.dcx");
/// string dir = work.CopyFromOriginal("Severance-dev/script/m10_00_00_00-luabnd-dcx");
/// </code>
/// </summary>
public sealed class TempWorkDir : IDisposable
{
    public string Path { get; }

    private TempWorkDir(string path) => Path = path;

    public static TempWorkDir Create(string? label = null)
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoulsAiStudio.Tests");
        string name = $"{(string.IsNullOrEmpty(label) ? "work" : label)}-{Guid.NewGuid():N}";
        string path = System.IO.Path.Combine(root, name);
        TestPaths.AssertWritable(path);
        Directory.CreateDirectory(path);
        return new TempWorkDir(path);
    }

    /// <summary>Path inside the work dir (forward or back slashes accepted). Does not create anything.</summary>
    public string Combine(string relative) =>
        System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// Copy a file or directory (recursively) from <c>test-data/original/&lt;relative&gt;</c>
    /// to the same relative location under the work dir. Returns the destination path.
    /// Copies are made writable (read-only attribute cleared).
    /// </summary>
    public string CopyFromOriginal(string relative)
    {
        string src = System.IO.Path.Combine(TestPaths.RequireOriginalDir(), relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        string dst = Combine(relative);
        TestPaths.AssertWritable(dst);
        if (File.Exists(src))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            File.SetAttributes(dst, FileAttributes.Normal);
        }
        else if (Directory.Exists(src))
        {
            CopyDirectory(src, dst);
        }
        else
        {
            throw new FileNotFoundException($"Not found in test-data/original: {relative}", src);
        }
        return dst;
    }

    /// <summary>Copy several entries; returns destination paths in order.</summary>
    public IReadOnlyList<string> CopyFromOriginal(params string[] relatives) => relatives.Select(CopyFromOriginal).ToList();

    /// <summary>Convenience: copy from <c>test-data/original/Severance-dev/script/&lt;name&gt;</c>.</summary>
    public string CopyScript(string name) => CopyFromOriginal("Severance-dev/script/" + name);

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = System.IO.Path.Combine(dst, System.IO.Path.GetRelativePath(src, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("SAS_KEEP_TEMP") == "1") return;
        try
        {
            if (!Directory.Exists(Path)) return;
            foreach (string f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { /* best effort; temp dir */ }
        catch (UnauthorizedAccessException) { }
    }
}
