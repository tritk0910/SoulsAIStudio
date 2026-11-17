namespace SoulsAiStudio.Core.Watch;

/// <summary>What a watched path belongs to (decides how the app reacts to a change).</summary>
public enum WatchKind
{
    /// <summary>A luabnd: the mod's packed .dcx, its unpacked WitchyBND/Yabber folder, or the game's .dcx. Tag = luabnd name.</summary>
    Luabnd,
    /// <summary>The loaded character's anibnd. Tag = chr id.</summary>
    Anibnd,
    /// <summary>The loaded character's chrbnd. Tag = chr id.</summary>
    Chrbnd,
    /// <summary><c>param\gameparam\gameparam.parambnd.dcx</c>.</summary>
    GameParam,
}

/// <summary>
/// One watched file (or folder, recursively). Several specs may share a <see cref="Tag"/> (e.g. a luabnd's packed
/// file and its unpacked folder).
/// </summary>
public sealed record WatchSpec(WatchKind Kind, string Tag, string Path, bool IsDirectory = false)
{
    public string FullPath { get; } = System.IO.Path.GetFullPath(Path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
    public string Key => $"{Kind}|{FullPath}";
}

/// <summary>An external change: files of <see cref="Spec"/> that were created, changed or deleted.</summary>
public sealed record WatchChange(WatchSpec Spec, IReadOnlyList<string> Files);
