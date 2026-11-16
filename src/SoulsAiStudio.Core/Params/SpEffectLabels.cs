using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Params;

/// <summary>
/// User-defined SpEffect names (id → label), e.g. 200210 = "Strong parry (right)": player-side SpEffects that enemy
/// <c>Goal.Interrupt</c> handlers react to have no useful SpEffectParam row name. Stored per user as JSON
/// (<c>spEffectLabels.json</c>), written atomically on every change. A missing file is created with
/// <see cref="Defaults"/>; a corrupt one loads empty and is kept as <c>.corrupt</c> before the next save overwrites it.
/// Not thread-safe (UI thread).
/// </summary>
public sealed class SpEffectLabels
{
    public const int MaxLabelLength = 120;

    /// <summary>Seeded only when the file is first created: a default the user deleted stays deleted.</summary>
    public static IReadOnlyDictionary<int, string> Defaults { get; } = new SortedDictionary<int, string>
    {
        [200210] = "Strong parry (right)",
        [200211] = "Strong parry (left)",
        [200215] = "Weak parry (left)",
        [200216] = "Weak parry (right)",
    };

    private readonly SortedDictionary<int, string> _labels = new();
    private bool _corrupt;

    public string FilePath { get; }

    /// <summary>Increases on every change (cheap cache key for UI lists).</summary>
    public int Version { get; private set; }

    /// <summary>Last load/save problem, or null.</summary>
    public string? LastError { get; private set; }

    public IReadOnlyDictionary<int, string> All => _labels;
    public int Count => _labels.Count;

    private SpEffectLabels(string path) => FilePath = path;

    /// <summary>Loads <paramref name="path"/>; creates it with <see cref="Defaults"/> if it does not exist. Never throws.</summary>
    public static SpEffectLabels Load(string path)
    {
        var s = new SpEffectLabels(Path.GetFullPath(path));
        if (!File.Exists(s.FilePath))
        {
            foreach (var (id, label) in Defaults) s._labels[id] = label;
            s.TrySave();
            return s;
        }
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(s.FilePath));
            var labels = root?["labels"] as JsonObject ?? throw new JsonException("missing \"labels\" object");
            foreach (var (key, value) in labels)
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
                if (value is JsonValue v && v.TryGetValue(out string? text) && Clean(text) is { Length: > 0 } clean)
                    s._labels[id] = clean;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            s._labels.Clear();
            s._corrupt = ex is JsonException or InvalidOperationException;
            s.LastError = $"Could not read {Path.GetFileName(s.FilePath)}: {ex.Message}";
        }
        return s;
    }

    public string? Get(int id) => _labels.TryGetValue(id, out var l) ? l : null;

    /// <summary>Sets (or with an empty/blank label removes) a label and saves. False if nothing changed.</summary>
    public bool Set(int id, string? label)
    {
        string clean = Clean(label);
        if (clean.Length == 0) return Remove(id);
        if (_labels.TryGetValue(id, out var old) && old == clean) return false;
        _labels[id] = clean;
        Changed();
        return true;
    }

    public bool Remove(int id)
    {
        if (!_labels.Remove(id)) return false;
        Changed();
        return true;
    }

    /// <summary>
    /// Single line, trimmed, at most <see cref="MaxLabelLength"/> chars. '%' becomes "pct": ImGui.NET 1.87 passes text
    /// to printf-style functions (Text, SetTooltip…), where a stray '%' would garble the line.
    /// </summary>
    public static string Clean(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        string s = label.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Replace("%", "pct").Trim();
        return s.Length > MaxLabelLength ? s[..MaxLabelLength].TrimEnd() : s;
    }

    private void Changed()
    {
        Version++;
        TrySave();
    }

    private bool TrySave()
    {
        try
        {
            // Keep a corrupt file for the user instead of silently overwriting it.
            if (_corrupt && File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            _corrupt = false;
            var labels = new JsonObject();
            foreach (var (id, label) in _labels) labels[id.ToString(CultureInfo.InvariantCulture)] = label;
            var root = new JsonObject { ["version"] = 1, ["labels"] = labels };
            AtomicFile.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not save {Path.GetFileName(FilePath)}: {ex.Message}";
            return false;
        }
    }
}
