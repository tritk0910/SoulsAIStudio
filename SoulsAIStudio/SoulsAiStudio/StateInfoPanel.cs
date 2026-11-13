#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DSAnimStudio;
using DSAnimStudio.ImguiOSD;
using DSAnimStudio.TaeEditor;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Redesigned "StateInfo Toggle" section of DSAS's Entity window (drawn from one <c>// [SAS]</c> line in
    /// <c>ImguiOSD/Window.Entity.cs</c>; the DSAS original stays below it, unused, for upstream merges).
    /// <para>
    /// Behavior is the same as DSAS: the same <c>StateInfoSelectConfig_Enabled/Names</c> dictionaries of the Entity
    /// window (read/written through its public Get/Set methods), same rename / delete / add dialogs, the project json
    /// is saved on rename/delete/add (not on toggle), and an ID that has a name but no on/off entry is shown and
    /// stored as ON. What changed is the presentation: filter box, IDs used by the current animation grouped first
    /// with event counts, bulk All On / All Off / Invert, clear ON/OFF state, tooltips, opaque scrolling list.
    /// </para>
    /// </summary>
    public sealed class StateInfoPanel
    {
        private static readonly Vector4 OnColor = new(0.45f, 0.85f, 0.45f, 1f);
        private static readonly Vector4 OffColor = new(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Vector4 HeaderColor = new(0.6f, 0.75f, 0.95f, 1f);
        private static readonly Vector4 PanelBg = new(0.12f, 0.12f, 0.13f, 1f);

        private string _filter = "";
        private bool _onlyCurrentAnim;

        /// <summary>
        /// ON/OFF button drawn right after a "StateInfo" field in the Parameters panel: toggles that StateInfo for viewport
        /// simulation, same data as Entity > StateInfo Toggle (not saved to the project json on toggle, like DSAS).
        /// </summary>
        public static void ParameterToggle(string? paramName, int stateInfo)
        {
            if (!string.Equals(paramName, "StateInfo", StringComparison.OrdinalIgnoreCase) || stateInfo < 0) return;
            var entity = OSD.WindowEntity;
            if (entity == null) return;
            bool on = entity.IsStateInfoEnabled(stateInfo);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, on ? new Vector4(0.20f, 0.45f, 0.20f, 1f) : new Vector4(0.30f, 0.30f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? new Vector4(0.25f, 0.55f, 0.25f, 1f) : new Vector4(0.40f, 0.40f, 0.40f, 1f));
            bool click = ImGui.SmallButton((on ? "ON" : "OFF") + $"##sas_si_param_{stateInfo}");
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
            {
                string name = entity.GetStateInfoSelectConfig_Names().TryGetValue(stateInfo, out var n) && !string.IsNullOrEmpty(n) ? $" ({n})" : "";
                ImGui.SetTooltip($"StateInfo {stateInfo}{name} is {(on ? "ON" : "OFF")}: events with this StateInfo are " +
                                 (on ? "simulated" : "ignored") + " in the viewport.\nClick to toggle (same as Entity > StateInfo Toggle).");
            }
            if (!click) return;
            var enabled = entity.GetStateInfoSelectConfig_Enabled();
            enabled[stateInfo] = !on;
            entity.SetStateInfoSelectConfig_Enabled(enabled); // also adds an empty name entry if missing (DSAS behavior)
        }

        /// <summary>Draws the section. Always returns true (the caller's DSAS version is then skipped).</summary>
        public bool Draw(Window.Entity entity, TaeEditorScreen? tae, ref bool anyFieldFocused)
        {
            var names = entity.GetStateInfoSelectConfig_Names();
            var enabled = entity.GetStateInfoSelectConfig_Enabled();

            // Same as DSAS: a named ID without an on/off entry counts (and is stored) as ON.
            bool changed = false;
            foreach (var id in names.Keys)
                if (!enabled.ContainsKey(id)) { enabled[id] = true; changed = true; }

            int on = enabled.Count(kv => kv.Value);
            int off = enabled.Count - on;
            bool open = ImGui.TreeNode($"StateInfo Toggle  ({on} on, {off} off)###sas_stateinfo");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("StateInfo IDs gate TAE events that have a StateInfo field (attacks, bullets, FFX, sounds...).\n" +
                                 "ON = those events are simulated in the viewport, OFF = ignored.\n" +
                                 "IDs first seen in an animation start OFF.");
            if (!open)
                return true; // DSAS only wrote the defaults while the section was expanded

            try
            {
                var usage = CountUsageInCurrentAnim(tae);
                var allIds = new SortedSet<int>(names.Keys);
                allIds.UnionWith(enabled.Keys);

                // ---- toolbar
                ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 260));
                ImGui.InputTextWithHint("##sas_si_filter", "Filter by ID or name", ref _filter, 64);
                if (ImGui.IsItemActive()) anyFieldFocused = true;
                ImGui.SameLine();
                if (ImGui.Button("All On")) { SetAll(enabled, allIds, _ => true); changed = true; }
                Tip("Turn every StateInfo ON (DSAS default for named IDs).");
                ImGui.SameLine();
                if (ImGui.Button("All Off")) { SetAll(enabled, allIds, _ => false); changed = true; }
                Tip("Turn every StateInfo OFF.");
                ImGui.SameLine();
                if (ImGui.Button("Invert")) { SetAll(enabled, allIds, id => !IsOn(enabled, id)); changed = true; }
                Tip("Swap ON and OFF for every StateInfo.");
                ImGui.SameLine();
                if (ImGui.Button("+ Add")) AskAdd(entity);
                Tip("Add a StateInfo ID by number.");

                ImGui.Checkbox("Only IDs used by the current animation", ref _onlyCurrentAnim);
                if (usage.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(OffColor, $"({usage.Count} used, {usage.Values.Sum()} events)");
                }

                // ---- list
                var visible = allIds.Where(id => Matches(id, names)).ToList();
                var used = visible.Where(usage.ContainsKey).ToList();
                var other = _onlyCurrentAnim ? new List<int>() : visible.Where(id => !usage.ContainsKey(id)).ToList();

                float rowH = ImGui.GetFrameHeightWithSpacing();
                int rows = used.Count + other.Count + (used.Count > 0 ? 1 : 0) + (other.Count > 0 ? 1 : 0);
                float height = Math.Clamp(rows * rowH + 8, rowH * 2, rowH * 14);

                ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
                ImGui.BeginChild("##sas_si_list", new Vector2(-1, height), true);
                try
                {
                    if (visible.Count == 0)
                        ImGui.TextColored(OffColor, allIds.Count == 0 ? "No StateInfo IDs yet. Play an animation or use + Add." : "No match for the filter.");
                    else if (ImGui.BeginTable("##sas_si_table", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH))
                    {
                        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 36);
                        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70);
                        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Events", ImGuiTableColumnFlags.WidthFixed, 56);
                        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 96);

                        if (used.Count > 0) GroupHeader("Used by current animation");
                        foreach (var id in used) changed |= Row(entity, enabled, names, usage, id);
                        if (other.Count > 0) GroupHeader(used.Count > 0 ? "Other" : "All");
                        foreach (var id in other) changed |= Row(entity, enabled, names, usage, id);
                        ImGui.EndTable();
                    }
                }
                finally
                {
                    ImGui.EndChild();
                    ImGui.PopStyleColor();
                }
            }
            finally
            {
                if (changed) entity.SetStateInfoSelectConfig_Enabled(enabled);
                ImGui.TreePop();
            }
            return true;
        }

        private static bool IsOn(Dictionary<int, bool> enabled, int id) => !enabled.TryGetValue(id, out bool v) || v;

        private static void SetAll(Dictionary<int, bool> enabled, IEnumerable<int> ids, Func<int, bool> value)
        {
            foreach (var id in ids.ToList())
                enabled[id] = value(id);
        }

        private bool Matches(int id, Dictionary<int, string> names)
        {
            if (string.IsNullOrWhiteSpace(_filter)) return true;
            string f = _filter.Trim();
            if (id.ToString().Contains(f, StringComparison.Ordinal)) return true;
            return names.TryGetValue(id, out var n) && n != null && n.Contains(f, StringComparison.OrdinalIgnoreCase);
        }

        private static void GroupHeader(string text)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(2);
            ImGui.TextColored(HeaderColor, text);
        }

        private static void Tip(string text)
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
        }

        /// <returns>True if the on/off state changed.</returns>
        private static bool Row(Window.Entity entity, Dictionary<int, bool> enabled, Dictionary<int, string> names,
            Dictionary<int, int> usage, int id)
        {
            bool isOn = IsOn(enabled, id);
            bool before = isOn;
            names.TryGetValue(id, out var name);
            usage.TryGetValue(id, out int count);

            ImGui.TableNextRow();
            ImGui.PushID(id);

            ImGui.TableSetColumnIndex(0);
            ImGui.Checkbox("##on", ref isOn);
            Tip(isOn ? $"StateInfo {id} is ON: its events are simulated. Click to turn OFF."
                     : $"StateInfo {id} is OFF: its events are ignored in the viewport. Click to turn ON.");

            ImGui.TableSetColumnIndex(1);
            ImGui.TextColored(isOn ? OnColor : OffColor, id.ToString());

            ImGui.TableSetColumnIndex(2);
            if (string.IsNullOrWhiteSpace(name)) ImGui.TextColored(OffColor, "(unnamed)");
            else if (isOn) ImGui.TextUnformatted(name);
            else ImGui.TextColored(OffColor, name);

            ImGui.TableSetColumnIndex(3);
            if (count > 0)
            {
                ImGui.TextUnformatted(count.ToString());
                Tip($"{count} event(s) in the current animation use StateInfo {id}.");
            }
            else ImGui.TextColored(OffColor, "-");

            ImGui.TableSetColumnIndex(4);
            if (ImGui.SmallButton("Rename")) AskRename(id, name);
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) AskDelete(id);
            Tip($"Delete StateInfo {id} from the list.");

            ImGui.PopID();
            if (isOn != before)
            {
                enabled[id] = isOn;
                return true;
            }
            return false;
        }

        /// <summary>StateInfo ID → number of events using it in the animation shown in the graph.</summary>
        private static Dictionary<int, int> CountUsageInCurrentAnim(TaeEditorScreen? tae)
        {
            var result = new Dictionary<int, int>();
            var graph = tae?.Graph;
            if (graph?.AnimRef == null) return result;
            try
            {
                foreach (var act in graph.GetActionListCopy_UsesLock(useGhostIfAvailable: true))
                {
                    int id = act.GetStateInfoID();
                    if (id < 0) continue;
                    result[id] = result.TryGetValue(id, out int c) ? c + 1 : 1;
                }
            }
            catch (Exception) { /* graph is being swapped while loading; show no usage this frame */ }
            return result;
        }

        // ---- dialogs: same text, validation and saving as DSAS's original section

        private static void AskRename(int id, string? current)
        {
            DialogManager.AskForInputString("Rename StateInfo", $"Enter new name for StateInfo {id}:", "", result =>
            {
                var w = OSD.WindowEntity;
                var names = w.GetStateInfoSelectConfig_Names();
                names[id] = result;
                w.SetStateInfoSelectConfig_Names(names);
                zzz_DocumentManager.CurrentDocument.GameData.SaveProjectJson();
            }, checkError: null, canBeCancelled: true, startingText: current ?? "");
        }

        private static void AskDelete(int id)
        {
            DialogManager.AskYesNo("Delete StateInfo?", $"Are you sure you wish to delete StateInfo {id}?", result =>
            {
                if (result)
                {
                    var w = OSD.WindowEntity;
                    var names = w.GetStateInfoSelectConfig_Names();
                    var enabled = w.GetStateInfoSelectConfig_Enabled();
                    names.Remove(id);
                    enabled.Remove(id);
                    w.SetStateInfoSelectConfig_Names(names);
                    w.SetStateInfoSelectConfig_Enabled(enabled);
                }
                zzz_DocumentManager.CurrentDocument.GameData.SaveProjectJson(silent: true);
            }, allowCancel: true);
        }

        private static void AskAdd(Window.Entity entity)
        {
            DialogManager.AskForInputString("Add New StateInfo", "Enter ID for the new StateInfo to add:", "", result =>
            {
                var digits = result.ExtractDigits();
                if (digits.Length == 0)
                {
                    zzz_NotificationManagerIns.PushNotificationWarn($"Cannot add StateInfo - Unable to parse an integer ID from given string '{result}'.");
                    return;
                }
                int id = int.Parse(digits);
                var enabled = entity.GetStateInfoSelectConfig_Enabled();
                if (enabled.ContainsKey(id))
                {
                    zzz_NotificationManagerIns.PushNotificationWarn($"Cannot add StateInfo - ID {id} already exists.");
                    return;
                }
                var names = entity.GetStateInfoSelectConfig_Names();
                names[id] = "";
                enabled[id] = true;
                entity.SetStateInfoSelectConfig_Names(names);
                entity.SetStateInfoSelectConfig_Enabled(enabled);
                zzz_DocumentManager.CurrentDocument.GameData.SaveProjectJson(silent: true);
            }, checkError: input =>
            {
                var digits = input.ExtractDigits();
                if (digits.Length == 0)
                    return $"Unable to parse an integer ID from given string '{input}'.";
                return entity.GetStateInfoSelectConfig_Enabled().ContainsKey(int.Parse(digits))
                    ? $"StateInfo ID {int.Parse(digits)} already exists."
                    : null;
            }, canBeCancelled: true);
        }
    }
}
