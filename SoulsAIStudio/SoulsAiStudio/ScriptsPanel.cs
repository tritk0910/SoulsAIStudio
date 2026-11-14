#nullable enable
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Left "Project" panel: the project's AI scripts + goal outline of the active script (ui-spec §2c), and the characters
    /// opened for animation editing (animations-only projects, or extra characters in an AI project).
    /// </summary>
    public sealed class ScriptsPanel
    {
        private readonly SasApp _app;
        private readonly LuaEditorPanel _editor;
        private string _filter = "";
        public bool IsOpen = true;

        public ScriptsPanel(SasApp app, LuaEditorPanel editor)
        {
            _app = app;
            _editor = editor;
        }

        /// <returns>True when the panel has focus.</returns>
        public bool Build()
        {
            if (!IsOpen) return false;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(10, 60), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(320, vp.WorkSize.Y - 120), ImGuiCond.FirstUseEver);
            bool focused = false;
            var project = _app.Project;
            if (project == null) return false;
            bool visible = Theme.BeginOpaque(SasLayout.ScriptsWindow, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                ImGui.TextColored(Theme.Accent, project.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{project.FilePath}\nMod: {project.ModDir}");

                LayoutSwitch();

                if (project.Scripts.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.Text("AI scripts");
                    foreach (var s in project.Scripts)
                    {
                        bool active = _editor.ActiveScript?.Key == s.Key;
                        if (ImGui.Selectable($"{s.Label}##{s.Key}", active)) _app.OpenScript(s);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{s.Luabnd} / {s.EntryPath}\nRight-click: unpack / repack {s.Luabnd}");
                        if (ImGui.BeginPopupContextItem($"##sas_script_ctx_{s.Key}"))
                        {
                            if (ImGui.MenuItem($"Unpack / Repack {s.Luabnd}...")) _app.OpenLuabndTools();
                            ImGui.EndPopup();
                        }
                    }

                    if (_editor.ActiveScript is { } script) BuildCharacter(script);
                }

                ImGui.Separator();
                BuildCharacters(project);

                if (project.Scripts.Count > 0)
                {
                    ImGui.Separator();
                    BuildOutline();
                }
            }
            finally { ImGui.End(); }
            return focused;
        }

        /// <summary>"AI | Animation" toggle (same as Souls AI → Layout).</summary>
        private void LayoutSwitch()
        {
            var layout = _app.CurrentLayout;
            ImGui.TextDisabled("Layout");
            ImGui.SameLine();
            if (ImGui.RadioButton("AI##sas_layout_ai", layout == WorkspaceLayout.Ai)) _app.SetLayout(WorkspaceLayout.Ai);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor shown (as a tab next to Graph).");
            ImGui.SameLine();
            if (ImGui.RadioButton("Animation##sas_layout_anim", layout == WorkspaceLayout.Animation)) _app.SetLayout(WorkspaceLayout.Animation);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lua Editor hidden: the Graph (TAE timeline) stays visible for animation editing.");
        }

        private string _addChr = "";
        private string? _addChrError;
        private string? _renameChr;
        private string _renameText = "";

        /// <summary>Characters opened directly for animation editing (no AI script needed).</summary>
        private void BuildCharacters(SasProject project)
        {
            ImGui.Text("Characters");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Characters opened for animation (TAE) editing, without an AI script.\n" +
                                 "Click one to load its model and animations. Saving (Ctrl+S in the graph) writes only into the mod folder.");
            if (project.Characters.Count == 0)
                ImGui.TextDisabled(project.Scripts.Count > 0 ? "(optional: add one below)" : "(none: add one below)");

            string? remove = null;
            foreach (var c in project.Characters)
            {
                var files = _app.ResolveCharacterId(c.ChrId);
                bool loaded = files != null && _app.Bridge.IsLoaded(files);
                if (files == null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Dim);
                if (ImGui.Selectable($"{c.Label}##sas_chr_{c.ChrId}", loaded, ImGuiSelectableFlags.AllowItemOverlap,
                        new Vector2(Math.Max(40, ImGui.GetContentRegionAvail().X - 96), 0)) && files != null)
                    _app.LoadCharacterId(c.ChrId);
                if (files == null) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(files == null ? $"{c.ChrId}.anibnd.dcx not found in the mod or the game chr folder."
                        : $"{files.AnibndPath}\n{files.ChrbndPath ?? "(no chrbnd: no model)"}\nClick: load model & animations. Right-click: rename / remove.");
                if (ImGui.BeginPopupContextItem($"##sas_chr_ctx_{c.ChrId}"))
                {
                    if (ImGui.MenuItem("Rename...")) { _renameChr = c.ChrId; _renameText = c.DisplayName; _renameFocused = false; _renameStartFrame = ImGui.GetFrameCount(); }
                    if (ImGui.MenuItem("Remove from project")) remove = c.ChrId;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only removes it from this list. No file is deleted.");
                    ImGui.EndPopup();
                }
                ImGui.SameLine(Math.Max(48, ImGui.GetContentRegionMax().X - 90));
                if (files == null) ImGui.TextColored(Theme.Warn, "missing");
                else if (files.AnibndFromMod) ImGui.TextColored(Theme.Ok, "mod");
                else
                {
                    if (ImGui.SmallButton($"Copy to mod##sas_cp_{c.ChrId}")) _app.CopyCharacterToMod(c.ChrId);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{c.ChrId} only exists in the game folder, which is never written to,\n" +
                                         $"so its animations can't be saved. This copies chr\\{c.ChrId}.anibnd.dcx into the mod\n" +
                                         "(the game file stays untouched) and loads the mod copy.");
                }
                if (_renameChr == c.ChrId)
                {
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.IsWindowAppearing() || !_renameFocused) { ImGui.SetKeyboardFocusHere(); _renameFocused = true; }
                    if (ImGui.InputTextWithHint($"##sas_chr_rename_{c.ChrId}", "Name (e.g. Owl), Enter to apply", ref _renameText, 64,
                            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                    {
                        _app.RenameCharacter(c.ChrId, _renameText);
                        _renameChr = null;
                        _renameFocused = false;
                    }
                    else if (_renameFocused && !ImGui.IsItemActive() && ImGui.GetFrameCount() > _renameStartFrame + 2)
                    {
                        _renameChr = null; // clicked elsewhere / Esc: cancel
                        _renameFocused = false;
                    }
                }
            }
            if (remove != null) _app.RemoveCharacter(remove);

            ImGui.SetNextItemWidth(90);
            bool enter = ImGui.InputTextWithHint("##sas_add_chr", "c0000", ref _addChr, 16, ImGuiInputTextFlags.EnterReturnsTrue);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Character id from the chr folder, e.g. c5060 (Owl), c5400 (Isshin), c5000 (True Monk).");
            ImGui.SameLine();
            if (ImGui.Button("Add##sas_add_chr_btn") || enter)
            {
                _addChrError = _app.AddCharacter(_addChr);
                if (_addChrError == null) _addChr = "";
            }
            if (_addChrError != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                ImGui.TextWrapped(_addChrError);
                ImGui.PopStyleColor();
            }
        }

        private bool _renameFocused;
        private int _renameStartFrame;

        private string _chrEdit = "";
        private string? _chrEditKey;

        /// <summary>Character (model + animations) for the active script, loaded into DSAS's viewport/timeline.</summary>
        private void BuildCharacter(SoulsAiStudio.Core.Project.ProjectScript script)
        {
            ImGui.Separator();
            var files = _app.ResolveCharacter(script);
            if (_chrEditKey != script.Key) { _chrEditKey = script.Key; _chrEdit = script.Character; }

            ImGui.Text("Character");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputTextWithHint("##sas_chr", files?.ChrId ?? "c0000", ref _chrEdit, 16, ImGuiInputTextFlags.EnterReturnsTrue))
                _app.SetCharacter(script, _chrEdit);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Leave empty to guess from the script id (e.g. 506300 → c5060). Enter to apply.");
            ImGui.SameLine();
            bool loaded = files != null && _app.Bridge.IsLoaded(files);
            if (files == null) ImGui.TextColored(Theme.Warn, "not found");
            else if (!files.AnibndFromMod)
            {
                if (ImGui.Button("Copy to mod##sas_chr_copy")) _app.CopyCharacterToMod(files.ChrId);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{files.ChrId} only exists in the game folder, which is never written to (nor opened, since DS Anim Studio\n" +
                                     "writes its workspace files next to what it opens). This copies the anibnd into the mod's chr folder and loads it.");
            }
            else if (ImGui.Button(loaded ? "Reload##sas_chr_load" : "Load model & anims##sas_chr_load")) _app.LoadCharacter(script);
            if (files != null)
            {
                ImGui.TextDisabled($"{files.ChrId} ({(files.AnibndFromMod ? "mod" : "game")}){(loaded ? " – loaded" : "")}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(files.AnibndPath + "\n" + (files.ChrbndPath ?? "(no chrbnd)"));
            }
        }

        private void BuildOutline()
        {
            var o = _editor.Active;
            ImGui.Text("Goals");
            if (o?.Doc == null) { ImGui.TextDisabled("(no script open)"); return; }

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##sas_outline_filter", "Filter goals", ref _filter, 64);
            ImGui.BeginChild("##sas_outline", Vector2.Zero, false);
            try
            {
            var current = o.GoalAtCursor;
            foreach (var group in o.Outline.GroupBy(g => g.Group).OrderBy(g => g.Key))
            {
                var items = group.Where(g => _filter.Length == 0 || g.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (items.Count == 0) continue;
                ImGui.SetNextItemOpen(group.Key != GoalGroup.Act || _filter.Length > 0, ImGuiCond.Appearing);
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.GroupHeader);
                bool open = ImGui.TreeNode($"{group.Key} ({items.Count})##grp{group.Key}");
                ImGui.PopStyleColor();
                if (!open) continue;
                try
                {
                foreach (var g in items)
                {
                    if (ImGui.Selectable($"{g.QualifiedName}##ol{g.Line}", g == current)) _editor.JumpTo(g.Line);
                    if (ImGui.IsItemHovered())
                    {
                        string anims = g.AnimIds.Count == 0 ? "-" : string.Join(", ", g.AnimIds.Distinct().Take(12));
                        ImGui.SetTooltip($"function ({g.Parameters})\nLine {g.Line + 1}–{g.EndLine + 1}\n" +
                                         $"AddSubGoal: {g.AddSubGoalCount}\nAnims: {anims}");
                    }
                    if (g.AddSubGoalCount > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{g.AddSubGoalCount}");
                    }
                }
                }
                finally { ImGui.TreePop(); }
            }
            }
            finally { ImGui.EndChild(); }
        }
    }
}
