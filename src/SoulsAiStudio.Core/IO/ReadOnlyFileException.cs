namespace SoulsAiStudio.Core.IO;

/// <summary>Thrown when a save targets a file marked read-only. The UI should ask the user before clearing the flag.</summary>
public sealed class ReadOnlyFileException(string path)
    : IOException($"'{path}' is read-only. Clear the read-only flag to save over it.")
{
    public string FilePath { get; } = path;
}
