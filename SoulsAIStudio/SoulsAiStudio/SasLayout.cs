#nullable enable
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Default docking of our panels into DSAS's dockspace (user request): Scripts below "Animations",
    /// Lua Editor as a tab next to "Graph" or beside it (<see cref="SasSettings.EditorBesideGraph"/>). ImGui.NET 1.87 doesn't
    /// bind DockBuilder, but DSAS's cimgui.dll (native 1.88 docking) exports it, so we P/Invoke the functions we need.
    /// <para>
    /// Also keeps DSAS's custom-drawn TAE graph inside its own window. The graph is not ImGui: it is drawn into a full-screen
    /// overlay window (<c>ImGuiDebugDrawer</c>, in front of every docked window) clipped to the Graph window's rect, and reads
    /// MonoGame input inside that rect. So it must be skipped when the Graph tab is hidden (another tab of its node is in
    /// front) or one of our windows is under the mouse, and no window may ever be docked into the Graph window's own inner
    /// dockspace (see <see cref="SasHooks.GraphDockSpaceFlags"/>); a window already there (old imgui.ini) is moved out.
    /// </para>
    /// </summary>
    public sealed class SasLayout
    {
        public const int CurrentVersion = 4;
        /// <summary>Left panel (AI scripts, characters, goal outline). Was "Scripts##sas_scripts" before layout v4.</summary>
        public const string ScriptsWindow = "Project###sas_project";
        private const string LegacyScriptsWindow = "Scripts##sas_scripts";
        public const string EditorWindow = "Lua Editor##sas_editor";

        private readonly Dictionary<string, uint> _dsasDockIds = new();
        private bool _requested;
        private bool? _placeEditorRequest; // true = beside Graph, false = tab in Graph's node
        private bool _startCheckDone;

        // Graph window state, reported by DSAS's Window.Update in the current frame.
        private bool _graphVisible;
        private int _graphVisibleFrame = -1;

        // Our windows, probed after they were built (see ProbeWindows).
        private uint _editorDockId;
        private bool _ourWindowHovered;
        private readonly List<string> _nestedInGraph = new();
        private int _cooldownFrames;
        private int _nestedFrames;
        private const int NestedFramesBeforeRecovery = 90;

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint igDockBuilderSplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatioForNodeAtDir, out uint outIdAtDir, out uint outIdAtOppositeDir);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderDockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderFinish(uint nodeId);

        /// <summary>Called by DSAS windows every frame (via <see cref="SasHooks.ReportWindowDock"/>).</summary>
        public void ReportDock(string title, uint dockId) => _dsasDockIds[title] = dockId;

        /// <summary>DSAS's Graph window called Begin this frame; <paramref name="visible"/> = Begin's result (false: hidden tab).</summary>
        public void ReportGraphBegin(bool visible)
        {
            _graphVisible = visible;
            _graphVisibleFrame = ImGui.GetFrameCount();
        }

        public void RequestApply() => _requested = true;

        /// <summary>A saved workspace replaced the whole dock layout: keep it as it is (no start-up re-docking of the editor).</summary>
        public void WorkspaceLoaded()
        {
            _startCheckDone = true;
            _placeEditorRequest = null;
            _requested = false;
            _nestedFrames = 0;
            _cooldownFrames = 30;
            FocusEditorTabFrames = 0;
        }

        /// <summary>Re-docks the Lua Editor soon: beside Graph (50/50, Graph left) or as a tab in Graph's node.</summary>
        public void RequestEditorPlacement(bool besideGraph) => _placeEditorRequest = besideGraph;

        /// <summary>Dock node DSAS's "Graph" window lives in (0 = unknown/floating).</summary>
        public uint GraphDockId => _dsasDockIds.TryGetValue("Graph", out uint id) ? id : 0;

        /// <summary>The Graph window is the visible tab of its node this frame (DSAS submits it every frame).</summary>
        public bool GraphVisible => _graphVisibleFrame == ImGui.GetFrameCount() && _graphVisible;

        /// <summary>One of our windows is under the mouse (ImGui hover state): the graph must not take the mouse.</summary>
        public bool OurWindowHovered => _ourWindowHovered;

        /// <summary>One of our windows sits inside the Graph window's area (docked into its inner dockspace): hide the graph.</summary>
        public bool WindowNestedInGraph => _nestedInGraph.Count > 0;

        /// <summary>The Lua Editor is docked in a node other than Graph's (beside it, or elsewhere): no need to bring it to front.</summary>
        public bool EditorOutsideGraphNode => _editorDockId != 0 && GraphDockId != 0 && _editorDockId != GraphDockId;

        /// <summary>
        /// Applies the layout once per <see cref="CurrentVersion"/> (or when requested from the menu), the Lua Editor
        /// placement, and the recovery of windows nested in the graph. Must run after DSAS's windows (so their dock ids are
        /// known) and before our windows call Begin.
        /// </summary>
        public void ApplyIfNeeded(SasSettings settings)
        {
            if (_cooldownFrames > 0) _cooldownFrames--;
            if (!_dsasDockIds.TryGetValue("Graph", out uint graphNode) || graphNode == 0) return;

            // Recover from a window that ended up inside the graph (dropped into its inner dockspace before the fix, then
            // saved in imgui.ini): undock it (floating) so it is usable again. Only when that state has been seen for a
            // while (window rects are not reliable on the first frames or right after a docking change), and without
            // DockBuilderFinish (a node can disappear when a window leaves it; finishing it crashed natively).
            _nestedFrames = _nestedInGraph.Count > 0 && !IsDraggingWindow() ? _nestedFrames + 1 : 0;
            if (_nestedFrames >= NestedFramesBeforeRecovery && ImGui.GetFrameCount() > 300 && _cooldownFrames == 0)
            {
                foreach (string name in _nestedInGraph) igDockBuilderDockWindow(name, 0);
                _nestedInGraph.Clear();
                _nestedFrames = 0;
                _cooldownFrames = 30;
                return;
            }

            if (_placeEditorRequest is bool beside && _cooldownFrames == 0)
            {
                // Two steps: first undock the editor (floating). Leaving its node can make ImGui merge that split and
                // change/delete node ids (Graph's included), so it is placed a few frames later, with Graph's fresh id.
                if (_editorDockId != 0)
                {
                    igDockBuilderDockWindow(EditorWindow, 0);
                    _cooldownFrames = 5;
                    return;
                }
                _placeEditorRequest = null;
                _startCheckDone = true;
                PlaceEditor(beside, graphNode);
                _cooldownFrames = 10;
                FocusEditorTabFrames = beside ? 0 : 30;
                return;
            }

            // On start: "side by side" chosen but imgui.ini has the editor as a tab in Graph's node → split it.
            if (!_startCheckDone && settings.LayoutVersion >= CurrentVersion && _editorDockId != 0 && _cooldownFrames == 0)
            {
                _startCheckDone = true;
                if (settings.EditorBesideGraph && _editorDockId == graphNode)
                {
                    _placeEditorRequest = true;
                    return;
                }
            }

            if (!_requested && settings.LayoutVersion >= CurrentVersion) return;
            if (!_dsasDockIds.TryGetValue("Animations", out uint animNode) || animNode == 0) return;
            _dsasDockIds.TryGetValue("Parameters", out uint paramsNode);

            if (!_requested && settings.LayoutVersion is >= 1 and < 4)
            {
                // v4: the "Scripts" panel became "Project" (new window id). Put it where the old one was docked: submit the
                // old window once (hidden right after: it is never submitted again) to read its dock node.
                ImGui.SetNextWindowBgAlpha(0);
                bool shown = ImGui.Begin(LegacyScriptsWindow, ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
                uint oldDock = ImGui.GetWindowDockID();
                ImGui.End();
                _ = shown;
                if (oldDock != 0) igDockBuilderDockWindow(ScriptsWindow, oldDock);
                else
                {
                    igDockBuilderSplitNode(animNode, ImGuiDir.Down, 0.55f, out uint bottom, out uint _);
                    igDockBuilderDockWindow(ScriptsWindow, bottom);
                    igDockBuilderFinish(animNode);
                }
                settings.LayoutVersion = CurrentVersion;
                settings.Save();
                return;
            }
            if (_requested || settings.LayoutVersion < 1)
            {
                // v1: split the Animations node (existing windows stay on top, Scripts gets the bottom 55%),
                // Lua Editor as a tab next to Graph or beside it (setting).
                igDockBuilderSplitNode(animNode, ImGuiDir.Down, 0.55f, out uint bottom, out uint _);
                igDockBuilderDockWindow(ScriptsWindow, bottom);
                igDockBuilderFinish(animNode);
                _placeEditorRequest = settings.EditorBesideGraph; // placed by the two-step path above on later frames
            }
            // v3: Combo player in its own node right of Parameters / Entity / Log, so selecting a TAE event (DSAS focuses
            // Parameters) never hides it.
            if (paramsNode != 0)
            {
                igDockBuilderSplitNode(paramsNode, ImGuiDir.Right, 0.5f, out uint comboNode, out uint _);
                igDockBuilderDockWindow(ComboPanel.WindowName, comboNode);
                igDockBuilderFinish(paramsNode);
            }

            settings.LayoutVersion = CurrentVersion;
            settings.Save();
            _requested = false;
            _startCheckDone = true;
            _cooldownFrames = 10;
            FocusEditorTabFrames = settings.EditorBesideGraph ? 0 : 30;
        }

        /// <summary>
        /// Beside: split Graph's node 50/50 (Graph and any other tabs of that node stay left, the editor goes right).
        /// Tab: dock the editor into Graph's node (its old node, now empty, is removed by ImGui and its space merged back).
        /// No DockBuilderFinish in the tab case: when the editor leaves its sibling node, ImGui merges the split and Graph's
        /// node id may no longer exist; finishing it dereferenced a null node (native crash). The window's dock id is set,
        /// so ImGui docks it on its next Begin anyway.
        /// </summary>
        private static void PlaceEditor(bool besideGraph, uint graphNode)
        {
            if (besideGraph)
            {
                igDockBuilderSplitNode(graphNode, ImGuiDir.Right, 0.5f, out uint right, out uint _);
                igDockBuilderDockWindow(EditorWindow, right);
                igDockBuilderFinish(graphNode); // graphNode is now the parent of the split: still exists
            }
            else igDockBuilderDockWindow(EditorWindow, graphNode);
        }

        /// <summary>A window (any panel) is being dragged for docking (ImGui's internal "_IMWINDOW" drag-drop payload).</summary>
        public static unsafe bool IsDraggingWindow()
        {
            var payload = ImGui.GetDragDropPayload();
            return payload.NativePtr != null && payload.IsDataType("_IMWINDOW");
        }

        /// <summary>
        /// Call after our windows were built this frame, with each of our panels and whether it is open. Re-enters each open
        /// window (ImGui appends to a window already begun this frame; nothing is drawn) to read its hover state, dock node
        /// and rect.
        /// </summary>
        public void ProbeWindows(IEnumerable<(string Name, bool Open)> windows)
        {
            _ourWindowHovered = false;
            _editorDockId = 0;
            _nestedInGraph.Clear();
            uint graphNode = GraphDockId;
            var g = DSAnimStudio.ImguiOSD.OSD.SpWindowGraph;
            Vector2 gMin = g.LastPosition, gMax = g.LastPosition + g.LastSize;
            bool dragging = IsDraggingWindow();
            foreach (var (name, open) in windows)
            {
                if (!open) continue;
                ImGui.Begin(name);
                try
                {
                    if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                        _ourWindowHovered = true;
                    uint dock = ImGui.IsWindowDocked() ? ImGui.GetWindowDockID() : 0;
                    if (name == EditorWindow) _editorDockId = dock;
                    // Docked in a node that is not Graph's but lies inside the Graph window = inside its inner dockspace.
                    if (dock != 0 && graphNode != 0 && dock != graphNode && GraphVisible && !dragging)
                    {
                        Vector2 center = ImGui.GetWindowPos() + ImGui.GetWindowSize() * 0.5f;
                        if (center.X > gMin.X && center.X < gMax.X && center.Y > gMin.Y && center.Y < gMax.Y)
                            _nestedInGraph.Add(name);
                    }
                }
                finally { ImGui.End(); }
            }
        }

        /// <summary>
        /// Frames left during which the Lua Editor tab is brought to front (it shares the node with Graph, and DSAS
        /// requests focus for Graph a few frames after loading a character).
        /// </summary>
        public int FocusEditorTabFrames;
    }
}
