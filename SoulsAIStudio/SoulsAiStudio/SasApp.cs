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
        private readonly ViewportLightingStore _lighting;
        public RecentProjects Recent { get; } = new(Path.Combine(SasSettings.UserDataDir, "recent.json"));
        public ProjectSession? Session { get; private set; }
        public SasLayout Layout { get; } = new();
        public DsasBridge Bridge { get; } = new();
        private bool _autoLoadCharacter;
        private string? _autoLoadChrId;
        private bool _spaceWasDown;
        public SasProject? Project => Session?.Project;

        private readonly StartScreen _start;
        private readonly NewProjectWizard _wizard;
        private readonly ScriptsPanel _scripts;
        private readonly LuaEditorPanel _editor;
        private readonly ComboPanel _combo;
        private readonly LuabndToolsPanel _luabnd;
        private readonly List<string> _errors = new();
        private Task<SasProject>? _openingProject;
        private SasInputProbe? _probe;

        public SasApp()
        {
            _start = new StartScreen(this) { IsOpen = Settings.ShowStartScreenOnLaunch };
            _wizard = new NewProjectWizard(this);
            _editor = new LuaEditorPanel(this);
            _scripts = new ScriptsPanel(this, _editor);
            _combo = new ComboPanel(this, _editor);
            _luabnd = new LuabndToolsPanel(this);
            SasHooks.ApplyLightingPreset(Settings.Lighting499);
            _lighting = new ViewportLightingStore(Settings);
            _lighting.Restore(); // the user's own Toolbox values win over the preset

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
            _scripts.IsOpen = true;
            ApplyLayout(CurrentLayout);

            var reopen = project.Scripts.FirstOrDefault(s => s.Key == project.Ui.OpenScriptKey) ?? project.Scripts.FirstOrDefault();
            if (reopen != null) _editor.Open(reopen);
            // Animations-only project (or a character was the last thing opened): load that character.
            _autoLoadChrId = project.Characters.FirstOrDefault(c => c.ChrId == project.Ui.OpenCharacter)?.ChrId
                             ?? (reopen == null ? project.Characters.FirstOrDefault()?.ChrId : null);
            _autoLoadCharacter = reopen != null && _autoLoadChrId == null;
            if (project.IsAnimationsOnly) _combo.SelectManualTab();
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

            HandleComboSpace();
            _lighting.Tick();
            // Skybox preset once, after DSAS has loaded its config and cubemaps (an editor document exists).
            if (Settings.SkyboxPresetVersion < 1 && DSAnimStudio.zzz_DocumentManager.CurrentDocument?.EditorScreen != null
                && SasHooks.ApplySkyboxPreset(Settings.Lighting499))
            {
                Settings.SkyboxPresetVersion = 1;
                Settings.Save();
            }
            // TAE graph like DSAS 4.9.9: dragging an unselected block selects it and drags at once (once; DSAS saves it).
            if (Settings.GraphSelectPresetVersion < 1 && DSAnimStudio.zzz_DocumentManager.CurrentDocument?.EditorScreen != null
                && DSAnimStudio.Main.Config != null)
            {
                DSAnimStudio.Main.Config.UseOldActionSelectBehavior = true;
                DSAnimStudio.Main.SaveConfig(); // DSAS otherwise saves it only on a clean exit
                Settings.GraphSelectPresetVersion = 1;
                Settings.Save();
            }
            bool focused = false;
            focused |= _start.Build();
            focused |= _wizard.Build();
            if (Session != null)
            {
                if (_autoLoadCharacter && _editor.ActiveScript is { } s)
                {
                    _autoLoadCharacter = false;
                    var files = ResolveCharacter(s);
                    if (files is { AnibndFromMod: true } && !Bridge.IsLoaded(files)) LoadCharacter(s);
                }
                if (_autoLoadChrId != null && DSAnimStudio.zzz_DocumentManager.CurrentDocument != null)
                {
                    string id = _autoLoadChrId;
                    _autoLoadChrId = null;
                    var files = ResolveCharacterId(id);
                    if (files is { AnibndFromMod: true } && !Bridge.IsLoaded(files)) LoadCharacterId(id);
                }
                Layout.ApplyIfNeeded(Settings);
                if (Layout.FocusEditorTabFrames > 0) { ImGui.SetWindowFocus(SasLayout.EditorWindow); Layout.FocusEditorTabFrames--; }
                focused |= _scripts.Build();
                focused |= _editor.Build();
                focused |= _combo.Build();
                focused |= _luabnd.Build();
            }
            SasHooks.GraphHidden = Session != null && _editor.VisibleInDockId != 0 && _editor.VisibleInDockId == Layout.GraphDockId;
            BuildErrorPopup();
            if (SasInputProbe.Enabled) (_probe ??= new SasInputProbe()).Draw(focused);
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
                ImGui.Separator();
                if (ImGui.MenuItem("Clear Recent Projects")) Recent.Clear();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Forget the list. Project files are not deleted.");
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Save Script", "Ctrl+S", false, _editor.ActiveIsDirty)) _editor.SaveActive();
            if (ImGui.MenuItem("Close Project", "", false, Session != null)) CloseProject();
            if (ImGui.MenuItem("Luabnd (Unpack / Repack)...", "", false, Session != null)) _luabnd.Open();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unpack a map's .luabnd.dcx into a WitchyBND folder, or repack a Witchy/Yabber folder into the mod's .luabnd.dcx,\n" +
                                 "without leaving the app. Saving in the Lua Editor already updates both; this is for edits made in other tools.");
            ImGui.Separator();
            if (ImGui.BeginMenu("Layout", Session != null))
            {
                if (ImGui.MenuItem("AI (Lua Editor)", "", CurrentLayout == WorkspaceLayout.Ai)) SetLayout(WorkspaceLayout.Ai);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor shown as a tab next to Graph.");
                if (ImGui.MenuItem("Animation", "", CurrentLayout == WorkspaceLayout.Animation)) SetLayout(WorkspaceLayout.Animation);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor hidden so the Graph (TAE timeline) stays visible. Default for animations-only projects.");
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Project Panel", "", _scripts.IsOpen, Session != null)) _scripts.IsOpen = !_scripts.IsOpen;
            if (ImGui.MenuItem("Lua Editor", "", _editor.IsOpen, Session != null)) _editor.IsOpen = !_editor.IsOpen;
            if (ImGui.MenuItem("Combo Player", "", _combo.IsOpen, Session != null)) _combo.IsOpen = !_combo.IsOpen;
            if (ImGui.MenuItem("Allow Windows IME in Lua Editor", "", Settings.AllowImeInEditor)) { Settings.AllowImeInEditor = !Settings.AllowImeInEditor; Settings.Save(); }
            SasHooks.ScrubFollow.MenuItem();
            if (ImGui.MenuItem("Lighting Like DSAS 4.9.9", "", Settings.Lighting499))
            {
                Settings.Lighting499 = !Settings.Lighting499;
                Settings.Save();
                SasHooks.ApplyLightingPreset(Settings.Lighting499);
                SasHooks.ApplySkyboxPreset(Settings.Lighting499);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: light from the side and above, normal ambient, no subsurface wrap: clear light/dark sides (DSAS 4.9.9),\n" +
                                 "tonemap brightness 2 / contrast 0.5, skybox on with DefaultCubemap_Old.\n" +
                                 "Off: DSAS 5.0 lighting (light from near the camera, double ambient, SSS, no skybox): brighter but flatter.\n" +
                                 "Fine-tune with Window > Toolbox > [Lighting].");
            if (ImGui.MenuItem("Blend Root Motion (DSAS 4.9.9)", "", Settings.BlendRootMotion))
            {
                Settings.BlendRootMotion = !Settings.BlendRootMotion;
                Settings.Save();
                Bridge.SetRootMotionBlending(Settings.BlendRootMotion);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: during a TAE Blend the character's movement mixes the previous and the new anim by blend weight.\n" +
                                 "Off (DSAS 5.0): movement follows only the new anim, so it can stop or jump when a blend starts.");
            if (ImGui.MenuItem("Reset Panel Layout", "", false, Session != null))
            {
                _scripts.IsOpen = _combo.IsOpen = true;
                ApplyLayout(CurrentLayout);
                Layout.RequestApply();
            }
            ImGui.EndMenu();
        }

        /// <summary>
        /// Space pauses/resumes a combo started from the Combo panel, or normal playback when DSAS ignores Space because a panel has focus (DSAS's own Space only toggles normal playback,
        /// which the combo overrides every frame). Ignored while typing in a text field / the Lua editor.
        /// </summary>
        private void HandleComboSpace()
        {
            bool down = Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Space);
            bool pressed = down && !_spaceWasDown;
            _spaceWasDown = down;
            // Only typing contexts block it: the Lua editor, or any active ImGui text field (not merely a focused panel).
            if (!pressed || _editor.HadKeyboardFocus || ImGui.IsAnyItemActive() || !DSAnimStudio.Main.Active) return;
            if (Bridge.ToggleComboPause()) Notify(Bridge.ComboPaused ? "Combo paused (Space to resume)" : "Combo resumed");
            // DSAS skips its own Space (transport play/pause) while an ImGui panel such as Combo has focus
            // (OSD.AnyFieldFocused, the value its Update used this frame) → do it for normal playback here.
            else if (DSAnimStudio.ImguiOSD.OSD.AnyFieldFocused && !Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)
                     && !Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl))
                Bridge.TogglePlayback();
        }

        // ---------- DSAS integration: Window menu, sound volume ----------

        public void BuildWindowMenuItems()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Souls AI");
            if (ImGui.MenuItem("Project", "", _scripts.IsOpen, Session != null)) _scripts.IsOpen = !_scripts.IsOpen;
            if (ImGui.MenuItem("Lua Editor", "", _editor.IsOpen, Session != null)) _editor.IsOpen = !_editor.IsOpen;
            if (ImGui.MenuItem("Combo Player", "", _combo.IsOpen, Session != null)) _combo.IsOpen = !_combo.IsOpen;
            if (ImGui.MenuItem("Luabnd (Unpack / Repack)", "", _luabnd.IsOpen, Session != null)) { if (_luabnd.IsOpen) _luabnd.IsOpen = false; else _luabnd.Open(); }
        }

        /// <summary>Opens the Luabnd window (Project panel context menu).</summary>
        public void OpenLuabndTools() => _luabnd.Open();

        /// <summary>After a repack: reload open scripts of that luabnd. Returns the ones skipped because of unsaved edits.</summary>
        public IReadOnlyList<string> ReloadScriptsOf(string luabnd) => _editor.ReloadLuabnd(luabnd);

        public void TrackSoundVolume(float volume)
        {
            if (Math.Abs(volume - Settings.SoundVolume) < 0.001f || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
            Settings.SoundVolume = Math.Clamp(volume, 0, 200);
            Settings.Save();
        }

        // ---------- characters (model + animations in DSAS) ----------

        public SoulsAiStudio.Core.Project.CharacterFiles? ResolveCharacter(ProjectScript script) =>
            Project == null ? null : new SoulsAiStudio.Core.Project.CharacterLocator(Project.GameDir, Project.ModDir).Resolve(script);

        public void SetCharacter(ProjectScript script, string chr)
        {
            script.Character = chr.Trim();
            SaveProjectUiState();
        }

        // ---------- layout (AI / Animation) ----------

        /// <summary>Saved per project; animations-only projects default to <see cref="WorkspaceLayout.Animation"/>.</summary>
        public WorkspaceLayout CurrentLayout =>
            Project?.Ui.Layout ?? (Project?.IsAnimationsOnly == true ? WorkspaceLayout.Animation : WorkspaceLayout.Ai);

        public void SetLayout(WorkspaceLayout layout)
        {
            if (Project == null) return;
            Project.Ui.Layout = layout;
            ApplyLayout(layout);
            SaveProjectUiState();
        }

        /// <summary>Animation: Lua Editor closed, so the Graph it shares a dock node with stays visible. AI: editor shown in front.</summary>
        private void ApplyLayout(WorkspaceLayout layout)
        {
            _editor.IsOpen = layout == WorkspaceLayout.Ai;
            if (layout == WorkspaceLayout.Ai) Layout.FocusEditorTabFrames = 10;
            else Layout.FocusEditorTabFrames = 0;
        }

        /// <summary>Opens an AI script in the editor (switches to the AI layout if the editor was hidden).</summary>
        public void OpenScript(ProjectScript script)
        {
            _editor.Open(script);
            if (!_editor.IsOpen) SetLayout(WorkspaceLayout.Ai);
        }

        // ---------- characters of animations-only projects ----------

        private CharacterLocator? Locator => Project == null ? null : new CharacterLocator(Project.GameDir, Project.ModDir);

        public CharacterFiles? ResolveCharacterId(string chrId) => Locator?.ResolveId(chrId);

        /// <summary>Loads a project character's model + animations into DSAS.</summary>
        public void LoadCharacterId(string chrId)
        {
            if (Project == null) return;
            var files = ResolveCharacterId(chrId);
            if (files == null) { Notify($"{chrId}.anibnd.dcx not found in the mod or the game chr folder."); return; }
            if (!files.AnibndFromMod) { NotifyGameOnly(files.ChrId); return; }
            if (Bridge.IsLoaded(files)) return; // already the current document
            try
            {
                Bridge.LoadCharacter(files, Project.GameDir, Project.ModDir, chr =>
                {
                    Notify($"Loaded {chr}.");
                    if (_editor.IsOpen) Layout.FocusEditorTabFrames = 30;
                });
                Notify($"Loading {files.ChrId}...");
                if (Project.Ui.OpenCharacter != files.ChrId) { Project.Ui.OpenCharacter = files.ChrId; SaveProjectUiState(); }
            }
            catch (Exception ex) { ReportError($"Could not load {files.ChrId}", ex); }
        }

        /// <returns>Null on success, else the reason shown under the Add field.</returns>
        public string? AddCharacter(string input)
        {
            if (Project == null) return "No project open.";
            string? id = CharacterLocator.NormalizeChrId(input);
            if (id == null) return "Use a character id like c5060.";
            if (Project.Characters.Any(c => c.ChrId == id)) return $"{id} is already in the list.";
            if (ResolveCharacterId(id) is not { } files) return $"{id}.anibnd.dcx is not in the mod or the game chr folder.";
            if (new FileInfo(files.AnibndPath).Length < CharacterLocator.StubAnibndBytes)
                return $"{id} is a stub without animations of its own (variant chrs use their base chr, e.g. c5063 → c5060).";
            Project.Characters.Add(new ProjectCharacter { ChrId = id });
            SaveProjectUiState();
            return null;
        }

        public void RemoveCharacter(string chrId)
        {
            if (Project == null) return;
            Project.Characters.RemoveAll(c => c.ChrId == chrId);
            if (Project.Ui.OpenCharacter == chrId) Project.Ui.OpenCharacter = null;
            SaveProjectUiState();
        }

        public void RenameCharacter(string chrId, string name)
        {
            var c = Project?.Characters.FirstOrDefault(x => x.ChrId == chrId);
            if (c == null) return;
            c.DisplayName = name.Trim();
            SaveProjectUiState();
        }

        /// <summary>Copies a game-only anibnd into the mod's chr folder (game file untouched) and loads the copy.</summary>
        public void CopyCharacterToMod(string chrId)
        {
            if (Locator is not { } loc) return;
            try
            {
                string path = loc.CopyAnibndToMod(chrId);
                Notify($"Copied {chrId}.anibnd.dcx into the mod: {path}");
                LoadCharacterId(chrId);
            }
            catch (Exception ex) { ReportError($"Could not copy {chrId} into the mod", ex); }
        }

        /// <summary>
        /// Game-folder anibnds are not opened: DSAS writes workspace/project files next to the file it opens, and the game
        /// folder must never be written to. The user copies it into the mod first.
        /// </summary>
        private void NotifyGameOnly(string chrId) =>
            DSAnimStudio.ImguiOSD.DialogManager.DialogOK("Copy to mod first",
                $"{chrId}.anibnd.dcx only exists in the game folder. Souls AI Studio never writes into the game folder, and\n" +
                "opening a file there makes DS Anim Studio create its workspace files next to it.\n\n" +
                $"Click \"Copy to mod\" next to {chrId} in the Project panel: it copies the anibnd into your mod's chr folder\n" +
                "(the game file stays untouched) and opens that copy.");

        public void LoadCharacter(ProjectScript script)
        {
            var files = ResolveCharacter(script);
            if (files == null || Project == null) { Notify($"No anibnd found for {script.FileName}. Set the Character (e.g. c5060)."); return; }
            if (!files.AnibndFromMod) { NotifyGameOnly(files.ChrId); return; }
            try
            {
                Bridge.LoadCharacter(files, Project.GameDir, Project.ModDir, chr =>
                {
                    Notify($"Loaded {chr}.");
                    Layout.FocusEditorTabFrames = 30; // DSAS brings Graph to front on load; keep the Lua editor visible
                });
                Notify($"Loading {files.ChrId}...");
            }
            catch (Exception ex) { ReportError($"Could not load {files.ChrId}", ex); }
        }

        public void PlayAnim(int animId)
        {
            if (!Bridge.GotoAnim(animId))
                Notify($"Anim {animId} not found — load the character first (Project panel → Load model & anims, or click a character).");
        }

        // ---------- safety for DSAS writes ----------

        /// <summary>Non-null reason when a write to <paramref name="path"/> must be refused (game folder, outside mods).</summary>
        public string? WhyWriteIsBlocked(string path) =>
            SoulsAiStudio.Core.IO.WritePolicy.WhyBlocked(path, new[] { Project?.GameDir, Settings.LastGameDir, SasSettings.GuessGameDir() });

        /// <summary>Exit prompt for unsaved Lua (UI thread). True = OK to exit.</summary>
        public bool ConfirmExitWithUnsavedLua()
        {
            if (!_editor.AnyDirty) { SaveProjectUiState(); return true; }
            var r = System.Windows.Forms.MessageBox.Show(
                "Some Lua scripts have unsaved changes:\n\n" + string.Join("\n", _editor.DirtyScriptNames) +
                "\n\nQuit without saving?", "Souls AI Studio – unsaved changes",
                System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning,
                System.Windows.Forms.MessageBoxDefaultButton.Button2);
            if (r == System.Windows.Forms.DialogResult.Yes) { SaveProjectUiState(); return true; }
            return false;
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

        /// <summary>Text of the Lua script open in the editor (null if none).</summary>
        public string? ActiveScriptText => _editor.Active?.Editor.Text;

        /// <summary>Tools → Combo / F8: the Combo window's Manual tab (replaces DSAS's Combo Viewer).</summary>
        public void OpenComboManual()
        {
            if (Session == null) { Notify("Open a Souls AI project first: the combo player lives in its Combo window."); return; }
            _combo.OpenManual();
        }

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
                if (Theme.BeginOpaque("Souls AI Studio – Problem##sas_error", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
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
