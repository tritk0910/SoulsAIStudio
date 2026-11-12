#nullable enable
using System;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Time-cursor dragging in DSAS's TAE graph. Default (off): DSAS 4.9.9 behavior, edge scroll only (see EdgeScroll).
    /// "Center playhead while scrubbing" (on, like video editors): the graph scrolls so the playhead stays centered.
    /// <para>
    /// DSAS maps the mouse X to an absolute time. If we also re-centered the scroll on that time, every scroll step
    /// would move the time under a still mouse → runaway scrolling. So with follow ON, only the first frame of the
    /// drag uses the absolute position (click = jump there); after that the time moves by the mouse delta, and the
    /// scroll eases toward the playhead. Holding Shift falls back to DSAS's original behavior for that drag.
    /// </para>
    /// Called from two <c>// [SAS]</c> lines in <c>TaeEditor/NewGraphInput.cs</c> (absolute scrub branch) via <see cref="SasHooks"/>.
    /// </summary>
    public sealed class ScrubFollow
    {
        /// <summary>Per-frame fraction of the remaining distance the scroll moves toward the centered playhead.</summary>
        private const float EaseFactor = 0.3f;

        private readonly SasSettings _settings;

        public ScrubFollow(SasSettings settings) => _settings = settings;

        public bool Enabled
        {
            get => _settings.CenterPlayheadWhileScrubbing;
            set
            {
                if (_settings.CenterPlayheadWhileScrubbing == value) return;
                _settings.CenterPlayheadWhileScrubbing = value;
                _settings.Save();
            }
        }

        private bool ActiveFor(FancyInputHandler input) => Enabled && !input.ShiftHeld;

        /// <summary>Time for this scrub frame: absolute on the first frame, then previous time + mouse delta.</summary>
        public float AdjustTime(NewGraph graph, FancyInputHandler input, float absoluteTime, bool firstFrameOfDrag)
        {
            if (!ActiveFor(input) || firstFrameOfDrag || graph?.PlaybackCursor == null || graph.LayoutManager == null)
                return absoluteTime;
            float pps = graph.LayoutManager.SecondsPixelSize;
            if (pps <= 0) return absoluteTime;
            double t = graph.PlaybackCursor.CurrentTime + input.MousePositionDelta.X / pps;
            return (float)Math.Max(0, t);
        }

        /// <summary>Eases the horizontal scroll toward the playhead. Returns true if it handled scrolling this frame
        /// (then DSAS's own edge-zone auto-scroll is skipped).</summary>
        public bool CenterOnPlayhead(NewGraph graph, FancyInputHandler input)
        {
            if (graph?.LayoutManager == null || graph.ScrollViewer == null) return false;
            if (ActiveFor(input))
            {
                graph.LayoutManager.ScrollToPlaybackCursor(EaseFactor, modTime: false, clampTime: true);
                return true;
            }
            EdgeScroll(graph, input);
            return true;
        }

        /// <summary>
        /// DSAS 4.9.9-style drag (default): the playhead stays under the mouse and the timeline does not move,
        /// except when the mouse is dragged past the left/right edge of the track area; then it scrolls, faster the
        /// further out the mouse is. (DSAS 5.0 instead starts re-centering on the playhead once the mouse passes 60%.)
        /// </summary>
        private static void EdgeScroll(NewGraph graph, FancyInputHandler input)
        {
            const float EdgeMargin = 12, MinStep = 4, StepPerPixel = 0.25f, MaxStep = 80;
            var area = graph.WholeScrollViewerRect;
            float left = area.Left + EdgeMargin;
            float right = area.Right - EdgeMargin;
            float x = input.MousePosition.X;
            float step = 0;
            if (x > right) step = Math.Min(MaxStep, MinStep + (x - right) * StepPerPixel);
            else if (x < left) step = -Math.Min(MaxStep, MinStep + (left - x) * StepPerPixel);
            if (step == 0) return;
            graph.ScrollViewer.Scroll.X += step;
            graph.ScrollViewer.ClampScroll();
        }

        /// <summary>Menu entry (Action Graph menu, and optionally the Souls AI menu).</summary>
        public void MenuItem()
        {
            if (ImGui.MenuItem("Center Playhead While Scrubbing", "", Enabled))
                Enabled = !Enabled;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: while dragging the time cursor, keep the playhead centered and scroll the timeline under it.\n" +
                                 "Off (default, like DSAS 4.9.9): the cursor follows the mouse; the timeline only scrolls\n" +
                                 "when you drag past its left/right edge. Hold Shift while dragging: no auto-scroll.");
        }
    }
}
