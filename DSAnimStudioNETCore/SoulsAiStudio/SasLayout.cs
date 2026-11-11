#nullable enable
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Default docking of our panels into DSAS's dockspace (user request): Scripts below "Animations",
    /// Lua Editor as a tab next to "Graph". ImGui.NET 1.87 doesn't bind DockBuilder, but DSAS's cimgui.dll
    /// (native 1.88 docking) exports it, so we P/Invoke the three functions we need.
    /// </summary>
    public sealed class SasLayout
    {
        public const int CurrentVersion = 1;
        public const string ScriptsWindow = "Scripts##sas_scripts";
        public const string EditorWindow = "Lua Editor##sas_editor";

        private readonly Dictionary<string, uint> _dsasDockIds = new();
        private bool _requested;

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint igDockBuilderSplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatioForNodeAtDir, out uint outIdAtDir, out uint outIdAtOppositeDir);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderDockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderFinish(uint nodeId);

        /// <summary>Called by DSAS windows every frame (via <see cref="SasHooks.ReportWindowDock"/>).</summary>
        public void ReportDock(string title, uint dockId) => _dsasDockIds[title] = dockId;

        public void RequestApply() => _requested = true;

        /// <summary>Dock node DSAS's "Graph" window lives in (0 = unknown/floating).</summary>
        public uint GraphDockId => _dsasDockIds.TryGetValue("Graph", out uint id) ? id : 0;

        /// <summary>
        /// Applies the layout once per <see cref="CurrentVersion"/> (or when requested from the menu). Must run after DSAS's
        /// windows (so their dock ids are known) and before our windows call Begin.
        /// </summary>
        public void ApplyIfNeeded(SasSettings settings)
        {
            if (!_requested && settings.LayoutVersion >= CurrentVersion) return;
            if (!_dsasDockIds.TryGetValue("Animations", out uint animNode) || animNode == 0) return;
            if (!_dsasDockIds.TryGetValue("Graph", out uint graphNode) || graphNode == 0) return;

            // Split the Animations node: existing windows stay on top, Scripts gets the bottom 55%.
            igDockBuilderSplitNode(animNode, ImGuiDir.Down, 0.55f, out uint bottom, out uint _);
            igDockBuilderDockWindow(ScriptsWindow, bottom);
            igDockBuilderDockWindow(EditorWindow, graphNode);
            igDockBuilderFinish(animNode);
            igDockBuilderFinish(graphNode);

            settings.LayoutVersion = CurrentVersion;
            settings.Save();
            _requested = false;
            FocusEditorTabFrames = 30;
        }

        /// <summary>
        /// Frames left during which the Lua Editor tab is brought to front (it shares the node with Graph, and DSAS
        /// requests focus for Graph a few frames after loading a character).
        /// </summary>
        public int FocusEditorTabFrames;
    }
}
