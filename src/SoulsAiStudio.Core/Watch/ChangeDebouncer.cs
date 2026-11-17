namespace SoulsAiStudio.Core.Watch;

/// <summary>
/// Coalesces bursts of notifications per key: a key becomes due once nothing was noted for it during
/// <see cref="Quiet"/> (e.g. WitchyBND writing 60 files fires once, after the last one). Thread-safe; time is passed in.
/// </summary>
public sealed class ChangeDebouncer(TimeSpan quiet)
{
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public TimeSpan Quiet { get; } = quiet;

    public void Note(string key, DateTime utcNow)
    {
        lock (_gate) _last[key] = utcNow;
    }

    public bool IsPending
    {
        get { lock (_gate) return _last.Count > 0; }
    }

    /// <summary>Keys quiet for at least <see cref="Quiet"/>; they are removed (noting them again restarts the wait).</summary>
    public IReadOnlyList<string> TakeDue(DateTime utcNow)
    {
        lock (_gate)
        {
            if (_last.Count == 0) return [];
            var due = _last.Where(kv => utcNow - kv.Value >= Quiet).Select(kv => kv.Key).ToList();
            foreach (string k in due) _last.Remove(k);
            return due;
        }
    }

    public void Clear()
    {
        lock (_gate) _last.Clear();
    }
}
