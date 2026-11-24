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
    public sealed partial class SasApp
    {
        public SasSettings Settings { get; } = SasSettings.Load();
        private readonly ViewportLightingStore _lighting;
        private readonly ParametersTabFocus _paramsTab = new(); // Parameters tab in front on launch / graph selection
        public RecentProjects Recent { get; } = new(Path.Combine(SasSettings.UserDataDir, "recent.json"));
        public ProjectSession? Session { get; private set; }
        public SasLayout Layout { get; } = new();
        public DsasBridge Bridge { get; } = new();
        public Workspaces Workspaces { get; }
        public SpEffectLuaLink SpEffectLink { get; }
        private bool _autoLoadCharacter;
        /// <summary>Set when a project is opened: its character is reloaded even if it is the one already open.</summary>
        private bool _reloadIfAlreadyLoaded;
        private string? _autoLoadChrId;
        private bool _spaceWasDown;
        public SasProject? Project => Session?.Project;

        private readonly StartScreen _start;
        private readonly NewProjectWizard _wizard;
        private readonly ScriptsPanel _scripts;
        private readonly LuaEditorPanel _editor;
        private readonly ComboPanel _combo;
        private readonly LuabndToolsPanel _luabnd;
        private readonly EventFinderPanel _finder;
        private readonly SpEffectLabelsWindow _spLabels;
        private readonly UpdateService _updates;
        private readonly AnimSlotsWindow _animSlots;
        private readonly FindInScriptsPanel _findScripts;
        private readonly AddScriptsWindow _addScripts;
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
            _finder = new EventFinderPanel(this);
            _spLabels = new SpEffectLabelsWindow(this);
            _updates = new UpdateService(this);
            _animSlots = new AnimSlotsWindow(this);
            _findScripts = new FindInScriptsPanel(this);
            _addScripts = new AddScriptsWindow(this);
            Workspaces = new Workspaces(this, new Dictionary<string, (Func<bool>, Action<bool>)>
            {
                ["sas:project"] = (() => _scripts.IsOpen, v => _scripts.IsOpen = v),
                ["sas:editor"] = (() => _editor.IsOpen, v => _editor.IsOpen = v),
                ["sas:combo"] = (() => _combo.IsOpen, v => _combo.IsOpen = v),
            });
            SpEffectLink = new SpEffectLuaLink(this);
            Bridge.FormatSpEffect = SpEffectText;
            SasHooks.ApplyLightingPreset(Settings.Lighting499);
            DSAnimStudio.zzz_NotificationManagerIns.HideNotificationPopups = Settings.HideNotificationPopups;
            _lighting =new ViewportLightingStore(Settings);
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
            StartFileWatch(); // SasApp.Watch.cs
            Recent.Add(project.FilePath);
            Settings.LastProjectFolder = project.ProjectDir;
            Settings.LastGameDir = project.GameDir;
            if (!project.IsReadOnly) Settings.LastModDir = project.ModDir;
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
            _reloadIfAlreadyLoaded = true; // opening (again) a project reloads its character, TAE included, from disk
            if (project.IsAnimationsOnly) _combo.SelectManualTab();
            Notify($"Opened project '{project.Name}'.");
        }

        public void CloseProject()
        {
            if (!ConfirmDiscardUnsaved()) return;
            SaveProjectUiState();
            _editor.CloseAll();
            StopFileWatch();
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
            _paramsTab.Tick(Layout);
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
                    if (files != null && (!IsCharacterLoaded(files) || _reloadIfAlreadyLoaded)) LoadCharacter(s);
                    _reloadIfAlreadyLoaded = false;
                }
                if (_autoLoadChrId != null && DSAnimStudio.zzz_DocumentManager.CurrentDocument != null)
                {
                    string id = _autoLoadChrId;
                    _autoLoadChrId = null;
                    var files = ResolveCharacterId(id);
                    if (files != null && (!IsCharacterLoaded(files) || _reloadIfAlreadyLoaded)) LoadCharacterId(id, force: _reloadIfAlreadyLoaded);
                    _reloadIfAlreadyLoaded = false;
                }
                if (_resetPanelsInFrames > 0 && --_resetPanelsInFrames == 0) ResetPanelLayout();
                Layout.ApplyIfNeeded(Settings);
                if (Layout.FocusEditorTabFrames > 0) { if (!Layout.EditorOutsideGraphNode) ImGui.SetWindowFocus(SasLayout.EditorWindow); Layout.FocusEditorTabFrames--; }
                focused |= _scripts.Build();
                focused |= _editor.Build();
                focused |= _combo.Build();
                focused |= _luabnd.Build();
                PollFileWatch(); // external changes: reload / banners (SasApp.Watch.cs)
            }
            focused |= _findScripts.Build(); // also cancels its search when the project closes
            SpEffectLink.DrawRequested(); // SpEffect popup opened from the Combo panel
            focused |= _finder.Build(); // works with any loaded character, project or not
            focused |= _spLabels.Build();
            focused |= _animSlots.Build();
            focused |= _addScripts.Build();
            _updates.Build(); // checks GitHub on the first frame when enabled; shows the window when there is an update
            bool inProject = Session != null;
            Layout.ProbeWindows(new[]
            {
                (SasLayout.ScriptsWindow, inProject && _scripts.IsOpen), (SasLayout.EditorWindow, inProject && _editor.IsOpen),
                (ComboPanel.WindowName, inProject && _combo.IsOpen), (LuabndToolsPanel.WindowName, inProject && _luabnd.IsOpen),
                (EventFinderPanel.WindowName, _finder.IsOpen), (SpEffectLabelsWindow.WindowName, _spLabels.IsOpen), (AddScriptsWindow.WindowName, inProject && _addScripts.IsOpen),
                (AnimSlotsWindow.WindowName, _animSlots.IsOpen), (SpEffectLuaLink.AskWindowName, SpEffectLink.AskOpen), (SpEffectLuaLink.ResultWindowName, SpEffectLink.ResultOpen),
            });
            SasHooks.GraphHidden = !Layout.GraphVisible || Layout.WindowNestedInGraph
                                   || (inProject && _editor.VisibleInDockId != 0 && _editor.VisibleInDockId == Layout.GraphDockId);
            BuildErrorPopup();
            ImeGuard.SetImGuiTextFieldActive(DSAnimStudio.Main.WinForm?.Handle ?? IntPtr.Zero, ImGui.GetIO().WantTextInput);
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
            // Project items (New / Open / Recent / Save Script / Close) are in the File menu (BuildFileMenuItems); panel toggles
            // in Window; Follow Playhead in Action Graph. This menu keeps the AI tools only.
            if (ImGui.MenuItem("Find Events...", "Ctrl+F", false, DSAnimStudio.zzz_DocumentManager.CurrentDocument?.EditorScreen?.IsFileOpen == true)) OpenEventFinder();
            if (ImGui.MenuItem("Find in Scripts...", "Ctrl+Shift+F", false, Session != null)) OpenFindInScripts();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Find SpEffects, attacks, bullets, FFX, sounds… by id in every animation of the loaded character.");
            if (ImGui.MenuItem("SpEffect Labels...")) _spLabels.Open();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Name SpEffect ids (e.g. 200210 = Strong parry (right)); the names show in the Combo and Find Events windows.");
            if (ImGui.MenuItem("Swap / Add Animation...", "", false, Bridge.LoadedAnibnd != null)) _animSlots.Open();
            if (ImGui.MenuItem("Replace Animation HKX...", "", false, Bridge.LoadedAnibnd != null)) _animSlots.Open(replace: true);
            if (ImGui.MenuItem("Add SpEffect Handler to Lua...", "", false, Session != null)) SpEffectLink.Ask();
            if (ImGui.MenuItem("Undo Last Animation Swap", "", false, _animSlots.WhyCantUndoLastSwap() == null)) _animSlots.UndoLastSwap(); // anibnd + Lua scripts as one step
            if (ImGui.BeginMenu("Updates"))
            {
                if (ImGui.MenuItem("Check for Updates...")) _updates.StartCheck(manual: true);
                if (ImGui.MenuItem("Check on Launch", "", Settings.CheckForUpdatesOnStartup)) { Settings.CheckForUpdatesOnStartup = !Settings.CheckForUpdatesOnStartup; Settings.Save(); }
                if (ImGui.MenuItem("Include Pre-releases", "", Settings.IncludePrereleaseUpdates)) { Settings.IncludePrereleaseUpdates = !Settings.IncludePrereleaseUpdates; Settings.Save(); }
                ImGui.TextDisabled($"Current version: {SasHooks.AppVersion}");
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Luabnd (Unpack / Repack)...", "", false, Session != null)) _luabnd.Open();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unpack a map's .luabnd.dcx into a WitchyBND folder, or repack a Witchy/Yabber folder into the mod's .luabnd.dcx,\n" +
                                 "without leaving the app. Saving in the Lua Editor already updates both; this is for edits made in other tools.");
            ImGui.Separator();
            if (ImGui.BeginMenu("Layout", Session != null))
            {
                if (ImGui.MenuItem("AI (Lua Editor)", "", CurrentLayout == WorkspaceLayout.Ai)) SetLayout(WorkspaceLayout.Ai);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor shown (as a tab next to Graph, or beside it: see below).");
                if (ImGui.MenuItem("Animation", "", CurrentLayout == WorkspaceLayout.Animation)) SetLayout(WorkspaceLayout.Animation);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor hidden so the Graph (TAE timeline) stays visible. Default for animations-only projects.");
                ImGui.Separator();
                if (ImGui.MenuItem("Graph | Lua Editor side by side", "", Settings.EditorBesideGraph)) SetEditorPlacement(true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Split the Graph's panel 50/50: Graph on the left, Lua Editor on the right.");
                if (ImGui.MenuItem("Lua Editor as tab next to Graph", "", !Settings.EditorBesideGraph)) SetEditorPlacement(false);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor and Graph share one panel; click the tabs to switch.");
                if (ImGui.MenuItem("Reset Panel Layout##sas_layout_reset")) ResetPanelLayout();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Put the Souls AI panels back in their default places (keeps the Lua Editor placement above). Panels can also be dragged anywhere by their tab.\n" +
                                                            "Workspaces, Reset All Panels and the panel list are in the Window menu.");
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Allow Windows IME in Lua Editor", "", Settings.AllowImeInEditor)) { Settings.AllowImeInEditor = !Settings.AllowImeInEditor; Settings.Save(); }
            WatchFilesMenuItem();
            if (ImGui.MenuItem("Format Lua on Save", "", Settings.FormatOnSave)) { Settings.FormatOnSave = !Settings.FormatOnSave; Settings.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Indent, spacing and blank lines are tidied when a Lua script is saved (one undo step; skipped when the script has a syntax error).");
            if (ImGui.MenuItem("Format Lua Script", "Shift+Alt+F", false, _editor.Active?.Doc != null)) _editor.FormatActive();
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
            ImGui.EndMenu();
        }

        /// <summary>Top of DSAS's File menu: project items, then DSAS's own items (animation files) under a small header.</summary>
        public void BuildFileMenuItems()
        {
            ImGui.TextDisabled("Souls AI project");
            if (ImGui.MenuItem("Start Screen")) ShowStartScreen();
            if (ImGui.MenuItem("New Project...")) ShowNewProjectWizard();
            if (ImGui.MenuItem("Open Project...")) BrowseAndOpenProject();
            if (ImGui.BeginMenu("Recent Projects", Recent.Paths.Count > 0))
            {
                foreach (string p in Recent.Paths)
                    if (ImGui.MenuItem(Path.GetFileNameWithoutExtension(p), "", false, File.Exists(p))) OpenProject(p);
                ImGui.Separator();
                if (ImGui.MenuItem("Clear Recent Projects")) Recent.Clear();
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Add AI Scripts...", "", false, Session != null)) _addScripts.Open();
            if (ImGui.MenuItem("Save Script", "Ctrl+S", false, _editor.ActiveIsDirty)) _editor.SaveActive();
            if (ImGui.MenuItem($"Save All Scripts ({_editor.DirtyCount})", "", false, _editor.DirtyCount > 0)) _editor.SaveAll();
            if (ImGui.MenuItem("Close Project", "", false, Session != null)) CloseProject();
            ImGui.Separator();
            ImGui.TextDisabled("Animation files (TAE)");
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
            ImGui.Separator();
            Workspaces.MenuItems();
            if (ImGui.MenuItem("Reset All Panels to Default")) ResetAllPanels();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("DSAS's default layout, with the Souls AI panels docked in their default places.");
        }

        /// <summary>DSAS's default (V5) dock layout, then our panels docked into it as on first launch.</summary>
        private void ResetAllPanels()
        {
            DSAnimStudio.ImguiOSD.OSD.RequestLoadDefaultLayout = DSAnimStudio.ImguiOSD.OSD.DefaultLayoutTypes.V5;
            _resetPanelsInFrames = 10; // dock ours once DSAS's windows sit in the new nodes (same frame: wrong / stale node ids)
        }

        private int _resetPanelsInFrames;

        /// <summary>Ctrl+F in the graph / Edit > Find: the Find Events window. False when no character is loaded (DSAS then does nothing).</summary>
        public bool OpenEventFinder()
        {
            if (DSAnimStudio.zzz_DocumentManager.CurrentDocument?.EditorScreen?.IsFileOpen != true) return false;
            _finder.Open();
            return true;
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

        public IReadOnlyList<OpenScript> OpenScripts => _editor.OpenScripts;
        public LuaEditorPanel EditorPanel => _editor;

        /// <summary>Opens "Find in Scripts" (Ctrl+Shift+F in the Lua editor); <paramref name="seed"/> prefills the query.</summary>
        public void OpenFindInScripts(string? seed = null) => _findScripts.Open(seed);

        /// <summary>Opens the Swap / Add animation window (Animations panel buttons, Souls AI menu).</summary>
        public void OpenAnimSlots(bool add) => _animSlots.Open(add);

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

        private void ResetPanelLayout()
        {
            _scripts.IsOpen = _combo.IsOpen = true;
            ApplyLayout(CurrentLayout);
            Layout.RequestApply();
        }

        /// <summary>Lua Editor beside the Graph (50/50) or as a tab in its node; saved in settings and applied now.</summary>
        private void SetEditorPlacement(bool besideGraph)
        {
            Settings.EditorBesideGraph = besideGraph;
            Settings.Save();
            if (CurrentLayout != WorkspaceLayout.Ai) SetLayout(WorkspaceLayout.Ai); // the Animation layout hides the editor
            Layout.RequestEditorPlacement(besideGraph);
        }

        /// <summary>Opens an AI script in the editor (switches to the AI layout if the editor was hidden).</summary>
        public void OpenScript(ProjectScript script)
        {
            _editor.Open(script);
            if (!_editor.IsOpen) SetLayout(WorkspaceLayout.Ai);
        }

        /// <summary>
        /// Opens <paramref name="script"/> in a tab (adding it to the project when it is not there yet) and runs
        /// <paramref name="afterLoad"/> once its text is loaded. Used by the animation swap for scripts that share the character.
        /// </summary>
        public void OpenScriptWith(ProjectScript script, Action<OpenScript> afterLoad, bool activate)
        {
            if (Project == null) return;
            if (!Project.Scripts.Any(s => s.Key == script.Key))
            {
                Project.Scripts.Add(script);
                SaveProjectUiState();
            }
            _editor.Open(script, afterLoad, activate);
        }

        public void SaveAllScripts() => _editor.SaveAll();

        /// <summary>Opens the "Add AI Scripts" window; <paramref name="filter"/> prefills its filter.</summary>
        public void OpenAddScripts(string? filter = null) => _addScripts.Open(filter);

        /// <summary>
        /// Removes a script from the project (its file in the luabnd is not touched). Refused while its tab has unsaved edits.
        /// </summary>
        public void RemoveScript(ProjectScript script)
        {
            if (Project == null) return;
            if (!_editor.TryClose(script.Key))
            {
                Notify($"{script.FileName} has unsaved changes: save or undo them first.");
                return;
            }
            Project.Scripts.RemoveAll(s => s.Key == script.Key);
            SaveProjectUiState();
            Notify($"Removed {script.FileName} from the project (the script itself is unchanged)");
        }

        // ---------- characters of animations-only projects ----------

        private CharacterLocator? Locator => Project == null ? null : new CharacterLocator(Project.GameDir, Project.ModDir);

        public CharacterFiles? ResolveCharacterId(string chrId) => Locator?.ResolveId(chrId);

        /// <summary>
        /// Files DSAS actually opens: the mod's anibnd, or for a game-only character a read-only view copy in the
        /// project's cache (DSAS writes workspace files next to what it opens; the game folder is never written to).
        /// </summary>
        private CharacterFiles? OpenableFiles(CharacterFiles files) =>
            files.AnibndFromMod || Project == null ? files : Locator?.GameView(files.ChrId, Project.GameViewDir);

        /// <summary>True if this character (its mod anibnd or its read-only view copy) is the current DSAS document.</summary>
        public bool IsCharacterLoaded(CharacterFiles files)
        {
            if (files.AnibndFromMod || Project == null) return Bridge.IsLoaded(files);
            string view = Path.Combine(Project.GameViewDir, "chr", files.ChrId + ".anibnd.dcx");
            return Bridge.IsLoaded(files with { AnibndPath = view });
        }

        /// <summary>The loaded character is a read-only view of a game file (saving it is refused).</summary>
        public bool LoadedIsReadOnlyView =>
            Project != null && Bridge.LoadedAnibnd is { } p && IsUnder(p, Project.GameViewDir);

        private static bool IsUnder(string path, string dir)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        /// <summary>Loads a project character's model + animations into DSAS.</summary>
        public void LoadCharacterId(string chrId, bool force = false)
        {
            if (Project == null) return;
            var files = ResolveCharacterId(chrId);
            if (files == null) { Notify($"{chrId}.anibnd.dcx not found in the mod or the game chr folder."); return; }
            if (IsCharacterLoaded(files) && !force) return; // already the current document
            try
            {
                bool view = !files.AnibndFromMod;
                files = OpenableFiles(files) ?? throw new FileNotFoundException($"{chrId}.anibnd.dcx not found in the game chr folder.");
                Bridge.LoadCharacter(files, Project.GameDir, Project.ModDir, chr =>
                {
                    Notify(view ? $"Loaded {chr} from the game (read-only view{(Project.IsReadOnly ? "" : ": use Copy to mod to edit")})." : $"Loaded {chr}.");
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

        public void LoadCharacter(ProjectScript script)
        {
            var files = ResolveCharacter(script);
            if (files == null || Project == null) { Notify($"No anibnd found for {script.FileName}. Set the Character (e.g. c5060)."); return; }
            try
            {
                files = OpenableFiles(files) ?? throw new FileNotFoundException($"{files.ChrId}.anibnd.dcx not found in the game chr folder.");
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
                Notify($"Anim {AnimIds.Format(animId)} not found — load the character first (Project panel → Load model & anims, or click a character)," +
                       " or check the script's Anim category (Project panel).");
        }

        /// <summary>
        /// TAE anim id for an anim id written in the open script: its Anim category is applied (Isshin spear 540310:
        /// 3000 → a100_003000). Full ids (aXXX_ already) pass through.
        /// </summary>
        public int TaeAnimId(int luaAnimId) => _editor.ActiveScript?.ToTaeAnimId(luaAnimId) ?? luaAnimId;

        /// <summary>Plays an anim id written in the open script (see <see cref="TaeAnimId"/>).</summary>
        public void PlayLuaAnim(int luaAnimId) => PlayAnim(TaeAnimId(luaAnimId));

        /// <summary>Anim category of the open script (0 = a000).</summary>
        public int ActiveAnimCategory => _editor.ActiveScript?.AnimCategory ?? 0;

        public void SetAnimCategory(ProjectScript script, int category)
        {
            if (script.AnimCategory == category) return;
            script.AnimCategory = category;
            SaveProjectUiState();
            Notify(category == 0 ? $"{script.FileName}: anim ids refer to a000 (default)."
                                 : $"{script.FileName}: anim ids refer to a{category:D3} (e.g. 3000 → a{category:D3}_003000).");
        }

        // ---------- safety for DSAS writes ----------

        /// <summary>Non-null reason when a write to <paramref name="path"/> must be refused (game folder, outside mods).</summary>
        public string? WhyWriteIsBlocked(string path)
        {
            if (Project != null && IsUnder(path, Project.GameViewDir))
                return "This character is a read-only view of the game's file, so it can't be saved." +
                       (Project.IsReadOnly ? " This project has no mod folder." : " Use \"Copy to mod\" in the Project panel to edit a copy in your mod.");
            return SoulsAiStudio.Core.IO.WritePolicy.WhyBlocked(path, new[] { Project?.GameDir, Settings.LastGameDir, SasSettings.GuessGameDir() }, Project?.ModDir);
        }

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

        // ---------- SpEffect labels ----------

        /// <summary>User SpEffect names (<c>spEffectLabels.json</c> in the user data folder, shared by all projects).</summary>
        public SoulsAiStudio.Core.Params.SpEffectLabels SpEffectLabels { get; } =
            SoulsAiStudio.Core.Params.SpEffectLabels.Load(Path.Combine(SasSettings.UserDataDir, "spEffectLabels.json"));

        /// <summary>Name of a SpEffect: the user's label, else the SpEffectParam row name, else null.</summary>
        public string? SpEffectDisplayName(int id) => SpEffectLabels.Get(id) ?? Bridge.SpEffectName(id);

        /// <summary>"200210 Strong parry (right)", or just "200210" when the SpEffect has no name.</summary>
        public string SpEffectText(int id) => SpEffectDisplayName(id) is { } n ? $"{id} {n}" : id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Right-click menu on the last drawn item: set/remove the label of SpEffect <paramref name="id"/>.</summary>
        public void SpEffectLabelContextMenu(string strId, int id) => _spLabels.ContextMenu(strId, id);

        /// <summary>The same menu items, inside a popup/menu the caller already opened.</summary>
        public void SpEffectLabelMenuItems(int id) => _spLabels.ContextMenuItems(id);

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
