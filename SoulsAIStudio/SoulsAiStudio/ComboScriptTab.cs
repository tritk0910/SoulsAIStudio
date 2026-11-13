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
    /// "Manual" tab of the Combo window: a typed combo script in DSAS 4.9.9 Combo Viewer syntax plus
    /// <c>SpEffect &lt;id&gt; &lt;anim&gt;</c> (see <see cref="ComboScript"/>). Each anim's cut frame is resolved from the TAE
    /// (no start/end frames to type) and shown in a live preview; selecting a preview row lists that anim's cut points
    /// (JumpTable flags, SpEffect events, [Interrupt] = handled by Goal.Interrupt) to insert with one click. Replaces DSAS's
    /// Combo Viewer window.
    /// </summary>
    public sealed class ComboScriptTab
    {
        private const string ExampleComboAttack =
            "# Combo script: one anim per line.\n" +
            "# The word before the anim id says how the PREVIOUS anim ends:\n" +
            "#   EnemyComboAtk   = at its \"End If AI ComboAttack Queued\" block (JumpTable 23),\n" +
            "#                     like GOAL_COMMON_ComboRepeat / ComboFinal in the Lua\n" +
            "#   SpEffect <id>   = at its SpEffect <id> event (Goal.Interrupt branch)\n" +
            "#   NoCancel        = it plays to its end\n" +
            "# The last anim always plays to its end (with Loop: it is cut like line 1 and the combo restarts).\n" +
            "EnemyComboAtk 3000\n" +
            "EnemyComboAtk 3001\n" +
            "EnemyComboAtk 3002\n";

        private const string ExampleSpEffectStatic =
            "# SpEffect interrupt (Owl c5060 ids, change them for your character):\n" +
            "# 3048 has a SpEffect 5060030 event at frame 42. Goal.Interrupt handles 5060030 with\n" +
            "# ClearSubGoal + a new combo (3060 → 3061 → 3016), so 3048 is cut at frame 42.\n" +
            "EnemyComboAtk 3002\n" +
            "EnemyComboAtk 3048\n" +
            "SpEffect 5060030 3060\n" +
            "EnemyComboAtk 3061\n" +
            "EnemyComboAtk 3016\n";

        private readonly SasApp _app;
        private readonly Func<int, SpEffectHandler?> _handlerFor;
        private string _text;
        private ComboScriptResult _parsed;
        private List<DsasBridge.ComboPlan?> _preview = new(); // per parsed step
        private string? _previewKey;
        private int _previewAge;
        private int _selected = -1; // index into _parsed.Steps
        private bool _loop;
        private bool _looping; // Loop is on and the user has not pressed Stop
        private bool _showHelp;

        public ComboScriptTab(SasApp app, Func<int, SpEffectHandler?> handlerFor)
        {
            _app = app;
            _handlerFor = handlerFor;
            _text = app.Settings.ComboScript ?? ExampleComboAttack;
            _loop = app.Settings.ComboScriptLoop;
            _parsed = ComboScript.Parse(_text);
        }

        public void SetText(string text)
        {
            _text = text;
            _parsed = ComboScript.Parse(_text);
            _previewKey = null;
            _selected = -1;
            Save();
        }

        /// <summary>Another combo was started (From Lua tab): stop restarting this one.</summary>
        public void StopLooping() => _looping = false;

        /// <summary>Called every frame (even when the tab is hidden): restarts the combo for Loop.</summary>
        public void Tick()
        {
            if (_looping && _loop && _app.Bridge.ComboFinished) Play();
        }

        public void Build()
        {
            bool loaded = _app.Bridge.LoadedAnibnd != null;
            if (!loaded) ImGui.TextColored(Theme.Warn, "Load the character first (Scripts panel → Load model & anims).");

            if (ImGui.Button("▶ Play combo")) Play();
            ImGui.SameLine();
            if (ImGui.Button(_app.Bridge.ComboPaused ? "▶ Resume" : "|| Pause")) _app.Bridge.ToggleComboPause();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Space also pauses/resumes the combo (when not typing)");
            ImGui.SameLine();
            if (ImGui.Button("■ Stop")) { _looping = false; _app.Bridge.StopCombo(); }
            ImGui.SameLine();
            if (ImGui.Checkbox("Loop", ref _loop)) { _app.Settings.ComboScriptLoop = _loop; _app.Settings.Save(); if (!_loop) _looping = false; }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.BeginCombo("##sas_combo_examples", "Examples…"))
            {
                try
                {
                    if (ImGui.Selectable("ComboAttack chain (EnemyComboAtk)")) SetText(ExampleComboAttack);
                    if (ImGui.Selectable("SpEffect interrupt")) SetText(BuildSpEffectExample() ?? ExampleSpEffectStatic);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Built from the loaded character and the open Lua script when possible");
                }
                finally { ImGui.EndCombo(); }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(_showHelp ? "Hide help" : "Help")) _showHelp = !_showHelp;
            if (_showHelp) DrawHelp();

            RefreshPreview();

            var avail = ImGui.GetContentRegionAvail();
            float editorW = Math.Max(220, avail.X * 0.45f);
            float topH = Math.Max(120, avail.Y * 0.55f);
            if (ImGui.InputTextMultiline("##sas_combo_script", ref _text, 64 * 1024, new Vector2(editorW, topH), ImGuiInputTextFlags.AllowTabInput))
            {
                _parsed = ComboScript.Parse(_text);
                _previewKey = null;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) Save();
            ImGui.SameLine();
            ImGui.BeginChild("##sas_combo_cuts", new Vector2(0, topH), true);
            try { DrawCutOptions(); }
            finally { ImGui.EndChild(); }

            DrawPreview();
        }

        private void Save()
        {
            _app.Settings.ComboScript = _text;
            _app.Settings.Save();
        }

        private void Play()
        {
            _parsed = ComboScript.Parse(_text);
            var plan = _app.Bridge.PlayCombo(Requests(), play: true);
            if (plan == null) { _app.Notify("Load the character first (Scripts panel → Load model & anims)."); _looping = false; return; }
            if (_parsed.Steps.Count == 0) { _app.Notify("The combo script has no anim lines."); _looping = false; return; }
            _looping = _loop;
            _previewKey = null;
        }

        private List<DsasBridge.ComboRequest> Requests()
        {
            var cuts = _parsed.OwnCuts(_loop);
            return _parsed.Steps.Select((s, i) => new DsasBridge.ComboRequest(s.AnimId, cuts[i], StartFrame: s.StartFrame, EndFrame: s.EndFrame)).ToList();
        }

        /// <summary>Re-resolves cut frames when the script, the anibnd or the loop flag changes, and every ~0.5 s (TAE edits).</summary>
        private void RefreshPreview()
        {
            string key = $"{_text.GetHashCode()}|{_app.Bridge.LoadedAnibnd}|{_loop}";
            if (key == _previewKey && ++_previewAge < 30) return;
            _previewKey = key;
            _previewAge = 0;
            var plans = _app.Bridge.PlayCombo(Requests(), play: false);
            _preview = new List<DsasBridge.ComboPlan?>();
            for (int i = 0; i < _parsed.Steps.Count; i++) _preview.Add(plans != null && i < plans.Count ? plans[i] : null);
            if (_selected >= _parsed.Steps.Count) _selected = -1;
        }

        private void DrawPreview()
        {
            int playing = _app.Bridge.ComboIndex;
            ImGui.BeginChild("##sas_combo_preview", Vector2.Zero, false);
            try
            {
                if (ImGui.BeginTable("##sas_combo_preview_t", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Line", ImGuiTableColumnFlags.WidthFixed, 40);
                    ImGui.TableSetupColumn("Anim", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableSetupColumn("Frames", ImGuiTableColumnFlags.WidthFixed, 110);
                    ImGui.TableSetupColumn("Ends at", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableHeadersRow();
                    for (int i = 0; i < _parsed.Steps.Count; i++)
                    {
                        var s = _parsed.Steps[i];
                        var p = i < _preview.Count ? _preview[i] : null;
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        bool isPlaying = i == playing;
                        if (ImGui.Selectable($"{(isPlaying ? "▶" : " ")}{s.Line + 1}##row{i}", _selected == i, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            _selected = i;
                            _app.PlayAnim(s.AnimId);
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click: select (cut points on the right) + play this anim alone");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(isPlaying ? Theme.Ok : Theme.Accent, s.AnimId.ToString());
                        ImGui.TableNextColumn();
                        if (p == null) ImGui.TextDisabled("–");
                        else if (p.Missing) ImGui.TextColored(Theme.Error, "missing");
                        else ImGui.TextDisabled($"{p.StartFrame:0}–{p.EndFrame:0} / {p.TotalFrames:0}");
                        ImGui.TableNextColumn();
                        if (p == null) ImGui.TextDisabled("(character not loaded)");
                        else if (p.Missing) ImGui.TextColored(Theme.Error, p.CutReason);
                        else if (p.Warn) ImGui.TextColored(Theme.Warn, p.CutReason);
                        else ImGui.Text(p.CutReason + (p.EndFrame < p.TotalFrames ? $"  (f{p.EndFrame:0}, {p.EndFrame / 30f:0.00} s)" : ""));
                    }
                    foreach (var d in _parsed.Diagnostics)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(d.IsError ? Theme.Error : Theme.Warn, $" {d.Line + 1}");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(d.IsError ? Theme.Error : Theme.Warn, d.IsError ? "error" : "note");
                        ImGui.TableNextColumn();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(d.IsError ? Theme.Error : Theme.Warn, d.Message);
                    }
                    ImGui.EndTable();
                }
            }
            finally { ImGui.EndChild(); }
        }

        /// <summary>Right pane: where the selected anim can be cut; click = set the NEXT line's keyword (or add a line).</summary>
        private void DrawCutOptions()
        {
            if (_selected < 0 || _selected >= _parsed.Steps.Count)
            {
                ImGui.TextWrapped("Select a row in the list below to see where that anim can be cut (blocks and SpEffects), then click one to use it.");
                return;
            }
            var step = _parsed.Steps[_selected];
            ImGui.TextColored(Theme.Accent, $"Anim {step.AnimId} can end at:");
            ImGui.TextDisabled(_selected + 1 < _parsed.Steps.Count ? $"click = keyword of line {_parsed.Steps[_selected + 1].Line + 1}" : "click = add a next line");

            var jumps = _app.Bridge.GetJumpTableEvents(step.AnimId);
            var sps = _app.Bridge.GetSpEffectEvents(step.AnimId);
            if (jumps.Count == 0 && sps.Count == 0) ImGui.TextDisabled("no JumpTable / SpEffect events (or not loaded)");
            int k = 0;
            foreach (var (flag, frame) in jumps)
            {
                string kw = ComboScript.KeywordFor(new ComboCut(flag));
                if (ImGui.Selectable($"{kw}   f{frame:0}##j{k++}", false)) ApplyCut(kw);
            }
            foreach (var (sp, frame, endFrame) in sps)
            {
                var h = _handlerFor(sp);
                string label = $"SpEffect {sp}   f{frame:0}–{endFrame:0}" + (h != null ? (h.DependsOnDistance ? "   [Interrupt, distance]" : "   [Interrupt]") : "");
                if (h != null) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                bool click = ImGui.Selectable(label + $"##s{k++}", false);
                if (h != null) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(h == null ? "Goal.Interrupt of the open script has no branch for this SpEffect"
                        : $"Goal.Interrupt line {h.Line + 1}: if {h.Condition}" + (ComboSequence.ContainsClear(h.Body) ? "\nClearSubGoal, then: " : "\nthen: ")
                          + string.Join(" → ", ComboSequence.Flatten(h.Body, _ => 0).Select(x => x.AnimId))
                          + (endFrame - frame >= 2 ? $"\n\nActive f{frame:0}–{endFrame:0}: click inserts SpEffect {sp} (cut at f{frame:0});\nwrite SpEffect {sp}@<frame> to cut later in that window." : ""));
                if (click) ApplyCut($"{ComboScript.SpEffectKeyword} {sp}");
            }
            if (ImGui.Selectable($"NoCancel   (play to the end)##nc")) ApplyCut("NoCancel");
        }

        /// <summary>Replaces the keyword of the line after the selected step, or appends "keyword " as a new line.</summary>
        private void ApplyCut(string keyword)
        {
            var lines = _text.Replace("\r\n", "\n").Split('\n').ToList();
            if (_selected + 1 < _parsed.Steps.Count)
            {
                var next = _parsed.Steps[_selected + 1];
                string old = lines[next.Line];
                int indent = old.Length - old.TrimStart().Length;
                string idText = next.AnimId.ToString();
                int at = old.IndexOf(" " + idText, StringComparison.Ordinal);
                string rest = at >= 0 ? old[(at + 1)..] : idText;
                lines[next.Line] = old[..indent] + keyword + " " + rest;
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                lines.Add(keyword + " ");
                _app.Notify($"Added \"{keyword} \" — type the next anim id at the end of the script.");
            }
            int keep = _selected;
            SetText(string.Join("\n", lines));
            _selected = keep;
        }

        /// <summary>
        /// A real example for the loaded character: an anim referenced by the open script whose TAE has a SpEffect that
        /// Goal.Interrupt handles with a combo.
        /// </summary>
        private string? BuildSpEffectExample()
        {
            var text = _app.ActiveScriptText;
            if (text == null || _app.Bridge.LoadedAnibnd == null) return null;
            foreach (int anim in AnimReferences.Build(text).Select(a => a.AnimId).Distinct())
            {
                foreach (var (sp, frame, _) in _app.Bridge.GetSpEffectEvents(anim))
                {
                    var h = _handlerFor(sp);
                    if (h == null || h.DependsOnDistance) continue;
                    var chain = ComboSequence.Flatten(h.Body, _ => 0).Select(x => x.AnimId).ToList();
                    if (chain.Count == 0) continue;
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"# SpEffect interrupt, from your files:\n");
                    sb.Append($"# {anim} has a SpEffect {sp} event at frame {frame:0}. Goal.Interrupt (line {h.Line + 1}) handles it")
                      .Append(ComboSequence.ContainsClear(h.Body) ? " with ClearSubGoal + a new combo,\n" : " with a new combo,\n");
                    sb.Append($"# so {anim} is cut at frame {frame:0} and {string.Join(" → ", chain)} plays next.\n");
                    sb.Append($"EnemyComboAtk {anim}\n");
                    sb.Append($"SpEffect {sp} {chain[0]}\n");
                    foreach (int next in chain.Skip(1)) sb.Append($"EnemyComboAtk {next}\n");
                    return sb.ToString();
                }
            }
            return null;
        }

        private static void DrawHelp()
        {
            ImGui.BeginChild("##sas_combo_help", new Vector2(0, 150), true);
            try
            {
                ImGui.TextWrapped("One anim per line:  <how the previous anim ends> <anim id> [start frame] [end frame]");
                ImGui.TextColored(Theme.Accent, "EnemyComboAtk 3001");
                ImGui.SameLine(); ImGui.TextDisabled("previous anim is cut at its \"End If AI ComboAttack Queued\" block (JumpTable 23)");
                ImGui.TextColored(Theme.Accent, "SpEffect 5060030 3060");
                ImGui.SameLine(); ImGui.TextDisabled("previous anim is cut where its SpEffect 5060030 event starts (Goal.Interrupt)");
                ImGui.TextColored(Theme.Accent, "SpEffect 5358@30 3049");
                ImGui.SameLine(); ImGui.TextDisabled("same, but cut at frame 30 inside that SpEffect's active window (e.g. when a distance check passes)");
                ImGui.TextColored(Theme.Accent, "NoCancel 3016");
                ImGui.SameLine(); ImGui.TextDisabled("previous anim plays to its end");
                ImGui.TextColored(Theme.Accent, "EnemyAtk / PlayerRH / … / JumpTable 86");
                ImGui.SameLine(); ImGui.TextDisabled("other JumpTable flags (DSAS 4.9.9 names)");
                ImGui.TextWrapped("The keyword on the FIRST line is only used with Loop (the last anim is cut that way before restarting). " +
                                  "Comments: #, -- or //. Anim ids: 3002 or a000_003002. Click a row below to see an anim's cut points.");
            }
            finally { ImGui.EndChild(); }
        }
    }
}
