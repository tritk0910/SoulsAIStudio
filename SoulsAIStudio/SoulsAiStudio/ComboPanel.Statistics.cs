#nullable enable
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Statistics" of the From Lua tab: runs the Random path simulation (<see cref="ComboSimulator"/>) N times without
    /// playing anything or touching the user's choices, then shows the most frequent paths, how often each anim is reached
    /// and how often each SpEffect interrupt fires. Plus the "Attacks hit" switch for on-hit SpEffects.
    /// </summary>
    public sealed partial class ComboPanel
    {
        private ComboStatistics? _stats;
        private bool _statsAttacksHit;
        private int _statsRuns = 1000;
        private bool _statsOpen = true;

        private void DrawStatisticsButton()
        {
            if (ImGui.Button("Statistics") && _nodes.Count > 0)
            {
                _statsRuns = Math.Clamp(_statsRuns, 1, 100_000);
                _stats = NewSimulator().Run(_nodes, _statsRuns, new Random(), topPaths: 10);
                _statsAttacksHit = _attacksHit;
                _statsOpen = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs \"Random path\" many times (nothing is played, your choices are kept) and shows the most\n" +
                                 "frequent paths, how often each anim is reached and how often each SpEffect interrupt fires.");
            SameLineIfFits(70);
            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("##sas_combo_runs", ref _statsRuns, 0, 0);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Number of simulated runs");
            SameLineIfFits(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize("Attacks hit").X);
            // Results on screen follow the switch: re-run with the same number of runs (fast, nothing is played).
            if (ImGui.Checkbox("Attacks hit", ref _attacksHit) && _stats != null && _nodes.Count > 0)
            {
                _stats = NewSimulator().Run(_nodes, _stats.Runs, new Random(), topPaths: 10);
                _statsAttacksHit = _attacksHit;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: the anims' attacks / bullets hit the target, so SpEffects applied on hit (AtkParam / Bullet\n" +
                                 "spEffectId0..4) can fire Goal.Interrupt in Random path and Statistics. Off: they never fire.\n" +
                                 "On-hit branches are conditional in game: compare both settings.");
        }

        private void DrawStatistics()
        {
            if (_stats is not { } st) return;
            ImGui.SetNextItemOpen(_statsOpen);
            string header = $"Statistics: {st.Runs} runs, attacks hit {(_statsAttacksHit ? "on" : "off")}, {st.DistinctPaths} different path{(st.DistinctPaths == 1 ? "" : "s")} ({st.Elapsed.TotalMilliseconds:0} ms)###sas_combo_stats";
            _statsOpen = ImGui.CollapsingHeader(header);
            if (!_statsOpen) return;
            ImGui.BeginChild("##sas_combo_stats_body", new Vector2(0, Math.Min(260, ImGui.GetContentRegionAvail().Y * 0.5f)), true);
            try
            {
                ImGui.TextDisabled("Most frequent paths (click: play it)");
                int i = 0;
                foreach (var p in st.Paths)
                {
                    string text = p.Items.Count == 0 ? "(no anims)"
                        : string.Join(" → ", p.Items.Select(x => x.SpEffectId is int sp ? $"{x.Step.AnimId} (cut by {_app.SpEffectText(sp)})" : x.Step.AnimId.ToString()));
                    if (ImGui.Selectable($"{st.Percent(p.Count),6:0.0}%  {text}##stp{i++}") && p.Items.Count > 0) Play(p.Items);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Esc($"{p.Count} of {st.Runs} runs\n{text}"));
                }
                if (st.DistinctPaths > st.Paths.Count) ImGui.TextDisabled($"… {st.DistinctPaths - st.Paths.Count} more paths");

                ImGui.Spacing();
                ImGui.TextDisabled("Anims reached");
                foreach (var a in st.Anims)
                {
                    ImGui.Text(Esc($"{st.Percent(a.Count),6:0.0}%  {a.AnimId}" + (a.Cut > 0 ? $"   (cut by a SpEffect in {st.Percent(a.Cut):0.0}%)" : "")));
                }

                ImGui.Spacing();
                ImGui.TextDisabled("SpEffect interrupts fired");
                if (st.Interrupts.Count == 0) ImGui.TextDisabled("  none" + (_statsAttacksHit ? "" : " (on-hit SpEffects are off: \"Attacks hit\")"));
                foreach (var x in st.Interrupts)
                {
                    ImGui.Text(Esc($"{st.Percent(x.Count),6:0.0}%  SpEffect {_app.SpEffectText(x.SpEffectId)}  cuts {x.CutAnimId}"));
                    _app.SpEffectLabelContextMenu($"##st_sp_ctx{x.SpEffectId}_{x.CutAnimId}", x.SpEffectId);
                }
            }
            finally { ImGui.EndChild(); }
        }
    }
}
