#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DSAnimStudio;
using ImGuiNET;
using SoulsAiStudio.Core.Anim;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Animation Slots" window: Swap two animation ids (TAE entries, HKX files, Override HKX / Import references, and
    /// in an AI project the anim ids in the open Lua scripts) or Add a new empty animation (Override HKX of an existing
    /// anim, or a new .hkx file retargeted to this character). Changes go into the loaded anibnd (saved with Ctrl+S).
    /// </summary>
    public sealed class AnimSlotsWindow
    {
        public const string WindowName = "Animation Slots##sas_animslots";
        private readonly SasApp _app;
        public bool IsOpen;
        private bool _focus;
        private bool _selectAdd;

        // swap
        private string _a = "", _b = "";
        private AnimSwapPlan? _plan;
        private string? _planError;
        private string? _planKey;
        private readonly HashSet<int> _keepRefs = new();
        private bool _swapLua = true;

        // add
        private string _newId = "";
        private string _label = "";
        private bool _labelEdited;
        private int _source; // 0 = override HKX of an anim, 1 = .hkx file
        private string _overrideFrom = "";
        private string _hkxPath = "";
        private byte[]? _hkxBytes;
        private List<string> _hkxProblems = new();
        private string? _hkxInfo;

        private AnimSlotsBridge.Snapshot? _snap;
        private DateTime _snapTime;

        public AnimSlotsWindow(SasApp app) => _app = app;

        /// <summary>Opens on the Swap tab with the selected anim as A (or on Add).</summary>
        public void Open(bool add = false)
        {
            IsOpen = true;
            _focus = true;
            _selectAdd = add;
            _snap = AnimSlotsBridge.Read();
            if (_snap != null && _snap.SelectedId >= 0)
            {
                _a = AnimSlots.IdName(_snap.SelectedId);
                if (string.IsNullOrEmpty(_newId)) _newId = AnimSlots.IdName(AnimSlots.NextFreeId(_snap.Entries, _snap.HkxIds, _snap.SelectedId));
                if (string.IsNullOrEmpty(_overrideFrom)) _overrideFrom = AnimSlots.IdName(_snap.SelectedId);
            }
            _planKey = null;
        }

        public bool Build()
        {
            if (!IsOpen) return false;
            ImGui.SetNextWindowSize(new Vector2(640, 520), ImGuiCond.FirstUseEver);
            if (_focus) { ImGui.SetNextWindowFocus(); _focus = false; }
            bool focused = false;
            bool visible = Theme.BeginOpaque(WindowName, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                if (AnimSlotsBridge.WhyUnavailable(_app) is { } why) { ImGui.TextColored(Theme.Warn, why.Replace("%", "%%")); return focused; }
                if (_snap == null || DateTime.UtcNow - _snapTime > TimeSpan.FromSeconds(1)) { _snap = AnimSlotsBridge.Read(); _snapTime = DateTime.UtcNow; }
                if (_snap == null) { ImGui.TextDisabled("Load a character first."); return focused; }
                ImGui.TextDisabled($"{Path.GetFileName(_app.Bridge.LoadedAnibnd ?? "")}: changes are saved into this anibnd with Ctrl+S. Undo a swap: \"Undo last swap\" (Swap tab), or Edit > Undo [Global] Action (Ctrl+Shift+Z).");
                if (ImGui.BeginTabBar("##sas_animslots_tabs"))
                {
                    if (ImGui.BeginTabItem("Swap")) { DrawSwapHistory(); DrawSwap(); ImGui.EndTabItem(); }
                    if (BeginTab("Add", _selectAdd)) { DrawAdd(); ImGui.EndTabItem(); }
                    _selectAdd = false;
                    ImGui.EndTabBar();
                }
            }
            finally { ImGui.End(); }
            return focused;
        }

        private static unsafe bool BeginTab(string label, bool select)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(label + "\0");
            fixed (byte* p = bytes) return ImGuiNative.igBeginTabItem(p, null, select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None) != 0;
        }

        private static void Tip(string s) { if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.Replace("%", "%%")); }

        private int? ParseId(string text)
        {
            var proj = AnimSlotsBridge.Tae?.Proj;
            if (proj == null || string.IsNullOrWhiteSpace(text)) return null;
            return SplitAnimID.TryParse(proj, text.Trim(), out var id, out _) ? id.GetFullID(proj) : null;
        }

        private string Describe(int id)
        {
            var e = _snap!.Entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return _snap.HkxIds.Contains(id) ? "(HKX file only, no entry)" : "(does not exist)";
            string how = e.Kind switch
            {
                AnimHeaderKind.OverrideHkx => $"Override HKX: {AnimSlots.IdName(e.RefId)}",
                AnimHeaderKind.ImportOther => $"imports {AnimSlots.IdName(e.RefId)}",
                _ => _snap.HkxIds.Contains(id) ? "own HKX" : "own HKX (missing!)",
            };
            return $"\"{e.Name}\", {how}";
        }

        // ---------------------------------------------------------------- swap

        private void DrawSwap()
        {
            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##swap_a", "a000_003001", ref _a, 32);
            ImGui.SameLine(); ImGui.TextUnformatted("<->"); ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##swap_b", "a000_003012", ref _b, 32);
            ImGui.SameLine();
            if (ImGui.SmallButton("Selected -> A") && _snap!.SelectedId >= 0) _a = AnimSlots.IdName(_snap.SelectedId);
            Tip("Put the animation selected in the Animations list into A");

            int? a = ParseId(_a), b = ParseId(_b);
            string key = $"{a}|{b}|{_snap!.Entries.Count}";
            if (key != _planKey)
            {
                _planKey = key;
                _plan = null; _planError = null; _keepRefs.Clear();
                if (a is int ia && b is int ib)
                {
                    try { _plan = AnimSlots.PlanSwap(_snap.Entries, _snap.HkxIds, ia, ib); }
                    catch (ArgumentException ex) { _planError = ex.Message; }
                }
            }
            if (a is int da) ImGui.TextDisabled($"A {AnimSlots.IdName(da)}: {Describe(da)}");
            if (b is int db) ImGui.TextDisabled($"B {AnimSlots.IdName(db)}: {Describe(db)}");
            if (_planError != null) { ImGui.TextColored(Theme.Warn, _planError); return; }
            if (_plan == null) { ImGui.TextDisabled("Enter two animation ids (3001 or a000_003001)."); return; }
            var plan = _plan;
            bool crossCategory = plan.A / 1_000_000 != plan.B / 1_000_000;

            ImGui.Separator();
            ImGui.BeginChild("##swap_preview", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2.2f), false);
            try
            {
                ImGui.TextColored(Theme.Accent, "What changes");
                foreach (var (f, t) in plan.EntryMoves) ImGui.BulletText($"TAE entry {AnimSlots.IdName(f)} -> {AnimSlots.IdName(t)} (with all its events)");
                foreach (var (f, t) in plan.HkxMoves) ImGui.BulletText($"HKX file {AnimSlots.IdName(f)}.hkx -> {AnimSlots.IdName(t)}.hkx");
                foreach (var (id, o, n) in plan.NameChanges) ImGui.BulletText($"Label \"{o}\" -> \"{n}\" ({AnimSlots.IdName(id)})");
                if (plan.RefChanges.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(Theme.Accent, $"Animations that borrow A or B ({plan.RefChanges.Count})");
                    ImGui.TextDisabled("Ticked = follow the motion/entry they used (so they still play the same thing).");
                    foreach (var r in plan.RefChanges)
                    {
                        bool follow = !_keepRefs.Contains(r.EntryId);
                        string kind = r.Kind == AnimHeaderKind.OverrideHkx ? "Override HKX" : "Import";
                        if (ImGui.Checkbox($"{AnimSlots.IdName(r.EntryId)}: {kind} {AnimSlots.IdName(r.OldRef)} -> {AnimSlots.IdName(r.NewRef)}##ref{r.EntryId}", ref follow))
                        { if (follow) _keepRefs.Remove(r.EntryId); else _keepRefs.Add(r.EntryId); }
                        Tip(follow ? "It will point at the new id of the same motion." : $"It will keep pointing at {AnimSlots.IdName(r.OldRef)}, i.e. play what is there after the swap.");
                    }
                }
                DrawLuaPart(plan);
                foreach (var w in plan.Warnings) ImGui.TextColored(Theme.Warn, w.Replace("%", "%%"));
                if (crossCategory) ImGui.TextColored(Theme.Warn, "Swapping across categories is not supported: pick two ids of the same aXXX category.");
            }
            finally { ImGui.EndChild(); }

            ImGui.BeginDisabled(crossCategory);
            if (ImGui.Button($"Swap {AnimSlots.IdName(plan.A)} <-> {AnimSlots.IdName(plan.B)}")) DoSwap(plan);
            ImGui.EndDisabled();
        }

        // ---------------------------------------------------------------- swap: Lua of every script of this character

        /// <summary>One AI script that drives the loaded character (found in every luabnd of the mod and the game).</summary>
        private sealed class LuaRow
        {
            public required SharedScript Shared;
            public bool Selected;
            /// <summary>aXXX category its anim ids refer to: the project's setting, or a guess (a000) the user can change.</summary>
            public int Category;
            /// <summary>Anim id literals in the file as loaded (null while loading/decompiling).</summary>
            public volatile List<int>? Values;
            public volatile string? Error;
            /// <summary>Category guessed from the TAE (scripts not in the project); null note = sure or user-chosen.</summary>
            public bool CategoryGuessed;
            public string? CategoryNote;
            public ProjectScript Script => Shared.Script;
            public string Key => Shared.Script.Key;
        }

        private volatile List<LuaRow>? _luaRows;
        private string? _luaRowsAnibnd;
        private volatile string? _luaScanError;
        private readonly Dictionary<string, (long Version, List<int> Values)> _openValues = new();

        private void StartLuaScan(string anibnd)
        {
            var session = _app.Session;
            if (session == null) return;
            _luaRowsAnibnd = anibnd;
            _luaRows = null;
            _luaScanError = null;
            var projectScripts = session.Project.Scripts.ToList();
            var locator = new CharacterLocator(session.Project.GameDir, session.Project.ModDir);
            _ = Task.Run(async () =>
            {
                try
                {
                    var (candidates, errors) = session.Catalog.Scan();
                    var rows = SharedCharacterScripts.Find(candidates, projectScripts, locator, anibnd)
                        .Select(sh => new LuaRow
                        {
                            Shared = sh,
                            Category = sh.Script.AnimCategory,
                            Selected = sh.Candidate.Source != LuabndSource.Game, // game-only luabnd: opt in (saving creates a mod copy)
                        })
                        .OrderBy(r => r.Script.Luabnd).ThenBy(r => r.Script.FileName).ToList();
                    if (errors.Count > 0) _luaScanError = "Some luabnds could not be read: " + string.Join("; ", errors);
                    _luaRows = rows;
                    foreach (var r in rows) // sequential: the decompiler runs one process at a time
                    {
                        try
                        {
                            var doc = await session.OpenAsync(r.Script);
                            r.Values = AnimSlots.FindLuaAnimLiterals(doc.SavedText).Select(l => l.Value).ToList();
                        }
                        catch (Exception ex) { r.Error = ex.GetBaseException().Message; }
                    }
                }
                catch (Exception ex)
                {
                    _luaScanError = "Could not list the AI scripts: " + ex.GetBaseException().Message;
                    _luaRows ??= new();
                }
            });
        }

        /// <summary>Anim id literals of a row: the live editor text when the script is open, else the file as loaded.</summary>
        private List<int>? ValuesOf(LuaRow r)
        {
            var o = _app.OpenScripts.FirstOrDefault(x => x.Script.Key == r.Key);
            if (o?.Doc == null || o.Loading != null) return r.Values;
            if (_openValues.TryGetValue(r.Key, out var c) && c.Version == o.Editor.Version) return c.Values;
            var values = AnimSlots.FindLuaAnimLiterals(o.Editor.Text).Select(l => l.Value).ToList();
            _openValues[r.Key] = (o.Editor.Version, values);
            return values;
        }

        private static int CategoryOf(LuaRow r) => r.Shared.InProject ? r.Script.AnimCategory : r.Category;

        private static int? ToLua(int full, int cat) => full / 1_000_000 == cat ? (cat == 0 ? full : full % 1_000_000) : null;

        private void DrawLuaPart(AnimSwapPlan plan)
        {
            if (_app.Project == null || _app.Project.IsAnimationsOnly) return; // animations-only: no Lua to ask about
            ImGui.Spacing();
            ImGui.Checkbox("Also swap the ids in Lua", ref _swapLua);
            Tip("On: the AI keeps doing the same moves, only the ids change (AddSubGoal anim ids and SetCoolTime anims).\n" +
                "Off: the AI now plays the other animation where it used A or B.");
            if (!_swapLua) return;
            string? anibnd = _app.Bridge.LoadedAnibnd;
            if (anibnd == null) return;
            if (_luaRowsAnibnd != anibnd) StartLuaScan(anibnd);
            ImGui.SameLine();
            if (ImGui.SmallButton("Rescan")) StartLuaScan(anibnd);
            Tip("Look again for scripts of this character in every luabnd of the mod and the game.");
            if (_luaScanError is { } err) ImGui.TextColored(Theme.Warn, err.Replace("%", "%%"));
            var rows = _luaRows;
            if (rows == null) { ImGui.TextDisabled("Looking for every AI script of this character (all luabnds)..."); return; }

            ImGui.TextDisabled("Every AI script that drives this character. Ticked scripts that are not open get opened in a tab\n" +
                               "with the change (unsaved): check them, then Save all (Lua editor) or Souls AI > Save All Scripts.");
            var cats = _snap!.Entries.Select(e => e.Id / 1_000_000).Distinct().OrderBy(x => x).ToArray();
            HashSet<int>? taeIds = null;
            int loading = 0, unused = 0;
            foreach (var r in rows)
            {
                var values = ValuesOf(r);
                if (values != null && !r.Shared.InProject && !r.CategoryGuessed)
                {
                    r.CategoryGuessed = true;
                    var (guess, sure) = AnimSlots.GuessLuaCategory(values, taeIds ??= _snap.Entries.Select(e => e.Id).ToHashSet());
                    r.Category = guess;
                    if (!sure && cats.Length > 1) { r.Selected = false; r.CategoryNote = "category unknown: pick it, then tick"; }
                }
                int cat = CategoryOf(r);
                if (values == null)
                {
                    if (r.Error is { } e) ImGui.TextColored(Theme.Warn, $"{r.Script.Luabnd} > {r.Script.FileName}: {e}".Replace("%", "%%"));
                    else loading++;
                    continue;
                }
                int? la = ToLua(plan.A, cat), lb = ToLua(plan.B, cat);
                bool otherCat = la == null || lb == null;
                int count = la is int x && lb is int y ? values.Count(v => v == x || v == y) : 0;
                // Project scripts know their category: hide when unaffected. Others show while their (guessed) category is off.
                if (r.Shared.InProject ? otherCat || count == 0 : !otherCat && count == 0) { unused++; continue; }

                ImGui.PushID(r.Key);
                try
                {
                    ImGui.BeginDisabled(otherCat);
                    bool sel = r.Selected && !otherCat;
                    if (ImGui.Checkbox($"{r.Script.Luabnd} > {r.Script.FileName}", ref sel)) r.Selected = sel;
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextDisabled(otherCat ? $"uses a{cat:D3}: not affected" : $"{count} anim id{(count == 1 ? "" : "s")} ({la} <-> {lb})");
                    var notes = new List<string>();
                    bool open = _app.OpenScripts.Any(o => o.Script.Key == r.Key);
                    if (!open) notes.Add("opens in a tab");
                    if (!r.Shared.InProject) notes.Add("added to the project");
                    if (r.Shared.Candidate.IsBytecode && !open) notes.Add("bytecode: saved as plain text");
                    if (r.Shared.Candidate.Source == LuabndSource.Game) notes.Add($"game only: saving creates {r.Script.Luabnd}.luabnd.dcx in the mod");
                    if (r.CategoryNote != null) notes.Insert(0, r.CategoryNote);
                    if (notes.Count > 0) { ImGui.SameLine(); ImGui.TextColored(Theme.Warn, "(" + string.Join(", ", notes) + ")"); }
                    if (!r.Shared.InProject && cats.Length > 1)
                    {
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80);
                        if (ImGui.BeginCombo("##cat", $"a{r.Category:D3}"))
                        {
                            foreach (int c in cats)
                                if (ImGui.Selectable($"a{c:D3}", c == r.Category)) { r.Category = c; r.CategoryNote = null; }
                            ImGui.EndCombo();
                        }
                        Tip("Anim category this script's ids refer to. The script is not in the project, so it was guessed from\n" +
                            "which category has its anim ids; when several have all of them you must pick (e.g. Isshin's\n" +
                            "spear phase 540310 uses a100, but every id it uses also exists in a000).");
                    }
                }
                finally { ImGui.PopID(); }
            }
            if (loading > 0) ImGui.TextDisabled($"Reading {loading} more script{(loading == 1 ? "" : "s")} (decompiling bytecode if needed)...");
            if (unused > 0) ImGui.TextDisabled($"{unused} other script{(unused == 1 ? "" : "s")} of this character don't use these ids.");
        }

        private void DoSwap(AnimSwapPlan plan)
        {
            try
            {
                var record = new AnimSwapRecord(plan.A, plan.B, DateTime.Now);
                var entry = new SwapEntry(record, _app.Project);
                entry.Handle = AnimSlotsBridge.ApplySwap(plan, _keepRefs, onUndo: () => OnSwapUndoRedo(entry, undo: true), onRedo: () => OnSwapUndoRedo(entry, undo: false));
                _history.Add(record);
                _entries.Insert(0, entry);
                if (_entries.Count > _history.Capacity) _entries.RemoveRange(_history.Capacity, _entries.Count - _history.Capacity);
                int luaEdits = 0, scripts = 0, opening = 0;
                if (_swapLua && _app.Project is { IsAnimationsOnly: false } && _luaRows is { } rows)
                {
                    foreach (var r in rows)
                    {
                        int cat = CategoryOf(r);
                        if (!r.Selected || ToLua(plan.A, cat) is not int la || ToLua(plan.B, cat) is not int lb) continue;
                        if (ValuesOf(r) is not { } values || !values.Any(v => v == la || v == lb)) continue;
                        var open = _app.OpenScripts.FirstOrDefault(o => o.Script.Key == r.Key);
                        if (open?.Doc != null && open.Loading == null)
                        {
                            int n = SwapIn(open, la, lb, record);
                            if (n > 0) { luaEdits += n; scripts++; }
                            continue;
                        }
                        if (!r.Shared.InProject) r.Script.AnimCategory = r.Category;
                        record.Pending++;
                        _app.OpenScriptWith(r.Script, o =>
                        {
                            record.Pending--;
                            if (record.IsUndone) { _app.Notify($"{o.Script.FileName}: the swap was undone before it opened, its ids were left as they are."); return; }
                            int n = SwapIn(o, la, lb, record);
                            _app.Notify($"{o.Script.FileName}: swapped {n} anim id{(n == 1 ? "" : "s")} ({la} <-> {lb}), not saved yet.");
                        }, activate: false);
                        opening++;
                    }
                }
                string lua = (luaEdits > 0 ? $" and {luaEdits} anim ids in {scripts} open script{(scripts == 1 ? "" : "s")}" : "")
                             + (opening > 0 ? $"; opening {opening} more script{(opening == 1 ? "" : "s")} with the change" : "");
                _app.Notify($"Swapped {AnimSlots.IdName(plan.A)} <-> {AnimSlots.IdName(plan.B)}{lua}. Nothing is saved yet: Ctrl+S for the anibnd, Save all for the scripts. " +
                            "Undo all of it with \"Undo last swap\" (Animation Slots window, Souls AI menu) or Undo [Global] Action.");
                (_a, _b) = (_b, _a);
            }
            catch (Exception ex) { _app.ReportError("Swap animations", ex); }
            _snap = AnimSlotsBridge.Read();
            _planKey = null;
        }

        /// <summary>
        /// Swaps the two ids in an open script as one editor undo step; records it in <paramref name="record"/> (with the
        /// ids' counts before, to recognise the state later). Returns how many literals changed.
        /// </summary>
        private static int SwapIn(OpenScript o, int la, int lb, AnimSwapRecord? record)
        {
            string text = o.Editor.Text;
            if (AnimSlots.SwapLuaAnimIds(text, la, lb, out int n) is not { } e) return 0;
            var change = record != null ? SwapScriptChange.Measure(o.Script, text, la, lb) : null;
            if (!o.Editor.ApplyEdit(e.StartLine, e.StartColumn, e.EndLine, e.EndColumn, e.Text)) return 0;
            if (change != null) record!.AddScript(change);
            return n;
        }

        // ---------------------------------------------------------------- swap: one undo for TAE/HKX + Lua

        /// <summary>A recorded swap with its DSAS global undo action and the project it was made in.</summary>
        private sealed class SwapEntry(AnimSwapRecord record, object? project)
        {
            public AnimSwapRecord Record { get; } = record;
            public object? Project { get; } = project;
            public AnimSlotsBridge.SwapHandle? Handle;
        }

        private readonly AnimSwapHistory _history = new(capacity: 10);
        private readonly List<SwapEntry> _entries = new();

        /// <summary>
        /// DSAS undid / redid the swap's global action (Ctrl+Shift+Z/Y, Edit menu, or "Undo last swap"): the Lua part
        /// follows. A swap is its own inverse, so both apply the same id swap again to each script's current text.
        /// </summary>
        private void OnSwapUndoRedo(SwapEntry entry, bool undo)
        {
            var rec = entry.Record;
            rec.IsUndone = undo;
            _snap = null;
            _planKey = null;
            string verb = undo ? "Undid" : "Redid";
            if (rec.Scripts.Count == 0) { _app.Notify($"{verb} swap {rec.Description}."); return; }
            if (_app.Project == null || !ReferenceEquals(_app.Project, entry.Project))
            {
                _app.ReportError($"{verb} swap {rec.Description}", new InvalidOperationException(
                    "The anibnd part was " + (undo ? "undone" : "redone") + ", but the project with its scripts is no longer open, so these Lua ids were not changed: " +
                    string.Join(", ", rec.Scripts.Select(c => $"{c.Script.FileName} ({c.LuaA} <-> {c.LuaB})"))));
                return;
            }
            var notes = new List<string>();
            foreach (var c in rec.Scripts)
            {
                bool wasOpen = _app.OpenScripts.Any(o => o.Script.Key == c.Key && o.Doc != null && o.Loading == null);
                // Open: runs now. Closed meanwhile: reopened as a tab (unsaved change), runs once it has loaded.
                _app.OpenScriptWith(c.Script, o =>
                {
                    string note = $"{o.Script.FileName}: {ApplyRecorded(o, c, undo)}";
                    if (wasOpen) notes.Add(note);
                    else _app.Notify($"{verb} swap {rec.Description}: {o.Script.FileName} had been closed, reopened it. {note}");
                }, activate: false);
            }
            _app.Notify($"{verb} swap {rec.Description} in the anibnd" + (notes.Count > 0 ? " and Lua: " + string.Join("; ", notes) : "") + ". Not saved yet.");
        }

        private static string ApplyRecorded(OpenScript o, SwapScriptChange c, bool undo)
        {
            var action = c.Decide(o.Editor.Text, undo);
            if (action == SwapLuaAction.Skip) return $"{c.LuaA} / {c.LuaB} already as wanted, left alone";
            int n = SwapIn(o, c.LuaA, c.LuaB, null);
            string done = $"swapped {n} id{(n == 1 ? "" : "s")} ({c.LuaA} <-> {c.LuaB})";
            return action == SwapLuaAction.ApplyUnsure ? done + " (these ids were edited since the swap: check it)" : done;
        }

        private SwapEntry? LastApplied() => _history.LastApplied is { } r ? _entries.FirstOrDefault(e => e.Record == r) : null;

        /// <summary>Why "Undo last swap" can't run now, or null.</summary>
        public string? WhyCantUndoLastSwap()
        {
            if (LastApplied() is not { Handle: { } h } e) return "No swap to undo.";
            if (e.Record.Pending > 0) return "Scripts of this swap are still opening.";
            return AnimSlotsBridge.WhyCantUndo(h);
        }

        /// <summary>Undoes the last swap (anibnd + its Lua scripts) as one step, without DSAS's confirmation dialog.</summary>
        public void UndoLastSwap()
        {
            if (LastApplied() is not { Handle: { } h } e) { _app.Notify("No animation swap to undo."); return; }
            if (WhyCantUndoLastSwap() is { } why) { _app.Notify($"Can't undo swap {e.Record.Description}: {why}"); return; }
            try { AnimSlotsBridge.Undo(h); } // its undo callback reverts the Lua part next frame
            catch (Exception ex) { _app.ReportError("Undo swap", ex); }
        }

        private void DrawSwapHistory()
        {
            string? why = WhyCantUndoLastSwap();
            var last = _history.LastApplied;
            ImGui.BeginDisabled(why != null);
            if (ImGui.Button(last != null ? $"Undo last swap ({last.Description})" : "Undo last swap")) UndoLastSwap();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip((why ?? "Undo the swap in the anibnd and in every script it changed, as one step.\n" +
                                  "Edit > Undo [Global] Action (Ctrl+Shift+Z) does the same; Redo [Global] re-applies both.").Replace("%", "%%"));
            if (_history.Recent.Count == 0) { ImGui.Separator(); return; }
            if (!ImGui.TreeNode($"Recent swaps ({_history.Recent.Count})##sas_swaphist")) { ImGui.Separator(); return; }
            foreach (var r in _history.Recent)
            {
                string scripts = r.Scripts.Count == 0 ? "no Lua" : string.Join(", ", r.Scripts.Select(c => $"{c.Script.FileName} ({c.LuaA} <-> {c.LuaB})"));
                if (r.Pending > 0) scripts += $" (+{r.Pending} opening)";
                string line = $"{r.Time:HH:mm:ss}  {r.Description}  -  {scripts}";
                if (r.IsUndone) ImGui.TextDisabled(line + "  [undone]");
                else ImGui.TextUnformatted(line);
            }
            ImGui.TreePop();
            ImGui.Separator();
        }

        // ---------------------------------------------------------------- add

        private void DrawAdd()
        {
            ImGui.TextDisabled("New animation ID");
            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##add_id", "a000_003100", ref _newId, 32);
            int? id = ParseId(_newId);
            string? taken = id is int i ? AnimSlots.WhyIdTaken(_snap!.Entries, _snap.HkxIds, i) : "Enter an id (3100 or a000_003100).";
            ImGui.SameLine();
            if (ImGui.SmallButton("Next free") && (id ?? _snap!.SelectedId) is int from && from >= 0)
                _newId = AnimSlots.IdName(AnimSlots.NextFreeId(_snap!.Entries, _snap.HkxIds, from));
            Tip("First free id after this one (or after the selected animation) in the same category");
            if (taken != null) ImGui.TextColored(Theme.Warn, taken);

            if (!_labelEdited && id is int li) _label = AnimSlots.IdName(li) + ".hkt";
            ImGui.TextDisabled("Name (label in the list)");
            ImGui.SetNextItemWidth(300);
            if (ImGui.InputText("##add_label", ref _label, 64)) _labelEdited = true;

            ImGui.Spacing();
            ImGui.TextDisabled("Motion");
            ImGui.RadioButton("Use the motion of an existing animation (Override HKX)", ref _source, 0);
            if (_source == 0)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(150);
                ImGui.InputTextWithHint("##add_from", "a000_003001", ref _overrideFrom, 32);
                ImGui.SameLine();
                if (ImGui.SmallButton("Selected##from") && _snap!.SelectedId >= 0) _overrideFrom = AnimSlots.IdName(_snap.SelectedId);
                if (ParseId(_overrideFrom) is int f) ImGui.TextDisabled(Describe(f));
                ImGui.Unindent();
            }
            ImGui.RadioButton("A new .hkx file (already retargeted to this character's skeleton)", ref _source, 1);
            if (_source == 1)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(-90);
                ImGui.InputText("##add_path", ref _hkxPath, 400, ImGuiInputTextFlags.ReadOnly);
                ImGui.SameLine();
                if (ImGui.Button("Browse...")) BrowseHkx();
                if (_hkxInfo != null) ImGui.TextDisabled(_hkxInfo);
                ImGui.PushTextWrapPos(0);
                foreach (var p in _hkxProblems) ImGui.TextColored(Theme.Warn, p.Replace("%", "%%"));
                ImGui.PopTextWrapPos();
                ImGui.Unindent();
            }
            ImGui.Spacing();
            ImGui.TextDisabled("Events: none (an empty animation). Copy events in with the graph if you need them.");

            bool sourceOk = _source == 0 ? ParseId(_overrideFrom) is int src && _snap!.Entries.Any(e => e.Id == src) || ParseId(_overrideFrom) is int s2 && _snap!.HkxIds.Contains(s2)
                                         : _hkxBytes != null;
            // Wrong file type / unreadable / bones beyond this skeleton: it would break the character, so it can't be added.
            bool blocking = _source == 1 && _hkxProblems.Any(p => p.StartsWith("Not a Havok") || p.StartsWith("Could not read") || p.Contains("no animation") || p.Contains("skeleton has"));
            ImGui.BeginDisabled(taken != null || !sourceOk || blocking);
            if (ImGui.Button(id is int bid ? $"Add {AnimSlots.IdName(bid)}" : "Add")) DoAdd(id!.Value);
            ImGui.EndDisabled();
            if (blocking) ImGui.TextColored(Theme.Error, "This file can't be added to this character (see above).");
            else if (_source == 1 && _hkxProblems.Count > 0)
                ImGui.TextColored(Theme.Warn, "Warnings above: it can still be added, but check it plays correctly before saving.");
        }

        private void BrowseHkx()
        {
            DSAnimStudio.Main.MainThreadLazyDispatch(() =>
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog { Title = "Animation .hkx for this character", Filter = "Havok animation (*.hkx)|*.hkx|All files (*.*)|*.*" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                LoadHkx(dlg.FileName);
            });
        }

        private void LoadHkx(string path)
        {
            _hkxPath = path;
            _hkxBytes = null;
            _hkxProblems = new List<string>();
            _hkxInfo = null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var check = AnimSlots.CheckHkx(bytes, _snap?.CompendiumIds ?? new HashSet<string>());
                _hkxProblems.AddRange(check.Problems);
                if (check.IsSekiroTagfile) _hkxProblems.AddRange(AnimSlotsBridge.CheckAgainstSkeleton(bytes, _snap?.Compendium));
                _hkxBytes = bytes;
                _hkxInfo = $"{bytes.Length / 1024.0:0.#} KB" + (_hkxProblems.Count == 0 ? ", matches this character's skeleton" : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _hkxProblems.Add("Could not read the file: " + ex.Message); }
        }

        private void DoAdd(int id)
        {
            try
            {
                int? from = _source == 0 ? ParseId(_overrideFrom) : null;
                AnimSlotsBridge.ApplyAdd(id, string.IsNullOrWhiteSpace(_label) ? AnimSlots.IdName(id) + ".hkt" : _label.Trim(), from, _source == 1 ? _hkxBytes : null);
                _app.Notify($"Added {AnimSlots.IdName(id)}. Save with Ctrl+S.");
                _snap = AnimSlotsBridge.Read();
                _newId = AnimSlots.IdName(AnimSlots.NextFreeId(_snap!.Entries, _snap.HkxIds, id));
                _labelEdited = false;
            }
            catch (Exception ex) { _app.ReportError("Add animation", ex); }
        }
    }
}
