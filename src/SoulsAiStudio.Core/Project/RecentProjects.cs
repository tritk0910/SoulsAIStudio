using System.Text.Json;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Project;

/// <summary>Most-recently-used project list, stored per user (default <c>%AppData%\SoulsAiStudio\recent.json</c>).</summary>
public sealed class RecentProjects
{
    public const int MaxEntries = 10;
    private readonly string _storePath;
    private List<string> _paths = [];

    public RecentProjects(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoulsAiStudio", "recent.json");
        try
        {
            if (File.Exists(_storePath))
                _paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_storePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _paths = []; // a corrupt MRU list must never block startup
        }
    }

    /// <summary>Recent project paths, newest first (missing files included so the UI can grey them out).</summary>
    public IReadOnlyList<string> Paths => _paths;

    public void Add(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        _paths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, full);
        if (_paths.Count > MaxEntries) _paths.RemoveRange(MaxEntries, _paths.Count - MaxEntries);
        Persist();
    }

    public void Remove(string projectPath)
    {
        if (_paths.RemoveAll(p => string.Equals(p, projectPath, StringComparison.OrdinalIgnoreCase)) > 0) Persist();
    }

    private void Persist()
    {
        try { AtomicFile.WriteAllText(_storePath, JsonSerializer.Serialize(_paths)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
