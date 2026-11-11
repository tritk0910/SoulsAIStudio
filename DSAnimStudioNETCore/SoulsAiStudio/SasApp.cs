#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Formats;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Root of the Souls AI Studio UI inside DSAS. Owns settings, the open project and all panels
    /// (one object, constructor-injected parts — no new statics besides the <see cref="SasHooks"/> seam).
    /// </summary>
    public sealed class SasApp
    {
        public SasSettings Settings { get; } = SasSettings.Load();
        public RecentProjects Recent { get; } = new(Path.Combine(SasSettings.UserDataDir, "recent.json"));
        public ProjectSession? Session { get; private set; }
        public SasProject? Project => Session?.Project;

        private readonly StartScreen _start;
        private readonly NewProjectWizard _wizard;
        private readonly ScriptsPanel _scripts;
        private readonly LuaEditorPanel _editor;
        private readonly List<string> _errors = new();
        private Task<SasProject>? _openingProject;

        public SasApp()
        {
            _start = new StartScreen(this) { IsOpen = Settings.ShowStartScreenOnLaunch };
            _wizard = new NewProjectWizard(this);
            _editor = new LuaEditorPanel(this);
            _scripts = new ScriptsPanel(this, _editor);

            // "--sas-project <file.sasproj>" (or a bare .sasproj path, e.g. from a file association) opens it on launch.
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.FindIndex(args, a => a.Equals("--sas-project", StringComparison.OrdinalIgnoreCase));
            string? startup = i >= 0 && i + 1 < args.Length ? args[i + 1]
                : args.Skip(1).FirstOrDefault(a => a.EndsWith(SasProject.Extension, StringComparison.OrdinalIgnoreCase));
            if (startup != null && File.Exists(startup)) OpenProject(startup);
        }

        // ---------- project lifecycle ----------

        public void ShowStartScreen() => _start.IsOpen = true;
        public void ShowNewProjectWizard() { _start.IsOpen = false; _wizard.Open(); }

        public void BrowseAndOpenProject()
        {
            DSAnimStudio.Main.MainThreadLazyDispatch(() =>
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Open Souls AI Studio project",
                    Filter = "Souls AI Studio project (*.sasproj)|*.sasproj",
                    InitialDirectory = Directory.Exists(Settings.LastProjectFolder) ? Settings.LastProjectFolder : "",
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) OpenProject(dlg.FileName);
            });
        }

        public void OpenProject(string path)
        {
            if (!ConfirmDiscardUnsaved()) return;
            _openingProject = Task.Run(() => SasProject.Load(path));
        }

        /// <summary>Activates a loaded/created project (UI thread).</summary>
        public void ActivateProject(SasProject project)
        {
            OodleLoader.TryLoad(project.GameDir); // DCX_KRAK luabnd/gameparam
            Session = new ProjectSession(project, Settings.DecompilerPath);
            Recent.Add(project.FilePath);
            Settings.LastProjectFolder = project.ProjectDir;
            Settings.LastGameDir = project.GameDir;
            Settings.LastModDir = project.ModDir;
            Settings.Save();
            _start.IsOpen = false;
            _editor.CloseAll();
            _scripts.IsOpen = _editor.IsOpen = true;

            var reopen = project.Scripts.FirstOrDefault(s => s.Key == project.Ui.OpenScriptKey) ?? project.Scripts.FirstOrDefault();
            if (reopen != null) _editor.Open(reopen);
            Notify($"Opened project '{project.Name}'.");
        }

        public void CloseProject()
        {
            if (!ConfirmDiscardUnsaved()) return;
            SaveProjectUiState();
            _editor.CloseAll();
            Session = null;
            _start.IsOpen = true;
        }

        public void SaveProjectUiState()
        {
            if (Project == null) return;
            Project.Ui.OpenScriptKey = _editor.ActiveScript?.Key;
            try { Project.Save(); } catch (Exception ex) { ReportError("Could not save project file", ex); }
        }

        private bool ConfirmDiscardUnsaved()
        {
            if (!_editor.AnyDirty) return true;
            // Keep it simple and safe: refuse, ask the user to save first.
            DSAnimStudio.ImguiOSD.DialogManager.DialogOK("Unsaved changes",
                "Some scripts have unsaved changes. Save them (Ctrl+S) or revert before switching project.");
            return false;
        }

        // ---------- frame ----------

        public void BuildWindows(ref bool anyFieldFocused)
        {
            if (_openingProject is { IsCompleted: true } t)
            {
                _openingProject = null;
                if (t.IsCompletedSuccessfully) ActivateProject(t.Result);
                else ReportError("Could not open project", t.Exception?.GetBaseException());
            }

            bool focused = false;
            focused |= _start.Build();
            focused |= _wizard.Build();
            if (Session != null)
            {
                focused |= _scripts.Build();
                focused |= _editor.Build();
            }
            BuildErrorPopup();
            // While any of our windows has focus, DSAS's TAE shortcuts must not fire (CLAUDE.md §6 key routing).
            if (focused) anyFieldFocused = true;
        }

        public void BuildMenus(ref bool anyItemFocused, ref bool isAnyMenuExpanded)
        {
            // The menu bar is built before DSAS shows its "Welcome to DS Anim Studio" dialog in the same frame;
            // our start screen replaces it.
            DSAnimStudio.ImguiOSD.OSD.RequestShowWelcome = false;
            if (!ImGui.BeginMenu("Souls AI")) return;
            isAnyMenuExpanded = true;
            if (ImGui.MenuItem("Start Screen")) ShowStartScreen();
            if (ImGui.MenuItem("New Project...")) ShowNewProjectWizard();
            if (ImGui.MenuItem("Open Project...")) BrowseAndOpenProject();
            if (ImGui.BeginMenu("Recent Projects", Recent.Paths.Count > 0))
            {
                foreach (string p in Recent.Paths)
                    if (ImGui.MenuItem(Path.GetFileNameWithoutExtension(p), "", false, File.Exists(p))) OpenProject(p);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Save Script", "Ctrl+S", false, _editor.ActiveIsDirty)) _editor.SaveActive();
            if (ImGui.MenuItem("Close Project", "", false, Session != null)) CloseProject();
            ImGui.Separator();
            if (ImGui.MenuItem("Scripts Panel", "", _scripts.IsOpen, Session != null)) _scripts.IsOpen = !_scripts.IsOpen;
            if (ImGui.MenuItem("Lua Editor", "", _editor.IsOpen, Session != null)) _editor.IsOpen = !_editor.IsOpen;
            ImGui.EndMenu();
        }

        // ---------- safety for DSAS writes ----------

        /// <summary>Non-null reason when DSAS must not write <paramref name="path"/> (inside the game folder, outside mods).</summary>
        public string? WhyWriteIsBlocked(string path)
        {
            string full = Path.GetFullPath(path);
            foreach (string? game in new[] { Project?.GameDir, Settings.LastGameDir, SasSettings.GuessGameDir() })
            {
                if (string.IsNullOrEmpty(game)) continue;
                string g = Path.GetFullPath(game).TrimEnd('\\') + "\\";
                string mods = g + "mods\\";
                if (full.StartsWith(g, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(mods, StringComparison.OrdinalIgnoreCase))
                    return $"Refusing to overwrite a file in the game folder:\n{full}\n\n" +
                           "Open the file from your mod folder (mods\\<your mod>) instead, so the original game files stay untouched.";
            }
            return null;
        }

        public void BackupBeforeExternalWrite(string path)
        {
            if (!File.Exists(path)) return;
            if (Session != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Project!.ModDir), StringComparison.OrdinalIgnoreCase))
                Session.Backups.BackupBeforeSave(new[] { path });
            else
            {
                string bak = path + ".sasbak";
                if (!File.Exists(bak)) File.Copy(path, bak);
            }
        }

        // ---------- notifications / errors ----------

        public void Notify(string text) => DSAnimStudio.zzz_NotificationManagerIns.PushNotification(text);

        public void ReportError(string what, Exception? ex)
        {
            string msg = ex == null ? what : $"{what}: {ex.Message}";
            lock (_errors) { if (_errors.Count < 20) _errors.Add(msg); }
            System.Diagnostics.Debug.WriteLine(ex);
        }

        private void BuildErrorPopup()
        {
            lock (_errors)
            {
                if (_errors.Count == 0) return;
                ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);
                bool open = true;
                if (ImGui.Begin("Souls AI Studio – Problem##sas_error", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
                {
                    foreach (string e in _errors) ImGui.TextWrapped("• " + e);
                    if (ImGui.Button("OK")) open = false;
                }
                ImGui.End();
                if (!open) _errors.Clear();
            }
        }
    }
}
