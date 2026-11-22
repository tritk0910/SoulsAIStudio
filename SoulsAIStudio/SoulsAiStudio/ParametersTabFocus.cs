#nullable enable
using DSAnimStudio.ImguiOSD;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Shows the Parameters tab (not Log, its tab neighbour) after launch / character load, and when the user selects one
    /// or more blocks in the TAE graph. The tab is brought to the front of its dock node without keeping the keyboard:
    /// focus is dropped right away (the graph reads its keys through MonoGame), so graph shortcuts (Delete, Ctrl+C, ...) keep working.
    /// </summary>
    internal sealed class ParametersTabFocus
    {
        /// <summary>Log's Begin returned true this frame (it is the visible tab of its node). Set by SasHooks.</summary>
        public static bool LogVisible;

        private object? _doc;
        private int _startupFrames; // while > 0, Log brought up by load notifications is sent back behind Parameters
        private int _selCount;
        private object? _selFirst;
        private bool _pressInGraph, _pending;
        private int _afterRelease;

        /// <summary>Call once per frame, after DSAS built its windows (graph focus state is current).</summary>
        public void Tick(SasLayout layout)
        {
            var doc = DSAnimStudio.zzz_DocumentManager.CurrentDocument;
            if (!ReferenceEquals(doc, _doc)) { _doc = doc; _startupFrames = 1200; } // launch / new character
            var screen = doc?.EditorScreen;
            if (screen == null) return;

            if (_startupFrames > 0)
            {
                // Ends at the user's first click: from then on, which tab is in front is theirs to choose.
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _startupFrames = 0;
                else
                {
                    _startupFrames--;
                    // Not while the user works in another panel (e.g. typing in the Lua editor during a character reload).
                    bool other = ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow) && !OSD.SpWindowGraph.Focused
                        && !OSD.SpWindowNotifications.Focused && !OSD.SpWindowParameters.Focused;
                    if (LogVisible && !OSD.SpWindowNotifications.Focused && !other) Show();
                }
            }

            // A press that starts on the graph (click or box select) owns the selection changes until a few frames after
            // its release: DSAS applies a box selection on release, sometimes a frame later, and a fast drag ends with the
            // mouse anywhere (outside the graph, over another panel).
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _pressInGraph = MouseOnGraph(layout);
            bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && _pressInGraph) { _afterRelease = 4; _pending = true; }
            bool graphOwns = _pressInGraph && (down || _afterRelease > 0);

            var sel = screen.NewSelectedActions;
            int count = sel?.Count ?? 0;
            object? first = count > 0 ? sel![0] : null;
            bool changed = count != _selCount || !ReferenceEquals(first, _selFirst);
            _selCount = count;
            _selFirst = first;
            // Selection made in the graph (mouse, or keys like Ctrl+A with the mouse on it). Selecting from elsewhere (Lua
            // editor links, finders) keeps the tabs as they are.
            if (changed && count > 0 && (graphOwns || MouseOnGraph(layout))) _pending = true;

            // Act once the button is up: focusing during the drag does nothing useful and the release frame may still
            // change the selection.
            if (_pending && !down)
            {
                if (count > 0 && (_afterRelease > 0 || changed)) Show();
                if (_afterRelease <= 1) _pending = false;
            }
            if (!down && _afterRelease > 0 && --_afterRelease == 0) _pressInGraph = false;
        }

        /// <summary>
        /// The mouse is on the graph's timeline. Its blocks are drawn and clicked through MonoGame, so ImGui never reports
        /// the Graph window as hovered or focused: test its rect. Not when Graph is a hidden tab (e.g. Lua Editor in front
        /// in the same node), not on the dock tab bar / title, and not over our panels (Lua editor docked inside the graph).
        /// Any ImGui window cannot be the test: DSAS's block tooltip is one, always under the mouse.
        /// </summary>
        private static bool MouseOnGraph(SasLayout layout)
        {
            var g = OSD.SpWindowGraph;
            if (!g.IsOpen || !layout.GraphVisible || layout.OurWindowHovered) return false;
            var m = ImGui.GetMousePos();
            float top = g.LastPosition.Y + ImGui.GetFrameHeight(); // tab bar / title bar
            return m.X >= g.LastPosition.X && m.Y >= top
                && m.X < g.LastPosition.X + g.LastSize.X && m.Y < g.LastPosition.Y + g.LastSize.Y;
        }

        /// <summary>
        /// Brings Parameters to the front of its dock node, then drops the keyboard focus again. Not given back to Graph:
        /// the graph reads its input through MonoGame (it is never ImGui-focused), and focusing it would also pull its tab
        /// in front of the Lua Editor when both share a node.
        /// </summary>
        private static void Show()
        {
            if (!OSD.SpWindowParameters.IsOpen) return;
            ImGui.SetWindowFocus(OSD.SpWindowParameters.ImguiTag); // also selects its tab in the dock node
            ImGui.SetWindowFocus(null);
        }
    }
}
