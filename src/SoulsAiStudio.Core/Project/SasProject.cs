using System.Text.Json;
using System.Text.Json.Serialization;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Project;

/// <summary>
/// A Souls AI Studio project (<c>*.sasproj</c>, JSON). Remembers the game/mod folders and the AI scripts the
/// user works on, so nothing has to be re-entered on every launch.
/// </summary>
public sealed class SasProject
{
    public const string Extension = ".sasproj";
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";

    /// <summary>Sekiro install folder (unpacked with UXM), e.g. <c>...\steamapps\common\Sekiro</c>.</summary>
    public string GameDir { get; set; } = "";

    /// <summary>Mod Engine mod folder that receives every write, e.g. <c>...\Sekiro\mods\Severance-dev</c>.</summary>
    public string ModDir { get; set; } = "";

    /// <summary>Optional override for <c>DSLuaDecompiler.exe</c>; empty = app setting.</summary>
    public string DecompilerPath { get; set; } = "";

    public List<ProjectScript> Scripts { get; set; } = [];
    public ProjectUiState Ui { get; set; } = new();

    /// <summary>Full path of the loaded/saved .sasproj file (not serialized).</summary>
    [JsonIgnore] public string FilePath { get; private set; } = "";
    [JsonIgnore] public string ProjectDir => Path.GetDirectoryName(FilePath) ?? "";
    [JsonIgnore] public string BackupDir => Path.Combine(ProjectDir, "backups");
    [JsonIgnore] public string CacheDir => Path.Combine(ProjectDir, "cache");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SasProject Load(string path)
    {
        var project = JsonSerializer.Deserialize<SasProject>(File.ReadAllText(path), Json)
            ?? throw new FormatException($"'{path}' is not a valid project file.");
        if (project.FormatVersion > CurrentFormatVersion)
            throw new NotSupportedException($"'{path}' was made by a newer version (format {project.FormatVersion}).");
        project.FilePath = Path.GetFullPath(path);
        project.ThrowIfInvalid();
        return project;
    }

    /// <summary>Folder problems that would make saving unsafe (see <see cref="WritePolicy.ValidateFolders"/>).</summary>
    public IReadOnlyList<string> Validate() => WritePolicy.ValidateFolders(GameDir, ModDir);

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count > 0) throw new InvalidOperationException("Invalid project folders: " + string.Join(" ", problems));
    }

    public void Save() => SaveAs(FilePath);

    public void SaveAs(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Project has no file path yet.");
        FilePath = Path.GetFullPath(path);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Creates the project folder layout (<c>backups/</c>, <c>cache/</c>) and writes the file.</summary>
    public static SasProject Create(string path, string name, string gameDir, string modDir, IEnumerable<ProjectScript> scripts)
    {
        var p = new SasProject { Name = name, GameDir = gameDir, ModDir = modDir, Scripts = scripts.ToList() };
        p.ThrowIfInvalid();
        p.SaveAs(path);
        Directory.CreateDirectory(p.BackupDir);
        Directory.CreateDirectory(p.CacheDir);
        return p;
    }
}

/// <summary>One AI script in the project, identified by its luabnd and the path inside it.</summary>
public sealed class ProjectScript
{
    /// <summary>Luabnd base name, e.g. <c>m10_00_00_00</c> or <c>aicommon</c>.</summary>
    public string Luabnd { get; set; } = "";

    /// <summary>Path inside the luabnd, e.g. <c>out\bin\506300_battle.lua</c>.</summary>
    public string EntryPath { get; set; } = "";

    /// <summary>Optional display name, e.g. "Inner Owl (Father)".</summary>
    public string DisplayName { get; set; } = "";

    [JsonIgnore] public string FileName => EntryPath[(EntryPath.LastIndexOf('\\') + 1)..];
    [JsonIgnore] public string Key => $"{Luabnd}|{EntryPath}";
    [JsonIgnore] public string Label => string.IsNullOrEmpty(DisplayName) ? FileName : $"{DisplayName} ({FileName})";
}

public sealed class ProjectUiState
{
    public string? OpenScriptKey { get; set; }
    public Dictionary<string, int> CursorLineByScript { get; set; } = [];
}
