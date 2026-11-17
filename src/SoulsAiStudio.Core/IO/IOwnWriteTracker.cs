namespace SoulsAiStudio.Core.IO;

/// <summary>
/// Told about files the app itself is about to write, so a file watcher doesn't report them as external changes
/// (see <see cref="Watch.FileChangeWatcher"/>).
/// </summary>
public interface IOwnWriteTracker
{
    /// <summary>
    /// Marks <paramref name="paths"/> (files, or folders = everything under them) as being written by us. Changes there
    /// are held back until the returned scope is disposed; then the files' new state is taken as known (not external).
    /// </summary>
    IDisposable BeginOwnWrite(IEnumerable<string> paths);
}

public static class OwnWriteTrackerExtensions
{
    private sealed class Nothing : IDisposable { public static readonly Nothing Instance = new(); public void Dispose() { } }

    /// <summary>Null-safe <see cref="IOwnWriteTracker.BeginOwnWrite"/>.</summary>
    public static IDisposable Track(this IOwnWriteTracker? tracker, params string[] paths) =>
        tracker?.BeginOwnWrite(paths) ?? Nothing.Instance;
}
