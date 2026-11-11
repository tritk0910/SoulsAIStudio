#nullable enable
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.App
{
    /// <summary>Left panel: the project's AI scripts + goal outline of the active script (ui-spec §2c).</summary>
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
            bool visible = ImGui.Begin("Scripts##sas_scripts", ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                ImGui.TextColored(Theme.Accent, project.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{project.FilePath}\nMod: {project.ModDir}");

                ImGui.Separator();
                ImGui.Text("AI scripts");
                foreach (var s in project.Scripts)
                {
                    bool active = _editor.ActiveScript?.Key == s.Key;
                    if (ImGui.Selectable($"{s.Label}##{s.Key}", active)) _editor.Open(s);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{s.Luabnd} / {s.EntryPath}");
                }

                ImGui.Separator();
                BuildOutline();
            }
            finally { ImGui.End(); }
            return focused;
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
