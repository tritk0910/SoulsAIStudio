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
    public sealed partial class ComboPanel
    {
        public const string WindowName = "Combo##sas_combo";
        private readonly SasApp _app;
        private readonly LuaEditorPanel _editor;
        private readonly Random _rng = new();
        private readonly Dictionary<(int Line, int Level), int> _choice = new(); // (branch line, interrupt level) -> arm index
        private readonly Dictionary<(int Line, int Depth), int> _interrupt = new(); // step -> chosen SpEffect candidate (-1 none)
        private readonly Dictionary<(int Line, int Depth), float> _trigger = new(); // step -> chosen trigger frame inside the SpEffect window
        private readonly Dictionary<int, IReadOnlyList<DsasBridge.SpEffectEvent>> _spEvents = new(); // anim -> TAE SpEffects + their cycle SpEffects
        private string? _spEventsAnibnd;
        private SpEffectInterrupts.Table? _handlers;
        private const int MaxInterruptDepth = 6;
        private string? _cacheKey;
        private IReadOnlyList<ComboNode> _nodes = Array.Empty<ComboNode>();
        private GoalFunction? _goal;
        /// <summary>Combo started from a Goal.Interrupt SpEffect arm (<c>== S</c>) instead of a goal: its handler.</summary>
        private SpEffectHandler? _start;
        /// <summary>SpEffect picked in the "Start from" dropdown; kept until the user moves the cursor.</summary>
        private int? _pickedSp;
        private string _startFilter = "";
        private bool _follow = true;
        /// <summary>Cursor position set by our own jump (click on a step): while the cursor stays there, keep the
        /// current goal instead of following it into e.g. Goal.Interrupt.</summary>
        private (int Line, int Column)? _ownJump;
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

        /// <summary>Shows the Manual tab next time the window is drawn (without focusing it).</summary>
        public void SelectManualTab() => _selectManual = true;

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
                    // Animations-only project: no Lua to read combos from, only the Manual tab.
                    if (_app.Project?.IsAnimationsOnly != true && ImGui.BeginTabItem("From Lua")) { BuildContents(); ImGui.EndTabItem(); }
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
            if (o?.Doc == null) { _goal = null; _start = null; _nodes = Array.Empty<ComboNode>(); return; }
            if (_ownJump != null && o.Editor.Cursor != _ownJump) { _ownJump = null; _pickedSp = null; } // the user moved the cursor: follow again
            bool following = _follow && _ownJump == null;
            var goal = following ? o.GoalAtCursor ?? _goal : _goal;
            if (goal != null && _goal != null && goal.Name != _goal.Name) goal = o.Outline.FirstOrDefault(g => g.Name == goal.Name) ?? goal;

            string handlersKey = $"{o.Script.Key}|{o.OutlineVersion}";
            if (handlersKey != _handlersKey) { _handlers = SpEffectInterrupts.Build(o.Editor.Text); _handlersKey = handlersKey; }

            // Start from a SpEffect arm: picked in the dropdown, or the cursor is inside "== S" in Goal.Interrupt.
            int? sp = _pickedSp
                ?? (following ? (o.GoalAtCursor?.Name == "Interrupt" && _handlers != null ? SpEffectInterrupts.HandlerAt(_handlers, o.CursorLine)?.SpEffectId : null)
                              : _start?.SpEffectId);
            SpEffectHandler? start = sp is int id && _handlers != null && _handlers.Handlers.TryGetValue(id, out var h) ? h : null;

            string key = $"{handlersKey}|{goal?.Name}|{start?.SpEffectId}";
            if (key == _cacheKey) return;
            _cacheKey = key;
            _goal = goal;
            _start = start;
            _choice.Clear();
            _interrupt.Clear();
            _trigger.Clear();
            _plan = null;
            _nodes = start != null ? start.Body
                : goal == null ? Array.Empty<ComboNode>() : ComboSequence.Build(o.Editor.Text, goal);
        }

        private string? _handlersKey;

        /// <summary>"Start from" dropdown: the goal at the cursor, or any SpEffect arm of Goal.Interrupt.</summary>
        private void DrawStartFrom()
        {
            var handlers = _handlers?.Handlers.Values
                .GroupBy(h => h.Line).Select(g => g.First()) // "sp == 1 or sp == 2": one arm
                .OrderBy(h => h.Line).ToList() ?? new List<SpEffectHandler>();
            ImGui.TextDisabled("Start from");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            string label = _start != null ? StartLabel(_start) : _goal != null ? $"{_goal.QualifiedName} (line {_goal.Line + 1})" : "(put the cursor inside a goal)";
            if (ImGui.BeginCombo("##sas_combo_start", label, ImGuiComboFlags.HeightLarge))
            {
                try
                {
                    if (ImGui.IsWindowAppearing()) { _startFilter = ""; ImGui.SetKeyboardFocusHere(); }
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##sas_combo_start_filter", "filter: SpEffect id, name or anim", ref _startFilter, 64);
                    if (ImGui.Selectable($"Goal at the cursor{(_editor.Active?.GoalAtCursor is { } g ? $" ({g.QualifiedName})" : "")}", _start == null))
                    { _pickedSp = null; _ownJump = null; _follow = true; _cacheKey = null; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Follow the Lua editor cursor again (Act, Kengeki… or a \"== SpEffect\" arm in Goal.Interrupt)");
                    if (handlers.Count == 0) ImGui.TextDisabled("Goal.Interrupt has no SpEffect arms in this script.");
                    else ImGui.TextDisabled($"Goal.Interrupt SpEffect arms ({handlers.Count}):");
                    foreach (var h in handlers)
                    {
                        string text = StartLabel(h);
                        if (_startFilter.Length > 0 && !text.Contains(_startFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ImGui.Selectable(text + $"##sp_start{h.Line}", _start?.Line == h.Line))
                        {
                            _pickedSp = h.SpEffectId;
                            _ownJump = _editor.Active?.Editor.Cursor; // keep it until the cursor moves
                            _cacheKey = null;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Goal.Interrupt line {h.Line + 1}:\nif {h.Condition}" + (h.DependsOnDistance ? "\n[distance]: the arm also tests the distance to the target" : "") + "\nRight-click: set a label for this SpEffect");
                        _app.SpEffectLabelContextMenu($"##sp_start_ctx{h.Line}", h.SpEffectId);
                    }
                }
                finally { ImGui.EndCombo(); }
            }
            else if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play an Act/Kengeki goal (cursor inside it), or start straight from a SpEffect interrupt:\n" +
                "the anims Goal.Interrupt queues when that SpEffect activates. Putting the cursor inside an\n" +
                "\"interruptSpeffect == S\" arm also selects it.");
        }

        /// <summary>"SpEffect 5060040 name -> 3037 / 3018 [25%] (line 812)" (name = user label or SpEffectParam name).</summary>
        private string StartLabel(SpEffectHandler h)
        {
            var first = FirstAnims(h.Body).Distinct().Take(4).ToList();
            string anims = h.Replans ? "  -> Replanning()" : first.Count > 0 ? "  -> " + string.Join(" / ", first) : "  (no anims)";
            string tags = (h.Chance is double ch ? $" [{ch:0}%]" : "") + (h.DependsOnDistance ? " [distance]" : h.HasStateCondition ? " [conditional]" : "");
            return $"SpEffect {_app.SpEffectText(h.SpEffectId)}{anims}{tags}  (line {h.Line + 1})";
        }

        /// <summary>First anim of every arm path (one per random/condition arm at the top), for the dropdown summary.</summary>
        private static IEnumerable<int> FirstAnims(IReadOnlyList<ComboNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n is ComboStep s) { yield return s.AnimId; yield break; }
                if (n is ComboBranch b)
                {
                    bool any = false;
                    foreach (var a in b.Arms) foreach (int id in FirstAnims(a.Body).Take(1)) { any = true; yield return id; }
                    if (any) yield break;
                }
            }
        }

        private void BuildContents()
        {
            DrawStartFrom();
            if (_goal == null && _start == null) { ImGui.TextDisabled("Put the cursor inside a goal (e.g. Goal.Act50 or Goal.Kengeki03),\nor pick a SpEffect interrupt in \"Start from\"."); return; }

            if (_start != null)
            {
                ImGui.TextColored(Theme.Accent, $"Goal.Interrupt > SpEffect {_app.SpEffectText(_start.SpEffectId)}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click: set a label for this SpEffect");
                _app.SpEffectLabelContextMenu("##sas_combo_hdr_ctx", _start.SpEffectId);
                ImGui.SameLine();
                ImGui.TextDisabled($"line {_start.Line + 1}");
            }
            else
            {
                ImGui.TextColored(Theme.Accent, _goal!.QualifiedName);
                ImGui.SameLine();
                ImGui.TextDisabled($"line {_goal.Line + 1}");
            }
            ImGui.SameLine();
            ImGui.Checkbox("Follow cursor", ref _follow);
            if (_follow) _backTo = null;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: this tab shows the goal the Lua editor cursor is in. Move the cursor into another\n" +
                                 "Goal.XXX function and the combo here switches to it (your branch/interrupt choices reset).\n" +
                                 "Clicking an anim in this tab jumps the editor there but keeps this goal.\n\n" +
                                 "Off: this tab stays on the current goal, wherever you move the cursor.\n" +
                                 "Useful to keep a combo while you read Goal.Interrupt or another goal.");
            DrawBackToTable();

            // Goal.Activate / Goal.Kengeki_Activate: the act weight table instead of a combo.
            if (CurrentWeightTable() is { } wt) { DrawWeightTable(wt); return; }

            string spKey = _app.Bridge.LoadedAnibnd + "|" + _app.ActiveAnimCategory; // the cache holds Lua ids: reset when the category changes
            if (_spEventsAnibnd != spKey) { _spEvents.Clear(); _spEventsAnibnd = spKey; }
            var path = BuildPath(_nodes, 0);
            bool loaded = _app.Bridge.LoadedAnibnd != null;
            if (!loaded) ImGui.TextColored(Theme.Warn, "Load the character first (Project panel → Load model & anims, or click a character).");
            if (_start != null) DrawStartNotes(_start);

            if (ImGui.Button("▶ Play combo") && path.Count > 0) Play(path);
            ImGui.SameLine();
            if (ImGui.Button("Random path")) { RollRandom(); Play(BuildPath(_nodes, 0)); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Plays the combo top-down like the game: the goal's random branches, then each anim's TAE SpEffects in\n" +
                "time order (before ComboAttack Queued). At each one Goal.Interrupt is rolled right there, with one\n" +
                "interruptRand shared by its condition and the ifs inside; a miss lets the anim go on to the next one.\n" +
                "Timers / SpEffect / HP checks and distance tests inside an arm count as 50/50.\n" +
                "Arms whose own condition tests the distance are never picked: choose them yourself.");
            ImGui.SameLine();
            if (ImGui.Button(_app.Bridge.ComboPaused ? "▶ Resume" : "|| Pause")) _app.Bridge.ToggleComboPause();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Space also pauses/resumes the combo");
            ImGui.SameLine();
            if (ImGui.Button("■ Stop")) { _app.Bridge.StopCombo(); }
            ImGui.SameLine();
            if (ImGui.Button("→ Manual") && path.Count > 0)
            {
                string from = _start != null ? $"Goal.Interrupt SpEffect {_app.SpEffectText(_start.SpEffectId)} (line {_start.Line + 1})" : $"Goal.{_goal!.Name} (line {_goal.Line + 1})";
                _manual.SetText($"# From Lua: {from}\n" + ComboScript.Format(path.Select(p =>
                    (_app.TaeAnimId(p.Step.AnimId), p.SpEffectId is int sp ? new ComboCut(-1, sp, p.Cut != p.SpStart ? p.Cut : null) : ComboCut.ComboAttackQueued)).ToList()));
                _selectManual = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this path (with its SpEffect interrupts) into the Manual tab as a combo script to edit");
            ImGui.TextDisabled(path.Count == 0 ? "no anims in this path"
                : string.Join(" → ", path.Select(p => p.Cut != null ? p.Step.AnimId + " (SpEffect cut)" : p.Step.AnimId.ToString())));

            ImGui.Separator();
            ImGui.BeginChild("##sas_combo_tree", new Vector2(0, _plan != null ? -110 : 0), false);
            int? cutBy = null;
            try { DrawNodes(_nodes, 0, 0, ref cutBy); }
            finally { ImGui.EndChild(); }

            if (_plan != null) DrawPlan();
        }

        /// <summary>What playing a SpEffect arm on its own means (shown above the tree).</summary>
        private void DrawStartNotes(SpEffectHandler h)
        {
            ImGui.TextDisabled("if " + h.Condition);
            if (h.Replans)
                ImGui.TextColored(Theme.Warn, "This arm only calls Replanning(): the AI picks a new act in Goal.Activate, no combo to play here.");
            else if (ComboSequence.Flatten(h.Body, Arms(0)).Count == 0 && !ComboSequence.Branches(h.Body).Any())
                ImGui.TextColored(Theme.Warn, "This arm adds no AddSubGoal: nothing to play.");
            if (!h.Replans && !ComboSequence.ContainsClear(h.Body))
                ImGui.TextDisabled("No ClearSubGoal: in game these anims are queued after the current combo; here they play on their own.");
            if (h.DependsOnDistance) ImGui.TextDisabled("[distance] the arm tests the distance to the target: pick the branches below yourself.");
            else if (h.HasStateCondition) ImGui.TextDisabled("[conditional] the arm also checks game state (timer, SpEffect, HP...).");
        }

        private readonly Dictionary<int, Func<ComboBranch, int>> _armsAt = new();

        /// <summary>Arm chooser for branches at an interrupt level (0 = the goal, 1 = a handler run from it, …).</summary>
        private Func<ComboBranch, int> Arms(int level)
        {
            if (!_armsAt.TryGetValue(level, out var f))
                _armsAt[level] = f = b => _choice.TryGetValue((b.Line, level), out int k) ? k : DefaultArm(b);
            return f;
        }

        private const string NoBranchTaken = "(no branch taken)";

        /// <summary>
        /// A branch decided by game state (distance, HP, SpEffect…) with no <c>else</c> defaults to "(no branch taken)":
        /// nothing happens unless the user says the condition holds. Random branches default to their first arm.
        /// </summary>
        private static int DefaultArm(ComboBranch b)
        {
            if (b.Arms.Any(a => a.Probability != null)) return 0;
            int none = b.Arms.Count - 1;
            return none >= 0 && b.Arms[none].Condition == NoBranchTaken ? none : 0;
        }

        /// <summary>Dropdown order: for state-decided branches "(no branch taken)" comes first.</summary>
        private static IEnumerable<int> ArmOrder(ComboBranch b)
        {
            int def = DefaultArm(b);
            if (def != 0) yield return def;
            for (int k = 0; k < b.Arms.Count; k++) if (k != def || def == 0) yield return k;
        }

        /// <summary>
        /// Random path, simulated top-down like the game: the goal's branches are rolled when the goal runs (one value per
        /// random variable, shared by every <c>if</c> that reads it). Then each anim plays in order; at each TAE SpEffect
        /// that Goal.Interrupt handles (before "ComboAttack Queued"), the interrupt runs right there with ONE fresh roll
        /// per random variable (e.g. <c>interruptRand</c>) used by its own condition AND the <c>if</c>s inside it. If it
        /// fires, the anim is cut and the new chain plays the same way; if not, the anim goes on. Every firing rolls anew.
        /// Game-state checks (timers, SpEffects, HP, distance inside the arm) are a coin flip; arms whose own condition
        /// tests the distance are never picked (choose them yourself).
        /// </summary>
        private void RollRandom()
        {
            _choice.Clear();
            _interrupt.Clear();
            _trigger.Clear();
            RollBranches(_nodes, 0, new Dictionary<string, int>(), Array.Empty<string>());
            RollInterrupts(_nodes, 0);
            _plan = null;
        }

        private IReadOnlySet<string> RandomVars => _handlers?.RandomVars ?? new HashSet<string>();

        /// <summary>
        /// Evaluates a condition with the given rolls (a variable is rolled 1–100 on first use, then reused). Unknown
        /// game-state parts are a coin flip.
        /// </summary>
        private bool Passes(string condition, Dictionary<string, int> rolls, IReadOnlySet<string> matched)
        {
            bool ok = RandomCondition.Evaluate(condition, RandomVars, matched,
                v => rolls.TryGetValue(v, out int x) ? x : rolls[v] = _rng.Next(1, 101), out bool unknown);
            return ok && (!unknown || _rng.Next(2) == 0);
        }

        /// <summary>Rolls the branches of a chain top-down (only the arms actually taken are entered).</summary>
        private void RollBranches(IReadOnlyList<ComboNode> nodes, int level, Dictionary<string, int> rolls, IReadOnlyCollection<string> matchedSp)
        {
            var matched = matchedSp.Count == 0 ? (IReadOnlySet<string>)new HashSet<string>() : new HashSet<string>(matchedSp);
            foreach (var n in nodes)
            {
                if (n is not ComboBranch b) continue;
                int pick = b.Arms.Count - 1;
                for (int k = 0; k < b.Arms.Count; k++)
                    if (Passes(b.Arms[k].Condition, rolls, matched)) { pick = k; break; }
                _choice[(b.Line, level)] = pick;
                RollBranches(b.Arms[pick].Body, level, rolls, matchedSp);
            }
        }

        private void RollInterrupts(IReadOnlyList<ComboNode> nodes, int level)
        {
            if (level >= MaxInterruptDepth) return;
            var steps = ComboSequence.Flatten(nodes, Arms(level));
            var spVars = _handlers?.SpEffectVars ?? new HashSet<string>();
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                var c = Candidates(s.AnimId);
                float? queued = i < steps.Count - 1 ? _app.Bridge.GetComboQueuedFrame(_app.TaeAnimId(s.AnimId)) : null;
                int pick = -1;
                for (int k = 0; k < c.Count && pick < 0; k++)
                {
                    if (queued is float q && c[k].Frame >= q) continue; // the next anim has started: this SpEffect never comes
                    var h = c[k].Handler;
                    if (h.OuterDistance) continue; // the arm itself tests the distance: the user decides
                    // Goal.Interrupt runs now: one fresh roll per random variable for the arm AND the ifs inside it.
                    var rolls = new Dictionary<string, int>();
                    if (!Passes(h.Condition, rolls, spVars)) continue;
                    RollBranches(h.Body, level + 1, rolls, spVars);
                    // The arm's branches may add nothing (e.g. "no branch taken"): the anim keeps playing.
                    if (!ComboSequence.ClearsOnPath(h.Body, Arms(level + 1)) && ComboSequence.Flatten(h.Body, Arms(level + 1)).Count == 0) continue;
                    pick = k;
                }
                _interrupt[(s.Line, level)] = pick;
                if (pick < 0) continue;
                RollInterrupts(c[pick].Handler.Body, level + 1);
                if (ComboSequence.ClearsOnPath(c[pick].Handler.Body, Arms(level + 1))) return;
            }
        }

        /// <summary>A step on the played path; Cut = frame where a chosen SpEffect interrupt cuts it.</summary>
        private sealed record PathItem(ComboStep Step, float? Cut, int? SpEffectId = null, float? SpStart = null);

        /// <summary>
        /// Flattens the chosen branches; where the user picked a SpEffect interrupt on a step, that anim is cut at the
        /// SpEffect's frame and the handler's sequence follows (replacing the rest when it starts with ClearSubGoal,
        /// queued after it otherwise).
        /// </summary>
        private List<PathItem> BuildPath(IReadOnlyList<ComboNode> nodes, int depth)
        {
            var result = new List<PathItem>();
            var steps = ComboSequence.Flatten(nodes, Arms(depth));
            List<PathItem>? appendAfter = null;
            foreach (var s in steps)
            {
                var chosen = ChosenInterrupt(s, depth);
                if (chosen == null || depth >= MaxInterruptDepth) { result.Add(new PathItem(s, null)); continue; }
                var (frame, spStart, _, handler) = chosen.Value;
                var rest = BuildPath(handler.Body, depth + 1);
                bool clears = ComboSequence.ClearsOnPath(handler.Body, Arms(depth + 1));
                // The chosen arm neither clears nor adds anims (e.g. random arm "no branch taken"): the anim keeps playing.
                if (!clears && rest.Count == 0) { result.Add(new PathItem(s, null)); continue; }
                result.Add(new PathItem(s, frame, handler.SpEffectId, spStart));
                if (clears) { result.AddRange(rest); return result; }
                (appendAfter ??= new()).AddRange(rest);
            }
            if (appendAfter != null) result.AddRange(appendAfter);
            return result;
        }

        private IReadOnlyList<(int SpEffectId, float Frame, float EndFrame, SpEffectHandler Handler, string Via)> Candidates(int animId)
        {
            if (_handlers == null) return Array.Empty<(int, float, float, SpEffectHandler, string)>();
            var events = SpEvents(animId);
            var result = new List<(int, float, float, SpEffectHandler, string)>();
            foreach (var e in events)
                if (_handlers.Handlers.TryGetValue(e.SpEffectId, out var h) && !h.Replans && ComboSequence.AffectsCombo(h.Body))
                    result.Add((e.SpEffectId, e.Frame, e.EndFrame, h, ViaText(e)));
            return result;
        }

        /// <summary>" (cycle of TAE SpEffect 205032, every 0.5 s)" for cycle SpEffects, else "".</summary>
        private string ViaText(DsasBridge.SpEffectEvent e) =>
            e.ViaSpEffectId is int via ? $" (cycle of TAE SpEffect {_app.SpEffectText(via)}{(e.Interval > 0 ? $", every {e.Interval:0.##} s" : "")})" : "";

        /// <summary>TAE SpEffect events of an anim. Empty results are not cached: the anibnd may still be loading.</summary>
        private IReadOnlyList<DsasBridge.SpEffectEvent> SpEvents(int animId)
        {
            if (_spEvents.TryGetValue(animId, out var events)) return events;
            events = _app.Bridge.GetSpEffectEventsDetailed(_app.TaeAnimId(animId));
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
                return $"  {_app.SpEffectText(e.SpEffectId)} @ frames {e.Frame:0}–{e.EndFrame:0}{ViaText(e)}  ({how})";
            });
            return "TAE SpEffect events:\n" + string.Join("\n", lines);
        }

        /// <summary>Chosen interrupt of a step: trigger frame (default = SpEffect start), the SpEffect window and its handler.</summary>
        private (float Frame, float Start, float End, SpEffectHandler Handler)? ChosenInterrupt(ComboStep s, int depth)
        {
            if (!_interrupt.TryGetValue((s.Line, depth), out int k) || k < 0) return null;
            var c = Candidates(s.AnimId);
            if (k >= c.Count) return null;
            float start = c[k].Frame, end = Math.Max(c[k].Frame, c[k].EndFrame);
            float at = _trigger.TryGetValue((s.Line, depth), out float t) ? Math.Clamp(t, start, end) : start;
            return (at, start, end, c[k].Handler);
        }

        /// <summary>Trigger-frame picker for a chosen interrupt whose SpEffect stays active for a while.</summary>
        private void DrawTriggerFrame(ComboStep s, int level, (float Frame, float Start, float End, SpEffectHandler Handler) ci)
        {
            if (ci.End - ci.Start < 2) return;
            int lo = (int)Math.Round(ci.Start), hi = (int)Math.Round(ci.End), at = (int)Math.Round(ci.Frame);
            ImGui.TextDisabled("Triggers at");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 130));
            if (ImGui.SliderInt($"##trig{s.Line}_{level}", ref at, lo, hi, "frame %d")) { _trigger[(s.Line, level)] = at; _plan = null; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"SpEffect {_app.SpEffectText(ci.Handler.SpEffectId)} is active from frame {lo} to {hi} ({lo / 30f:0.00}–{hi / 30f:0.00} s).\n" +
                                 "Its SpEffectParam can re-trigger the interrupt every interval while it is active,\n" +
                                 "so pick the frame where the handler's condition (e.g. distance) would become true.");
            ImGui.SameLine();
            if (ImGui.SmallButton($"start##ts{s.Line}_{level}")) { _trigger[(s.Line, level)] = lo; _plan = null; }
            ImGui.SameLine();
            if (ImGui.SmallButton($"end##te{s.Line}_{level}")) { _trigger[(s.Line, level)] = hi; _plan = null; }
        }

        /// <summary>" [35%]", " [distance]", " [conditional]", or " [always]" when the arm has no random/state check (Random path always picks it).</summary>
        private static string ChanceTag(SpEffectHandler h) =>
            (h.Chance is double ch ? $" [{ch:0}%]" : "") +
            (h.OuterDistance ? " [distance]" : h.HasStateCondition ? " [conditional]" : h.DependsOnDistance ? " [distance inside]" : h.Chance == null ? " [always]" : "");

        private void DrawInterruptChoice(ComboStep s, int depth)
        {
            var c = Candidates(s.AnimId);
            if (c.Count == 0) return;
            int k = _interrupt.TryGetValue((s.Line, depth), out int v) ? v : -1;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X));
            string label = k >= 0 && k < c.Count
                ? $"SpEffect {_app.SpEffectText(c[k].SpEffectId)}{(c[k].Via.Length > 0 ? " (cycle)" : "")} (f{c[k].Frame:0}–{c[k].EndFrame:0}) -> Interrupt" + ChanceTag(c[k].Handler)
                : $"No interrupt ({c.Count} SpEffect interrupt{(c.Count == 1 ? "" : "s")} available)";
            if (!ImGui.BeginCombo($"##sp{s.Line}_{depth}", label))
            {
                if (k >= 0 && k < c.Count) _app.SpEffectLabelContextMenu($"##sp_ctx{s.Line}_{depth}", c[k].SpEffectId);
                return;
            }
            try
            {
                if (ImGui.Selectable("Continue normally (no interrupt)", k < 0)) { _interrupt[(s.Line, depth)] = -1; _trigger.Remove((s.Line, depth)); _plan = null; }
                for (int i = 0; i < c.Count; i++)
                {
                    var h = c[i].Handler;
                    string text = $"SpEffect {_app.SpEffectText(c[i].SpEffectId)}{c[i].Via} @ frames {c[i].Frame:0}–{c[i].EndFrame:0} -> Goal.Interrupt line {h.Line + 1}"
                        + (h.ClearsQueue ? " (ClearSubGoal)" : ComboSequence.ContainsClear(h.Body) ? " (ClearSubGoal in a branch)" : "") + ChanceTag(h);
                    if (ImGui.Selectable(text + $"##c{i}", i == k)) { _interrupt[(s.Line, depth)] = i; _trigger.Remove((s.Line, depth)); _plan = null; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("if " + h.Condition + (ChanceTag(h) == " [always]"
                        ? "\n[always]: the arm has no random or state check, so in game it fires every time the anim\nreaches this SpEffect (Random path always picks it). Pick \"Continue normally\" to see the rest."
                        : "") + (h.OuterDistance
                        ? "\n[distance]: tests the distance to the target, so Random path never picks it.\nCheck the conditions in the code and choose the trigger frame yourself."
                        : "") + "\nRight-click: set a label for this SpEffect");
                    _app.SpEffectLabelContextMenu($"##c_ctx{i}", c[i].SpEffectId);
                }
            }
            finally { ImGui.EndCombo(); }
        }

        private void Play(List<PathItem> path)
        {
            _manual.StopLooping();
            _plan = _app.Bridge.PlayCombo(path.Select(p => (_app.TaeAnimId(p.Step.AnimId), p.Cut)).ToList());
            if (_plan == null) _app.Notify("Load the character first (Project panel → Load model & anims, or click a character).");
        }

        /// <param name="cutBy">
        /// Set once a step of this interrupt level was cut by a SpEffect interrupt that runs ClearSubGoal: the steps
        /// after it (same level, incl. branch bodies) are dropped from the AI queue and never play (same rule as BuildPath).
        /// </param>
        private void DrawNodes(IReadOnlyList<ComboNode> nodes, int depth, int level, ref int? cutBy)
        {
            // depth = indent; level = SpEffect interrupt nesting (key of _interrupt, same as BuildPath).
            foreach (var n in nodes)
            {
                ImGui.PushID(n.Line * 31 + depth * 7 + level);
                try
                {
                    switch (n)
                    {
                        case ComboStep s when cutBy is int by:
                            ImGui.Indent(depth * 14 + 2);
                            if (ImGui.Selectable($"##step{s.Line}", false, ImGuiSelectableFlags.None, new Vector2(0, 0)))
                            {
                                _editor.JumpTo(s.Line);
                                if (_editor.Active is { } ed0) _ownJump = ed0.Editor.Cursor;
                                _app.PlayLuaAnim(s.AnimId);
                            }
                            ImGui.SameLine(0, 0);
                            ImGui.TextDisabled($"{s.AnimId}   {ShortGoal(s.GoalType)}   not reached (queue cleared by the SpEffect cut on {by})");
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip($"Line {s.Line + 1}: {s.GoalType}\nThe SpEffect interrupt on anim {by} runs ClearSubGoal(), so this anim is removed from\n" +
                                                 $"the AI queue and never plays on this path (its own SpEffect interrupts are not rolled).\n" +
                                                 $"Set {by} to \"Continue normally\" to play the rest of the combo.\n\nClick: jump to code + play this anim");
                            ImGui.Unindent(depth * 14 + 2);
                            break;
                        case ComboStep s:
                            ImGui.Indent(depth * 14 + 2);
                            if (ImGui.Selectable($"{s.AnimId}   {ShortGoal(s.GoalType)}##step{s.Line}", false, ImGuiSelectableFlags.None,
                                    new Vector2(Candidates(s.AnimId).Count > 0 ? 210 : 0, 0)))
                            {
                                _editor.JumpTo(s.Line);
                                if (_editor.Active is { } ed) _ownJump = ed.Editor.Cursor;
                                _app.PlayLuaAnim(s.AnimId);
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Line {s.Line + 1}: {s.GoalType}\nClick: jump to code + play this anim\nRight-click: label its SpEffects\n\n" + SpEffectSummary(s.AnimId));
                            StepContextMenu(s);
                            DrawInterruptChoice(s, level);
                            ImGui.Unindent(depth * 14 + 2);
                            if (ChosenInterrupt(s, level) is { } ci && level < MaxInterruptDepth)
                            {
                                ImGui.Indent((depth + 1) * 14 + 2);
                                ImGui.TextColored(Theme.Warn, $"SpEffect {_app.SpEffectText(ci.Handler.SpEffectId)} @ frame {ci.Frame:0} -> Goal.Interrupt (line {ci.Handler.Line + 1})" + (ComboSequence.ClearsOnPath(ci.Handler.Body, Arms(level + 1)) ? ", ClearSubGoal: replaces the rest of the combo" : ", no ClearSubGoal: queued after the rest"));
                                _app.SpEffectLabelContextMenu($"##ci_ctx{s.Line}_{level}", ci.Handler.SpEffectId);
                                DrawTriggerFrame(s, level, ci);
                                ImGui.Unindent((depth + 1) * 14 + 2);
                                int? innerCut = null;
                                DrawNodes(ci.Handler.Body, depth + 1, level + 1, ref innerCut);
                                // Same rule as BuildPath: an arm that neither clears nor adds anims lets the anim keep playing.
                                if (ComboSequence.ClearsOnPath(ci.Handler.Body, Arms(level + 1))) cutBy = s.AnimId;
                            }
                            break;
                        case ComboClear:
                            ImGui.Indent(depth * 14 + 2);
                            ImGui.TextDisabled("ClearSubGoal()");
                            ImGui.Unindent(depth * 14 + 2);
                            break;
                        case ComboBranch b:
                            ImGui.Indent(depth * 14 + 2);
                            int chosen = Arms(level)(b);
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.BeginCombo($"##br{b.Line}", ArmLabel(b.Arms[chosen])))
                            {
                                try
                                {
                                    foreach (int k in ArmOrder(b))
                                        if (ImGui.Selectable(ArmLabel(b.Arms[k]) + $"##arm{k}", k == chosen)) { _choice[(b.Line, level)] = k; _plan = null; }
                                }
                                finally { ImGui.EndCombo(); }
                            }
                            ImGui.Unindent(depth * 14 + 2);
                            DrawNodes(b.Arms[chosen].Body, depth + 1, level, ref cutBy);
                            break;
                    }
                }
                finally { ImGui.PopID(); }
            }
        }

        /// <summary>Right-click on a step: label any SpEffect of its TAE (incl. cycle SpEffects).</summary>
        private void StepContextMenu(ComboStep s)
        {
            if (!ImGui.BeginPopupContextItem($"##step_ctx{s.Line}")) return;
            try
            {
                var ids = SpEvents(s.AnimId).Select(e => e.SpEffectId).Distinct().ToList();
                if (ids.Count == 0) ImGui.TextDisabled("No SpEffect events in this anim's TAE (or not loaded).");
                foreach (int id in ids)
                    if (ImGui.BeginMenu($"SpEffect {_app.SpEffectText(id)}##m{id}"))
                    {
                        _app.SpEffectLabelMenuItems(id);
                        ImGui.EndMenu();
                    }
            }
            finally { ImGui.EndPopup(); }
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
                    if (p.Missing) { ImGui.TextColored(Theme.Error, $"{ComboScript.FormatAnimId(p.AnimId)}: {p.CutReason}"); continue; }
                    var col = idx == playing ? Theme.Ok : Theme.Dim;
                    ImGui.TextColored(col, $"{(idx == playing ? "▶" : " ")} {ComboScript.FormatAnimId(p.AnimId)}  frames 0–{p.EndFrame:0} / {p.TotalFrames:0}  ({p.CutReason})");
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
