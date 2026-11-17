#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Watch;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Script tabs whose luabnd changed on disk (file watcher, see SasApp.Watch.cs): the script is read again in the
    /// background; a clean tab takes the new text silently, a tab with unsaved edits gets a banner
    /// (Keep mine / Load theirs / Show diff). The user's buffer is never replaced without a click.
    /// </summary>
    public sealed partial class LuaEditorPanel
    {
        private sealed class DiskState
        {
            public Task<ScriptDocument>? Checking;
            /// <summary>Another change arrived while checking: check again afterwards.</summary>
            public bool Recheck;
            /// <summary>Newer text on disk while the tab has unsaved edits (banner shown).</summary>
            public ScriptDocument? Theirs;
            public bool ShowDiff;
            public List<DiffHunk>? Diff;
            public List<DiffLine?>? DiffRows;
            public long DiffVersion = -1;
        }

        private readonly Dictionary<OpenScript, DiskState> _disk = new();

        /// <summary>The luabnd changed outside the app: re-read its open scripts (UI thread).</summary>
        public void OnLuabndChangedOnDisk(string luabnd)
        {
            foreach (var o in _open.Where(o => string.Equals(o.Script.Luabnd, luabnd, StringComparison.OrdinalIgnoreCase)))
            {
                if (o.Doc == null && o.Error != null && o.Loading == null)
                {
                    o.Error = null; // it could not be opened before: the new file may be readable
                    o.Loading = _app.Session!.OpenAsync(o.Script);
                    continue;
                }
                if (!_disk.TryGetValue(o, out var st)) _disk[o] = st = new DiskState();
                if (st.Checking != null || o.Loading != null || o.Saving != null) { st.Recheck = true; continue; }
                st.Checking = _app.Session!.OpenAsync(o.Script);
            }
        }

        /// <summary>Called once per frame from <see cref="Build"/>.</summary>
        private void PollDiskChecks()
        {
            if (_disk.Count == 0) return;
            foreach (var (o, st) in _disk.ToList())
            {
                if (!_open.Contains(o)) { _disk.Remove(o); continue; }
                if (st.Checking == null)
                {
                    // Deferred while the tab was loading/saving.
                    if (st.Recheck && o.Loading == null && o.Saving == null && _app.Session != null)
                    {
                        st.Recheck = false;
                        st.Checking = _app.Session.OpenAsync(o.Script);
                    }
                    else if (st.Theirs == null && !st.Recheck) _disk.Remove(o);
                    continue;
                }
                if (!st.Checking.IsCompleted) continue;
                var task = st.Checking;
                st.Checking = null;
                if (!task.IsCompletedSuccessfully)
                {
                    _app.Notify($"{o.Script.FileName} changed on disk but could not be read: {task.Exception?.GetBaseException().Message}");
                    continue;
                }
                OnDiskTextRead(o, st, task.Result);
            }
        }

        private void OnDiskTextRead(OpenScript o, DiskState st, ScriptDocument theirs)
        {
            if (o.Doc == null) return;
            string mine = o.Editor.Text;
            if (theirs.SavedText == mine)
            {
                // Same as the buffer (e.g. another script of this luabnd changed, or the edit was undone on disk too).
                o.Doc = theirs;
                if (o.Editor.IsDirty) o.Editor.MarkClean();
                st.Theirs = null;
                return;
            }
            if (!o.IsDirty && o.Saving == null)
            {
                Adopt(o, theirs);
                st.Theirs = null;
                _app.Notify($"{o.Script.FileName} changed on disk: reloaded.");
                return;
            }
            if (theirs.SavedText == o.Doc.SavedText && st.Theirs == null) return; // this script's text didn't change on disk
            st.Theirs = theirs;
            st.Diff = null;
        }

        /// <summary>Replaces the buffer with the disk text as one undo step (Ctrl+Z gets the previous text back), cursor kept.</summary>
        private void Adopt(OpenScript o, ScriptDocument theirs)
        {
            string current = o.Editor.Text;
            int lastNl = current.LastIndexOf('\n');
            int endLine = Math.Max(0, o.Editor.LineCount - 1);
            int endCol = (lastNl < 0 ? current : current[(lastNl + 1)..]).Length;
            if (!o.Editor.ApplyEdit(0, 0, endLine, endCol, theirs.SavedText) && o.Editor.Text != theirs.SavedText)
                o.Editor.Text = theirs.SavedText; // read-only editor: plain replace
            o.Editor.MarkClean();
            o.Doc = theirs;
            o.LastEdit = DateTime.UtcNow - AnalysisDebounce; // re-analyze now
        }

        /// <summary>Banner above the active tab's editor when its file changed on disk while it has unsaved edits.</summary>
        private void BuildDiskBanner(OpenScript o)
        {
            if (!_disk.TryGetValue(o, out var st) || st.Theirs == null) return;
            if (!o.IsDirty)
            {
                // The user saved or undid in the meantime: nothing of theirs to lose any more.
                if (o.Saving == null) { Adopt(o, st.Theirs); st.Theirs = null; }
                return;
            }
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.35f, 0.27f, 0.08f, 1f));
            try
            {
                float h = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
                if (ImGui.BeginChild("##sas_disk_banner", new Vector2(-1, h), false, ImGuiWindowFlags.NoScrollbar))
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Warn, $"{o.Script.FileName} changed on disk.");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(("Another program (VSCode, WitchyBND...) changed this script while you have unsaved edits.\n" +
                                          "Your edits are kept until you choose.").Replace("%", "%%"));
                    ImGui.SameLine();
                    if (ImGui.Button("Keep mine"))
                    {
                        o.Doc = st.Theirs; // the disk version is now the known one: the same change doesn't ask again
                        st.Theirs = null;
                        st.ShowDiff = false;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep your text. Saving (Ctrl+S) will overwrite the version on disk.");
                    ImGui.SameLine();
                    if (st.Theirs != null && ImGui.Button("Load theirs"))
                    {
                        Adopt(o, st.Theirs);
                        st.Theirs = null;
                        st.ShowDiff = false;
                        _app.Notify($"{o.Script.FileName}: loaded the version on disk (Ctrl+Z brings your edits back).");
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Replace your text with the version on disk. Ctrl+Z in the editor brings your edits back.");
                    ImGui.SameLine();
                    if (st.Theirs != null && ImGui.Button(st.ShowDiff ? "Hide diff" : "Show diff")) st.ShowDiff = !st.ShowDiff;
                }
                ImGui.EndChild();
            }
            finally { ImGui.PopStyleColor(); }
            if (st.Theirs != null && st.ShowDiff) BuildDiskDiff(o, st);
        }

        private void BuildDiskDiff(OpenScript o, DiskState st)
        {
            if (st.Diff == null || st.DiffVersion != o.Editor.Version)
            {
                st.DiffVersion = o.Editor.Version;
                st.Diff = LineDiff.Hunks(LineDiff.Compute(o.Editor.Text, st.Theirs!.SavedText), context: 2);
                st.DiffRows = new List<DiffLine?>();
                foreach (var hunk in st.Diff)
                {
                    if (st.DiffRows.Count > 0) st.DiffRows.Add(null); // "..." between hunks
                    st.DiffRows.AddRange(hunk.Lines.Select(l => (DiffLine?)l));
                }
            }
            var removed = new Vector4(0.95f, 0.45f, 0.45f, 1f);
            var added = new Vector4(0.45f, 0.85f, 0.45f, 1f);
            ImGui.TextDisabled($"Mine (-) vs on disk (+): {st.Diff.Sum(h => h.Removed)} line(s) only in mine, {st.Diff.Sum(h => h.Added)} only on disk. Click a line to go to it.");
            if (ImGui.BeginChild("##sas_disk_diff", new Vector2(-1, Math.Min(260, ImGui.GetContentRegionAvail().Y * 0.45f)), true, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var rows = st.DiffRows!;
                if (rows.Count == 0) ImGui.TextDisabled("No differences in text (line endings only).");
                unsafe
                {
                    var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                    clipper.Begin(rows.Count);
                    while (clipper.Step())
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            if (rows[i] is not DiffLine l) { ImGui.TextDisabled("  ..."); continue; }
                            float x0 = ImGui.GetCursorPosX();
                            if (ImGui.Selectable($"##sas_diff{i}", false, ImGuiSelectableFlags.AllowItemOverlap) && l.OldLine >= 0) o.Editor.GoToLine(l.OldLine);
                            if (ImGui.IsItemHovered() && l.OldLine < 0) ImGui.SetTooltip("Only on disk (line " + (l.NewLine + 1) + " there).");
                            ImGui.SameLine(x0);
                            string mark = l.Kind switch { DiffKind.Removed => "-", DiffKind.Added => "+", _ => " " };
                            string num = l.OldLine >= 0 ? (l.OldLine + 1).ToString().PadLeft(5) : "     ";
                            ImGui.PushStyleColor(ImGuiCol.Text, l.Kind switch { DiffKind.Removed => removed, DiffKind.Added => added, _ => Theme.Dim });
                            ImGui.TextUnformatted($"{num} {mark} {l.Text}"); // verbatim: '%' and '##' in the Lua are safe
                            ImGui.PopStyleColor();
                        }
                    clipper.End();
                    clipper.Destroy();
                }
            }
            ImGui.EndChild();
        }
    }
}
