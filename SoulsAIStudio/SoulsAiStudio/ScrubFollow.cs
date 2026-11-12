#nullable enable
using System;
using DSAnimStudio;
using DSAnimStudio.TaeEditor;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Follow playhead while scrubbing" for DSAS's TAE graph (like video editors): while the user drags the time
    /// cursor, the graph scrolls so the playhead stays in the middle of the visible timeline.
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
            get => _settings.FollowPlayheadWhileScrubbing;
            set
            {
                if (_settings.FollowPlayheadWhileScrubbing == value) return;
                _settings.FollowPlayheadWhileScrubbing = value;
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
            if (!ActiveFor(input) || graph?.LayoutManager == null) return false;
            graph.LayoutManager.ScrollToPlaybackCursor(EaseFactor, modTime: false, clampTime: true);
            return true;
        }

        /// <summary>Menu entry (Action Graph menu, and optionally the Souls AI menu).</summary>
        public void MenuItem()
        {
            if (ImGui.MenuItem("Follow Playhead While Scrubbing", "", Enabled))
                Enabled = !Enabled;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("While dragging the time cursor, keep the playhead centered in the timeline.\n" +
                                 "Hold Shift while dragging to scrub the old way (no follow).");
        }
    }
}
