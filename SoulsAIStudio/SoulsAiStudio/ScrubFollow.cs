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

        /// <summary>
        /// DSAS 5.0 "Relative Timeline Scrub" (off by default). While it is on, DSAS's relative drag runs instead of
        /// everything in this class: the mouse is warped onto the playhead and the graph re-centers on it every frame.
        /// DSAS 5.0 ships it on, which is what made the time cursor snap to the middle even with centering off.
        /// </summary>
        public bool UseRelativeScrub
        {
            get => _settings.UseDsasRelativeScrub;
            set
            {
                if (_settings.UseDsasRelativeScrub == value) return;
                _settings.UseDsasRelativeScrub = value;
                _settings.Save();
            }
        }

        /// <summary>Replaces DSAS's "Relative Timeline Scrub - Enabled" checkbox (Action Graph menu).</summary>
        public void RelativeScrubMenuItem()
        {
            if (ImGui.MenuItem("Relative Timeline Scrub - Enabled", "", UseRelativeScrub))
                UseRelativeScrub = !UseRelativeScrub;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("DSAS 5.0 relative scrub: the mouse is moved onto the playhead and the timeline re-centers\n" +
                                 "on it while dragging. Off (default): the time cursor drag below applies (DSAS 4.9.9 style,\n" +
                                 "or centered if \"Center Playhead While Scrubbing\" is on). The other \"Relative Timeline\n" +
                                 "Scrub\" options only matter while this is on.");
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
        /// DSAS 4.9.9-style drag (default): the playhead stays under the mouse and the timeline does not move while the
        /// mouse is inside the track area; only within <see cref="EdgeZone"/> px of the left/right edge (or past it) does it
        /// scroll, faster the closer to / further past the edge (4.9.9: 8 px/frame x (depth/96)^2). Scrolling right stops once
        /// the anim end is in view. (DSAS 5.0 instead starts re-centering on the playhead once the mouse passes 60%.)
        /// </summary>
        private static void EdgeScroll(NewGraph graph, FancyInputHandler input)
        {
            var area = graph.WholeScrollViewerRect;
            float x = input.MousePosition.X;
            float depth = 0;
            if (x > area.Right - EdgeZone) depth = x - (area.Right - EdgeZone);
            else if (x < area.Left + EdgeZone) depth = -((area.Left + EdgeZone) - x);
            if (depth == 0) return;
            float k = depth / EdgeZone;
            float step = Math.Sign(depth) * Math.Min(MaxStep, EdgeSpeed * k * k);
            var sv = graph.ScrollViewer;
            if (step > 0)
            {
                // Like 4.9.9's playback auto-scroll: do not scroll past "anim end + margin" (the virtual area is a whole
                // screen wider than the anim), but never pull back a scroll the user already made further right.
                float animEndScroll = (float)(graph.PlaybackCursor.MaxTime * graph.LayoutManager.SecondsPixelSize)
                                      + graph.LayoutManager.AfterAutoScrollHorizontalMargin - sv.Viewport.Width;
                if (sv.Scroll.X >= animEndScroll) return;
                sv.Scroll.X = Math.Min(sv.Scroll.X + step, animEndScroll);
            }
            else
            {
                sv.Scroll.X += step;
            }
            sv.ClampScroll();
        }

        /// <summary>DSAS 4.9.9 <c>ScrubScrollStartMargin</c> / <c>ScrubScrollSpeed</c>.</summary>
        private const float EdgeZone = 96, EdgeSpeed = 8, MaxStep = 80;

        /// <summary>Menu entry (Action Graph menu, and optionally the Souls AI menu).</summary>
        public void MenuItem()
        {
            if (ImGui.MenuItem("Center Playhead While Scrubbing", "", Enabled))
                Enabled = !Enabled;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: while dragging the time cursor, the timeline re-centers on the playhead and the mouse is moved onto it (DSAS 5.0 relative scrub).\n" +
                                 "Off (default, like DSAS 4.9.9): the cursor follows the mouse; the timeline only scrolls\n" +
                                 "when you drag to (or past) its left/right edge. Hold Shift while dragging: no auto-scroll.\n" +
                                 "The \"Relative Timeline Scrub\" sub-options (sync mouse, sensitivity…) apply while this is on.");
        }
    }
}
