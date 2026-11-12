#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Combo player (CLAUDE.md P2, first step): shows the AddSubGoal sequence of the goal under the cursor with its
    /// if/elseif/else branches (random % where known), lets the user pick a path (or roll Random), and plays it in the
    /// viewport through DSAS's combo player, cutting each anim at "End If AI ComboAttack Queued".
    /// Not yet: SpEffect / OnHit interrupt branches (needs TAE SpEffect index + params).
    /// </summary>
    public sealed class ComboPanel
    {
        public const string WindowName = "Combo##sas_combo";
        private readonly SasApp _app;
        private readonly LuaEditorPanel _editor;
        private readonly Random _rng = new();
        private readonly Dictionary<int, int> _choice = new(); // branch line -> arm index
        private string? _cacheKey;
        private IReadOnlyList<ComboNode> _nodes = Array.Empty<ComboNode>();
        private GoalFunction? _goal;
        private bool _follow = true;
        private List<DsasBridge.ComboPlan>? _plan;
        public bool IsOpen = true;

        public ComboPanel(SasApp app, LuaEditorPanel editor)
        {
            _app = app;
            _editor = editor;
        }

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            if (!IsOpen) return false;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(340, vp.WorkSize.Y - 340), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(560, 300), ImGuiCond.FirstUseEver);
            bool focused = false;
            bool visible = Theme.BeginOpaque(WindowName, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                Refresh();
                BuildContents();
            }
            finally { ImGui.End(); }
            return focused;
        }

        private void Refresh()
        {
            var o = _editor.Active;
            if (o?.Doc == null) { _goal = null; _nodes = Array.Empty<ComboNode>(); return; }
            var goal = _follow ? o.GoalAtCursor ?? _goal : _goal;
            if (goal != null && _goal != null && goal.Name != _goal.Name) goal = o.Outline.FirstOrDefault(g => g.Name == goal.Name) ?? goal;
            string key = $"{o.Script.Key}|{goal?.Name}|{o.OutlineVersion}";
            if (key == _cacheKey) return;
            _cacheKey = key;
            _goal = goal;
            _choice.Clear();
            _plan = null;
            _nodes = goal == null ? Array.Empty<ComboNode>() : ComboSequence.Build(o.Editor.Text, goal);
        }

        private void BuildContents()
        {
            if (_goal == null) { ImGui.TextDisabled("Put the cursor inside a goal (e.g. Goal.Act50 or Goal.Kengeki03)."); return; }

            ImGui.TextColored(Theme.Accent, _goal.QualifiedName);
            ImGui.SameLine();
            ImGui.TextDisabled($"line {_goal.Line + 1}");
            ImGui.SameLine();
            ImGui.Checkbox("Follow cursor", ref _follow);

            var path = ComboSequence.Flatten(_nodes, ChosenArm);
            bool loaded = _app.Bridge.LoadedAnibnd != null;
            if (!loaded) ImGui.TextColored(Theme.Warn, "Load the character first (Scripts panel → Load model & anims).");

            if (ImGui.Button("▶ Play combo") && path.Count > 0) Play(path);
            ImGui.SameLine();
            if (ImGui.Button("Random path")) { RollRandom(); Play(ComboSequence.Flatten(_nodes, ChosenArm)); }
            ImGui.SameLine();
            if (ImGui.Button(_app.Bridge.ComboPaused ? "▶ Resume" : "|| Pause")) _app.Bridge.ToggleComboPause();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Space also pauses/resumes the combo");
            ImGui.SameLine();
            if (ImGui.Button("■ Stop")) { _app.Bridge.StopCombo(); }
            ImGui.SameLine();
            ImGui.TextDisabled(path.Count == 0 ? "no anims in this path" : string.Join(" → ", path.Select(s => s.AnimId)));

            ImGui.Separator();
            ImGui.BeginChild("##sas_combo_tree", new Vector2(0, _plan != null ? -110 : 0), false);
            try { DrawNodes(_nodes, 0); }
            finally { ImGui.EndChild(); }

            if (_plan != null) DrawPlan();
        }

        private int ChosenArm(ComboBranch b) => _choice.TryGetValue(b.Line, out int k) ? k : 0;

        private void RollRandom()
        {
            foreach (var b in ComboSequence.Branches(_nodes))
            {
                var weights = b.Arms.Select(a => a.Probability ?? 100.0 / b.Arms.Count).ToArray();
                double roll = _rng.NextDouble() * weights.Sum(), acc = 0;
                int pick = weights.Length - 1;
                for (int k = 0; k < weights.Length; k++) { acc += weights[k]; if (roll < acc) { pick = k; break; } }
                _choice[b.Line] = pick;
            }
        }

        private void Play(List<ComboStep> path)
        {
            _plan = _app.Bridge.PlayCombo(path.Select(s => s.AnimId).ToList());
            if (_plan == null) _app.Notify("Load the character first (Scripts panel → Load model & anims).");
        }

        private void DrawNodes(IReadOnlyList<ComboNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                ImGui.PushID(n.Line * 31 + depth);
                try
                {
                    switch (n)
                    {
                        case ComboStep s:
                            ImGui.Indent(depth * 14 + 2);
                            if (ImGui.Selectable($"{s.AnimId}   {ShortGoal(s.GoalType)}##step{s.Line}"))
                            {
                                _editor.JumpTo(s.Line);
                                _app.PlayAnim(s.AnimId);
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Line {s.Line + 1}: {s.GoalType}\nClick: jump to code + play this anim");
                            ImGui.Unindent(depth * 14 + 2);
                            break;
                        case ComboClear:
                            ImGui.Indent(depth * 14 + 2);
                            ImGui.TextDisabled("ClearSubGoal()");
                            ImGui.Unindent(depth * 14 + 2);
                            break;
                        case ComboBranch b:
                            ImGui.Indent(depth * 14 + 2);
                            int chosen = ChosenArm(b);
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.BeginCombo($"##br{b.Line}", ArmLabel(b.Arms[chosen])))
                            {
                                try
                                {
                                    for (int k = 0; k < b.Arms.Count; k++)
                                        if (ImGui.Selectable(ArmLabel(b.Arms[k]) + $"##arm{k}", k == chosen)) { _choice[b.Line] = k; _plan = null; }
                                }
                                finally { ImGui.EndCombo(); }
                            }
                            ImGui.Unindent(depth * 14 + 2);
                            DrawNodes(b.Arms[chosen].Body, depth + 1);
                            break;
                    }
                }
                finally { ImGui.PopID(); }
            }
        }

        private void DrawPlan()
        {
            ImGui.Separator();
            int playing = _app.Bridge.ComboIndex;
            int idx = 0;
            ImGui.BeginChild("##sas_combo_plan", Vector2.Zero, false);
            try
            {
                foreach (var p in _plan!)
                {
                    if (p.Missing) { ImGui.TextColored(Theme.Error, $"{p.AnimId}: {p.CutReason}"); continue; }
                    var col = idx == playing ? Theme.Ok : Theme.Dim;
                    ImGui.TextColored(col, $"{(idx == playing ? "▶" : " ")} {p.AnimId}  frames 0–{p.EndFrame:0} / {p.TotalFrames:0}  ({p.CutReason})");
                    idx++;
                }
            }
            finally { ImGui.EndChild(); }
        }

        private static string ArmLabel(ComboArm a) =>
            (a.Condition == "else" ? "else" : a.Condition.StartsWith("(") ? a.Condition : "if " + a.Condition)
            + (a.Probability is double p ? $"   ({p:0.#}%)" : "");

        private static string ShortGoal(string goalType) => goalType.StartsWith("GOAL_COMMON_") ? goalType["GOAL_COMMON_".Length..] : goalType;
    }
}
