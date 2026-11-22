#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// The only seam between DSAS and Souls AI Studio. Every DSAS edit is a one-line call into this class marked
    /// <c>// [SAS]</c> (CLAUDE.md §6). Keep these methods tiny; real work lives in <see cref="SasApp"/> and Core.
    /// </summary>
    public static class SasHooks
    {
        private static readonly Lazy<SasApp> _app = new(() => new SasApp()); // thread-safe: WriteBinder runs on DSAS's loading thread
        public static SasApp App => _app.Value;

        /// <summary>
        /// True while DSAS's "Graph" window is not visible: another tab of its dock node (e.g. our Lua Editor) is in front,
        /// or one of our windows sits inside the Graph window (see <see cref="SasLayout"/>). The TAE graph is custom-drawn
        /// (not ImGui), so DSAS must skip its draw + mouse input then, or it paints over that window.
        /// </summary>
        public static volatile bool GraphHidden;

        public const string AppName = "Souls AI Studio";
        public const string AppVersion = "0.4.0";

        /// <summary>Window title (replaces "DS ANIM STUDIO x"); keeps the DSAS base version for credit/bug reports.</summary>
        public static string AppTitle(string dsasVersion) => $"{AppName} {AppVersion}";

        /// <summary>DSAS Animations panel, next to a category's "Properties..." / "Duplicate...": our Swap / Add buttons.</summary>
        public static void AnimCategoryButtons()
        {
            try
            {
                if (!AnimCategoryButtonsInList)
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 13); // no button row (it is in the toolbar): start the list right under the tag row (measured: DSAS left a ~20 px gap)
                    return;
                }
                ImGui.SameLine();
                if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Swap...")) App.OpenAnimSlots(add: false);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Swap two animation ids (events, HKX files and the animations that borrow them)");
                ImGui.SameLine();
                if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Add Anim...")) App.OpenAnimSlots(add: true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add a new empty animation: Override HKX of an existing one, or a .hkx file for this character");
            }
            catch (Exception ex) { App.ReportError("Animation slots", ex); }
        }

        /// <summary>
        /// The category buttons (Properties / Duplicate / Swap / Add Anim) live in the Animations panel's fixed toolbar
        /// (<see cref="AnimListToolbar"/>), not under each category header where they scrolled away with the list.
        /// </summary>
        public const bool AnimCategoryButtonsInList = false;

        /// <summary>
        /// Animations panel, top of its contents: a menu-bar toolbar that never scrolls, acting on the category of the
        /// selected animation (DSAS drew these buttons under the category header, so a long list had to be scrolled to the top).
        /// </summary>
        private static readonly AnimSearchPopup _animSearch = new();

        public static void AnimListToolbar()
        {
            try
            {
                if (!ImGui.BeginMenuBar()) return;
                try
                {
                    var screen = DSAnimStudio.Main.TAE_EDITOR;
                    var proj = screen?.FileContainer?.Proj;
                    var category = screen?.SelectedAnimCategory;
                    bool has = proj != null && category != null;
                    ImGui.BeginDisabled(proj == null);
                    if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Find##sas_at_find")) _animSearch.Open();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Find an animation by id (3004), name (slash up) or animation file name (a000_021004.hkt), and jump to it");
                    ImGui.EndDisabled();
                    _animSearch.Draw(screen);
                    ImGui.BeginDisabled(!has);
                    if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Props##sas_at_props")) screen!.ShowDialogEditAnimCategoryProperties(category);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(has ? $"Properties of category [{category!.CategoryID}] (the selected animation's)" : "Select an animation first");
                    if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Dup##sas_at_dup")) screen!.ShowDialogDuplicateToNewTaeSection(proj, category);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(has ? $"Duplicate category [{category!.CategoryID}] to a new section" : "Select an animation first");
                    ImGui.EndDisabled();
                    bool loaded = App.Bridge.LoadedAnibnd != null;
                    ImGui.BeginDisabled(!loaded);
                    if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Swap##sas_at_swap")) App.OpenAnimSlots(add: false);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Swap two animation ids (events, HKX files and the animations that borrow them)");
                    if (DSAnimStudio.ImguiOSD.Tools.SimpleClickButton("Add##sas_at_add")) App.OpenAnimSlots(add: true);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Add a new empty animation: Override HKX of an existing one, or a .hkx file for this character");
                    ImGui.EndDisabled();
                }
                finally { ImGui.EndMenuBar(); }
            }
            catch (Exception ex) { App.ReportError("Animations toolbar", ex); }
        }

        /// <summary>
        /// Launch credits screen: our name and the fork's own components on top; DS Anim Studio's credits and thanks
        /// follow unchanged (respect for everyone who built and supported DSAS).
        /// </summary>
        public static string SplashText(string dsasCredits) =>
            $"SOULS AI STUDIO {AppVersion} - Lua AI editor for Sekiro, built on DS Anim Studio (GPLv3)\n" +
            "ImGuiColorTextEditNet (c) 2017 BalazsJako, 2022 CSinkers - DSLuaDecompiler (c) 2019 katalash\n\n" +
            "Built on the work of everyone below - thank you.\n\n" +
            dsasCredits;

        /// <summary>
        /// Starting camera distance when a character loads. DSAS frames the hit capsule edge to edge, which is very close;
        /// we back off so the whole character, its weapon swings and the ground around it are visible.
        /// </summary>
        public const float ViewportStartZoomOut = 1.8f;

        public static float StartCameraDistance(float dsasDistance) => dsasDistance * ViewportStartZoomOut;
        /// <summary>Called from <c>OSD.Build</c> after DSAS windows; draws our windows.</summary>
        public static void BuildWindows(ref bool anyFieldFocused)
        {
            try { App.BuildWindows(ref anyFieldFocused); }
            catch (Exception ex) { App.ReportError("UI error", ex); }
        }

        /// <summary>Undo-to-saved-state tracking for the Animations list's unsaved mark (see <see cref="AnimCleanTracker"/>).</summary>
        public static readonly AnimCleanTracker AnimClean = new();

        /// <summary>TaeUndoMan: right before a new per-anim action, an undo or a redo.</summary>
        public static void BeforeAnimChange(DSAnimStudio.DSAProj.Animation? anim)
        {
            try { AnimClean.BeforeChange(anim); } catch (Exception ex) { App.ReportError("Undo tracking", ex); }
        }

        /// <summary>TaeUndoMan: after an undo/redo marked the anim modified.</summary>
        public static void AfterAnimUndoRedo(DSAnimStudio.DSAProj.Animation? anim)
        {
            try { AnimClean.AfterUndoRedo(anim); } catch (Exception ex) { App.ReportError("Undo tracking", ex); }
        }

        /// <summary>
        /// DSAS 4.9.9 "solo on hover", for SpEffects only. While the mouse is over a TAE block (and nothing is playing or
        /// scrubbing), SpEffect blocks are active only if they are the hovered block, whatever the time: hovering a SpEffect
        /// lists just that SpEffect in the viewport; hovering any other block lists none. Not hovering: SpEffects active at
        /// the current time, as before. Every other event type (attack/bullet hitboxes…) keeps the time-based rule.
        /// Skipped when DSAS's own "Solo Highlight Action On Hover" (all types) is enabled.
        /// </summary>
        public static void SoloSpEffectOnHover(DSAnimStudio.TaeEditor.TaeActionSimulationEnvironment sim, DSAnimStudio.DSAProj.Action act,
            DSAnimStudio.DSAProj.Action? dsasSoloHover)
        {
            try
            {
                if (dsasSoloHover != null || act == null) return;
                var screen = DSAnimStudio.Main.TAE_EDITOR;
                var hover = screen?.NewHoverAction_NeedsNoSelection;
                if (hover == null) return;
                var cursor = screen!.PlaybackCursor;
                if (cursor == null || cursor.IsPlaying || cursor.Scrubbing) return;
                if (!sim.NewSimulation_EvCheck_SpEffects(act)) return;
                act.NewSimulationEnter = false;
                act.NewSimulationExit = false;
                act.NewSimulationActive = ReferenceEquals(act, hover);
            }
            catch (Exception) { } // never break DSAS's simulation
        }

        /// <summary>
        /// A combo is playing (not paused). NewCombo drives the time itself and sets the playback cursor's IsPlaying to false
        /// every frame, so DSAS's sound manager treated slot sounds (PlaySound_BySlot…) as stopped and never kept them alive.
        /// </summary>
        public static bool ComboIsPlaying(DSAnimStudio.TaeEditor.TaeEditorScreen? tae)
        {
            try
            {
                var c = tae?.Graph?.ViewportInteractor?.Combo;
                return c != null && c.PlaybackState == DSAnimStudio.NewCombo.PlaybackStates.Playing && !c.Paused;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// DSAS SpEffectParam row read (reader at the row start): for Sekiro also reads cycleOccurrenceSpEffectId and
        /// motionInterval, so combos can follow e.g. TAE SpEffect 205032 → cycle SpEffect 5032 handled in Goal.Interrupt.
        /// </summary>
        public static void ReadSpEffectCycle(DSAnimStudio.ParamData.SpEffectParam row, SoulsFormats.BinaryReaderEx br)
        {
            try
            {
                if (DSAnimStudio.zzz_DocumentManager.CurrentDocument?.GameRoot?.GameType != SoulsAssetPipeline.SoulsGames.SDT) return;
                long start = br.Position;
                if (start + SoulsAiStudio.Core.Formats.Params.SekiroParamOffsets.SpEffectRowSize > br.Length) return;
                row.SasCycleSpEffectId = br.GetInt32(start + SoulsAiStudio.Core.Formats.Params.SekiroParamOffsets.SpEffectCycleOccurrenceSpEffectId);
                row.SasMotionInterval = br.GetSingle(start + SoulsAiStudio.Core.Formats.Params.SekiroParamOffsets.SpEffectMotionInterval);
            }
            catch (Exception) { } // never break DSAS's param load
        }

        private static readonly SameHkxIndex _sameHkx = new();

        /// <summary>Animations list: <paramref name="anim"/> plays the same HKX as the selected anim (DSAS 4.9.9 highlight).</summary>
        public static bool SharesHkxWithSelected(DSAnimStudio.DSAProj? proj, DSAnimStudio.DSAProj.Animation? selected, DSAnimStudio.DSAProj.Animation anim)
        {
            try { return _sameHkx.Shares(proj, selected, anim); }
            catch (Exception) { return false; }
        }

        /// <summary>DSAProj: all modified flags cleared (after a save).</summary>
        public static void AllAnimsSaved() => AnimClean.Reset();

        /// <summary>
        /// DSAS windows that are part of another window (graph helpers), overlays or the documents strip: never moved, and
        /// closed when floating as DSAS does.
        /// </summary>
        private static bool IsFixedDsasWindow(DSAnimStudio.ImguiOSD.Window w) =>
            w is DSAnimStudio.ImguiOSD.Window.NewTransport or DSAnimStudio.ImguiOSD.Window.DocManToolbar
                or DSAnimStudio.ImguiOSD.Window.Tooltip or DSAnimStudio.ImguiOSD.Window.Graph_Tracks
                or DSAnimStudio.ImguiOSD.Window.Graph_Actions or DSAnimStudio.ImguiOSD.Window.NewComboViewer;

        /// <summary>
        /// DSAS <c>Window.Update</c>: flags of a DSAS panel outside DSAS's debug dock-edit mode. DSAS always added NoMove, so
        /// Viewport, Animations, Parameters, Graph… could never be dragged by their tab, undocked or re-docked. Unless the
        /// user locks the layout (Window > Lock Panel Layout), they move like our panels (After Effects-style layout).
        /// </summary>
        public static ImGuiWindowFlags DsasWindowFlags(DSAnimStudio.ImguiOSD.Window window, ImGuiWindowFlags dsasFlags)
        {
            try
            {
                if (App.Settings.LockPanelLayout || IsFixedDsasWindow(window)) return dsasFlags;
                var flags = dsasFlags & ~ImGuiWindowFlags.NoMove;
                // Viewport / Graph are NoInputs so the mouse reaches the 3D view / custom-drawn timeline under them, which
                // also made their tab impossible to grab. Take inputs only over the tab strip, and for the whole drag.
                if ((flags & ImGuiWindowFlags.NoInputs) != 0)
                {
                    // (ImGui.NET 1.87's ImGuiIO field offsets don't match DSAS's native 1.88: use the functions, not io.MousePos.)
                    Vector2 mouse = ImGui.GetMousePos();
                    Vector2 pos = window.LastPosition, size = window.LastSize;
                    float strip = ImGui.GetFrameHeight() + 2;
                    bool inStrip = mouse.X >= pos.X && mouse.X < pos.X + size.X && mouse.Y >= pos.Y && mouse.Y < pos.Y + strip;
                    if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _tabDragWindow = inStrip ? window : (ReferenceEquals(_tabDragWindow, window) ? null : _tabDragWindow);
                    if (ReferenceEquals(_tabDragWindow, window)) flags &= ~ImGuiWindowFlags.NoInputs;
                }
                return flags;
            }
            catch (Exception) { return dsasFlags; }
        }

        /// <summary>NoInputs DSAS window whose tab strip the mouse is over (or that is being dragged by its tab).</summary>
        private static DSAnimStudio.ImguiOSD.Window? _tabDragWindow;

        /// <summary>
        /// DSAS closes a panel that is floating (not docked) as soon as it loses focus. True keeps that behaviour (locked
        /// layout, fixed windows); false lets a panel dragged out of the dock stay open as a floating window.
        /// </summary>
        public static bool CloseUndockedDsasWindow(DSAnimStudio.ImguiOSD.Window window)
        {
            try { return App.Settings.LockPanelLayout || IsFixedDsasWindow(window); }
            catch (Exception) { return true; }
        }

        /// <summary>Start of DSAS's <c>OSD.Build</c>, before any window of the frame: applies a workspace chosen in a menu.</summary>
        public static void BeforeOsdBuild()
        {
            try { App.Workspaces.ApplyPending(); } catch (Exception ex) { App.ReportError("Workspace", ex); }
        }

        /// <summary>Called from DSAS <c>Window.Update</c> right after <c>ImGui.Begin</c>: reports its dock node.</summary>
        public static void ReportWindowDock(string title, uint dockId) => App.Layout.ReportDock(title, dockId);

        /// <summary>
        /// DSAS <c>Window.Update</c>: wraps its <c>ImGui.Begin</c> and returns its result unchanged. For the Graph window,
        /// false means it is a hidden tab (another tab of its dock node is in front): the custom-drawn graph must then
        /// neither draw nor take input (see <see cref="GraphHidden"/>).
        /// </summary>
        public static bool ReportWindowBegin(DSAnimStudio.ImguiOSD.Window window, bool visible)
        {
            if (ReferenceEquals(window, DSAnimStudio.ImguiOSD.OSD.SpWindowGraph)) App.Layout.ReportGraphBegin(visible);
            if (ReferenceEquals(window, DSAnimStudio.ImguiOSD.OSD.SpWindowNotifications)) ParametersTabFocus.LogVisible = visible;
            return visible;
        }

        /// <summary>
        /// DSAS Graph window: flags of its inner dockspace (it hosts the "Tracks" / "Actions" helper windows). While any
        /// window is dragged for docking, the inner dockspace is only kept alive (no drop targets): otherwise ImGui prefers
        /// its smaller drop target over the main dockspace, so a panel dropped anywhere on the graph got docked INSIDE the
        /// Graph window, under the custom-drawn timeline (which covered it and ate its clicks), and the Graph's own node
        /// could never be split by dragging. Unchanged in DSAS's "Debug ImGui Dock Edit" mode.
        /// </summary>
        public static ImGuiDockNodeFlags GraphDockSpaceFlags(ImGuiDockNodeFlags dsasFlags)
        {
            try
            {
                if (DSAnimStudio.ImguiOSD.OSD.DebugIsDockEditMode || !SasLayout.IsDraggingWindow()) return dsasFlags;
                return dsasFlags | ImGuiDockNodeFlags.KeepAliveOnly;
            }
            catch (Exception) { return dsasFlags; }
        }

        /// <summary>
        /// TaeEditorScreen: the mouse inside the graph rect belongs to the graph (drag/select/hover blocks), i.e. the graph
        /// is visible and none of our windows (floating over it) is under the mouse.
        /// </summary>
        /// <summary>
        /// Before drawing the TAE graph: re-read its display rect from the Graph window's current size. DSAS only does this in
        /// the graph's Update, which is skipped while one of our panels has focus, so after a docking change the graph kept
        /// its old (larger) rect and drew over the panel next to it.
        /// </summary>
        public static void RefreshGraphRect(DSAnimStudio.TaeEditor.NewGraph graph)
        {
            try { graph.ScrollViewer.SetDisplayRect(graph.WholeScrollViewerRect, graph.LayoutManager.GetVirtualAreaSize()); }
            catch (Exception) { }
        }

        public static bool GraphTakesMouse =>!GraphHidden && !App.Layout.OurWindowHovered;

        /// <summary>Called by DSAS right after it creates the document for an open-recent request.</summary>
        public static void BeforeOpenRecent(DSAnimStudio.zzz_DocumentIns doc, DSAnimStudio.DSAProj.TaeContainerInfo container)
        {
            try { App.Bridge.BeforeOpenRecent(doc, container); } catch (Exception ex) { App.ReportError("Load hook", ex); }
        }

        /// <summary>
        /// Root motion during a blend: weighted between the outgoing and incoming anim like DSAS 4.9.9 (5.0 used only the
        /// incoming anim → the character stops/jumps when a blend starts). Read when a model's anim container is created.
        /// </summary>
        public static bool RootMotionBlending
        {
            get { try { return App.Settings.BlendRootMotion; } catch (Exception) { return true; } }
        }

        /// <summary>
        /// A combo step just requested its anim (NewCombo.GoToNextComboEntry).
        /// 1) The container may still hold a "sync time" from the previous anim (set when it hit its end/looped, e.g. the last
        ///    anim of the previous combo); DSAS's playback cursor pops it next frame and absolute-scrubs the NEW anim to that
        ///    time, so a replayed combo started mid-anim (e.g. 3002 at 1.2 s). Discard it.
        /// 2) Every container scrub copies the container's loop flag (DSAS "Loop" setting) into the slot request, so combo
        ///    entries looped even with IsLoop = false: an anim cut at its end wrapped to frame 0 and kept playing from the
        ///    start while it blended out. Use the entry's flag; DSAS restores the setting when the combo is over.
        /// </summary>
        public static void OnComboAnimRequested(DSAnimStudio.NewAnimationContainer container, bool isLoop)
        {
            if (container == null) return;
            try { container.PopSyncTime(); container.EnableLooping = isLoop; } catch (Exception) { }
        }

        /// <summary>Multiplier on the viewport ambient light: DSAS 5.0 doubles it, 4.9.9 did not.</summary>
        public static float AmbientLightBoost
        {
            get { try { return App.Settings.Lighting499 ? 1f : 2f; } catch (Exception) { return 2f; } }
        }

        /// <summary>
        /// Sets the viewport light for the chosen look. DSAS 4.9.9: light from the side/above (H -0.75, V -0.75) and no
        /// subsurface "wrap" lighting, so models have clear light and dark sides. DSAS 5.0: light from near the camera
        /// (H 0.2, V -0.2) and SSS x2, which looks flatter. The Toolbox sliders still adjust both afterwards.
        /// </summary>
        public static void ApplyLightingPreset(bool lighting499)
        {
            DSAnimStudio.ViewportEnvironment.LightRotationH = lighting499 ? -0.75f : 0.2f;
            DSAnimStudio.ViewportEnvironment.LightRotationV = lighting499 ? -0.75f : -0.2f;
            DSAnimStudio.ViewportEnvironment.SSSMult = lighting499 ? 0f : 2f;
            // Tonemap (not saved by DSAS, so applied on every launch).
            DSAnimStudio.ViewportEnvironment.FlverSceneBrightness = lighting499 ? 2f : 1f;
            DSAnimStudio.ViewportEnvironment.FlverSceneContrast = lighting499 ? 0.5f : 0.6f;
        }

        /// <summary>
        /// Skybox part of the preset: "Show Cubemap As Skybox" and the cubemap (DefaultCubemap_Old for the 4.9.9 look).
        /// DSAS saves these in its own config, so this runs once (and when the menu toggle changes), never on every launch.
        /// Returns false while DSAS has not loaded its cubemaps yet.
        /// </summary>
        public static bool ApplySkyboxPreset(bool lighting499)
        {
            var names = DSAnimStudio.ViewportEnvironment.CubemapNames;
            if (names == null || names.Length == 0) return false;
            int idx = Array.FindIndex(names, n => string.Equals(n, lighting499 ? "DefaultCubemap_Old" : "DefaultCubemap", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) DSAnimStudio.ViewportEnvironment.CubemapNameIndex = idx;
            DSAnimStudio.ViewportEnvironment.DrawCubemap = lighting499;
            return true;
        }

        /// <summary>Log window's "Hide Popup Notifications" was toggled: remember it across launches.</summary>
        public static void HideNotificationPopupsChanged(bool hide)
        {
            try { App.Settings.HideNotificationPopups = hide; App.Settings.Save(); }
            catch (Exception) { }
        }

        /// <summary>Initial sound volume for each new DSAS document (DSAS kept it per document and reset it to 100).</summary>
        public static float SavedSoundVolume => App.Settings.SoundVolume;

        /// <summary>Called every frame by DSAS's Sound window; persists the volume once the user stops dragging.</summary>
        public static void TrackSoundVolume(float volume)
        {
            try { App.TrackSoundVolume(volume); } catch (Exception ex) { App.ReportError("Sound volume", ex); }
        }

        /// <summary>DSAS's Combo Viewer is replaced by the Manual tab of the Souls AI Combo window: never drawn.</summary>
        public const bool HideDsasComboViewer = true;

        /// <summary>Tools menu entry replacing DSAS's "Combo Viewer" item.</summary>
        public static void ComboMenuItem()
        {
            try { if (ImGuiNET.ImGui.MenuItem("Combo (Manual script)", "F8")) App.OpenComboManual(); }
            catch (Exception ex) { App.ReportError("Menu error", ex); }
        }

        /// <summary>F8 (was DSAS's Combo Viewer toggle).</summary>
        public static void OpenComboScript()
        {
            try { App.OpenComboManual(); } catch (Exception ex) { App.ReportError("Combo", ex); }
        }

        /// <summary>Called at the end of DSAS's "Window" menu.</summary>
        public static void BuildWindowMenuItems()
        {
            try { App.BuildWindowMenuItems(); } catch (Exception ex) { App.ReportError("Window menu", ex); }
        }

        /// <summary>
        /// Called right before DSAS serializes a TAE. Keeps the output readable by older tools (DSAS 4.9.9):
        /// event bank (ActionSetVersion) -1 → the game's bank (Sekiro chr = 14); null headers written as full headers.
        /// </summary>
        public static void MakeTaeCompatible(SoulsAssetPipeline.Animation.TAE tae, SoulsAssetPipeline.SoulsGames? game)
        {
            if (tae.ActionSetVersion < 0 && game == SoulsAssetPipeline.SoulsGames.SDT) tae.ActionSetVersion = 14;
            foreach (var anim in tae.Animations)
                if (anim.Header != null && anim.Header.IsNullHeader) anim.Header.IsNullHeader = false;
        }

        /// <summary>Called from <c>MenuBar.BuildMenuBar</c> before the DSAS menus.</summary>
        public static void BuildMenus(ref bool anyItemFocused, ref bool isAnyMenuExpanded)
        {
            try { App.BuildMenus(ref anyItemFocused, ref isAnyMenuExpanded); }
            catch (Exception ex) { App.ReportError("Menu error", ex); }
        }

        /// <summary>Top of DSAS's File menu: the Souls AI project items (DSAS's own items, for animation files, follow).</summary>
        public static void BuildFileMenuProjectItems()
        {
            try { App.BuildFileMenuItems(); }
            catch (Exception ex) { App.ReportError("Menu error", ex); }
        }

        /// <summary>Called from DSAS's menu bar after "Window": the DSAS 4.9.9 "FMOD Sound" menu (dropped in 5.0).</summary>
        public static void FmodSoundMenu(ref bool isAnyMenuExpanded)
        {
            try { SoulsAiStudio.App.FmodSoundMenu.Build(ref isAnyMenuExpanded); }
            catch (Exception ex) { App.ReportError("FMOD Sound menu", ex); }
        }

        /// <summary>Called from the DSAS font build: adds Vietnamese/Latin-extended glyphs (DSAS's DS3 glyph list lacks them).</summary>
        public static void AddExtraGlyphs(ImFontGlyphRangesBuilderPtr builder)
        {
            static void Range(ImFontGlyphRangesBuilderPtr b, int from, int to)
            {
                for (int c = from; c <= to; c++) b.AddChar((ushort)c);
            }
            Range(builder, 0x00A0, 0x00FF); // Latin-1 supplement
            Range(builder, 0x0100, 0x024F); // Latin Extended-A/B (ă, đ, ơ, ư)
            Range(builder, 0x0300, 0x036F); // combining diacritics
            Range(builder, 0x1EA0, 0x1EF9); // Vietnamese precomposed (ạ … ỹ)
            Range(builder, 0x2190, 0x21FF); // arrows used in the UI (→ ←)
            Range(builder, 0x25A0, 0x25FF); // geometric shapes (▶ ●)
        }

        /// <summary>
        /// Called at the top of DSAS's <c>SaveCurrentFile</c>, before it writes anything (workspace json, .dsasbak,
        /// .dsaproj). Returns false (and tells the user) when the anibnd lives in the game folder.
        /// </summary>
        public static bool AllowDsasSave(string anibndPath)
        {
            string? why = App.WhyWriteIsBlocked(anibndPath);
            if (why == null) return true;
            DSAnimStudio.ImguiOSD.DialogManager.DialogOK("Save blocked (Souls AI Studio)", why);
            return false;
        }

        /// <summary>DSAS ShowDialogFind (Ctrl+F in the graph, Edit > Find Value): true = our Find Events window opened instead.</summary>
        public static bool OpenEventFinder()
        {
            try { return App.OpenEventFinder(); }
            catch (Exception ex) { App.ReportError("Find Events", ex); return false; }
        }

        /// <summary>Called first in DSAS's FormClosing: true cancels the close (unsaved Lua and the user chose to stay).</summary>
        public static bool CancelCloseForUnsavedLua()
        {
            try { return App.ConfirmExitWithUnsavedLua() == false; }
            catch (Exception) { return false; } // never block exit because of our own bug
        }

        /// <summary>
        /// Replaces DSAS's in-place <c>File.WriteAllBytes</c> when saving an anibnd: refuses writes into the game
        /// folder (outside <c>mods\</c>), backs up the old file, and writes atomically.
        /// </summary>
        // ---- TAE graph: follow playhead while scrubbing (see ScrubFollow) ----
        private static readonly Lazy<ScrubFollow> _scrubFollow = new(() => new ScrubFollow(App.Settings));
        public static ScrubFollow ScrubFollow => _scrubFollow.Value;

        /// <summary>Called from <c>NewGraphInput</c>'s absolute scrub branch: the time to set this frame.</summary>
        public static float FollowScrubTime(DSAnimStudio.TaeEditor.NewGraph graph, DSAnimStudio.FancyInputHandler input, float absoluteTime, bool firstFrame)
        {
            try { return ScrubFollow.AdjustTime(graph, input, absoluteTime, firstFrame); }
            catch (Exception) { return absoluteTime; }
        }

        /// <summary>Called instead of DSAS's edge-zone scrub scroll; true = we scrolled (centered the playhead).</summary>
        public static bool FollowScrubCenter(DSAnimStudio.TaeEditor.NewGraph graph, DSAnimStudio.FancyInputHandler input)
        {
            try { return ScrubFollow.CenterOnPlayhead(graph, input); }
            catch (Exception) { return false; }
        }

        /// <summary>Replaces DSAS's <c>Main.Config.NewRelativeTimelineScrubEnabled</c> checks (click in the ruler, per-frame scroll).</summary>
        public static bool UseRelativeTimelineScrub()
        {
            // "Center Playhead While Scrubbing" ON uses DSAS 5.0's relative scrub: the view re-centers on the playhead
            // and the mouse pointer is moved onto it, exactly the centered drag users know (user request).
            try { return ScrubFollow.UseRelativeScrub || ScrubFollow.Enabled; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Called by DSAS's Parameters panel after each integer field: StateInfo gets an ON/OFF toggle, the SpEffect id of a
        /// SpEffect event a "Lua" button (jump to / add its Goal.Interrupt branch).
        /// </summary>
        public static void ParameterRowExtras(DSAnimStudio.DSAProj.Action? act, string? paramName, int paramIndex, int value)
        {
            try { StateInfoPanel.ParameterToggle(paramName, value); } catch (Exception ex) { App.ReportError("StateInfo toggle", ex); }
            try { if (act != null && SpEffectLuaLink.IsSpEffectField(act.Type, paramIndex)) App.SpEffectLink.Draw(value); }
            catch (Exception ex) { App.ReportError("SpEffect → Lua", ex); }
        }

        /// <summary>Replaces DSAS's "Relative Timeline Scrub - Enabled" checkbox in the Action Graph menu.</summary>
        public static void RelativeScrubMenuItem()
        {
            try { ScrubFollow.RelativeScrubMenuItem(); } catch (Exception ex) { App.ReportError("Menu error", ex); }
        }

        // ---- TAE graph: mouse focus (see GraphClickRouter) ----
        private static readonly GraphClickRouter _graphClicks = new();

        /// <summary>Called from <c>Window.Graph.BuildContents</c>: is the mouse over the graph (not a popup/other window)?</summary>
        public static void ReportGraphHovered(bool hovered) => _graphClicks.ReportGraphHovered(hovered);

        /// <summary>Called first in <c>FancyInputHandler.Update</c>: a fresh click on the graph is not blocked by another panel's focus.</summary>
        public static void BeforeMouseInput(DSAnimStudio.FancyInputHandler input)
        {
            if (!ReferenceEquals(input, DSAnimStudio.Main.Input)) return;
            try { _graphClicks.BeforeMouseInput(); } catch (Exception) { }
        }

        /// <summary>
        /// Called when a move/resize drag on a TAE block is released: true when nothing moved (a plain click, which with
        /// DSAS 4.9.9 selection starts a "Move" drag). DSAS would still record an undo step and mark the anim modified.
        /// </summary>
        public static bool IsNoOpBlockDrag(DSAnimStudio.TaeEditor.NewGraphInput drag) =>
            drag.CurrentDragType is DSAnimStudio.TaeEditor.NewGraphInput.DragTypes.Move
                or DSAnimStudio.TaeEditor.NewGraphInput.DragTypes.ResizeLeft or DSAnimStudio.TaeEditor.NewGraphInput.DragTypes.ResizeRight
            && drag.MoveResizeDragFrameDelta == 0
            && (drag.CurrentDragType != DSAnimStudio.TaeEditor.NewGraphInput.DragTypes.Move || drag.MoveResizeDragRowDelta == 0);

        [System.Runtime.InteropServices.DllImport("cimgui", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern IntPtr igGetCurrentWindow();
        [System.Runtime.InteropServices.DllImport("cimgui", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void igBringWindowToDisplayFront(IntPtr window);

        /// <summary>
        /// Called by DSAS's hover tooltip window (inside its Begin) instead of <c>ImGui.SetWindowFocus()</c>: draws it on
        /// top without taking keyboard focus. Taking focus made ImGui hand focus, once the tooltip closed, to the most
        /// recently focused window (often the Lua editor), which then blocked the graph's mouse input (drag/resize).
        /// </summary>
        public static void BringTooltipToFront()
        {
            try { IntPtr w = igGetCurrentWindow(); if (w != IntPtr.Zero) igBringWindowToDisplayFront(w); }
            catch (Exception) { }
        }

        /// <summary>
        /// Height for DSAS's "Documents" strip window: from its top down to where the main dock space starts. DSAS asked
        /// for 4 px, which ImGui raised to the 32 px minimum window size, so the invisible strip covered the top of the
        /// dock tabs below it (Animations / Graph / Lua Editor tabs only reacted on their lower half).
        /// </summary>
        public static Vector2 DocManToolbarSize(ImGuiViewportPtr viewport) =>
            new(viewport.Size.X, viewport.WorkPos.Y - viewport.Pos.Y + DSAnimStudio.ImguiOSD.OSD.DocManToolbarHeight);

        /// <summary>Pushed around the Documents strip's Begin so <see cref="DocManToolbarSize"/> is not raised to 32 px.</summary>
        public static void PushDocManToolbarStyle() => ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1, 1));

        /// <summary>Called from DSAS's "Action Graph" menu.</summary>
        public static void BuildGraphMenuItems()
        {
            try
            {
                ScrubFollow.MenuItem();
                if (ImGui.MenuItem("Action Text Past Short Blocks", "", App.Settings.GraphTextOverflow))
                {
                    App.Settings.GraphTextOverflow = !App.Settings.GraphTextOverflow;
                    App.Settings.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("On (DSAS 4.9.9): a block's name runs on to the right of a short block, up to the next block on its row,\n" +
                                     "so short blocks stay readable. Off (DSAS 5.0): the text is cut at the block's end.");
                GraphViewMenuItems();
                ImGui.Separator();
                RemoveEmptyRowsMenuItem();
                TaeJsonExportMenuItems();
            }
            catch (Exception ex) { App.ReportError("Menu error", ex); }
        }

        // ---------- DSAS 4.9 clipboard JSON (DsasLegacyClipboard) ----------

        /// <summary>
        /// TAE graph Ctrl+V: if the clipboard holds DSAS 4.9's JSON (copied in DSAS 4.9, or exported here), fills
        /// <paramref name="into"/> from it and returns true; otherwise false (DSAS reads its own hex clipboard).
        /// </summary>
        public static bool PasteLegacyClipboard(string? text, DSAnimStudio.DSAProj proj, DSAnimStudio.DSAProj.ClipboardContents into)
        {
            try
            {
                if (!DsasLegacyClipboard.TryRead(text, proj, out var actions, out var warnings)) return false;
                into.Actions = actions;
                if (warnings.Count > 0)
                    App.Notify($"Pasted {actions.Count} action(s) from DSAS 4.9 JSON; check these:\n  " + string.Join("\n  ", warnings.Take(8)) +
                               (warnings.Count > 8 ? $"\n  ... and {warnings.Count - 8} more" : ""));
                else App.Notify($"Pasted {actions.Count} action(s) from DSAS 4.9 JSON.");
                return true;
            }
            catch (Exception ex) { App.ReportError("Pasting DSAS 4.9 JSON", ex); return false; }
        }

        // ---------- animation name (F2) ----------

        /// <summary>
        /// F2 / Edit &gt; Edit Current Animation Name: renames the animation's Anim File Name (the name stored in the TAE,
        /// shown in the list), as DSAS 4.9 did, instead of DSAS 5's custom display name. A custom name the animation still
        /// has is cleared so the list shows the new file name. One undo step. Always returns true (DSAS's dialog skipped).
        /// </summary>
        public static bool RenameAnimFileName(DSAnimStudio.TaeEditor.TaeEditorScreen tae, DSAnimStudio.DSAProj proj, DSAnimStudio.DSAProj.Animation? anim)
        {
            try
            {
                if (anim == null || anim.IS_DUMMY_ANIM) return true;
                var header = anim.SAFE_GetHeaderClone();
                if (header == null) { App.Notify("This animation has no header to rename."); return true; }
                string id = anim.SplitID.GetFormattedIDString(proj);
                string current = header.AnimFileName ?? "";
                DSAnimStudio.ImguiOSD.DialogManager.AskForInputString("Rename Animation", $"Anim File Name of {id}:", "", result =>
                {
                    string? name = string.IsNullOrWhiteSpace(result) ? null : result.Trim();
                    if ((name ?? "") == current && string.IsNullOrEmpty(anim.Info.DisplayName)) return;
                    tae.GetUndoManForSpecificAnim(anim).NewAction(() =>
                    {
                        var h = anim.SAFE_GetHeaderClone();
                        h.AnimFileName = name;
                        anim.SAFE_SetHeader(h);
                        anim.Info.DisplayName = null;
                    }, actionDescription: $"Rename animation {id}");
                    anim.SAFE_SetIsModified(true);
                    tae.RecreateAnimList();
                    tae.UpdateSelectedTaeAnimInfoText();
                }, checkError: null, canBeCancelled: true, startingText: current);
            }
            catch (Exception ex) { App.ReportError("Rename animation", ex); }
            return true;
        }

        // ---------- TAE graph: vertical zoom, selection focus ----------

        public const float GraphRowHeightMin = 12f, GraphRowHeightMax = 48f, GraphRowHeightDefault = 16f;

        /// <summary>Row height the graph uses (read every frame by its layout).</summary>
        public static float GraphRowHeight => Math.Clamp(App.Settings.GraphRowHeight, GraphRowHeightMin, GraphRowHeightMax);

        /// <summary>Font size for graph texts drawn at <paramref name="baseSize"/> with DSAS's 16 px rows.</summary>
        public static float GraphTextSize(float baseSize) => baseSize * GraphRowHeight / GraphRowHeightDefault;

        /// <summary>Ctrl+wheel over the graph: with Shift, changes the row height (true = handled; DSAS zooms time otherwise).</summary>
        public static bool GraphRowZoomWheel(bool shift, float wheelDelta)
        {
            if (!shift) return false;
            if (wheelDelta != 0) SetGraphRowHeight(GraphRowHeight * MathF.Pow(1.12f, Math.Sign(wheelDelta)));
            return true;
        }

        private static DateTime _rowHeightSaveAt = DateTime.MaxValue;

        public static void SetGraphRowHeight(float h)
        {
            h = MathF.Round(Math.Clamp(h, GraphRowHeightMin, GraphRowHeightMax));
            if (h == App.Settings.GraphRowHeight) return;
            App.Settings.GraphRowHeight = h;
            _rowHeightSaveAt = DateTime.UtcNow.AddSeconds(1); // saved once the wheel stops (SaveGraphRowHeightIfDue)
        }

        private static void SaveGraphRowHeightIfDue()
        {
            if (DateTime.UtcNow < _rowHeightSaveAt) return;
            _rowHeightSaveAt = DateTime.MaxValue;
            App.Settings.Save();
        }

        private static bool SelectionFocus(List<DSAnimStudio.DSAProj.Action> selected) =>
            App.Settings.GraphDimUnselected && selected.Count > 0;

        /// <summary>Drawing a block: while some actions are selected, the others are darkened (the selection stands out).</summary>
        public static void DimIfNotSelected(DSAnimStudio.DSAProj.Action act, List<DSAnimStudio.DSAProj.Action> selected, DSAnimStudio.DSAProj.Action? inspector,
            ref Microsoft.Xna.Framework.Color fill, ref Microsoft.Xna.Framework.Color line, ref Microsoft.Xna.Framework.Color text)
        {
            SaveGraphRowHeightIfDue();
            if (!SelectionFocus(selected) || act == inspector || selected.Contains(act)) return;
            fill = Dim(fill, 0.4f);
            line = Dim(line, 0.4f);
            text = Dim(text, 0.5f);
        }

        /// <summary>Over the rows' background, under the blocks: darker while actions are selected.</summary>
        public static void DimGraphBackground(List<DSAnimStudio.DSAProj.Action> selected, float width, float height)
        {
            if (!SelectionFocus(selected)) return;
            DSAnimStudio.ImGuiDebugDrawer.DrawRect(Microsoft.Xna.Framework.Vector2.Zero, new Microsoft.Xna.Framework.Vector2(width, height),
                Microsoft.Xna.Framework.Color.Black * 0.35f);
        }

        private static Microsoft.Xna.Framework.Color Dim(Microsoft.Xna.Framework.Color c, float k) =>
            new((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k), c.A);

        private static void GraphViewMenuItems()
        {
            if (ImGui.MenuItem("Darken Unselected Actions", "", App.Settings.GraphDimUnselected))
            {
                App.Settings.GraphDimUnselected = !App.Settings.GraphDimUnselected;
                App.Settings.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("While one or more blocks are selected, the other blocks and the background are darkened.");
            float h = GraphRowHeight;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Row Height (Ctrl+Shift+Wheel)", ref h, GraphRowHeightMin, GraphRowHeightMax, "%.0f px"))
                SetGraphRowHeight(h);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Vertical zoom of the action graph: taller rows and bigger text.\n" +
                                 "Ctrl+Shift+mouse wheel over the graph does the same (Ctrl+wheel zooms time). Right-click: reset to 16.");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) SetGraphRowHeight(GraphRowHeightDefault);
        }

        /// <summary>DSAS opening an anibnd that has a .dsaproj: true = build the TAE from the anibnd (our Reload), see DsasBridge.</summary>
        public static bool ReadTaeFromAnibndInstead(string dsaprojPath)
        {
            try { return App.Bridge.ShouldReadTaeFromAnibnd(dsaprojPath); }
            catch (Exception) { return false; }
        }

        /// <summary>After DSAS built the TAE from the anibnd: anim names from the .dsaproj (on our Reload only).</summary>
        public static void AfterTaeFromAnibnd(DSAnimStudio.DSAProj proj, string dsaprojPath, SoulsAssetPipeline.Animation.TAE.Template template, DSAnimStudio.zzz_DocumentIns doc)
        {
            try { App.Bridge.AfterTaeFromAnibnd(proj, dsaprojPath, template, doc); }
            catch (Exception ex) { App.ReportError("Reload: animation names", ex); }
        }

        /// <summary>
        /// File menu toggles stored in DSAS's config (game file caching, config autosaving): DSAS only wrote the config when
        /// autosaving was on, so turning autosaving off (or changing caching with it off) was lost at the next start. A change
        /// now writes the config once, on the next frame (after the new value is assigned).
        /// </summary>
        public static bool SaveConfigIfChanged(bool oldValue, bool newValue)
        {
            if (oldValue != newValue)
                DSAnimStudio.Main.MainThreadLazyDispatch(() =>
                {
                    try { DSAnimStudio.Main.SaveConfig(isManual: true); }
                    catch (Exception ex) { App.ReportError("Saving the config", ex); }
                });
            return newValue;
        }

        /// <summary>TAE graph paste (inside its undo step): names the rows DSAS just added and drops rows left empty.</summary>
        public static void AfterPasteActions(DSAnimStudio.DSAProj.Animation? anim, DSAnimStudio.DSAProj? proj)
        {
            if (anim == null) return;
            try
            {
                TaeTrackTools.RemoveEmptyTracks(anim);
                TaeTrackTools.NameUnnamedTracks(anim, proj);
            }
            catch (Exception ex) { App.ReportError("After paste", ex); }
        }

        private static void RemoveEmptyRowsMenuItem()
        {
            var tae = DSAnimStudio.zzz_DocumentManager.CurrentDocument?.EditorScreen;
            var anim = tae?.SelectedAnim;
            bool has = anim != null && tae?.CurrentAnimUndoMan != null;
            if (ImGui.MenuItem("Remove Empty Rows (Current Animation)", "", false, has) && has)
            {
                int removed = 0;
                tae!.CurrentAnimUndoMan.NewAction(() =>
                {
                    removed = TaeTrackTools.RemoveEmptyTracks(anim!);
                    TaeTrackTools.NameUnnamedTracks(anim!, tae.FileContainer?.Proj);
                }, () => { }, "Remove empty rows");
                if (removed > 0) anim!.SAFE_SetIsModified(true);
                App.Notify(removed > 0 ? $"Removed {removed} empty row(s)." : "No empty row in this animation.");
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Deletes the rows of the selected animation that hold no action (undo: Ctrl+Z).\nPasting actions also does this.");
        }

        private static void TaeJsonExportMenuItems()
        {
            var tae = DSAnimStudio.zzz_DocumentManager.CurrentDocument?.EditorScreen;
            var anim = tae?.SelectedAnim;
            bool has = tae?.FileContainer?.Proj != null && anim != null;
            if (ImGui.MenuItem("Export Current TAE as JSON (DSAS 4.9) to Clipboard", "", false, has) && has)
            {
                var actions = anim!.SAFE_GetActions();
                System.Windows.Forms.Clipboard.SetText(DsasLegacyClipboard.Write(actions));
                App.Notify($"Copied the {actions.Count} action(s) of {anim.SplitID.GetFormattedIDString(tae!.FileContainer.Proj)} as DSAS 4.9 JSON: paste in DSAS 4.9 with Ctrl+V.");
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Every action of the selected animation, in DS Anim Studio 4.9's clipboard format.\n" +
                                 "Paste it in DSAS 4.9 with Ctrl+V. (Ctrl+V here also accepts what DSAS 4.9 copies.)");
            if (ImGui.MenuItem("Export Current TAE as JSON (DSAS 4.9) to File...", "", false, has) && has)
            {
                string id = anim!.SplitID.GetFormattedIDString(tae!.FileContainer.Proj);
                using var dlg = new System.Windows.Forms.SaveFileDialog
                {
                    Title = "Export current TAE as JSON (DSAS 4.9 clipboard format)",
                    Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                    FileName = id + ".tae.json",
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    try
                    {
                        var actions = anim.SAFE_GetActions();
                        File.WriteAllText(dlg.FileName, DsasLegacyClipboard.Write(actions));
                        App.Notify($"Exported the {actions.Count} action(s) of {id} to {Path.GetFileName(dlg.FileName)}.");
                    }
                    catch (Exception ex) { App.ReportError("Exporting the TAE as JSON", ex); }
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Same JSON, saved to a file (open it, copy all, paste in DSAS 4.9).");
        }

        private static object? _textRowsFor;
        private static readonly Dictionary<int, List<int>> _textRowStarts = new();

        /// <summary>
        /// TAE graph, drawing a block's text: right edge (graph pixels) of the area its text may use. DSAS 5.0 clipped it to
        /// the block, so a short block only showed "Spaw…". Like DSAS 4.9.9 the text now runs on up to the start of the next
        /// block on the same row (never over another block). Starts per row are gathered once per frame (per action list).
        /// </summary>
        public static int ActionTextRight(DSAnimStudio.DSAProj.Action act, int blockRight, List<DSAnimStudio.DSAProj.Action> all,
            Func<DSAnimStudio.DSAProj.Action, int> rowOf, float secondsPixelSize)
        {
            try
            {
                if (!App.Settings.GraphTextOverflow) return blockRight;
                if (!ReferenceEquals(_textRowsFor, all))
                {
                    _textRowsFor = all;
                    _textRowStarts.Clear();
                    foreach (var a in all)
                    {
                        int r = rowOf(a);
                        if (!_textRowStarts.TryGetValue(r, out var list)) _textRowStarts[r] = list = new List<int>();
                        list.Add((int)(a.StartTime * secondsPixelSize));
                    }
                    foreach (var list in _textRowStarts.Values) list.Sort();
                }
                if (!_textRowStarts.TryGetValue(rowOf(act), out var starts)) return blockRight;
                int start = (int)(act.StartTime * secondsPixelSize);
                // First block starting after this one (blocks starting inside it keep the old clip at its end).
                foreach (int s in starts)
                    if (s > start) return Math.Max(blockRight, s - 2);
                return int.MaxValue / 4; // last block on its row: up to the visible edge (the caller clamps)
            }
            catch (Exception) { return blockRight; }
        }

        // ---- Entity window: redesigned StateInfo Toggle section ----
        private static readonly Lazy<StateInfoPanel> _stateInfoPanel = new(() => new StateInfoPanel());

        /// <summary>Called from <c>Window.Entity</c>; draws our StateInfo section and returns true (DSAS's is skipped).</summary>
        public static bool DrawStateInfoPanel(DSAnimStudio.ImguiOSD.Window.Entity entity, DSAnimStudio.TaeEditor.TaeEditorScreen? tae, ref bool anyFieldFocused)
        {
            try { return _stateInfoPanel.Value.Draw(entity, tae, ref anyFieldFocused); }
            catch (Exception ex) { App.ReportError("StateInfo panel", ex); return false; } // fall back to DSAS's section
        }

        public static void WriteBinder(string path, byte[] bytes)
        {
            string? why = App.WhyWriteIsBlocked(path);
            if (why != null)
            {
                DSAnimStudio.ImguiOSD.DialogManager.DialogOK("Save blocked (Souls AI Studio)", why);
                throw new IOException(why);
            }
            App.BackupBeforeExternalWrite(path);
            using var own = SoulsAiStudio.Core.IO.OwnWriteTrackerExtensions.Track(App.FileWatch, path); // our save: not an external change
            SoulsAiStudio.Core.IO.AtomicFile.WriteAllBytes(path, bytes);
        }
    }
}
