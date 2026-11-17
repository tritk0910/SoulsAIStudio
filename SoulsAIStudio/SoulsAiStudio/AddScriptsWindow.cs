#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Add AI Scripts" (Project panel): the New Project wizard's script list for the open project. Lists every Lua script in
    /// the mod's and the game's luabnds (mod first) and adds the ticked ones to the project; already added ones are shown
    /// ticked and greyed out.
    /// </summary>
    public sealed class AddScriptsWindow
    {
        public const string WindowName = "Add AI Scripts##sas_add_scripts";

        private readonly SasApp _app;
        private bool _open;
        private Task<(List<ScriptCandidate> Scripts, List<string> Errors)>? _scan;
        private CancellationTokenSource? _cts;
        private List<ScriptCandidate> _candidates = new();
        private List<string> _errors = new();
        private readonly HashSet<ScriptCandidate> _selected = new();
        private string _filter = "";
        private bool _battleOnly = true;
        private bool _editedOnly;
        private bool _focusFilter;

        public AddScriptsWindow(SasApp app) => _app = app;

        public bool IsOpen => _open;

        public void Open(string? filter = null)
        {
            _open = true;
            _focusFilter = true;
            if (filter != null) _filter = filter;
            if (_scan == null) StartScan(); // rescan each time: the mod may have new luabnds
        }

        private void StartScan()
        {
            var session = _app.Session;
            if (session == null) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            _scan = Task.Run(() => session.Catalog.Scan(cts.Token));
        }

        private void PollScan()
        {
            if (_scan is not { IsCompleted: true } t) return;
            _scan = null;
            if (t.IsCompletedSuccessfully)
            {
                _candidates = t.Result.Scripts.OrderBy(c => c.IsBytecode).ThenBy(c => c.Luabnd).ThenBy(c => c.FileName).ToList();
                _errors = t.Result.Errors;
                _selected.RemoveWhere(s => !_candidates.Contains(s));
            }
            else if (!t.IsCanceled) _errors = new List<string> { t.Exception?.GetBaseException().Message ?? "Scan failed" };
        }

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            var project = _app.Project;
            if (project == null) { _open = false; _cts?.Cancel(); return false; }
            PollScan();
            if (!_open) return false;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(760, 520), ImGuiCond.FirstUseEver);
            bool visible = Theme.BeginOpaque(WindowName, ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
            bool focused = false;
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                var inProject = project.Scripts.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                ImGui.TextDisabled("Scripts of the mod's and the game's luabnds (the mod's copy wins). Tick the ones to add to this project.");
                ImGui.SetNextItemWidth(260);
                if (_focusFilter) { ImGui.SetKeyboardFocusHere(); _focusFilter = false; }
                ImGui.InputTextWithHint("##sas_add_filter", "Filter (e.g. 540, m11_02)", ref _filter, 64);
                ImGui.SameLine(); ImGui.Checkbox("Battle scripts only", ref _battleOnly);
                ImGui.SameLine(); ImGui.Checkbox("Edited (plain text) only", ref _editedOnly);
                ImGui.SameLine();
                ImGui.BeginDisabled(_scan != null);
                if (ImGui.Button(_scan != null ? "Scanning..." : "Rescan")) StartScan();
                ImGui.EndDisabled();
                foreach (string e in _errors.Take(3)) ImGui.TextColored(Theme.Warn, e.Replace("%", "%%"));

                float tableH = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 4;
                BuildTable(inProject, Math.Max(120, tableH));

                int n = _selected.Count(c => !inProject.Contains(Key(c)));
                ImGui.BeginDisabled(n == 0);
                if (ImGui.Button(n == 0 ? "Add" : $"Add {n} script{(n == 1 ? "" : "s")}")) Add(project, inProject);
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Close")) _open = false;
                if (_scan != null) { ImGui.SameLine(); ImGui.TextDisabled("Reading luabnds..."); }
            }
            finally { ImGui.End(); }
            if (!_open) _selected.Clear();
            return focused;
        }

        private static string Key(ScriptCandidate c) => $"{c.Luabnd}|{c.EntryPath}";

        private void BuildTable(HashSet<string> inProject, float height)
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("##sas_add_candidates", 5, flags, new Vector2(0, height))) return;
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
                ImGui.TableSetupColumn("Script", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Luabnd", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableHeadersRow();
                foreach (var c in Visible())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool already = inProject.Contains(Key(c));
                    bool sel = already || _selected.Contains(c);
                    ImGui.BeginDisabled(already);
                    if (ImGui.Checkbox($"##{c.Luabnd}{c.EntryPath}", ref sel))
                    {
                        if (sel) _selected.Add(c); else _selected.Remove(c);
                    }
                    ImGui.EndDisabled();
                    ImGui.TableNextColumn();
                    if (already) { ImGui.TextDisabled(c.FileName); ImGui.SameLine(); ImGui.TextDisabled("(in project)"); }
                    else ImGui.TextUnformatted(c.FileName);
                    ImGui.TableNextColumn(); ImGui.TextDisabled(c.Luabnd);
                    ImGui.TableNextColumn(); ImGui.TextDisabled(c.Source switch
                    {
                        LuabndSource.ModUnpacked => "mod (unpacked)",
                        LuabndSource.ModPacked => "mod",
                        _ => "game",
                    });
                    ImGui.TableNextColumn();
                    if (c.IsBytecode) ImGui.TextDisabled("bytecode"); else ImGui.TextColored(Theme.Ok, "plain text");
                }
            }
            finally { ImGui.EndTable(); }
        }

        private IEnumerable<ScriptCandidate> Visible()
        {
            string f = _filter.Trim();
            return _candidates.Where(c =>
                (!_battleOnly || c.Kind == "battle") &&
                (!_editedOnly || !c.IsBytecode) &&
                (f.Length == 0 || c.FileName.Contains(f, StringComparison.OrdinalIgnoreCase)
                               || c.Luabnd.Contains(f, StringComparison.OrdinalIgnoreCase)));
        }

        private void Add(SasProject project, HashSet<string> inProject)
        {
            var added = _selected.Where(c => !inProject.Contains(Key(c)))
                .OrderBy(c => c.Luabnd).ThenBy(c => c.FileName).Select(c => c.ToProjectScript()).ToList();
            if (added.Count == 0) return;
            project.Scripts.AddRange(added);
            _selected.Clear();
            _app.SaveProjectUiState();
            _app.OpenScript(added[0]);
            _app.Layout.FocusEditorTabFrames = 10; // show it (the editor tab may be behind Graph)
            _app.Notify(added.Count == 1 ? $"Added {added[0].FileName} to the project" : $"Added {added.Count} scripts to the project");
            _open = false;
        }
    }
}
