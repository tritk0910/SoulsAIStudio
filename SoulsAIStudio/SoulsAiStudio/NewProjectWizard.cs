#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Formats;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Single-screen New Project wizard (ui-spec §2b): game folder → mod folder → pick AI scripts → create.
    /// </summary>
    public sealed class NewProjectWizard
    {
        private readonly SasApp _app;
        private bool _open;
        private string _name = "";
        private string _gameDir = "", _modDir = "", _location = "";
        private string _filter = "";
        private bool _battleOnly = true, _editedOnly;
        private Task<(List<ScriptCandidate> Scripts, List<string> Errors)>? _scan;
        private List<ScriptCandidate> _candidates = new();
        private List<string> _scanErrors = new();
        private readonly HashSet<ScriptCandidate> _selected = new();
        private string? _validation;

        public NewProjectWizard(SasApp app) => _app = app;

        public void Open()
        {
            var s = _app.Settings;
            _gameDir = Directory.Exists(s.LastGameDir) ? s.LastGameDir : SasSettings.GuessGameDir();
            _modDir = Directory.Exists(s.LastModDir) ? s.LastModDir : "";
            _location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SoulsAiStudio");
            _name = "";
            _selected.Clear();
            _candidates.Clear();
            _scanErrors.Clear();
            _validation = null;
            _open = true;
            if (Directory.Exists(_gameDir) && Directory.Exists(_modDir)) StartScan();
        }

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            if (!_open) return false;
            PollScan();

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(900, 680), ImGuiCond.Appearing);
            bool focused = false;
            bool visible = Theme.BeginOpaque("New Project##sas_wizard", ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
            try
            {
            if (visible)
            {
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

                Step("1", "Folders");
                FolderField("Sekiro folder", ref _gameDir, "The game install, unpacked with UXM (contains chr\\, script\\, param\\).");
                FolderField("Mod folder", ref _modDir, "Your Mod Engine mod, e.g. Sekiro\\mods\\MyMod. Everything is saved here; game files are never touched.");
                if (ImGui.Button(_scan == null ? "Scan for AI scripts" : "Scanning...") && _scan == null) StartScan();
                ImGui.SameLine();
                ImGui.TextDisabled(_candidates.Count > 0 ? $"{_candidates.Count} scripts found" : "");
                foreach (string e in _scanErrors.Take(3)) ImGui.TextColored(Theme.Warn, e);

                ImGui.Spacing();
                Step("2", "Pick AI scripts");
                ImGui.SetNextItemWidth(260);
                ImGui.InputTextWithHint("##filter", "Filter (e.g. 506300, m10)", ref _filter, 64);
                ImGui.SameLine(); ImGui.Checkbox("Battle scripts only", ref _battleOnly);
                ImGui.SameLine(); ImGui.Checkbox("Already edited (plain text) only", ref _editedOnly);
                BuildCandidateTable();
                ImGui.TextDisabled($"{_selected.Count} selected. Compiled (bytecode) scripts are decompiled when opened.");

                ImGui.Spacing();
                Step("3", "Project");
                ImGui.SetNextItemWidth(300);
                ImGui.InputTextWithHint("Name", "e.g. Owl rework", ref _name, 64);
                FolderField("Save in", ref _location, "The project file, backups and cache are stored in <Save in>\\<Name>\\.");
                if (!string.IsNullOrWhiteSpace(_name))
                    ImGui.TextDisabled("→ " + ProjectPath());

                ImGui.Separator();
                if (_validation != null) ImGui.TextColored(Theme.Error, _validation);
                if (ImGui.Button("Create Project", new Vector2(160, 30))) TryCreate();
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(100, 30))) _open = false;
            }
            }
            finally { ImGui.End(); }
            return focused;
        }

        private static void Step(string n, string title)
        {
            ImGui.TextColored(Theme.Accent, $"{n}.");
            ImGui.SameLine();
            ImGui.Text(title);
        }

        private void FolderField(string label, ref string value, string help)
        {
            ImGui.SetNextItemWidth(600);
            ImGui.InputText($"##{label}", ref value, 512);
            ImGui.SameLine();
            if (ImGui.Button($"Browse##{label}")) BrowseFolder(label);
            ImGui.SameLine();
            ImGui.Text(label);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(help);
            if (!string.IsNullOrEmpty(value) && !Directory.Exists(value) && label != "Save in")
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Error, "(not found)");
            }
        }

        private void BrowseFolder(string label)
        {
            DSAnimStudio.Main.MainThreadLazyDispatch(() =>
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = label, UseDescriptionForTitle = true };
                string current = label switch { "Sekiro folder" => _gameDir, "Mod folder" => _modDir, _ => _location };
                if (Directory.Exists(current)) dlg.SelectedPath = current;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                switch (label)
                {
                    case "Sekiro folder": _gameDir = dlg.SelectedPath; break;
                    case "Mod folder": _modDir = dlg.SelectedPath; break;
                    default: _location = dlg.SelectedPath; break;
                }
            });
        }

        private void StartScan()
        {
            _validation = null;
            if (!Directory.Exists(_gameDir) || !Directory.Exists(_modDir))
            {
                _validation = "Pick both the Sekiro folder and the mod folder first.";
                return;
            }
            OodleLoader.TryLoad(_gameDir);
            var catalog = new ScriptCatalog(_gameDir, _modDir);
            _scan = Task.Run(() => catalog.Scan());
        }

        private void PollScan()
        {
            if (_scan is not { IsCompleted: true } t) return;
            _scan = null;
            if (t.IsCompletedSuccessfully)
            {
                _candidates = t.Result.Scripts.OrderBy(c => c.IsBytecode).ThenBy(c => c.Luabnd).ThenBy(c => c.FileName).ToList();
                _scanErrors = t.Result.Errors;
                _selected.RemoveWhere(s => !_candidates.Contains(s));
            }
            else _scanErrors = new List<string> { t.Exception?.GetBaseException().Message ?? "Scan failed" };
        }

        private void BuildCandidateTable()
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("##candidates", 5, flags, new Vector2(0, 300))) return;
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Script", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Luabnd", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();
            try
            {
            foreach (var c in Visible())
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool sel = _selected.Contains(c);
                if (ImGui.Checkbox($"##{c.Luabnd}{c.EntryPath}", ref sel))
                {
                    if (sel) _selected.Add(c); else _selected.Remove(c);
                    if (sel && string.IsNullOrWhiteSpace(_name) && c.NpcId != null) _name = $"AI {c.NpcId}";
                }
                ImGui.TableNextColumn(); ImGui.Text(c.FileName);
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

        private IEnumerable<ScriptCandidate> Visible() => _candidates.Where(c =>
            (!_battleOnly || c.Kind == "battle") &&
            (!_editedOnly || !c.IsBytecode) &&
            (_filter.Length == 0 || c.FileName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                 || c.Luabnd.Contains(_filter, StringComparison.OrdinalIgnoreCase)));

        private string ProjectPath() =>
            Path.Combine(_location, SafeName(_name), SafeName(_name) + SasProject.Extension);

        private static string SafeName(string s) =>
            string.Concat(s.Trim().Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        private void TryCreate()
        {
            _validation = null;
            var folderProblems = SoulsAiStudio.Core.IO.WritePolicy.ValidateFolders(_gameDir, _modDir);
            if (folderProblems.Count > 0) { _validation = string.Join(" ", folderProblems); return; }
            if (_selected.Count == 0) { _validation = "Select at least one AI script."; return; }
            if (string.IsNullOrWhiteSpace(_name)) { _validation = "Give the project a name."; return; }
            string path = ProjectPath();
            if (File.Exists(path)) { _validation = "A project with this name already exists there."; return; }
            try
            {
                var project = SasProject.Create(path, _name.Trim(), _gameDir, _modDir,
                    _selected.OrderBy(s => s.Luabnd).ThenBy(s => s.FileName).Select(s => s.ToProjectScript()));
                _open = false;
                _app.ActivateProject(project);
            }
            catch (Exception ex) { _validation = "Could not create project: " + ex.Message; }
        }
    }
}
