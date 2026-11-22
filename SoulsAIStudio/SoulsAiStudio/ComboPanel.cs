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
    /// (<see cref="ComboScriptTab"/>, replaces DSAS's Combo Viewer). Interrupt candidates include on-hit SpEffects (AtkParam / Bullet).
    /// </summary>
    public sealed partial class ComboPanel
    {
        public const string WindowName = "Combo##sas_combo";
        private readonly SasApp _app;
        private readonly LuaEditorPanel _editor;
        private readonly Random _rng = new();
        /// <summary>Branch arms, step interrupts and trigger frames chosen by the user (or by Random path).</summary>
        private readonly ComboChoices _sel = new();
        private Dictionary<(int Line, int Level), int> _choice => _sel.Arms; // (branch line, interrupt level) -> arm index
        private Dictionary<(int Line, int Level), int> _interrupt => _sel.Interrupts; // step -> chosen SpEffect candidate (-1 none)
        private Dictionary<(int Line, int Level), float> _trigger => _sel.Triggers; // step -> chosen trigger frame inside the SpEffect window
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
        /// <summary>"Attacks hit": Random path / Statistics may pick on-hit SpEffects (AtkParam / Bullet).</summary>
        private bool _attacksHit;

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
            _sel.Clear();
            _plan = null;
            _stats = null;
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
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Goal.Interrupt line {h.Line + 1}:\nif {h.Condition}" + (h.DependsOnDistance ? "\n[distance]: the arm also tests the distance to the target" : "") + "\nPick it, then right-click the box: go to its Goal.Interrupt branch, Lua scripts, label");
                        _app.SpEffectLabelContextMenu($"##sp_start_ctx{h.Line}", h.SpEffectId); // the full menu can't be clicked inside an open dropdown
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
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click: go to its Goal.Interrupt branch, Lua scripts, label");
                SpEffectMenu("##sas_combo_hdr_ctx", _start, null);
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
            if (_spEventsAnibnd != spKey) { _spEvents.Clear(); _behaviorSp.Clear(); _queuedFrames.Clear(); _stats = null; _spEventsAnibnd = spKey; }
            var path = BuildPath(_nodes, 0);
            bool loaded = _app.Bridge.LoadedAnibnd != null;
            if (!loaded) ImGui.TextColored(Theme.Warn, "Load the character first (Project panel → Load model & anims, or click a character).");
            if (_start != null) DrawStartNotes(_start);

            if (ImGui.Button("▶ Play combo") && path.Count > 0) Play(path);
            SameLineIfFits(ButtonWidth("Random path"));
            if (ImGui.Button("Random path")) { RollRandom(); Play(BuildPath(_nodes, 0)); }
            if (ImGui.IsItemHovered()) Theme.TooltipAboveMouse("Plays the combo top-down like the game: the goal's random branches, then each anim's TAE SpEffects in\n" +
                "time order (before ComboAttack Queued). At each one Goal.Interrupt is rolled right there, with one\n" +
                "interruptRand shared by its condition and the ifs inside; a miss lets the anim go on to the next one.\n" +
                "Timers / SpEffect / HP checks and distance tests inside an arm count as 50/50.\n" +
                "Arms whose own condition tests the distance are never picked: choose them yourself.\n" +
                "On-hit SpEffects (AtkParam / Bullet) only count with \"Attacks hit\" on.");
            SameLineIfFits(ButtonWidth("Statistics"));
            DrawStatisticsButton();
            SameLineIfFits(ButtonWidth("▶ Resume"));
            if (ImGui.Button(_app.Bridge.ComboPaused ? "▶ Resume" : "|| Pause")) _app.Bridge.ToggleComboPause();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Space also pauses/resumes the combo");
            SameLineIfFits(ButtonWidth("■ Stop"));
            if (ImGui.Button("■ Stop")) { _app.Bridge.StopCombo(); }
            SameLineIfFits(ButtonWidth("→ Manual"));
            if (ImGui.Button("→ Manual") && path.Count > 0)
            {
                string from = _start != null ? $"Goal.Interrupt SpEffect {_app.SpEffectText(_start.SpEffectId)} (line {_start.Line + 1})" : $"Goal.{_goal!.Name} (line {_goal.Line + 1})";
                _manual.SetText($"# From Lua: {from}\n" + ComboScript.Format(path.Select(p =>
                    (_app.TaeAnimId(p.Step.AnimId), p.SpEffectId is int sp ? new ComboCut(-1, sp, p.Cut != p.SpStart ? p.Cut : null) : ComboCut.ComboAttackQueued)).ToList()));
                _selectManual = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this path (with its SpEffect interrupts) into the Manual tab as a combo script to edit");
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(path.Count == 0 ? "no anims in this path"
                : string.Join(" → ", path.Select(p => p.Cut != null ? p.Step.AnimId + " (SpEffect cut)" : p.Step.AnimId.ToString())));
            ImGui.PopTextWrapPos();

            DrawStatistics();
            ComputeHasNext();
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

        /// <summary>Arm chooser for branches at an interrupt level (0 = the goal, 1 = a handler run from it, …).</summary>
        private Func<ComboBranch, int> Arms(int level) => _sel.ArmsAt(level);

        /// <summary>
        /// A branch decided by game state (distance, HP, SpEffect…) with no <c>else</c> defaults to "(no branch taken)":
        /// nothing happens unless the user says the condition holds. Random branches default to their first arm.
        /// </summary>
        private static int DefaultArm(ComboBranch b) => ComboChoices.DefaultArm(b);

        /// <summary>Dropdown order: for state-decided branches "(no branch taken)" comes first.</summary>
        private static IEnumerable<int> ArmOrder(ComboBranch b)
        {
            int def = DefaultArm(b);
            if (def != 0) yield return def;
            for (int k = 0; k < b.Arms.Count; k++) if (k != def || def == 0) yield return k;
        }

        /// <summary>Simulator over this panel's candidates and the loaded TAE (lookups memoized for its lifetime).</summary>
        private ComboSimulator NewSimulator() =>
            new(_handlers, Candidates, QueuedFrame, MaxInterruptDepth) { AttacksHit = _attacksHit };

        /// <summary>
        /// Random path, simulated top-down like the game (<see cref="ComboSimulator"/>): the goal's branches are rolled when
        /// the goal runs, then each anim's handled SpEffects in time order before "ComboAttack Queued", each interrupt
        /// rolled right there with fresh rolls shared by its condition and the ifs inside it.
        /// </summary>
        private void RollRandom()
        {
            NewSimulator().Roll(_nodes, _sel, _rng);
            _plan = null;
        }

        /// <summary>
        /// Flattens the chosen branches; where the user picked a SpEffect interrupt on a step, that anim is cut at the
        /// SpEffect's frame and the handler's sequence follows (replacing the rest when it starts with ClearSubGoal,
        /// queued after it otherwise).
        /// </summary>
        private List<ComboPathItem> BuildPath(IReadOnlyList<ComboNode> nodes, int depth) =>
            ComboSimulator.BuildPath(nodes, _sel, (s, level) => ChosenInterrupt(s, level) is { } c ? (c.Frame, c.Start, c.Handler) : null,
                MaxInterruptDepth, depth);

        /// <summary>
        /// SpEffects that can interrupt the anim and that Goal.Interrupt handles: TAE SpEffect events (+ cycle SpEffects) and
        /// SpEffects of its behavior events (on hit: AtkParam / Bullet; on self: BehaviorParam / Bullet shooter), by frame.
        /// </summary>
        private IReadOnlyList<InterruptCandidate> Candidates(int animId)
        {
            if (_handlers == null) return Array.Empty<InterruptCandidate>();
            var result = new List<InterruptCandidate>();
            foreach (var e in SpEvents(animId))
                if (_handlers.Handlers.TryGetValue(e.SpEffectId, out var h) && !h.Replans && ComboSequence.AffectsCombo(h.Body))
                    result.Add(ComboDiagnostics.WithObserveCheck(_handlers, new InterruptCandidate(e.SpEffectId, e.Frame, e.EndFrame, h,
                        e.ViaSpEffectId != null ? SpEffectSourceKind.Cycle : SpEffectSourceKind.Tae, ViaText(e))));
            foreach (var b in BehaviorSp(animId))
                if (_handlers.Handlers.TryGetValue(b.SpEffectId, out var h) && !h.Replans && ComboSequence.AffectsCombo(h.Body))
                {
                    var c = new InterruptCandidate(b.SpEffectId, b.Frame, b.EndFrame, h, b.Kind,
                        (b.Kind == SpEffectSourceKind.OnHit ? " on hit (" : " (") + LastLink(b.Chain) + ")", Detail: b.Chain);
                    // Goal.Interrupt only sees it when observed on the side it lands on (on hit: TARGET_ENE_0, else TARGET_SELF).
                    result.Add(ComboDiagnostics.WithObserveCheck(_handlers, c));
                }
            return result.Count < 2 ? result : result.OrderBy(c => c.Frame).ToList(); // stable: TAE first at equal frames
        }

        private static string LastLink(string chain)
        {
            int i = chain.LastIndexOf(" → ", StringComparison.Ordinal);
            return i < 0 ? chain : chain[(i + 3)..];
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

        private readonly Dictionary<int, IReadOnlyList<Core.Combo.BehaviorSpEffect>> _behaviorSp = new();
        private readonly Dictionary<int, float> _queuedFrames = new();

        /// <summary>SpEffects of the anim's behavior events (AtkParam / Bullet / BehaviorParam). Cached once resolvable.</summary>
        private IReadOnlyList<Core.Combo.BehaviorSpEffect> BehaviorSp(int animId)
        {
            if (_behaviorSp.TryGetValue(animId, out var list)) return list;
            var r = _app.Bridge.GetBehaviorSpEffects(_app.TaeAnimId(animId));
            if (r == null) return Array.Empty<Core.Combo.BehaviorSpEffect>();
            _behaviorSp[animId] = r;
            return r;
        }

        /// <summary>Start frame of the anim's "ComboAttack Queued" window (only found frames are cached).</summary>
        private float? QueuedFrame(int animId)
        {
            if (_queuedFrames.TryGetValue(animId, out float f)) return f;
            var q = _app.Bridge.GetComboQueuedFrame(_app.TaeAnimId(animId));
            if (q is float v) _queuedFrames[animId] = v;
            return q;
        }

        /// <summary>Steps (line, level) followed by another anim on the current path: their SpEffects after ComboAttack Queued never fire.</summary>
        private readonly HashSet<(int Line, int Level)> _hasNext = new();

        private void ComputeHasNext()
        {
            _hasNext.Clear();
            void Walk(IReadOnlyList<ComboNode> nodes, int level)
            {
                if (level > MaxInterruptDepth) return;
                var steps = ComboSequence.Flatten(nodes, Arms(level));
                for (int i = 0; i < steps.Count; i++)
                {
                    if (i < steps.Count - 1) _hasNext.Add((steps[i].Line, level));
                    if (level < MaxInterruptDepth && ChosenInterrupt(steps[i], level) is { } ci) Walk(ci.Handler.Body, level + 1);
                }
            }
            Walk(_nodes, 0);
        }

        /// <summary>Timing / observe hints of a candidate on a step (empty = fine).</summary>
        private List<string> CandidateWarnings(ComboStep s, int level, InterruptCandidate c) =>
            _handlers == null ? new List<string>()
                : ComboDiagnostics.Warnings(_handlers, c, QueuedFrame(s.AnimId), _hasNext.Contains((s.Line, level)));

        /// <summary>Tooltip text: every SpEffect event in the anim's TAE (and its behavior SpEffects) and whether Goal.Interrupt handles it.</summary>
        private string SpEffectSummary(int animId)
        {
            string How(int id) => _handlers != null && _handlers.Handlers.TryGetValue(id, out var h)
                ? (h.Replans ? "Interrupt: Replanning()" : $"Interrupt line {h.Line + 1}")
                : "no handler in Goal.Interrupt";
            var events = SpEvents(animId);
            string text = events.Count == 0 ? "TAE SpEffect events: none (or character not loaded)"
                : "TAE SpEffect events:\n" + string.Join("\n", events.Select(e =>
                    $"  {_app.SpEffectText(e.SpEffectId)} @ frames {e.Frame:0}–{e.EndFrame:0}{ViaText(e)}  ({How(e.SpEffectId)})"));
            var beh = BehaviorSp(animId);
            if (beh.Count > 0)
                text += "\nAttack / bullet SpEffects (params):\n" + string.Join("\n", beh.Select(b =>
                    $"  {_app.SpEffectText(b.SpEffectId)} {(b.Kind == SpEffectSourceKind.OnHit ? "on hit" : "on self")} @ frames {b.Frame:0}–{b.EndFrame:0} via {LastLink(b.Chain)}  ({How(b.SpEffectId)})"));
            return text;
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
            ImGui.SetNextItemWidth(Math.Max(60, ImGui.GetContentRegionAvail().X - ButtonWidth("start") - ButtonWidth("end") - 3 * ImGui.GetStyle().ItemSpacing.X));
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

        private static string KindTag(InterruptCandidate c) => c.Kind switch
        {
            SpEffectSourceKind.Cycle => " (cycle)",
            SpEffectSourceKind.OnHit => " (on hit)",
            SpEffectSourceKind.Behavior => " (behavior)",
            _ => "",
        };

        private void DrawInterruptChoice(ComboStep s, int depth)
        {
            var c = Candidates(s.AnimId);
            if (c.Count == 0) return;
            int k = _interrupt.TryGetValue((s.Line, depth), out int v) ? v : -1;
            var warnings = c.Select(x => CandidateWarnings(s, depth, x)).ToList();
            bool chosenWarn = k >= 0 && k < c.Count && warnings[k].Count > 0;
            bool anyWarn = warnings.Any(w => w.Count > 0);
            SameLineIfFits(140);
            ImGui.SetNextItemWidth(-1);
            string label = k >= 0 && k < c.Count
                ? $"{(chosenWarn ? "(!) " : "")}SpEffect {_app.SpEffectText(c[k].SpEffectId)}{KindTag(c[k])} (f{c[k].Frame:0}–{c[k].EndFrame:0})" + ChanceTag(c[k].Handler)
                : $"No interrupt ({c.Count} SpEffect interrupt{(c.Count == 1 ? "" : "s")} available){(anyWarn ? " (!)" : "")}";
            if (chosenWarn) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            bool open = ImGui.BeginCombo($"##sp{s.Line}_{depth}", label);
            if (chosenWarn) ImGui.PopStyleColor();
            if (!open)
            {
                if (anyWarn && ImGui.IsItemHovered())
                    ImGui.SetTooltip(Esc(string.Join("\n", c.Select((x, i) => (x, w: warnings[i])).Where(t => t.w.Count > 0)
                        .Select(t => $"SpEffect {_app.SpEffectText(t.x.SpEffectId)}{KindTag(t.x)}:\n  " + string.Join("\n  ", t.w)))
                        + "\n\nHints, not errors: the analysis only knows the loaded TAE, params and this script."));
                if (k >= 0 && k < c.Count) SpEffectMenu($"##sp_ctx{s.Line}_{depth}", c[k].Handler, c[k]);
                return;
            }
            try
            {
                if (ImGui.Selectable("Continue normally (no interrupt)", k < 0)) { _interrupt[(s.Line, depth)] = -1; _trigger.Remove((s.Line, depth)); _plan = null; }
                for (int i = 0; i < c.Count; i++)
                {
                    var h = c[i].Handler;
                    string text = $"{(warnings[i].Count > 0 ? "(!) " : "")}SpEffect {_app.SpEffectText(c[i].SpEffectId)}{c[i].Via} @ frames {c[i].Frame:0}–{c[i].EndFrame:0} -> Goal.Interrupt line {h.Line + 1}"
                        + (h.ClearsQueue ? " (ClearSubGoal)" : ComboSequence.ContainsClear(h.Body) ? " (ClearSubGoal in a branch)" : "") + ChanceTag(h)
                        + (c[i].Blocked ? " [not observed]" : "");
                    if (warnings[i].Count > 0) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                    bool picked = ImGui.Selectable(text + $"##c{i}", i == k);
                    if (warnings[i].Count > 0) ImGui.PopStyleColor();
                    if (picked) { _interrupt[(s.Line, depth)] = i; _trigger.Remove((s.Line, depth)); _plan = null; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Esc("if " + h.Condition + (ChanceTag(h) == " [always]"
                        ? "\n[always]: the arm has no random or state check, so in game it fires every time the anim\nreaches this SpEffect (Random path always picks it). Pick \"Continue normally\" to see the rest."
                        : "") + (h.OuterDistance
                        ? "\n[distance]: tests the distance to the target, so Random path never picks it.\nCheck the conditions in the code and choose the trigger frame yourself."
                        : "") + (c[i].OnHit
                        ? "\n[on hit]: applied to the target when this attack hits (frames = hitbox window). Random path and\nStatistics only pick it with \"Attacks hit\" on."
                        : c[i].Kind == SpEffectSourceKind.Behavior ? "\n[behavior]: applied to the character itself when this behavior / bullet event runs." : "")
                        + (c[i].Blocked ? "\n[not observed]: Goal.Interrupt never sees it in game (no AddObserveSpecialEffectAttribute on the side\nit lands on), so Random path and Statistics never pick it. You can still pick it here to preview." : "")
                        + (c[i].Detail.Length > 0 ? "\nSource: " + c[i].Detail : "")
                        + (warnings[i].Count > 0 ? "\n\n(!) " + string.Join("\n(!) ", warnings[i]) + "\n(hints, not errors)" : "")
                        + "\nPick it, then right-click the box: go to its Goal.Interrupt branch, add its observe, Lua scripts"));
                    _app.SpEffectLabelContextMenu($"##c_ctx{i}", c[i].SpEffectId); // the full menu can't be clicked inside an open dropdown
                }
            }
            finally { ImGui.EndCombo(); }
        }

        /// <summary>
        /// Right-click menu of a SpEffect interrupt (Start from list, interrupt dropdowns, chosen interrupt line): jump to its
        /// Goal.Interrupt branch in the active script, add the observe it lacks, list it in every script of the character
        /// (as in the Parameters panel's "Lua" button), and the SpEffect label items.
        /// </summary>
        private void SpEffectMenu(string strId, SpEffectHandler h, InterruptCandidate? c)
        {
            if (!ImGui.BeginPopupContextItem(strId)) return;
            try
            {
                int id = h.SpEffectId;
                ImGui.TextDisabled($"SpEffect {_app.SpEffectText(id)}");
                if (ImGui.MenuItem($"Go to Goal.Interrupt branch (line {h.Line + 1})"))
                {
                    _editor.JumpTo(h.Line);
                    _app.Layout.FocusEditorTabFrames = 10;
                }
                if (_handlers != null && _editor.ActiveScript is { } script)
                {
                    var on = ComboDiagnostics.ObservedOn(_handlers, id);
                    // Side it lands on: the target for on-hit SpEffects, the character itself otherwise (unknown: offer both).
                    var need = c == null ? new[] { SpEffectHandlerWriter.TargetSelf, SpEffectHandlerWriter.TargetEnemy }
                        : new[] { c.OnTarget ? SpEffectHandlerWriter.TargetEnemy : SpEffectHandlerWriter.TargetSelf };
                    foreach (string target in need)
                        if (!on.Contains(target) && (c == null ? on.Count == 0 : c.Blocked))
                            if (ImGui.MenuItem($"Add observe on {target}"))
                                _app.SpEffectLink.AddTo(script, id, target);
                    if (on.Count > 0) ImGui.TextDisabled("Observed on " + string.Join(", ", on));
                }
                if (ImGui.MenuItem("In all Lua scripts of this character...")) _app.SpEffectLink.Request(id);
                ImGui.Separator();
                _app.SpEffectLabelMenuItems(id);
            }
            finally { ImGui.EndPopup(); }
        }

        /// <summary>Indent of a tree depth: 12 px per level, at most a third of the panel so deep combos stay readable.</summary>
        private static float Ind(int depth) => 2 + Math.Min(depth * 12f, Math.Max(24f, ImGui.GetWindowWidth() / 3f));

        /// <summary>Width of a button (or small button) with this label.</summary>
        private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

        /// <summary>SameLine() when an item of <paramref name="width"/> still fits on the current line, else wrap to a new line.</summary>
        internal static void SameLineIfFits(float width)
        {
            float right = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width;
            float limit = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            if (right <= limit) ImGui.SameLine();
        }

        /// <summary>Escapes '%' for ImGui format-string APIs (SetTooltip / Text).</summary>
        private static string Esc(string s) => s.Replace("%", "%%");

        private void Play(IReadOnlyList<ComboPathItem> path)
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
                            ImGui.Indent(Ind(depth));
                            if (ImGui.Selectable($"##step{s.Line}", false, ImGuiSelectableFlags.None, new Vector2(0, 0)))
                            {
                                _editor.JumpTo(s.Line);
                                if (_editor.Active is { } ed0) _ownJump = ed0.Editor.Cursor;
                                _app.PlayLuaAnim(s.AnimId);
                            }
                            ImGui.SameLine(0, 0);
                            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                            ImGui.PushTextWrapPos(0);
                            ImGui.TextUnformatted($"{s.AnimId}   {ShortGoal(s.GoalType)}   not reached (queue cleared by the SpEffect cut on {by})");
                            ImGui.PopTextWrapPos();
                            ImGui.PopStyleColor();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip($"Line {s.Line + 1}: {s.GoalType}\nThe SpEffect interrupt on anim {by} runs ClearSubGoal(), so this anim is removed from\n" +
                                                 $"the AI queue and never plays on this path (its own SpEffect interrupts are not rolled).\n" +
                                                 $"Set {by} to \"Continue normally\" to play the rest of the combo.\n\nClick: jump to code + play this anim");
                            ImGui.Unindent(Ind(depth));
                            break;
                        case ComboStep s:
                            ImGui.Indent(Ind(depth));
                            if (ImGui.Selectable($"{s.AnimId}   {ShortGoal(s.GoalType)}##step{s.Line}", false, ImGuiSelectableFlags.None,
                                    new Vector2(Candidates(s.AnimId).Count > 0 ? Math.Min(210, Math.Max(120, ImGui.GetContentRegionAvail().X * 0.4f)) : 0, 0)))
                            {
                                _editor.JumpTo(s.Line);
                                if (_editor.Active is { } ed) _ownJump = ed.Editor.Cursor;
                                _app.PlayLuaAnim(s.AnimId);
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Line {s.Line + 1}: {s.GoalType}\nClick: jump to code + play this anim\nRight-click: label its SpEffects\n\n" + SpEffectSummary(s.AnimId));
                            StepContextMenu(s);
                            DrawInterruptChoice(s, level);
                            ImGui.Unindent(Ind(depth));
                            if (ChosenInterrupt(s, level) is { } ci && level < MaxInterruptDepth)
                            {
                                ImGui.Indent(Ind(depth + 1));
                                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                                ImGui.PushTextWrapPos(0);
                                ImGui.TextUnformatted($"SpEffect {_app.SpEffectText(ci.Handler.SpEffectId)} @ frame {ci.Frame:0} -> Goal.Interrupt (line {ci.Handler.Line + 1})" + (ComboSequence.ClearsOnPath(ci.Handler.Body, Arms(level + 1)) ? ", ClearSubGoal: replaces the rest of the combo" : ", no ClearSubGoal: queued after the rest"));
                                ImGui.PopTextWrapPos();
                                ImGui.PopStyleColor();
                                SpEffectMenu($"##ci_ctx{s.Line}_{level}", ci.Handler, null);
                                DrawTriggerFrame(s, level, ci);
                                ImGui.Unindent(Ind(depth + 1));
                                int? innerCut = null;
                                DrawNodes(ci.Handler.Body, depth + 1, level + 1, ref innerCut);
                                // Same rule as BuildPath: an arm that neither clears nor adds anims lets the anim keep playing.
                                if (ComboSequence.ClearsOnPath(ci.Handler.Body, Arms(level + 1))) cutBy = s.AnimId;
                            }
                            break;
                        case ComboClear:
                            ImGui.Indent(Ind(depth));
                            ImGui.TextDisabled("ClearSubGoal()");
                            ImGui.Unindent(Ind(depth));
                            break;
                        case ComboBranch b:
                            ImGui.Indent(Ind(depth));
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
                            ImGui.Unindent(Ind(depth));
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
