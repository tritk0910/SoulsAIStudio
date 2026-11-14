#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Luabnd" window: unpack a map's <c>.luabnd.dcx</c> into a WitchyBND folder and repack a Witchy or Yabber folder
    /// into the mod's <c>.luabnd.dcx</c>, without leaving the app (replaces "open in Explorer → drag onto WitchyBND").
    /// Only the mod folder is written; the old .dcx is backed up before every repack.
    /// </summary>
    public sealed class LuabndToolsPanel
    {
        public const string WindowName = "Luabnd (Unpack / Repack)##sas_luabnd";
        private readonly SasApp _app;
        public bool IsOpen;
        private string _filter = "";
        private bool _showGameOnly;
        private Task<List<LuabndStatus>>? _refresh;
        private List<LuabndStatus> _rows = new();
        private DateTime _lastRefresh = DateTime.MinValue;
        private readonly Dictionary<string, Task<string>> _busy = new(StringComparer.OrdinalIgnoreCase);
        private string? _confirmRepack;

        public LuabndToolsPanel(SasApp app) => _app = app;

        public void Open()
        {
            IsOpen = true;
            RequestRefresh();
            ImGui.SetWindowFocus(WindowName);
        }

        public void RequestRefresh() => _lastRefresh = DateTime.MinValue;

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            PollBusy();
            if (!IsOpen || _app.Session is not { } session) return false;
            PollRefresh(session);

            ImGui.SetNextWindowSize(new Vector2(860, 420), ImGuiCond.FirstUseEver);
            bool focused = false;
            bool visible = Theme.BeginOpaque(WindowName, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                ImGui.TextWrapped("Unpack / repack the AI script archives of your mod, like dragging them onto WitchyBND. " +
                                  "Repack reads the folder's _witchy-bnd4.xml or _yabber-bnd4.xml, so it works for both tools. " +
                                  "Saving a script in the Lua Editor already updates both the .dcx and the folder; Repack is for edits made outside the app (e.g. VSCode).");
                ImGui.Spacing();
                ImGui.SetNextItemWidth(220);
                ImGui.InputTextWithHint("##sas_lb_filter", "Filter (e.g. m10, aicommon)", ref _filter, 64);
                ImGui.SameLine();
                ImGui.Checkbox("Show game-only maps", ref _showGameOnly);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Maps the mod doesn't have yet.\nUnpack copies them from the game into the mod (the game file is untouched).");
                ImGui.SameLine();
                if (ImGui.Button(_refresh != null ? "Refreshing..." : "Refresh")) RequestRefresh();
                ImGui.SameLine();
                if (ImGui.Button("Open script folder")) OpenInExplorer(session.Catalog.ModScriptDir);
                BuildTable(session);
                BuildConfirm(session);
            }
            finally { ImGui.End(); }
            return focused;
        }

        private void BuildTable(ProjectSession session)
        {
            var projectMaps = new HashSet<string>(session.Project.Scripts.Select(s => s.Luabnd), StringComparer.OrdinalIgnoreCase);
            var rows = _rows.Where(r => (_showGameOnly || r.ModPackedTime != null || r.Unpacked != null || projectMaps.Contains(r.Luabnd))
                                        && (_filter.Length == 0 || r.Luabnd.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(r => projectMaps.Contains(r.Luabnd)).ThenByDescending(r => r.NeedsRepack).ThenBy(r => r.Luabnd).ToList();

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("##sas_lb_table", 5, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Luabnd", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn(".dcx in mod", ImGuiTableColumnFlags.WidthFixed, 95);
                ImGui.TableSetupColumn("Unpacked folder", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 165);
                ImGui.TableHeadersRow();
                if (rows.Count == 0)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    ImGui.TextDisabled(_refresh != null ? "Scanning..." : "No luabnd in the mod (tick \"Show game-only maps\").");
                }
                foreach (var r in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (projectMaps.Contains(r.Luabnd)) ImGui.TextColored(Theme.Accent, r.Luabnd); else ImGui.Text(r.Luabnd);
                    if (projectMaps.Contains(r.Luabnd) && ImGui.IsItemHovered()) ImGui.SetTooltip("Used by a script of this project.");

                    ImGui.TableNextColumn();
                    if (r.ModPackedTime is { } pt) { ImGui.Text(Ago(pt)); Tip(pt); } else ImGui.TextDisabled(r.InGame ? "(game only)" : "-");

                    ImGui.TableNextColumn();
                    if (r.Unpacked is { } kind)
                    {
                        ImGui.Text(kind == UnpackFormat.Yabber ? "Yabber" : "Witchy");
                        if (r.UnpackedNewest is { } ut) { ImGui.SameLine(); ImGui.TextDisabled(Ago(ut)); Tip(ut); }
                    }
                    else ImGui.TextDisabled("-");

                    ImGui.TableNextColumn();
                    if (_busy.ContainsKey(r.Luabnd)) ImGui.TextColored(Theme.Warn, "working...");
                    else if (r.NeedsRepack) ImGui.TextColored(Theme.Warn, r.ModPackedTime == null ? "not packed into the mod yet" : "folder changed → Repack");
                    else if (r.PackedIsNewer) ImGui.TextColored(Theme.Dim, ".dcx is newer than the folder");
                    else if (r.Unpacked != null) ImGui.TextColored(Theme.Ok, "in sync");
                    else ImGui.TextDisabled(r.ModPackedTime != null ? "packed only" : "game only");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(r.NeedsRepack ? "The unpacked folder has changes the .dcx doesn't have: Repack writes them into the mod's .dcx."
                            : r.PackedIsNewer ? "The .dcx was written after the folder's files (e.g. by another tool). Repack would replace it with the folder."
                            : r.Unpacked != null ? "The .dcx and the folder match." : "No unpacked folder: Unpack to edit the scripts as files.");

                    ImGui.TableNextColumn();
                    bool busy = _busy.ContainsKey(r.Luabnd);
                    ImGui.BeginDisabled(busy || r.Unpacked == null);
                    if (ImGui.SmallButton($"Repack##sas_rp_{r.Luabnd}"))
                    {
                        if (r.PackedIsNewer) { _confirmRepack = r.Luabnd; ImGui.OpenPopup("##sas_lb_confirm"); }
                        else StartRepack(session, r.Luabnd);
                    }
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(r.Unpacked == null ? "No unpacked folder: Unpack first."
                            : $"Folder → mod\\script\\{r.Luabnd}.luabnd.dcx (same compression/ids as the xml). The old .dcx is backed up first.");
                    ImGui.SameLine();
                    ImGui.BeginDisabled(busy || r.Unpacked != null);
                    if (ImGui.SmallButton($"Unpack##sas_up_{r.Luabnd}")) StartUnpack(session, r.Luabnd);
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(r.Unpacked != null ? "Already unpacked (existing folders are never overwritten)."
                            : $"{(r.ModPackedTime != null ? "Mod" : "Game")} .dcx → mod\\script\\{r.Luabnd}-luabnd-dcx\\ (WitchyBND format, _witchy-bnd4.xml).");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Folder##sas_of_{r.Luabnd}"))
                        OpenInExplorer(r.Unpacked != null ? session.Catalog.ModUnpackedDir(r.Luabnd) : session.Catalog.ModScriptDir);
                }
            }
            finally { ImGui.EndTable(); }
        }

        private void BuildConfirm(ProjectSession session)
        {
            if (!ImGui.BeginPopup("##sas_lb_confirm")) return;
            ImGui.Text($"{_confirmRepack}.luabnd.dcx is newer than every file in its unpacked folder.");
            ImGui.TextDisabled("Repacking replaces it with the folder's (older) content. The current .dcx is backed up first.");
            if (ImGui.Button("Repack anyway") && _confirmRepack != null) { StartRepack(session, _confirmRepack); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        private static void Tip(DateTime utc)
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
            return utc.ToLocalTime().ToString("yyyy-MM-dd");
        }

        private void StartRepack(ProjectSession session, string luabnd) => _busy[luabnd] = session.RepackAsync(luabnd);
        private void StartUnpack(ProjectSession session, string luabnd) => _busy[luabnd] = session.UnpackAsync(luabnd).ContinueWith(t => "unpack:" + t.Result, TaskContinuationOptions.OnlyOnRanToCompletion);

        /// <summary>Finishes repack/unpack tasks (even while the window is closed): notify, reload open scripts, refresh.</summary>
        private void PollBusy()
        {
            foreach (var (luabnd, task) in _busy.Where(kv => kv.Value.IsCompleted).ToList())
            {
                _busy.Remove(luabnd);
                RequestRefresh();
                if (!task.IsCompletedSuccessfully)
                {
                    _app.ReportError($"{luabnd}: {(task.IsCanceled ? "cancelled" : "failed")}", task.Exception?.GetBaseException());
                    continue;
                }
                if (task.Result.StartsWith("unpack:", StringComparison.Ordinal))
                {
                    _app.Notify($"Unpacked {luabnd} → {task.Result["unpack:".Length..]}");
                    continue;
                }
                var skipped = _app.ReloadScriptsOf(luabnd);
                _app.Notify($"Repacked {luabnd}.luabnd.dcx in the mod (backup taken)." +
                            (skipped.Count > 0 ? $" Not reloaded (unsaved edits): {string.Join(", ", skipped)}." : ""));
            }
        }

        private void PollRefresh(ProjectSession session)
        {
            if (_refresh is { IsCompleted: true } t)
            {
                _refresh = null;
                if (t.IsCompletedSuccessfully) _rows = t.Result;
                _lastRefresh = DateTime.UtcNow;
            }
            if (_refresh == null && (DateTime.UtcNow - _lastRefresh).TotalSeconds > 3)
            {
                var catalog = session.Catalog;
                _refresh = Task.Run(() => catalog.ListLuabnds().Select(catalog.Status).ToList());
            }
        }

        private void OpenInExplorer(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) { _app.Notify($"Folder not found: {dir}"); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { _app.ReportError("Could not open Explorer", ex); }
        }
    }
}
