using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SoulsAiStudio.Core.Update;

/// <summary>A GitHub release of the app with its Windows zip.</summary>
/// <param name="Sha256">Lower-case hex SHA-256 of the zip as GitHub reports it (asset "digest"); null when GitHub gave none.</param>
public sealed record ReleaseInfo(
    string Tag, Version Version, string Name, string Notes, bool Prerelease, string HtmlUrl,
    string? AssetName, string? AssetUrl, long AssetSize, string? Sha256)
{
    /// <summary>Has a zip with a checksum: can be installed by the app (else only "open the release page").</summary>
    public bool Installable => AssetUrl != null && Sha256 != null;
}

/// <summary>
/// Finds a newer release on GitHub (public API, no token, no user data sent: only a generic User-Agent).
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "tritk0910/SoulsAIStudio";
    public const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    /// <summary>"v0.2.3", "0.2.3", "v.0.1.0" → 0.2.3 / 0.1.0; null when not a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim().TrimStart('v', 'V').TrimStart('.');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        // 0.2 == 0.2.0: normalise missing parts to 0 so comparisons are exact.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft")) continue;
            string tag = Str(r, "tag_name") ?? "";
            if (ParseVersion(tag) is not { } version) continue;
            string? assetName = null, assetUrl = null, sha = null;
            long size = 0;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = Str(a, "name") ?? "";
                    if (!name.StartsWith("SoulsAIStudio-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                    assetName = name;
                    assetUrl = Str(a, "browser_download_url");
                    size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n) ? n : 0;
                    string? digest = Str(a, "digest");
                    sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64
                        ? digest[7..].ToLowerInvariant() : null;
                    break;
                }
            list.Add(new ReleaseInfo(tag, version, Str(r, "name") ?? tag, Str(r, "body") ?? "", Bool(r, "prerelease"),
                Str(r, "html_url") ?? ReleasesPage, assetName, assetUrl, size, sha));
        }
        return list;
    }

    /// <summary>The newest release above <paramref name="current"/> (pre-releases only when allowed; the skipped tag is ignored).</summary>
    public static ReleaseInfo? PickUpdate(IEnumerable<ReleaseInfo> releases, Version current, bool includePrereleases, string? skippedTag = null)
    {
        var cur = ParseVersion(current.ToString()) ?? current;
        return releases
            .Where(r => includePrereleases || !r.Prerelease)
            .Where(r => r.Version > cur)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault(r => !string.Equals(r.Tag, skippedTag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A client for the GitHub API and downloads. The User-Agent is generic (GitHub requires one).</summary>
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulsAIStudio-Updater");
        return http;
    }

    /// <summary>Dev/test only: a local JSON file in GitHub's format used instead of the GitHub API.</summary>
    public const string FeedOverrideVariable = "SOULSAISTUDIO_UPDATE_FEED";

    public static async Task<IReadOnlyList<ReleaseInfo>> FetchAsync(HttpClient http, CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable(FeedOverrideVariable) is { Length: > 0 } feed)
            return ParseReleases(await File.ReadAllTextAsync(feed, ct).ConfigureAwait(false));

        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ParseReleases(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Downloads a release zip, checks its SHA-256, unpacks it, and later (from the unpacked copy, after the app exited)
/// copies it over the install folder. Files of the old version that get replaced are moved to
/// <see cref="BackupDirName"/> first and moved back if anything fails. Files that are not in the zip (imgui.ini,
/// DSAS config, …) are never touched.
/// </summary>
public static class UpdateInstaller
{
    public const string ExeName = "Souls AI Studio.exe";
    public const string BackupDirName = "_update_backup";
    /// <summary>Written into the backup folder after a successful update: "old version → new version".</summary>
    public const string AppliedMarker = "applied.txt";

    /// <summary>Downloads the zip into <paramref name="workDir"/>, checks the SHA-256, unpacks it; returns the folder with the exe.</summary>
    public static async Task<string> DownloadAndStageAsync(HttpClient http, ReleaseInfo r, string workDir,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!r.Installable) throw new InvalidOperationException("This release has no zip with a checksum.");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);
        string zip = Path.Combine(workDir, r.AssetName ?? "update.zip");

        if (Uri.TryCreate(r.AssetUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            // Local feed (SOULSAISTUDIO_UPDATE_FEED, tests): copy instead of downloading.
            File.Copy(uri.LocalPath, zip, overwrite: true);
            progress?.Report(1);
        }
        else
        using (var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? r.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        string actual = Sha256Of(zip);
        if (!string.Equals(actual, r.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The download is damaged (SHA-256 {actual[..12]}… does not match {r.Sha256![..12]}…).");

        string unpack = Path.Combine(workDir, "app");
        ZipFile.ExtractToDirectory(zip, unpack); // rejects entries that would land outside the folder
        File.Delete(zip);
        return FindAppRoot(unpack) ?? throw new InvalidDataException($"The zip has no {ExeName}.");
    }

    public static string Sha256Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The folder holding the exe: <paramref name="dir"/> itself or its single top-level folder.</summary>
    public static string? FindAppRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, ExeName))) return dir;
        var subs = Directory.GetDirectories(dir);
        return subs.Length == 1 && File.Exists(Path.Combine(subs[0], ExeName)) ? subs[0] : null;
    }

    /// <summary>True when files can be created in <paramref name="dir"/> (not e.g. Program Files without admin).</summary>
    public static bool CanWriteTo(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".sas_write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Copies the staged app over <paramref name="installDir"/>. Every replaced file is moved to the backup folder first;
    /// on any error the new files are removed and the old ones moved back, then the error is rethrown.
    /// </summary>
    public static void Apply(string stagedDir, string installDir, string oldVersion, string newVersion, Action<string>? log = null)
    {
        stagedDir = Path.GetFullPath(stagedDir);
        installDir = Path.GetFullPath(installDir);
        string backup = Path.Combine(installDir, BackupDirName);
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        Directory.CreateDirectory(backup);

        var moved = new List<(string Dest, string Bak)>();
        var created = new List<string>();
        try
        {
            foreach (string src in Directory.EnumerateFiles(stagedDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(stagedDir, src);
                if (rel.StartsWith(BackupDirName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    string bak = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    Retry(() => File.Move(dest, bak));
                    moved.Add((dest, bak));
                }
                else created.Add(dest);
                Retry(() => File.Copy(src, dest, overwrite: true));
            }
            File.WriteAllText(Path.Combine(backup, AppliedMarker), $"{oldVersion} -> {newVersion}");
            log?.Invoke($"Updated {moved.Count} files, added {created.Count}.");
        }
        catch (Exception ex)
        {
            log?.Invoke("Update failed, restoring the previous version: " + ex.Message);
            foreach (string c in created) TryDelete(c);
            foreach (var (dest, bak) in moved)
            {
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(bak, dest);
                }
                catch (Exception rex) { log?.Invoke($"Could not restore {dest}: {rex.Message}"); }
            }
            throw;
        }
    }

    /// <summary>
    /// Next start after an update: returns "old -> new" when an update was just applied, and removes the backup
    /// folder (the new version runs, so it is no longer needed).
    /// </summary>
    public static string? TakeAppliedMarker(string installDir)
    {
        string backup = Path.Combine(installDir, BackupDirName);
        if (!Directory.Exists(backup)) return null;
        string? text = null;
        try { text = File.ReadAllText(Path.Combine(backup, AppliedMarker)).Trim(); } catch (IOException) { }
        try { Directory.Delete(backup, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return text;
    }

    private static void Retry(Action a)
    {
        // Antivirus / the exiting app can hold a file for a moment.
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < 20) { Thread.Sleep(250); }
        }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Formats sizes for the UI.</summary>
public static class UpdateText
{
    public static string Size(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
