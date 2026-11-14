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

    /// <summary>
    /// Characters (<c>cXXXX</c>) opened directly for animation (TAE) editing, without an AI script. An "animations only"
    /// project has characters and no scripts.
    /// </summary>
    public List<ProjectCharacter> Characters { get; set; } = [];
    public ProjectUiState Ui { get; set; } = new();

    /// <summary>Full path of the loaded/saved .sasproj file (not serialized).</summary>
    [JsonIgnore] public string FilePath { get; private set; } = "";
    [JsonIgnore] public string ProjectDir => Path.GetDirectoryName(FilePath) ?? "";
    [JsonIgnore] public string BackupDir => Path.Combine(ProjectDir, "backups");
    [JsonIgnore] public string CacheDir => Path.Combine(ProjectDir, "cache");
    /// <summary>No AI scripts: the project is for animation editing only (Lua editor hidden by default).</summary>
    [JsonIgnore] public bool IsAnimationsOnly => Scripts.Count == 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SasProject Load(string path)
    {
        SasProject? project;
        try { project = JsonSerializer.Deserialize<SasProject>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new FormatException($"'{path}' is not a valid project file: {ex.Message}", ex); }
        if (project == null) throw new FormatException($"'{path}' is not a valid project file.");
        project.Scripts ??= [];
        project.Characters ??= [];
        project.Ui ??= new ProjectUiState();
        project.Ui.CursorLineByScript ??= [];
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
    public static SasProject Create(string path, string name, string gameDir, string modDir, IEnumerable<ProjectScript> scripts,
        IEnumerable<ProjectCharacter>? characters = null)
    {
        var p = new SasProject
        {
            Name = name, GameDir = gameDir, ModDir = modDir, Scripts = scripts.ToList(), Characters = characters?.ToList() ?? [],
        };
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

    /// <summary>Character override, e.g. <c>c5060</c>. Empty = guessed from the script id (see <see cref="CharacterLocator"/>).</summary>
    public string Character { get; set; } = "";

    [JsonIgnore] public string FileName => EntryPath[(EntryPath.LastIndexOf('\\') + 1)..];
    [JsonIgnore] public string Key => $"{Luabnd}|{EntryPath}";
    [JsonIgnore] public string Label => string.IsNullOrEmpty(DisplayName) ? FileName : $"{DisplayName} ({FileName})";
}

/// <summary>A character opened for animation editing (see <see cref="SasProject.Characters"/>).</summary>
public sealed class ProjectCharacter
{
    /// <summary>Character id, e.g. <c>c5060</c>.</summary>
    public string ChrId { get; set; } = "";

    /// <summary>Optional name, e.g. "Owl".</summary>
    public string DisplayName { get; set; } = "";

    [JsonIgnore] public string Label => string.IsNullOrEmpty(DisplayName) ? ChrId : $"{ChrId}  {DisplayName}";
}

/// <summary>Panel arrangement: <see cref="Ai"/> shows the Lua editor, <see cref="Animation"/> hides it for TAE work.</summary>
public enum WorkspaceLayout { Ai, Animation }

public sealed class ProjectUiState
{
    public string? OpenScriptKey { get; set; }
    /// <summary>Character last opened from <see cref="SasProject.Characters"/>.</summary>
    public string? OpenCharacter { get; set; }
    /// <summary>Null = default for the project kind (animations-only → Animation).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkspaceLayout? Layout { get; set; }
    public Dictionary<string, int> CursorLineByScript { get; set; } = [];
}
