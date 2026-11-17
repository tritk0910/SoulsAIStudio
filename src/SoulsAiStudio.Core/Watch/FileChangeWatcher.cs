using System.Collections.Concurrent;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Watch;

/// <summary>
/// Watches project files (luabnds, the loaded anibnd/chrbnd, gameparam) for changes made by other tools
/// (VSCode, WitchyBND, DSAS, Smithbox…) and queues one <see cref="WatchChange"/> per spec once a burst is over.
/// <list type="bullet">
/// <item>Events arrive on thread-pool threads; results are queued and taken with <see cref="Drain"/> (UI thread).</item>
/// <item>Debounced per spec (default 500 ms quiet), then compared against the last known (size, mtime) of every file,
/// so a burst that ends where it started, or a change already seen, reports nothing.</item>
/// <item>Our own writes (<see cref="BeginOwnWrite"/>) are held back while writing and then taken as known.</item>
/// <item>A file still locked by its writer is retried; a watcher buffer overflow or error re-scans every spec;
/// missing folders are watched from their nearest existing parent and picked up when created.</item>
/// </list>
/// </summary>
public sealed class FileChangeWatcher : IOwnWriteTracker, IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);
    /// <summary>How many times a change to a locked file is retried (each after another quiet period) before it is reported anyway.</summary>
    public const int MaxLockedRetries = 20;

    private readonly object _gate = new();
    private readonly ChangeDebouncer _debouncer;
    private readonly ConcurrentQueue<WatchChange> _queue = new();
    private readonly Dictionary<string, WatchSpec> _specs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSignature> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lockedRetries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ownWrites = new(); // full paths (files or folders) being written by us; a multiset
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _missingDirs = new(); // natural watch folders that didn't exist at build time
    private readonly Timer? _timer;
    private readonly object _pumpGate = new();
    private bool _rebuild;
    private bool _disposed;

    /// <param name="autoPump">False for tests that call <see cref="Pump"/> themselves.</param>
    public FileChangeWatcher(TimeSpan? debounce = null, bool autoPump = true)
    {
        _debouncer = new ChangeDebouncer(debounce ?? DefaultDebounce);
        if (autoPump) _timer = new Timer(_ => Pump(DateTime.UtcNow), null, 100, 100);
    }

    /// <summary>Times the OS reported a watcher error (e.g. buffer overflow); each one triggers a full re-scan.</summary>
    public int ErrorCount { get; private set; }

    public IReadOnlyCollection<WatchSpec> Specs
    {
        get { lock (_gate) return _specs.Values.ToList(); }
    }

    /// <summary>
    /// Replaces what is watched. Specs that stay keep their known state (no spurious change); new ones are snapshotted
    /// now, so only changes from this moment on are reported.
    /// </summary>
    public void SetSpecs(IEnumerable<WatchSpec> specs)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var wanted = specs.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == _specs.Count && wanted.Keys.All(_specs.ContainsKey)) return;
            foreach (string k in _specs.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _specs.Remove(k);
            foreach (var (k, s) in wanted)
            {
                if (_specs.ContainsKey(k)) continue;
                _specs[k] = s;
                foreach (var (path, sig) in Scan(s)) _known[path] = sig;
            }
            // Forget files no spec covers any more.
            foreach (string p in _known.Keys.Where(p => !_specs.Values.Any(s => Covers(s, p))).ToList()) _known.Remove(p);
            RebuildWatchersLocked();
        }
    }

    /// <summary>External changes found since the last call (UI thread).</summary>
    public IReadOnlyList<WatchChange> Drain()
    {
        var list = new List<WatchChange>();
        while (_queue.TryDequeue(out var c)) list.Add(c);
        return list;
    }

    // ---------- own writes ----------

    public IDisposable BeginOwnWrite(IEnumerable<string> paths)
    {
        var full = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).ToList();
        lock (_gate) _ownWrites.AddRange(full);
        return new OwnWriteScope(this, full);
    }

    private sealed class OwnWriteScope(FileChangeWatcher owner, List<string> paths) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.EndOwnWrite(paths);
        }
    }

    private void EndOwnWrite(List<string> paths)
    {
        lock (_gate)
        {
            foreach (string p in paths) _ownWrites.Remove(p);
            foreach (WatchSpec s in _specs.Values)
            {
                if (!paths.Any(p => Overlaps(s, p))) continue;
                var now = Scan(s);
                foreach (var (file, sig) in now)
                    if (paths.Any(p => IsAtOrUnder(file, p))) _known[file] = sig;
                foreach (string gone in _known.Keys.Where(f => Covers(s, f) && !now.ContainsKey(f) && paths.Any(p => IsAtOrUnder(f, p))).ToList())
                    _known.Remove(gone);
            }
        }
    }

    // ---------- events (thread pool) ----------

    private void OnEvent(string path)
    {
        string full;
        try { full = Normalize(path); } catch (Exception) { return; }
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return;
            if (_missingDirs.Any(d => IsAtOrUnder(d, full))) _rebuild = true; // a folder we wait for (or its parent) appeared
            foreach (WatchSpec s in _specs.Values)
                if (Covers(s, full) || (s.IsDirectory && string.Equals(full, s.FullPath, StringComparison.OrdinalIgnoreCase)))
                    _debouncer.Note(s.Key, now);
        }
    }

    private void OnError()
    {
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return;
            ErrorCount++;
            _rebuild = true; // the watcher may be dead (folder deleted) or have lost events (overflow): rebuild + re-scan all
            foreach (string k in _specs.Keys) _debouncer.Note(k, now);
        }
    }

    /// <summary>Behaves like an OS buffer overflow: rebuild the watchers and re-scan every spec (tests, or a manual "re-check").</summary>
    public void SimulateOverflow() => OnError();

    // ---------- pump ----------

    /// <summary>Evaluates specs whose burst is over and queues external changes. Runs on a timer unless autoPump is off.</summary>
    public void Pump(DateTime utcNow)
    {
        if (!Monitor.TryEnter(_pumpGate)) return; // a slow pump (big folder) must not overlap the next tick
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_rebuild)
                {
                    // Watchers are re-created (a folder appeared, or an error): events in between may be lost, so re-scan all.
                    _rebuild = false;
                    RebuildWatchersLocked();
                    foreach (string k in _specs.Keys) _debouncer.Note(k, utcNow);
                }
                foreach (string key in _debouncer.TakeDue(utcNow))
                {
                    if (!_specs.TryGetValue(key, out var spec)) continue;
                    if (_ownWrites.Any(p => Overlaps(spec, p))) { _debouncer.Note(key, utcNow); continue; } // ours, still writing
                    Evaluate(spec, utcNow);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient (folder vanished mid-scan): try again later.
        }
        finally { Monitor.Exit(_pumpGate); }
    }

    private void Evaluate(WatchSpec spec, DateTime utcNow)
    {
        var now = Scan(spec);
        var changed = now.Where(kv => !_known.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();
        var deleted = _known.Keys.Where(f => Covers(spec, f) && !now.ContainsKey(f)).ToList();
        if (changed.Count == 0 && deleted.Count == 0) { _lockedRetries.Remove(spec.Key); return; }

        // Writer still busy (file opened without sharing): wait for another quiet period.
        if (changed.Any(IsLocked))
        {
            int n = _lockedRetries.GetValueOrDefault(spec.Key) + 1;
            if (n <= MaxLockedRetries)
            {
                _lockedRetries[spec.Key] = n;
                _debouncer.Note(spec.Key, utcNow);
                return;
            }
        }
        _lockedRetries.Remove(spec.Key);
        foreach (string f in changed) _known[f] = now[f];
        foreach (string f in deleted) _known.Remove(f);
        _queue.Enqueue(new WatchChange(spec, changed.Concat(deleted).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    // ---------- FileSystemWatcher set-up ----------

    private void RebuildWatchersLocked()
    {
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
        _missingDirs.Clear();

        var dirs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // folder → recursive
        foreach (WatchSpec s in _specs.Values)
        {
            string natural = s.IsDirectory ? s.FullPath : Path.GetDirectoryName(s.FullPath) ?? s.FullPath;
            string? existing = NearestExisting(natural);
            if (existing == null) continue;
            bool recursive = s.IsDirectory || !string.Equals(existing, natural, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(existing, natural, StringComparison.OrdinalIgnoreCase)) _missingDirs.Add(natural);
            dirs[existing] = dirs.GetValueOrDefault(existing) || recursive;
        }
        // A recursive watcher already covers its sub-folders.
        var roots = dirs.Where(d => !dirs.Any(o => o.Value && !string.Equals(o.Key, d.Key, StringComparison.OrdinalIgnoreCase) && IsAtOrUnder(d.Key, o.Key)))
                        .ToList();
        foreach (var (dir, recursive) in roots)
        {
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                };
                w.Changed += (_, e) => OnEvent(e.FullPath);
                w.Created += (_, e) => OnEvent(e.FullPath);
                w.Deleted += (_, e) => OnEvent(e.FullPath);
                w.Renamed += (_, e) => { OnEvent(e.OldFullPath); OnEvent(e.FullPath); };
                w.Error += (_, _) => OnError();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _missingDirs.Add(dir); // vanished just now: retried when something appears
            }
        }
    }

    private static string? NearestExisting(string dir)
    {
        string? d = dir;
        while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
        return string.IsNullOrEmpty(d) ? null : d;
    }

    // ---------- helpers ----------

    /// <summary>Files of a spec with their current signature (a file spec: 0 or 1 file).</summary>
    private static Dictionary<string, FileSignature> Scan(WatchSpec s)
    {
        var result = new Dictionary<string, FileSignature>(StringComparer.OrdinalIgnoreCase);
        if (!s.IsDirectory)
        {
            if (FileSignature.TryRead(s.FullPath) is { } sig) result[s.FullPath] = sig;
            return result;
        }
        if (!Directory.Exists(s.FullPath)) return result;
        try
        {
            foreach (string f in Directory.EnumerateFiles(s.FullPath, "*", SearchOption.AllDirectories))
                if (!IsIgnoredName(f) && FileSignature.TryRead(f) is { } sig) result[Path.GetFullPath(f)] = sig;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>Temp files of atomic writes (ours: <c>.name.guid.tmp</c>) and backups other tools leave in unpacked folders.</summary>
    public static bool IsIgnoredName(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".dsasbak", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".sasbak", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("~", StringComparison.Ordinal);
    }

    /// <summary>The file belongs to the spec (the file itself, or a non-ignored file in the folder).</summary>
    private static bool Covers(WatchSpec s, string file) => s.IsDirectory
        ? file.Length > s.FullPath.Length && IsAtOrUnder(file, s.FullPath) && !IsIgnoredName(file)
        : string.Equals(file, s.FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>An own-write path touches the spec (same file, inside its folder, or a folder containing it).</summary>
    private static bool Overlaps(WatchSpec s, string ownPath) => IsAtOrUnder(ownPath, s.FullPath) || IsAtOrUnder(s.FullPath, ownPath);

    private static bool IsAtOrUnder(string path, string dir) =>
        path.Length >= dir.Length && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
        && (path.Length == dir.Length || path[dir.Length] == Path.DirectorySeparatorChar);

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);

    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return true; } // sharing violation: the writer still has it open
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            _watchers.Clear();
            _debouncer.Clear();
        }
        _timer?.Dispose();
    }
}

/// <summary>What we compare to decide whether a file changed: size + last write time.</summary>
public readonly record struct FileSignature(long Length, long LastWriteTicks)
{
    public static FileSignature? TryRead(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new FileSignature(fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
