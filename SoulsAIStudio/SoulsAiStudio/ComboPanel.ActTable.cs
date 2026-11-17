#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Combo window, "From Lua" tab, when the cursor is in Goal.Activate / Goal.Kengeki_Activate (any function that hands
    /// a weight table to <c>Common_*_Activate</c>): the act weight table for a scenario (distance, the parry SpEffect,
    /// HP, ticked conditions), editable. Every change is one text edit in the Lua editor (Ctrl+Z undoes it).
    /// </summary>
    public sealed partial class ComboPanel
    {
        private WeightTable? _wt;
        private string? _wtKey;
        private long _wtVersion = -1;
        private DateTime _wtBuilt;
        private bool _wtForce;
        private WeightEvaluation? _wtEval;
        private readonly Dictionary<string, WeightScenario> _scenarios = new(); // per script|goal
        private readonly Dictionary<string, float> _wtEditing = new(); // cell id -> value being typed
        /// <summary>Set when "Open" on a row switched this tab to an act: the table goal to come back to.</summary>
        private string? _backTo;
        private bool _followBeforeOpen;
        private int _addSlot = -1;
        private float _addWeight = 100;
        private int _newCaseId;
        private int _editCaseId;
        private string _atomFilter = "";

        /// <summary>The weight table for the current goal, rebuilt when the text changes (at most every 250 ms while typing).</summary>
        private WeightTable? CurrentWeightTable()
        {
            var o = _editor.Active;
            // Weight tables live in Goal.Activate / Goal.Kengeki_Activate (Act/Kengeki goals never call Common_*_Activate).
            if (o?.Doc == null || _goal == null || _start != null || !_goal.Name.Contains("Activate", StringComparison.Ordinal)) return null;
            string key = $"{o.Script.Key}|{_goal.Name}";
            long v = o.Editor.Version;
            bool stale = key != _wtKey || v != _wtVersion;
            if (stale && (key != _wtKey || _wtForce || DateTime.UtcNow - _wtBuilt > TimeSpan.FromMilliseconds(250)))
            {
                _wt = WeightTable.BuildFor(o.Editor.Text, _goal.Name);
                _wtKey = key;
                _wtVersion = v;
                _wtBuilt = DateTime.UtcNow;
                _wtForce = false;
                _wtEval = null;
                _wtEditing.Clear();
            }
            return _wt;
        }

        private WeightScenario Scenario(WeightTable wt)
        {
            string key = $"{_editor.Active?.Script.Key}|{wt.Goal.Name}";
            if (!_scenarios.TryGetValue(key, out var s))
            {
                // Start on a labelled kengeki SpEffect (e.g. 200210 strong parry right) when there is one.
                int? sel = wt.SelectorValues.Cast<int?>().FirstOrDefault(id => _app.SpEffectLabels.Get(id!.Value) != null) ?? wt.SelectorValues.Cast<int?>().FirstOrDefault();
                s = new WeightScenario { Distance = 2, Selector = sel };
                _scenarios[key] = s;
            }
            return s;
        }

        /// <summary>Applies a text edit from the table to the Lua editor (one undo step) and rebuilds the table now.</summary>
        private bool ApplyWeightEdit(TextEdit? e)
        {
            var o = _editor.Active;
            if (e == null || o == null) return false;
            if (o.Editor.Version != _wtVersion) { _wtForce = true; return false; } // text changed under us: rebuild first
            bool ok = o.Editor.ApplyEdit(e.StartLine, e.StartColumn, e.EndLine, e.EndColumn, e.Text);
            if (!ok) _app.Notify("The script is read-only here (open a project with a mod folder to edit).");
            _wtForce = true;
            return ok;
        }

        private void DrawWeightTable(WeightTable wt)
        {
            var s = Scenario(wt);
            var e = _wtEval ??= wt.Evaluate(s);
            bool changed = false;

            ImGui.TextDisabled($"{wt.Picker} picks one {wt.TableVar}[N] at random, by weight.");
            if (ImGui.IsItemHovered())
                Tip($"{wt.Goal.QualifiedName} sets {wt.TableVar}[N] = weight under its conditions; slot N runs the function registered with\n" +
                                 $"{wt.FuncVar}[N] = REGIST_FUNC(..., Goal.XXX). Pick a situation below to see (and edit) the weights the AI uses then.\n\n" +
                                 "Edits are written straight into the Lua editor (Ctrl+Z undoes them; save as usual).");

            // ---- scenario
            if (wt.SelectorVar != null) changed |= DrawSelector(wt, s);
            if (wt.UsesDistance)
            {
                float d = (float)s.Distance;
                float max = (float)Math.Max(10, Math.Ceiling(wt.DistanceThresholds.Max() * 1.5));
                ImGui.TextDisabled("Distance");
                ImGui.SameLine(LabelWidth);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##wt_dist", ref d, 0, max, "%.1f m")) { s.Distance = Math.Round(d, 1); changed = true; }
                if (ImGui.IsItemHovered())
                    Tip($"Distance to the target ({string.Join(", ", wt.DistanceThresholds.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture)))} m are used in conditions).\nCtrl+Click to type a value.");
                // Quick buttons: one per distance band.
                var cuts = wt.DistanceThresholds.ToList();
                ImGui.Dummy(new Vector2(LabelWidth - 8, 0));
                for (int k = 0; k <= cuts.Count; k++)
                {
                    double lo = k == 0 ? 0 : cuts[k - 1], hi = k == cuts.Count ? lo + 5 : cuts[k];
                    double mid = k == cuts.Count ? lo + 1 : k == 0 ? Math.Max(0, hi - 0.5) : (lo + hi) / 2;
                    string lbl = k == 0 ? $"< {Fmt(hi)}" : k == cuts.Count ? $"{Fmt(lo)}+" : $"{Fmt(lo)}–{Fmt(hi)}";
                    ImGui.SameLine();
                    bool inBand = s.Distance >= lo && (k == cuts.Count || s.Distance < hi);
                    if (inBand) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
                    if (ImGui.SmallButton(lbl + $"##band{k}")) { s.Distance = Math.Round(mid, 2); changed = true; }
                    if (inBand) ImGui.PopStyleColor();
                }
            }
            if (wt.UsesHp)
            {
                float hp = (float)(s.HpRate * 100);
                ImGui.TextDisabled("HP (self)");
                ImGui.SameLine(LabelWidth);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##wt_hp", ref hp, 0, 100, "%.0f %%")) { s.HpRate = hp / 100.0; changed = true; }
            }
            changed |= DrawAtoms(wt, s);
            if (changed) { _wtEval = null; e = _wtEval = wt.Evaluate(s); }

            ImGui.Separator();
            DrawCasePath(e);
            DrawWeightRows(wt, e);
            DrawAddSlot(wt, e);

            if (wt.Warnings.Count > 0 && ImGui.CollapsingHeader($"Warnings ({wt.Warnings.Count})##wt_warn"))
                foreach (var w in wt.Warnings) ImGui.TextColored(Theme.Warn, w.Replace("%", "%%"));
            ImGui.TextDisabled("Not counted: SetCoolTime cooldowns (see the clock column) and what " + wt.Picker + " does in aicommon.");
        }

        /// <summary>SetTooltip treats its text as a printf format: escape '%' (chances, Lua modulo).</summary>
        private static void Tip(string text) => ImGui.SetTooltip(text.Replace("%", "%%"));

        private static float LabelWidth => ImGui.CalcTextSize("Kengeki SpEffect").X + 16;

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Kengeki: which kengeki SpEffect ReturnKengekiSpecialEffect returned (how the player deflected/guarded).</summary>
        private bool DrawSelector(WeightTable wt, WeightScenario s)
        {
            bool changed = false;
            ImGui.TextDisabled("Kengeki SpEffect");
            ImGui.SameLine(LabelWidth);
            ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X - 150));
            string cur = s.Selector is double sel ? _app.SpEffectText((int)sel) : "(none: not a kengeki)";
            if (ImGui.BeginCombo("##wt_sel", cur, ImGuiComboFlags.HeightLarge))
            {
                try
                {
                    if (ImGui.Selectable("(none: not a kengeki)", s.Selector == null)) { s.Selector = null; changed = true; }
                    foreach (int id in wt.SelectorValues)
                    {
                        var arm = wt.SelectorArm(id);
                        if (ImGui.Selectable($"{_app.SpEffectText(id)}{(arm != null ? $"  (line {arm.Value.Arm.Line + 1})" : "")}##sel{id}", s.Selector == id)) { s.Selector = id; changed = true; }
                        if (ImGui.IsItemHovered()) Tip("Right-click: set a label for this SpEffect");
                        _app.SpEffectLabelContextMenu($"##sel_ctx{id}", id);
                    }
                }
                finally { ImGui.EndCombo(); }
            }
            else if (ImGui.IsItemHovered())
                Tip($"{wt.SelectorVar} = ReturnKengekiSpecialEffect(ai): the kengeki (sword clash) SpEffect on this enemy,\n" +
                                 "set by how its attack was deflected/guarded (e.g. 200210 strong parry right). Each \"== id\" arm\n" +
                                 "gives the weights of the Kengeki goals that answer it. Right-click: label it.");
            if (s.Selector is double sv) _app.SpEffectLabelContextMenu("##wt_sel_ctx", (int)sv);

            ImGui.SameLine();
            if (ImGui.Button("Change id##wt_chg") && s.Selector is double cv) { _editCaseId = (int)cv; ImGui.OpenPopup("##wt_chg_pop"); }
            if (ImGui.IsItemHovered()) Tip("Make this arm answer another kengeki SpEffect (edits \"== id\" in the code)");
            if (ImGui.BeginPopup("##wt_chg_pop"))
            {
                try
                {
                    ImGui.TextDisabled("SpEffect id for this arm");
                    ImGui.SetNextItemWidth(140);
                    if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                    ImGui.InputInt("##wt_chg_id", ref _editCaseId, 0);
                    if (_app.SpEffectDisplayName(_editCaseId) is { } n) ImGui.TextDisabled(n.Replace("%", "%%"));
                    bool dup = wt.SelectorValues.Contains(_editCaseId) && s.Selector != _editCaseId;
                    if (dup) ImGui.TextColored(Theme.Warn, "Another arm already tests this id (the first arm wins).");
                    if (ImGui.Button("Apply") && s.Selector is double old && wt.SelectorArm((int)old) is { } a && _editCaseId > 0)
                    {
                        if (ApplyWeightEdit(WeightTable.SetSelectorValue(a.Compare, _editCaseId))) { s.Selector = _editCaseId; changed = true; }
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                }
                finally { ImGui.EndPopup(); }
            }

            ImGui.SameLine();
            if (ImGui.Button("+ Case##wt_add_case")) { _newCaseId = 0; _addSlot = -1; _addWeight = 100; ImGui.OpenPopup("##wt_case_pop"); }
            if (ImGui.IsItemHovered()) Tip("Add an \"elseif " + wt.SelectorVar + " == id then\" arm for another kengeki SpEffect");
            if (ImGui.BeginPopup("##wt_case_pop"))
            {
                try
                {
                    ImGui.TextDisabled("Kengeki SpEffect id");
                    ImGui.SetNextItemWidth(140);
                    if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                    ImGui.InputInt("##wt_case_id", ref _newCaseId, 0);
                    if (_app.SpEffectDisplayName(_newCaseId) is { } n) { ImGui.SameLine(); ImGui.TextDisabled(n.Replace("%", "%%")); }
                    // Labelled kengeki SpEffects (2002xx) not handled yet.
                    foreach (var kv in _app.SpEffectLabels.All.Where(kv => kv.Key is >= 200200 and < 200300 && !wt.SelectorValues.Contains(kv.Key)).Take(12))
                        if (ImGui.SmallButton($"{kv.Key} {kv.Value}##lbl{kv.Key}")) _newCaseId = kv.Key;
                    ImGui.TextDisabled("First act of the arm");
                    SlotCombo(wt, "##wt_case_slot", ref _addSlot, Array.Empty<int>());
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80);
                    ImGui.InputFloat("weight##wt_case_w", ref _addWeight, 0, 0, "%g");
                    bool exists = wt.SelectorValues.Contains(_newCaseId);
                    if (exists) ImGui.TextColored(Theme.Warn, "An arm already tests this id.");
                    var edit = _newCaseId > 0 && _addSlot >= 0 && !exists ? wt.AddSelectorCase(_editor.Active!.Editor.Text, _newCaseId, _addSlot, _addWeight) : null;
                    if (_newCaseId > 0 && _addSlot >= 0 && !exists && edit == null) ImGui.TextColored(Theme.Warn, "Can't find where to insert (the if chain's else/end is not at the start of its line).");
                    ImGui.BeginDisabled(edit == null);
                    if (ImGui.Button("Add case"))
                    {
                        if (ApplyWeightEdit(edit)) { s.Selector = _newCaseId; changed = true; }
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                }
                finally { ImGui.EndPopup(); }
            }
            return changed;
        }

        /// <summary>Conditions the app can't compute: checkboxes (unticked = not met).</summary>
        private bool DrawAtoms(WeightTable wt, WeightScenario s)
        {
            if (wt.Atoms.Count == 0) return false;
            bool changed = false;
            int on = wt.Atoms.Count(a => s.TrueAtoms.Contains(a.Key));
            if (!ImGui.CollapsingHeader($"Other conditions ({on} of {wt.Atoms.Count} on)###wt_atoms")) return false;
            ImGui.TextDisabled("Unticked = not met. Tick the ones that hold in the situation you want to see.");
            if (wt.Atoms.Count > 8)
            {
                ImGui.SetNextItemWidth(-90);
                ImGui.InputTextWithHint("##wt_atom_filter", "filter", ref _atomFilter, 64);
                ImGui.SameLine();
                if (ImGui.SmallButton("Untick all")) { s.TrueAtoms.Clear(); changed = true; }
            }
            ImGui.BeginChild("##wt_atoms_list", new Vector2(0, Math.Min(wt.Atoms.Count, 8) * ImGui.GetFrameHeightWithSpacing() + 4), false);
            try
            {
                foreach (var a in wt.Atoms)
                {
                    if (_atomFilter.Length > 0 && !a.Key.Contains(_atomFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    bool v = s.TrueAtoms.Contains(a.Key);
                    if (ImGui.Checkbox($"{a.Key}##atom{a.Line}_{a.Key.GetHashCode()}", ref v))
                    {
                        if (v) s.TrueAtoms.Add(a.Key); else s.TrueAtoms.Remove(a.Key);
                        changed = true;
                    }
                    if (ImGui.IsItemHovered()) Tip($"First used on line {a.Line + 1}" + (a.Hint != null ? "\n" + a.Hint : "") + "\nRight-click: jump to the code");
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _editor.JumpTo(a.Line);
                }
            }
            finally { ImGui.EndChild(); }
            return changed;
        }

        private void DrawCasePath(WeightEvaluation e)
        {
            ImGui.TextDisabled("Case:");
            if (e.CasePath.Count == 0) { ImGui.SameLine(); ImGui.TextDisabled("(top level)"); }
            for (int k = 0; k < e.CasePath.Count; k++)
            {
                var arm = e.CasePath[k];
                ImGui.SameLine();
                string text = arm.Cond == null ? "else" : "if " + arm.Text;
                if (text.Length > 60) text = text[..57] + "...";
                if (ImGui.SmallButton($"{text}##case{k}_{arm.Line}")) _editor.JumpTo(arm.Line);
                if (ImGui.IsItemHovered()) Tip($"Line {arm.Line + 1}: {(arm.Cond == null ? "else" : arm.Text)}\nClick: jump to the code");
                if (k < e.CasePath.Count - 1) { ImGui.SameLine(); ImGui.TextDisabled(">"); }
            }
        }

        private void DrawWeightRows(WeightTable wt, WeightEvaluation e)
        {
            if (e.Returned) { ImGui.TextColored(Theme.Warn, "The function returns early in this situation (e.g. no kengeki SpEffect): no act is picked here."); return; }
            if (e.Rows.Count == 0) { ImGui.TextColored(Theme.Warn, "No weight > 0 in this situation: nothing is picked (or aicommon falls back)."); return; }

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
            float h = Math.Min(ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 3, (e.Rows.Count + 1.5f) * ImGui.GetFrameHeightWithSpacing());
            if (!ImGui.BeginTable("##wt_rows", 6, flags, new Vector2(0, Math.Max(h, ImGui.GetFrameHeightWithSpacing() * 3)))) return;
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Slot");
                ImGui.TableSetupColumn("Goal", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Weight");
                ImGui.TableSetupColumn("Chance");
                ImGui.TableSetupColumn("CD");
                ImGui.TableSetupColumn("");
                ImGui.TableHeadersRow();
                var outline = _editor.Active?.Outline ?? Array.Empty<GoalFunction>();
                bool multi = e.Rows.GroupBy(r => r.Slot).Any(g => g.Count() > 1);
                foreach (var r in e.Rows)
                {
                    string id = $"{r.Slot}_{r.Source?.Line}";
                    ImGui.PushID(id);
                    try
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var g = r.Function != null ? outline.FirstOrDefault(x => x.Name == r.Function) : null;
                        // The whole row (behind the inputs/buttons) jumps to the weight in the code.
                        if (ImGui.Selectable($"{wt.TableVar}[{r.Slot}]##row", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                            if (r.Source != null) _editor.JumpTo(r.Source.Line);
                        if (ImGui.IsItemHovered())
                            Tip($"{wt.TableVar}[{r.Slot}] = {WeightTable.FormatNumber(r.Weight)}  (line {r.Source?.Line + 1})\n" +
                                (r.Function == null ? $"No {wt.FuncVar}[{r.Slot}] = REGIST_FUNC(...): nothing would run.\n" : $"Runs Goal.{r.Function}" + (g != null ? $" (line {g.Line + 1})" : " (not found in this file)") + "\n") +
                                "Click: jump to the weight in the code");
                        ImGui.TableNextColumn();
                        string anims = g != null && g.AnimIds.Count > 0 ? "  " + string.Join(" ", g.AnimIds.Distinct().Take(4)) + (g.AnimIds.Distinct().Count() > 4 ? "…" : "") : "";
                        string fn = r.Function ?? "(not registered)";
                        if (r.Function == null) ImGui.TextColored(Theme.Warn, fn);
                        else ImGui.TextUnformatted($"{fn}{anims}{(multi ? $"  (line {r.Source?.Line + 1})" : "")}");
                        ImGui.TableNextColumn();
                        float w = _wtEditing.TryGetValue("w" + id, out float tw) ? tw : (float)r.Weight;
                        ImGui.SetNextItemWidth(70);
                        if (ImGui.InputFloat("##w", ref w, 0, 0, "%g")) _wtEditing["w" + id] = Math.Max(0, w);
                        if (ImGui.IsItemDeactivatedAfterEdit() && _wtEditing.Remove("w" + id, out float nw) && r.Source != null && Math.Abs(nw - r.Weight) > 1e-6)
                            ApplyWeightEdit(WeightTable.SetWeight(r.Source, nw));
                        if (ImGui.IsItemHovered()) Tip("Weight (Enter to apply). 0 = never picked.");
                        ImGui.TableNextColumn();
                        float pc = _wtEditing.TryGetValue("p" + id, out float tp) ? tp : (float)r.Chance;
                        ImGui.SetNextItemWidth(70);
                        if (ImGui.InputFloat("##p", ref pc, 0, 0, "%.1f%%")) _wtEditing["p" + id] = Math.Clamp(pc, 0, 99.9f);
                        if (ImGui.IsItemDeactivatedAfterEdit() && _wtEditing.Remove("p" + id, out float np) && r.Source != null)
                        {
                            if (WeightTable.WeightForChance(e, r, np) is double nw2) ApplyWeightEdit(WeightTable.SetWeight(r.Source, nw2));
                            else _app.Notify("Can't reach that chance by changing only this weight (the others are 0).");
                        }
                        if (ImGui.IsItemHovered()) Tip("Chance to be picked in this situation. Type a % to change this weight\n(the other weights stay; the result is rounded to a whole weight).");
                        ImGui.TableNextColumn();
                        if (r.Cooldowns.Count > 0)
                        {
                            ImGui.TextDisabled("CD");
                            if (ImGui.IsItemHovered())
                                Tip("SetCoolTime: the weight drops (usually to 1) for a while after these anims played:\n" +
                                                 string.Join("\n", r.Cooldowns.Select(c => $"  anim {c.Anim}: {Fmt(c.Seconds)} s")));
                        }
                        ImGui.TableNextColumn();
                        ImGui.BeginDisabled(g == null);
                        if (ImGui.SmallButton("▶")) PlayGoalDefault(g!);
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Tip(g == null ? "Goal not found in this file" : $"Play Goal.{g.Name} (first arm of each branch, no interrupts)");
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Open")) OpenGoalFromTable(g!, wt.Goal.Name);
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Tip($"Show Goal.{r.Function}'s combo here (branches, SpEffect interrupts); a Back button returns to this table");
                        ImGui.EndDisabled();
                        ImGui.SameLine();
                        if (ImGui.SmallButton("x") && r.Source != null && _editor.Active is { } o)
                            ApplyWeightEdit(wt.RemoveAssign(o.Editor.Text, r.Source));
                        if (ImGui.IsItemHovered()) Tip($"Remove {wt.TableVar}[{r.Slot}] = {WeightTable.FormatNumber(r.Weight)} (line {r.Source?.Line + 1})\nfrom the code (or set it to 0 when other code shares the line)");
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndTable(); }
            ImGui.TextDisabled($"Total weight {WeightTable.FormatNumber(Math.Round(e.TotalWeight, 2))} · {e.Rows.Select(r => r.Slot).Distinct().Count()} acts");
        }

        private void DrawAddSlot(WeightTable wt, WeightEvaluation e)
        {
            if (e.Returned) return;
            var arm = e.CaseArm;
            ImGui.BeginDisabled(arm == null);
            if (ImGui.Button("+ Add act to this case")) { _addSlot = -1; _addWeight = 100; ImGui.OpenPopup("##wt_add_pop"); }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                Tip(arm == null ? "No if arm is taken in this situation: nowhere to add a weight." : $"Adds {wt.TableVar}[N] = weight under: {(arm.Cond == null ? "else" : "if " + arm.Text)} (line {arm.Line + 1})");
            if (arm == null || !ImGui.BeginPopup("##wt_add_pop")) return;
            try
            {
                var present = e.Rows.Select(r => r.Slot).ToHashSet();
                SlotCombo(wt, "##wt_add_slot", ref _addSlot, present);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("weight##wt_add_w", ref _addWeight, 0, 0, "%g");
                ImGui.BeginDisabled(_addSlot < 0 || _addWeight <= 0);
                if (ImGui.Button("Add") && _editor.Active is { } o)
                {
                    ApplyWeightEdit(wt.AddAssign(o.Editor.Text, arm, _addSlot, _addWeight));
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            }
            finally { ImGui.EndPopup(); }
        }

        /// <summary>Registered slots (slot N → Goal.XXX) to pick from.</summary>
        private void SlotCombo(WeightTable wt, string id, ref int slot, IReadOnlyCollection<int> exclude)
        {
            ImGui.SetNextItemWidth(220);
            string cur = slot >= 0 ? $"[{slot}] {(wt.Slots.TryGetValue(slot, out var r) ? r.Function : "?")}" : "choose an act";
            if (!ImGui.BeginCombo(id, cur, ImGuiComboFlags.HeightLarge)) return;
            try
            {
                var outline = _editor.Active?.Outline ?? Array.Empty<GoalFunction>();
                foreach (var reg in wt.Slots.Values.OrderBy(x => x.Slot))
                {
                    if (exclude.Contains(reg.Slot)) continue;
                    var g = outline.FirstOrDefault(x => x.Name == reg.Function);
                    string anims = g != null && g.AnimIds.Count > 0 ? "  " + string.Join(" ", g.AnimIds.Distinct().Take(3)) : "";
                    if (ImGui.Selectable($"[{reg.Slot}] {reg.Function}{anims}##s{reg.Slot}", reg.Slot == slot)) slot = reg.Slot;
                }
            }
            finally { ImGui.EndCombo(); }
        }

        /// <summary>Plays a goal's default path (first arm of random branches, "no branch" for state branches, no interrupts).</summary>
        private void PlayGoalDefault(GoalFunction g)
        {
            var o = _editor.Active;
            if (o == null) return;
            var nodes = ComboSequence.Build(o.Editor.Text, g);
            var steps = ComboSequence.Flatten(nodes, DefaultArm);
            if (steps.Count == 0) { _app.Notify($"Goal.{g.Name} adds no anim on its default path: open it to pick branches."); return; }
            Play(steps.Select(st => new ComboPathItem(st, 0, null)).ToList());
        }

        private void OpenGoalFromTable(GoalFunction g, string tableGoal)
        {
            if (_backTo == null) _followBeforeOpen = _follow;
            _backTo = tableGoal;
            _follow = false;
            _goal = g;
            _ownJump = null;
            _pickedSp = null;
            _cacheKey = null;
        }

        /// <summary>"← Back to Goal.Kengeki_Activate" after Open on a table row.</summary>
        private void DrawBackToTable()
        {
            if (_backTo == null || _goal == null || _goal.Name == _backTo) return;
            if (ImGui.SmallButton($"< Back to Goal.{_backTo}"))
            {
                var g = _editor.Active?.Outline.FirstOrDefault(x => x.Name == _backTo);
                if (g != null) { _goal = g; _start = null; _cacheKey = null; }
                _follow = _followBeforeOpen;
                _backTo = null;
            }
        }
    }
}
