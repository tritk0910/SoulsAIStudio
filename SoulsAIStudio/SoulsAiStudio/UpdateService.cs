#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Update;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// In-app update from GitHub Releases. On launch (setting, default on) and from Souls AI > Check for Updates, asks
    /// GitHub for a newer release; "Download and install" fetches the zip, checks its SHA-256 and unpacks it into
    /// %TEMP%. "Restart and update" closes the app the normal way (unsaved prompts included); as the process ends,
    /// the NEW version is started from %TEMP% with <c>--sas-apply-update</c>: it waits for the old process to exit,
    /// copies itself over the install folder (old files backed up, rolled back on error) and starts the updated app.
    /// Only a generic User-Agent is sent to GitHub.
    /// </summary>
    public sealed class UpdateService
    {
        public const string WindowName = "Update##sas_update";
        private const string ApplyArg = "--sas-apply-update";

        private enum State { Idle, Checking, Available, Downloading, Ready, Error }

        private readonly SasApp _app;
        private State _state;
        private bool _manual;
        private bool _open;
        private Task<IReadOnlyList<ReleaseInfo>>? _check;
        private Task<string>? _download;
        private CancellationTokenSource? _cts;
        private ReleaseInfo? _release;
        private double _progress;
        private string? _error;
        private string? _staged;
        private bool _startupDone;
        private static readonly HttpClientHolder Http = new();

        /// <summary>Set by "Restart and update"; started from Program's finally (see <see cref="LaunchPendingUpdate"/>).</summary>
        private static ProcessStartInfo? _pending;

        public UpdateService(SasApp app) => _app = app;

        public static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static string StagingRoot => Path.Combine(Path.GetTempPath(), "SoulsAiStudio-update");
        private static Version CurrentVersion => UpdateCheck.ParseVersion(SasHooks.AppVersion) ?? new Version(0, 0);

        // ---------------------------------------------------------------- startup / check

        /// <summary>First frame: report a just-applied update, clean up, then check if enabled.</summary>
        public void OnFirstFrame()
        {
            if (_startupDone) return;
            _startupDone = true;
            try
            {
                if (UpdateInstaller.TakeAppliedMarker(InstallDir) is { } done)
                {
                    _app.Notify($"Souls AI Studio updated ({done}).");
                    try { Directory.Delete(StagingRoot, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) { _app.ReportError("Update cleanup", ex); }
            if (_app.Settings.CheckForUpdatesOnStartup) StartCheck(manual: false);
        }

        public void StartCheck(bool manual)
        {
            if (_state is State.Checking or State.Downloading) { if (manual) _open = true; return; }
            _manual = manual;
            _state = State.Checking;
            _error = null;
            if (manual) _open = true;
            _check = Task.Run(() => UpdateCheck.FetchAsync(Http.Client));
        }

        private void PollTasks()
        {
            if (_check is { IsCompleted: true } c)
            {
                _check = null;
                if (c.IsFaulted || c.IsCanceled)
                {
                    _state = State.Error;
                    _error = "Could not reach GitHub: " + (c.Exception?.GetBaseException().Message ?? "cancelled");
                    if (!_manual) { _state = State.Idle; } // silent on launch
                }
                else
                {
                    var pick = UpdateCheck.PickUpdate(c.Result, CurrentVersion, _app.Settings.IncludePrereleaseUpdates,
                        _manual ? null : _app.Settings.SkippedUpdateTag);
                    if (pick == null)
                    {
                        _state = State.Idle;
                        if (_manual) { _open = false; _app.Notify($"Souls AI Studio {SasHooks.AppVersion} is the latest version."); }
                    }
                    else { _release = pick; _state = State.Available; _open = true; }
                }
            }
            if (_download is { IsCompleted: true } d)
            {
                _download = null;
                if (d.IsCanceled || _cts?.IsCancellationRequested == true) { _state = State.Available; }
                else if (d.IsFaulted) { _state = State.Error; _error = "Download failed: " + d.Exception!.GetBaseException().Message; }
                else { _staged = d.Result; _state = State.Ready; }
            }
        }

        private void StartDownload()
        {
            if (_release == null) return;
            _cts = new CancellationTokenSource();
            _progress = 0;
            _state = State.Downloading;
            var r = _release;
            var progress = new Progress<double>(p => _progress = p);
            string work = Path.Combine(StagingRoot, Sanitize(r.Tag));
            _download = Task.Run(() => UpdateInstaller.DownloadAndStageAsync(Http.Client, r, work, progress, _cts.Token));
        }

        private static string Sanitize(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        /// <summary>Closes the app the normal way; the updater starts when the process ends.</summary>
        private void RestartAndUpdate()
        {
            if (_staged == null || _release == null) return;
            var psi = new ProcessStartInfo(Path.Combine(_staged, UpdateInstaller.ExeName))
            {
                WorkingDirectory = _staged,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(ApplyArg);
            psi.ArgumentList.Add(InstallDir);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(SasHooks.AppVersion);
            psi.ArgumentList.Add(_release.Version.ToString(3));
            if (_app.Project?.FilePath is { Length: > 0 } proj) { psi.ArgumentList.Add("--sas-project"); psi.ArgumentList.Add(proj); }
            _pending = psi;
            DSAnimStudio.Main.REQUEST_EXIT_NEXT_IS_AUTOMATIC = false;
            DSAnimStudio.Main.REQUEST_EXIT = true;
        }

        /// <summary>Program's finally: start the updater if "Restart and update" was chosen.</summary>
        public static void LaunchPendingUpdate()
        {
            var psi = Interlocked.Exchange(ref _pending, null);
            if (psi == null) return;
            try { Process.Start(psi); }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Could not start the updater:\n\n" + ex.Message + "\n\nDownload the new version from " + UpdateCheck.ReleasesPage,
                    "Souls AI Studio update", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- window

        public void Build()
        {
            OnFirstFrame();
            PollTasks();
            if (!_open) return;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(620, 440), ImGuiCond.FirstUseEver);
            bool visible = Theme.BeginOpaque(WindowName, ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
            try
            {
                if (!visible) return;
                switch (_state)
                {
                    case State.Checking:
                        ImGui.TextUnformatted("Checking GitHub for a new version...");
                        break;
                    case State.Idle:
                        ImGui.TextUnformatted($"Souls AI Studio {SasHooks.AppVersion} is the latest version.");
                        break;
                    case State.Error:
                        ImGui.TextColored(Theme.Error, (_error ?? "Update failed.").Replace("%", "%%"));
                        if (ImGui.Button("Try again")) { if (_release != null && _staged == null && _error?.StartsWith("Download") == true) StartDownload(); else StartCheck(manual: true); }
                        ImGui.SameLine();
                        if (ImGui.Button("Open release page")) OpenUrl(_release?.HtmlUrl ?? UpdateCheck.ReleasesPage);
                        break;
                    default:
                        DrawRelease();
                        break;
                }
            }
            finally { ImGui.End(); }
        }

        private void DrawRelease()
        {
            var r = _release!;
            ImGui.TextColored(Theme.Accent, $"{r.Name}{(r.Prerelease ? " (pre-release)" : "")}");
            ImGui.TextDisabled($"You have {SasHooks.AppVersion}.");
            ImGui.Separator();
            float footer = ImGui.GetFrameHeightWithSpacing() * 3 + 8;
            ImGui.BeginChild("##sas_update_notes", new Vector2(0, -footer), true);
            try
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(r.Notes) ? "(no release notes)" : r.Notes.Replace("\r", ""));
                ImGui.PopTextWrapPos();
            }
            finally { ImGui.EndChild(); }

            bool canWrite = _canWrite ??= UpdateInstaller.CanWriteTo(InstallDirCached); // once: it creates a probe file
            switch (_state)
            {
                case State.Available when !r.Installable:
                    ImGui.TextColored(Theme.Warn, "This release has no download the app can check, install it from the release page.");
                    break;
                case State.Available when !canWrite:
                    ImGui.TextColored(Theme.Warn, $"The app folder is not writable ({InstallDirCached}).\nDownload the zip from the release page and extract it over the old one.");
                    break;
                case State.Available:
                    ImGui.TextDisabled("Your projects, backups and settings are kept. The layout (imgui.ini) is kept too.");
                    if (ImGui.Button($"Download and install ({UpdateText.Size(r.AssetSize)})")) StartDownload();
                    ImGui.SameLine();
                    if (ImGui.Button("Skip this version")) { _app.Settings.SkippedUpdateTag = r.Tag; _app.Settings.Save(); _open = false; _state = State.Idle; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Don't remind me about this version on launch\n(Souls AI > Check for Updates still shows it)");
                    ImGui.SameLine();
                    if (ImGui.Button("Later")) _open = false;
                    break;
                case State.Downloading:
                    ImGui.ProgressBar((float)_progress, new Vector2(-90, 0), $"{_progress * 100:0}%  of {UpdateText.Size(r.AssetSize)}");
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel")) _cts?.Cancel();
                    ImGui.TextDisabled("Downloading, then checking the SHA-256...");
                    break;
                case State.Ready:
                    ImGui.TextColored(Theme.Ok, "Downloaded and checked. The app will close, update itself and open again.");
                    if (ImGui.Button("Restart and update")) RestartAndUpdate();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Unsaved Lua / TAE changes are asked about first, as on a normal exit.");
                    ImGui.SameLine();
                    if (ImGui.Button("Later")) _open = false;
                    break;
            }
            ImGui.SameLine();
            if (ImGui.Button("Release page")) OpenUrl(r.HtmlUrl);
        }

        private bool? _canWrite;
        private string? _installDirCached;
        private string InstallDirCached => _installDirCached ??= InstallDir;

        // ---------------------------------------------------------------- apply mode (the new version, from %TEMP%)

        /// <summary>
        /// <c>"Souls AI Studio.exe" --sas-apply-update &lt;installDir&gt; &lt;pid&gt; &lt;oldVersion&gt; &lt;newVersion&gt; [--sas-project &lt;file&gt;]</c>:
        /// wait for the old app to exit, copy this (new) version over it, start it. Keep this argument format stable:
        /// the OLD version builds the command line and the NEW one parses it.
        /// </summary>
        public static bool RunApplyModeIfRequested(string[] args)
        {
            if (args.Length < 5 || args[0] != ApplyArg) return false;
            string install = args[1], oldVer = args[3], newVer = args[4];
            int.TryParse(args[2], out int pid);
            string? project = args.Length >= 7 && args[5] == "--sas-project" ? args[6] : null;
            string staged = InstallDir;
            string log = Path.Combine(Path.GetDirectoryName(staged) ?? staged, "update.log");
            void Log(string s) { try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {s}\n"); } catch (Exception) { } }

            System.Windows.Forms.Application.EnableVisualStyles();
            using var form = new System.Windows.Forms.Form
            {
                Text = "Souls AI Studio",
                Width = 420, Height = 120,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                MaximizeBox = false, MinimizeBox = false, ControlBox = false, TopMost = true,
            };
            form.Controls.Add(new System.Windows.Forms.Label
            {
                Text = $"Updating Souls AI Studio {oldVer} → {newVer}...",
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            });

            string? error = null;
            form.Shown += async (_, _) =>
            {
                await Task.Run(() =>
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(install).TrimEnd('\\'), Path.GetFullPath(staged).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The updater must run from its download folder, not from the app folder.");
                        Log($"apply {oldVer} -> {newVer} into {install}");
                        if (pid > 0)
                        {
                            try
                            {
                                using var old = Process.GetProcessById(pid);
                                if (!old.WaitForExit(60_000)) throw new TimeoutException("The old Souls AI Studio did not close within 60 s.");
                            }
                            catch (ArgumentException) { } // already gone
                        }
                        UpdateInstaller.Apply(staged, install, oldVer, newVer, Log);
                    }
                    catch (Exception ex) { error = ex.Message; Log("FAILED: " + ex); }
                });
                form.Close();
            };
            System.Windows.Forms.Application.Run(form);

            if (error != null)
                System.Windows.Forms.MessageBox.Show(
                    "The update could not be installed; the previous version was kept.\n\n" + error +
                    "\n\nYou can download the new version from " + UpdateCheck.ReleasesPage,
                    "Souls AI Studio update", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);

            try
            {
                var psi = new ProcessStartInfo(Path.Combine(install, UpdateInstaller.ExeName)) { WorkingDirectory = install, UseShellExecute = false };
                if (project != null) { psi.ArgumentList.Add("--sas-project"); psi.ArgumentList.Add(project); }
                Process.Start(psi);
            }
            catch (Exception ex) { Log("restart failed: " + ex.Message); }
            return true;
        }

        private sealed class HttpClientHolder
        {
            private System.Net.Http.HttpClient? _client;
            public System.Net.Http.HttpClient Client => _client ??= UpdateCheck.CreateHttpClient();
        }
    }
}
