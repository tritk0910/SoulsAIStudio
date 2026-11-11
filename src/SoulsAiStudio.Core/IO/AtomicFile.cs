namespace SoulsAiStudio.Core.IO;

/// <summary>
/// Crash-safe file writes: data goes to a temp file in the same directory, is flushed to disk, then
/// replaces the target in one rename. A failure part-way never leaves a half-written target.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? throw new ArgumentException($"No directory in '{path}'", nameof(path));
        // A read-only target is a deliberate signal (e.g. a protected backup copy); never silently clear it.
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly))
            throw new ReadOnlyFileException(full);
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, LuaText.Encode(text));
}
