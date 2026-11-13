#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using DSAnimStudio.ImguiOSD;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Diagnostics only (SAS_DEBUG_INPUT=1): shows the focus/hover/input state that decides whether the TAE graph
    /// takes a click, and appends every change to <c>SAS_DEBUG_LOG</c> (if set). Off by default, zero cost when off.
    /// </summary>
    internal sealed class SasInputProbe
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("SAS_DEBUG_INPUT") == "1";
        private readonly string? _logPath = Environment.GetEnvironmentVariable("SAS_DEBUG_LOG");
        private string _last = "";
        private long _frame;
        private int _currentWindowOffset = -1;

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr igGetCurrentContext();
        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr igGetCurrentWindowRead();

        /// <summary>g.HoveredWindow name: found as the field after g.CurrentWindow (located by scanning; debug only).</summary>
        private string HoveredWindowName()
        {
            IntPtr ctx = igGetCurrentContext(), cur = igGetCurrentWindowRead();
            if (ctx == IntPtr.Zero || cur == IntPtr.Zero) return "?";
            if (_currentWindowOffset < 0)
                for (int off = 0; off < 0x8000; off += 8)
                    if (Marshal.ReadIntPtr(ctx, off) == cur) { _currentWindowOffset = off; break; }
            if (_currentWindowOffset < 0) return "?";
            IntPtr hovered = Marshal.ReadIntPtr(ctx, _currentWindowOffset + 8);
            return hovered == IntPtr.Zero ? "-" : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(hovered)) ?? "?";
        }

        public void Draw(bool sasPanelFocused)
        {
            _frame++;
            var input = DSAnimStudio.Main.Input;
            var graph = DSAnimStudio.Main.TAE_EDITOR?.Graph;
            var doc = OSD.SpWindowDocManToolbar;
            string state =
                $"anyField={OSD.AnyFieldFocused} osdHovered={OSD.Hovered} sasFocused={sasPanelFocused} " +
                $"actualFocused={OSD.ActualFocusedWindow?.NewImguiWindowTitle ?? "-"} hoveredWin={HoveredWindowName()} " +
                $"ignoreUntilUp={input?.IsIgnoringMouseButtonsUntilAllAreUp} lHeld={input?.LeftClickHeld} " +
                $"drag={graph?.InputMan?.CurrentDragType} docHovered={doc.Hovered} docRect=({doc.LastPosition.Y:0},{doc.LastSize.Y:0})";
            ImGui.GetForegroundDrawList().AddText(new System.Numerics.Vector2(8, ImGui.GetIO().DisplaySize.Y - 20), 0xFF00FFFF, state);
            LogScrub(graph, input);
            if (state == _last) return;
            _last = state;
            if (_logPath != null)
                try { File.AppendAllText(_logPath, $"{_frame} {state}{Environment.NewLine}"); } catch (IOException) { }
        }

        /// <summary>While the time cursor is dragged: mouse X vs playhead screen X vs graph scroll X, one line per frame.</summary>
        private void LogScrub(DSAnimStudio.TaeEditor.NewGraph? graph, DSAnimStudio.FancyInputHandler? input)
        {
            if (_logPath == null || graph?.PlaybackCursor == null || graph.LayoutManager == null || input == null) return;
            var drag = graph.InputMan?.CurrentDragType;
            if (!input.LeftClickHeld || drag is not (DSAnimStudio.TaeEditor.NewGraphInput.DragTypes.TimelineScrub
                    or DSAnimStudio.TaeEditor.NewGraphInput.DragTypes.TimelineScrub_Relative)) return;
            float t = (float)graph.PlaybackCursor.GUICurrentTime;
            float headX = t * graph.LayoutManager.SecondsPixelSize - graph.ScrollViewer.RoundedScroll.X + graph.ScrollViewerContentsRect.X;
            try
            {
                File.AppendAllText(_logPath, $"{_frame} SCRUB drag={drag} mouseX={input.MousePosition.X:0} headX={headX:0} " +
                    $"scrollX={graph.ScrollViewer.Scroll.X:0} t={t:0.000} max={graph.PlaybackCursor.MaxTime:0.000} " +
                    $"area=({graph.WholeScrollViewerRect.Left},{graph.WholeScrollViewerRect.Right}){Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
