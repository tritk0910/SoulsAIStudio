#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Combo;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Combo window (CLAUDE.md P2). Tab "From Lua": the AddSubGoal sequence of the goal under the cursor with its
    /// if/elseif/else branches and SpEffect interrupt branches; pick a path (or Random) and play it, each anim cut at
    /// "End If AI ComboAttack Queued" or at the chosen SpEffect. Tab "Manual": a typed combo script
    /// (<see cref="ComboScriptTab"/>, replaces DSAS's Combo Viewer). Not yet: OnHit SpEffect sources (needs params).
    /// </summary>
    public sealed class ComboPanel
    {
        public const string WindowName = "Combo##sas_combo";
        private readonly SasApp _app;
        private readonly LuaEditorPanel _editor;
        private readonly Random _rng = new();
        private readonly Dictionary<int, int> _choice = new(); // branch line -> arm index
        private readonly Dictionary<(int Line, int Depth), int> _interrupt = new(); // step -> chosen SpEffect candidate (-1 none)
        private readonly Dictionary<int, IReadOnlyList<(int SpEffectId, float Frame)>> _spEvents = new(); // anim -> TAE SpEffects
        private string? _spEventsAnibnd;
        private SpEffectInterrupts.Table? _handlers;
        private const int MaxInterruptDepth = 6;
        private string? _cacheKey;
        private IReadOnlyList<ComboNode> _nodes = Array.Empty<ComboNode>();
        private GoalFunction? _goal;
        private bool _follow = true;
        private List<DsasBridge.ComboPlan>? _plan;
        public bool IsOpen = true;
        private readonly ComboScriptTab _manual;
        private bool _selectManual;

        public ComboPanel(SasApp app, LuaEditorPanel editor)
        {
            _app = app;
            _editor = editor;
            _manual = new ComboScriptTab(app, id => _handlers != null && _handlers.Handlers.TryGetValue(id, out var h) && !h.Replans && ComboSequence.AffectsCombo(h.Body) ? h : null);
        }

        /// <summary>BeginTabItem with flags but without a close button (ImGui.NET 1.87 only has the p_open overload).</summary>
        private static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(label + "\0");
            fixed (byte* p = bytes) return ImGuiNative.igBeginTabItem(p, null, flags) != 0;
        }

        /// <summary>Opens the window on the Manual tab (Tools menu / F8, replacing DSAS's Combo Viewer).</summary>
        public void OpenManual()
        {
            IsOpen = true;
            _selectManual = true;
            ImGui.SetWindowFocus(WindowName);
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
                if (ImGui.BeginTabBar("##sas_combo_tabs"))
                {
                    if (ImGui.BeginTabItem("From Lua")) { BuildContents(); ImGui.EndTabItem(); }
                    if (BeginTabItem("Manual", _selectManual ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    {
                        _manual.Build();
                        ImGui.EndTabItem();
                    }
                    _selectManual = false;
                    ImGui.EndTabBar();
                }
                _manual.Tick();
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
            _interrupt.Clear();
            _plan = null;
            string text = o.Editor.Text;
            _nodes = goal == null ? Array.Empty<ComboNode>() : ComboSequence.Build(text, goal);
            _handlers = SpEffectInterrupts.Build(text);
        }

        private void BuildContents()
        {
            if (_goal == null) { ImGui.TextDisabled("Put the cursor inside a goal (e.g. Goal.Act50 or Goal.Kengeki03)."); return; }

            ImGui.TextColored(Theme.Accent, _goal.QualifiedName);
            ImGui.SameLine();
            ImGui.TextDisabled($"line {_goal.Line + 1}");
            ImGui.SameLine();
            ImGui.Checkbox("Follow cursor", ref _follow);

            if (_spEventsAnibnd != _app.Bridge.LoadedAnibnd) { _spEvents.Clear(); _spEventsAnibnd = _app.Bridge.LoadedAnibnd; }
            var path = BuildPath(_nodes, 0);
            bool loaded = _app.Bridge.LoadedAnibnd != null;
            if (!loaded) ImGui.TextColored(Theme.Warn, "Load the character first (Scripts panel → Load model & anims).");

            if (ImGui.Button("▶ Play combo") && path.Count > 0) Play(path);
            ImGui.SameLine();
            if (ImGui.Button("Random path")) { RollRandom(); Play(BuildPath(_nodes, 0)); }
            ImGui.SameLine();
            if (ImGui.Button(_app.Bridge.ComboPaused ? "▶ Resume" : "|| Pause")) _app.Bridge.ToggleComboPause();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Space also pauses/resumes the combo");
            ImGui.SameLine();
            if (ImGui.Button("■ Stop")) { _app.Bridge.StopCombo(); }
            ImGui.SameLine();
            if (ImGui.Button("→ Manual") && path.Count > 0)
            {
                _manual.SetText($"# From Lua: Goal.{_goal.Name} (line {_goal.Line + 1})\n" + ComboScript.Format(path.Select(p =>
                    (p.Step.AnimId, p.SpEffectId is int sp ? new ComboCut(-1, sp) : ComboCut.ComboAttackQueued)).ToList()));
                _selectManual = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this path (with its SpEffect interrupts) into the Manual tab as a combo script to edit");
            ImGui.TextDisabled(path.Count == 0 ? "no anims in this path"
                : string.Join(" → ", path.Select(p => p.Cut != null ? p.Step.AnimId + " (SpEffect cut)" : p.Step.AnimId.ToString())));

            ImGui.Separator();
            ImGui.BeginChild("##sas_combo_tree", new Vector2(0, _plan != null ? -110 : 0), false);
            try { DrawNodes(_nodes, 0, 0); }
            finally { ImGui.EndChild(); }

            if (_plan != null) DrawPlan();
        }

        private int ChosenArm(ComboBranch b) => _choice.TryGetValue(b.Line, out int k) ? k : 0;

        private void RollRandom()
        {
            foreach (var b in ComboSequence.Branches(_nodes).Concat(_handlers?.Handlers.Values.SelectMany(h => ComboSequence.Branches(h.Body)) ?? Enumerable.Empty<ComboBranch>()))
            {
                var weights = b.Arms.Select(a => a.Probability ?? 100.0 / b.Arms.Count).ToArray();
                double roll = _rng.NextDouble() * weights.Sum(), acc = 0;
                int pick = weights.Length - 1;
                for (int k = 0; k < weights.Length; k++) { acc += weights[k]; if (roll < acc) { pick = k; break; } }
                _choice[b.Line] = pick;
            }
            _interrupt.Clear();
            RollInterrupts(_nodes, 0);
            _plan = null;
        }

        /// <summary>
        /// Random path, SpEffect part: walks the (already rolled) path; on each anim the TAE SpEffects are tried in time
        /// order and each fires with its Goal.Interrupt arm's random chance (<c>and rand &lt;= N</c>, else always). The
        /// first that fires cuts the anim and its handler chain is rolled the same way. SpEffects at/after the anim's
        /// "ComboAttack Queued" frame are skipped when another anim is queued after it (the anim is cut before them).
        /// </summary>
        private void RollInterrupts(IReadOnlyList<ComboNode> nodes, int level)
        {
            if (level >= MaxInterruptDepth) return;
            var steps = ComboSequence.Flatten(nodes, ChosenArm);
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                var c = Candidates(s.AnimId);
                float? queued = i < steps.Count - 1 ? _app.Bridge.GetComboQueuedFrame(s.AnimId) : null;
                int pick = -1;
                for (int k = 0; k < c.Count && pick < 0; k++)
                {
                    if (queued is float q && c[k].Frame >= q) continue;
                    if (_rng.NextDouble() * 100 < c[k].Handler.ChancePercent) pick = k;
                }
                _interrupt[(s.Line, level)] = pick;
                if (pick < 0) continue;
                RollInterrupts(c[pick].Handler.Body, level + 1);
                if (ComboSequence.ClearsOnPath(c[pick].Handler.Body, ChosenArm)) return;
            }
        }

        /// <summary>A step on the played path; Cut = frame where a chosen SpEffect interrupt cuts it.</summary>
        private sealed record PathItem(ComboStep Step, float? Cut, int? SpEffectId = null);

        /// <summary>
        /// Flattens the chosen branches; where the user picked a SpEffect interrupt on a step, that anim is cut at the
        /// SpEffect's frame and the handler's sequence follows (replacing the rest when it starts with ClearSubGoal,
        /// queued after it otherwise).
        /// </summary>
        private List<PathItem> BuildPath(IReadOnlyList<ComboNode> nodes, int depth)
        {
            var result = new List<PathItem>();
            var steps = ComboSequence.Flatten(nodes, ChosenArm);
            List<PathItem>? appendAfter = null;
            foreach (var s in steps)
            {
                var chosen = ChosenInterrupt(s, depth);
                if (chosen == null || depth >= MaxInterruptDepth) { result.Add(new PathItem(s, null)); continue; }
                var (frame, handler) = chosen.Value;
                var rest = BuildPath(handler.Body, depth + 1);
                bool clears = ComboSequence.ClearsOnPath(handler.Body, ChosenArm);
                // The chosen arm neither clears nor adds anims (e.g. random arm "no branch taken"): the anim keeps playing.
                if (!clears && rest.Count == 0) { result.Add(new PathItem(s, null)); continue; }
                result.Add(new PathItem(s, frame, handler.SpEffectId));
                if (clears) { result.AddRange(rest); return result; }
                (appendAfter ??= new()).AddRange(rest);
            }
            if (appendAfter != null) result.AddRange(appendAfter);
            return result;
        }

        private IReadOnlyList<(int SpEffectId, float Frame, SpEffectHandler Handler)> Candidates(int animId)
        {
            if (_handlers == null) return Array.Empty<(int, float, SpEffectHandler)>();
            var events = SpEvents(animId);
            var result = new List<(int, float, SpEffectHandler)>();
            foreach (var e in events)
                if (_handlers.Handlers.TryGetValue(e.SpEffectId, out var h) && !h.Replans && ComboSequence.AffectsCombo(h.Body)) result.Add((e.SpEffectId, e.Frame, h));
            return result;
        }

        /// <summary>TAE SpEffect events of an anim. Empty results are not cached: the anibnd may still be loading.</summary>
        private IReadOnlyList<(int SpEffectId, float Frame)> SpEvents(int animId)
        {
            if (_spEvents.TryGetValue(animId, out var events)) return events;
            events = _app.Bridge.GetSpEffectEvents(animId);
            if (events.Count > 0) _spEvents[animId] = events;
            return events;
        }

        /// <summary>Tooltip text: every SpEffect event in the anim's TAE and whether Goal.Interrupt handles it.</summary>
        private string SpEffectSummary(int animId)
        {
            var events = SpEvents(animId);
            if (events.Count == 0) return "TAE SpEffect events: none (or character not loaded)";
            var lines = events.Select(e =>
            {
                string how = _handlers != null && _handlers.Handlers.TryGetValue(e.SpEffectId, out var h)
                    ? (h.Replans ? "Interrupt: Replanning()" : $"Interrupt line {h.Line + 1}")
                    : "no handler in Goal.Interrupt";
                return $"  {e.SpEffectId} @ frame {e.Frame:0}  ({how})";
            });
            return "TAE SpEffect events:\n" + string.Join("\n", lines);
        }

        private (float Frame, SpEffectHandler Handler)? ChosenInterrupt(ComboStep s, int depth)
        {
            if (!_interrupt.TryGetValue((s.Line, depth), out int k) || k < 0) return null;
            var c = Candidates(s.AnimId);
            return k < c.Count ? (c[k].Frame, c[k].Handler) : null;
        }

        private void DrawInterruptChoice(ComboStep s, int depth)
        {
            var c = Candidates(s.AnimId);
            if (c.Count == 0) return;
            int k = _interrupt.TryGetValue((s.Line, depth), out int v) ? v : -1;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X));
            string label = k >= 0 && k < c.Count
                ? $"SpEffect {c[k].SpEffectId} @ f{c[k].Frame:0} ({c[k].Frame / 30f:0.00} s) -> Interrupt"
                : $"No interrupt ({c.Count} SpEffect interrupt{(c.Count == 1 ? "" : "s")} available)";
            if (!ImGui.BeginCombo($"##sp{s.Line}_{depth}", label)) return;
            try
            {
                if (ImGui.Selectable("Continue normally (no interrupt)", k < 0)) { _interrupt[(s.Line, depth)] = -1; _plan = null; }
                for (int i = 0; i < c.Count; i++)
                {
                    var h = c[i].Handler;
                    string text = $"SpEffect {c[i].SpEffectId} @ frame {c[i].Frame:0} -> Goal.Interrupt line {h.Line + 1}"
                        + (h.ClearsQueue ? " (ClearSubGoal)" : ComboSequence.ContainsClear(h.Body) ? " (ClearSubGoal in a branch)" : "") + (h.Chance is double ch ? $" [{ch:0}%]" : "") + (h.HasExtraCondition ? " [conditional]" : "");
                    if (ImGui.Selectable(text + $"##c{i}", i == k)) { _interrupt[(s.Line, depth)] = i; _plan = null; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("if " + h.Condition + (h.HasExtraCondition ? "\n(the arm has extra conditions; it only fires when they hold)" : ""));
                }
            }
            finally { ImGui.EndCombo(); }
        }

        private void Play(List<PathItem> path)
        {
            _manual.StopLooping();
            _plan = _app.Bridge.PlayCombo(path.Select(p => (p.Step.AnimId, p.Cut)).ToList());
            if (_plan == null) _app.Notify("Load the character first (Scripts panel → Load model & anims).");
        }

        private void DrawNodes(IReadOnlyList<ComboNode> nodes, int depth, int level)
        {
            // depth = indent; level = SpEffect interrupt nesting (key of _interrupt, same as BuildPath).
            foreach (var n in nodes)
            {
                ImGui.PushID(n.Line * 31 + depth * 7 + level);
                try
                {
                    switch (n)
                    {
                        case ComboStep s:
                            ImGui.Indent(depth * 14 + 2);
                            if (ImGui.Selectable($"{s.AnimId}   {ShortGoal(s.GoalType)}##step{s.Line}", false, ImGuiSelectableFlags.None,
                                    new Vector2(Candidates(s.AnimId).Count > 0 ? 210 : 0, 0)))
                            {
                                _editor.JumpTo(s.Line);
                                _app.PlayAnim(s.AnimId);
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Line {s.Line + 1}: {s.GoalType}\nClick: jump to code + play this anim\n\n" + SpEffectSummary(s.AnimId));
                            DrawInterruptChoice(s, level);
                            ImGui.Unindent(depth * 14 + 2);
                            if (ChosenInterrupt(s, level) is { } ci && level < MaxInterruptDepth)
                            {
                                ImGui.Indent((depth + 1) * 14 + 2);
                                ImGui.TextColored(Theme.Warn, $"SpEffect {ci.Handler.SpEffectId} @ frame {ci.Frame:0} -> Goal.Interrupt (line {ci.Handler.Line + 1})" + (ComboSequence.ClearsOnPath(ci.Handler.Body, ChosenArm) ? ", ClearSubGoal: replaces the rest of the combo" : ", no ClearSubGoal: queued after the rest"));
                                ImGui.Unindent((depth + 1) * 14 + 2);
                                DrawNodes(ci.Handler.Body, depth + 1, level + 1);
                            }
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
                            DrawNodes(b.Arms[chosen].Body, depth + 1, level);
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
