#nullable enable
using DSAnimStudio;
using DSAnimStudio.ImguiOSD;
using DSAnimStudio.TaeEditor;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Lets a click on the TAE graph work at once, like DSAS 4.9.9, even while another panel has keyboard focus.
    /// <para>
    /// DSAS ignores the mouse until every button is released whenever <see cref="OSD.AnyFieldFocused"/> was true in the
    /// previous ImGui frame (<c>FancyInputHandler.UpdateMouse</c>), and skips the graph update entirely while it is true.
    /// Our panels (Lua editor, Scripts, Combo) set it while they have keyboard focus, so the first click on the graph
    /// only moved ImGui's focus and was swallowed; the user had to click a second time to select/drag a block.
    /// </para>
    /// <para>
    /// Fix: when a mouse button goes down while ImGui reported (last frame) that the mouse is over the graph itself, not
    /// over a popup, menu or another window, the click belongs to the graph: clear the stale flag before DSAS reads it.
    /// ImGui moves its focus to the graph in the same frame, so the flag stays false afterwards (keys then go to the
    /// graph, as after any click on it). Keyboard input is unchanged while the mouse is merely resting over the graph.
    /// </para>
    /// </summary>
    internal sealed class GraphClickRouter
    {
        private bool _graphHovered;
        private bool _buttonsWereDown;
        private bool _clickOnGraph;

        /// <summary>Called by the DSAS Graph window every frame (inside its Begin).</summary>
        public void ReportGraphHovered(bool hovered) => _graphHovered = hovered;

        /// <summary>Called at the start of each <c>Main.Input.Update</c>, before DSAS evaluates the focus flag.</summary>
        public void BeforeMouseInput()
        {
            var ms = Mouse.GetState();
            bool down = ms.LeftButton == ButtonState.Pressed || ms.RightButton == ButtonState.Pressed || ms.MiddleButton == ButtonState.Pressed;
            if (!down) _clickOnGraph = false;
            else if (!_buttonsWereDown && (_clickOnGraph = IsOverGraph(ms)))
                ClearStaleDrag(new Vector2(ms.X / Main.DPI, ms.Y / Main.DPI));
            _buttonsWereDown = down;
            // ImGui moves its focus at the end of the click's frame, so the panel still counts as focused for that one
            // ImGui frame; DSAS would then drop the held button. Keep the flag clear for the whole press.
            if (_clickOnGraph) OSD.AnyFieldFocused = false;
        }

        /// <summary>
        /// DSAS skips the graph update while a panel has focus, so the graph's mouse state is from before that:
        /// <list type="bullet">
        /// <item>a press that started on another panel left it in "InteractingWithOtherPanels" (reset on release, which
        /// it never saw). No button was down before this press, so that state is stale and would swallow this click.</item>
        /// <item>its hover point (where a block drag/resize is looked up on mouse down) is where the mouse last was over
        /// the graph, so the press would start a selection rectangle instead of moving/resizing the block under it.
        /// Same math as <c>NewGraphInput.SetMouseHoverPosToMousePoint</c> (private).</item>
        /// </list>
        /// </summary>
        private static void ClearStaleDrag(Vector2 mouse)
        {
            var graph = Main.TAE_EDITOR?.Graph;
            var input = graph?.InputMan;
            if (graph == null || input == null) return;
            if (input.CurrentDragType is NewGraphInput.DragTypes.InteractingWithOtherPanels or NewGraphInput.DragTypes.CancelledDragStillHoldingMouse)
                input.ResetAllDrag();
            var r = graph.WholeScrollViewerRect;
            var absolute = graph.ScrollViewer.RoundedScroll + mouse - new Vector2(r.X, r.Y);
            input.CurrentMouseHoverPoint = absolute / new Vector2(graph.LayoutManager.SecondsPixelSize, graph.LayoutManager.RowHeight);
            input.CurrentMouseHoverPoint_Pixels = absolute;
        }

        private bool IsOverGraph(MouseState ms)
        {
            if (!_graphHovered || SasHooks.GraphHidden) return false;
            if (DialogManager.AnyDialogsShowing || MenuBar.IsAnyMenuOpen || !Main.Active) return false;
            var graph = Main.TAE_EDITOR?.Graph;
            return graph != null && graph.Rect.Contains(new Vector2(ms.X / Main.DPI, ms.Y / Main.DPI));
        }
    }
}
