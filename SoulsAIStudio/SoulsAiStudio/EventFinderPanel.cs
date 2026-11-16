#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using DSAnimStudio;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Find Events" (Ctrl+F in the graph): pick what to look for (SpEffect, attack, bullet, FFX, sound…), type an id,
    /// get every matching TAE event of the loaded character in one list; click a row to jump to it. SpEffect searches also
    /// find TAE SpEffects whose SpEffectParam cycle applies the id (e.g. 5032 ← 205032). DSAS's condition-based Find stays
    /// available through "Advanced…".
    /// </summary>
    public sealed class EventFinderPanel
    {
        public const string WindowName = "Find Events##sas_find";

        private sealed record Kind(string Name, string Hint, Func<DSAProj.Action, bool> Match, string[] KeyFields);

        private static bool NameHas(DSAProj.Action a, string s) =>
            a.Parameters?.Template?.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) == true;

        private static readonly int[] SpEffectTypes = { 67, 66, 302, 401, 331, 797 };

        private static readonly Kind[] Kinds =
        {
            new("Any event", "id or value (any field), or part of an event name", _ => true, Array.Empty<string>()),
            new("SpEffect", "SpEffect id, e.g. 5032 (also finds SpEffects whose cycle applies it), or part of your label", a => Array.IndexOf(SpEffectTypes, a.Type) >= 0, new[] { "SpEffectID", "SpEffectId" }),
            new("Attack / Behavior", "BehaviorJudgeID", a => a.Type is 1 or 5 or 304 or 307, new[] { "BehaviorJudgeID" }),
            new("Bullet", "BehaviorJudgeID or DummyPolyID", a => a.Type is 2 or 4, new[] { "BehaviorJudgeID", "DummyPolyID" }),
            new("FFX", "FFX id, e.g. 401 (SpawnOneShotFFX…)", a => NameHas(a, "FFX"), new[] { "FFXID" }),
            new("Sound", "SoundID, e.g. 710006101", a => NameHas(a, "Sound"), new[] { "SoundID" }),
            new("JumpTable (ChrActionFlag)", "flag id, e.g. 23 = ComboAttack Queued", a => a.Type == 0, Array.Empty<string>()),
            new("Blend", "(no value: lists every Blend block)", a => a.Type == 16, Array.Empty<string>()),
        };

        private sealed record Hit(DSAProj.AnimCategory Category, DSAProj.Animation Anim, DSAProj.Action Act, string AnimName,
            float StartFrame, float EndFrame, string EventName, string Matched, string Note, int? SpEffectId);

        private readonly SasApp _app;
        public bool IsOpen;
        private int _kind = 1;
        private string _query = "";
        private bool _thisAnimOnly;
        private bool _focusQuery;
        private string _searchedKey = "";
        private List<Hit> _hits = new();
        private int _animCount;
        private int _selected = -1;

        public EventFinderPanel(SasApp app) => _app = app;

        /// <summary>Opens the window; with one event selected in the graph, prefills its kind and main value.</summary>
        public void Open()
        {
            IsOpen = true;
            _focusQuery = true;
            ImGui.SetWindowFocus(WindowName);
            var screen = zzz_DocumentManager.CurrentDocument?.EditorScreen;
            if (screen?.NewSelectedActions is { Count: 1 } sel && sel[0] is { } act)
            {
                int k = Array.FindIndex(Kinds, x => x.KeyFields.Length > 0 && x.Match(act));
                if (k >= 0 && KeyValue(act, Kinds[k]) is { } v) { _kind = k; _query = v; }
            }
        }

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            if (!IsOpen) return false;
            // First open: centered in the main window (it used to appear over the Animations list).
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(820, 480), ImGuiCond.FirstUseEver);
            bool focused = false;
            bool visible = Theme.BeginOpaque(WindowName, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                var screen = zzz_DocumentManager.CurrentDocument?.EditorScreen;
                var proj = screen?.Proj;
                if (screen == null || proj == null || !screen.IsFileOpen) { ImGui.TextDisabled("Load a character first."); return focused; }

                ImGui.Text("Find");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200);
                if (ImGui.BeginCombo("##sas_find_kind", Kinds[_kind].Name))
                {
                    for (int i = 0; i < Kinds.Length; i++)
                        if (ImGui.Selectable(Kinds[i].Name, i == _kind)) _kind = i;
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X - 200));
                if (_focusQuery) { ImGui.SetKeyboardFocusHere(); _focusQuery = false; }
                ImGui.InputTextWithHint("##sas_find_query", Kinds[_kind].Hint, ref _query, 64);
                ImGui.SameLine();
                ImGui.Checkbox("This anim only", ref _thisAnimOnly);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off: every animation of the loaded character. On: only the selected animation.");

                Search(screen, proj);

                string status = _hits.Count == 0
                    ? (_query.Trim().Length == 0 && Kinds[_kind].KeyFields.Length > 0 ? "Type a value, or leave it empty to list them all." : "No match.")
                    : $"{_hits.Count} event{(_hits.Count == 1 ? "" : "s")} in {_animCount} animation{(_animCount == 1 ? "" : "s")}. Click a row to jump to it.";
                const string advanced = "Advanced (DSAS Find)…";
                float buttonW = ImGui.CalcTextSize(advanced).X + 16;
                ImGui.TextDisabled(status);
                // Button on the same line only when it fits after the text (narrow docked window: next line).
                if (ImGui.GetContentRegionAvail().X - ImGui.GetItemRectSize().X > buttonW + 24)
                    ImGui.SameLine(Math.Max(0, ImGui.GetContentRegionMax().X - buttonW));
                if (ImGui.SmallButton(advanced)) { DSAnimStudio.ImguiOSD.OSD.WindowFind.IsOpen = true; DSAnimStudio.ImguiOSD.OSD.WindowFind.IsRequestFocus = true; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("DS Anim Studio's Find window: search by conditions on categories, tracks and any field.");

                BuildTable(screen);
            }
            finally { ImGui.End(); }
            return focused;
        }

        private void BuildTable(DSAnimStudio.TaeEditor.TaeEditorScreen screen)
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("##sas_find_hits", 4, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Animation", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Frames", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Event", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();
                unsafe
                {
                    var ptr = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                    ptr.Begin(_hits.Count);
                    while (ptr.Step())
                        for (int i = ptr.DisplayStart; i < ptr.DisplayEnd; i++)
                        {
                            var h = _hits[i];
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            bool current = ReferenceEquals(screen.SelectedAnim, h.Anim);
                            if (ImGui.Selectable($"{h.AnimName}##sas_hit{i}", i == _selected, ImGuiSelectableFlags.SpanAllColumns))
                            {
                                _selected = i;
                                Jump(screen, h);
                            }
                            if (h.SpEffectId is int sp)
                            {
                                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Click: jump to it\nRight-click: set a label for SpEffect {sp}");
                                _app.SpEffectLabelContextMenu($"##sas_hit_ctx{i}", sp);
                            }
                            if (current) { ImGui.SameLine(); ImGui.TextColored(Theme.Accent, "•"); }
                            ImGui.TableNextColumn(); ImGui.TextDisabled($"{h.StartFrame:0}–{h.EndFrame:0}");
                            ImGui.TableNextColumn(); ImGui.Text(h.EventName);
                            ImGui.TableNextColumn();
                            ImGui.Text(h.Matched);
                            if (h.Note.Length > 0) { ImGui.SameLine(); ImGui.TextColored(Theme.Warn, h.Note); }
                        }
                    ptr.End();
                    ptr.Destroy();
                }
            }
            finally { ImGui.EndTable(); }
        }

        private static void Jump(DSAnimStudio.TaeEditor.TaeEditorScreen screen, Hit h)
        {
            screen.RequestHighlightAnimCategory(h.Category);
            screen.RequestHighlightAnimation(h.Anim);
            screen.RequestHighlightAction(h.Act);
            screen.SelectNewAnimRef(h.Category, h.Anim);
            screen.NewSelectedActions = new() { h.Act };
            screen.Graph?.LayoutManager?.ScrollToAction(h.Act);
        }

        /// <summary>Re-runs the search when the query, kind, scope, character or selected anim changed (cheap: ~1000 anims).</summary>
        private void Search(DSAnimStudio.TaeEditor.TaeEditorScreen screen, DSAProj proj)
        {
            string key = $"{_kind}|{_query}|{_thisAnimOnly}|{_app.SpEffectLabels.Version}|{proj.GetHashCode()}|{(_thisAnimOnly ? screen.SelectedAnim?.GetHashCode() : 0)}|{(int)(ImGui.GetTime() / 2)}";
            if (key == _searchedKey) return;
            _searchedKey = key;
            _selected = -1;
            _hits = Run(screen, proj, Kinds[_kind], _query.Trim(), _thisAnimOnly, _app.SpEffectLabels.Get);
            _animCount = _hits.Select(h => h.Anim).Distinct().Count();
        }

        private static List<Hit> Run(DSAnimStudio.TaeEditor.TaeEditorScreen screen, DSAProj proj, Kind kind, string query, bool thisAnimOnly, Func<int, string?> label)
        {
            var hits = new List<Hit>();
            bool listAll = query.Length == 0;
            if (listAll && kind.Name == "Any event") return hits;
            bool isNumber = long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number);
            var spParams = zzz_DocumentManager.CurrentDocument?.ParamManager?.SpEffectParam;
            float snap = DSAnimStudio.TaeEditor.TaeExtensionMethods.GetCurrentSnapInterval();
            if (snap <= 0) snap = 1 / 30f;

            foreach (var category in proj.SAFE_GetAllAnimCategories())
            {
                var anims = new List<DSAProj.Animation>();
                category.SafeAccessAnimations(list => anims.AddRange(list));
                foreach (var anim in anims)
                {
                    if (anim.IS_DUMMY_ANIM || (thisAnimOnly && !ReferenceEquals(anim, screen.SelectedAnim))) continue;
                    anim.SAFE_CheckActionInitialize(proj.Template);
                    string animName = anim.SplitID.GetFormattedIDString(proj);
                    foreach (var act in anim.SAFE_GetActions())
                    {
                        if (!kind.Match(act)) continue;
                        string evName = act.Parameters?.Template?.Name ?? $"Event {act.Type}";
                        string matched, note = "";
                        int? spId = SpEffectIdOf(act);
                        string? spLabel = spId is int sid ? label(sid) : null;
                        if (listAll) matched = Summary(act, kind, spParams, label);
                        else if (isNumber && FieldEquals(act, kind, number) is { } field) matched = $"{field} = {number}{SpName(act, kind, spParams, label)}";
                        else if (isNumber && kind.Name == "SpEffect" && CycleOf(act, number, spParams) is { } via)
                        {
                            matched = $"SpEffectID = {via}{(label((int)via) is { } vl ? "  " + vl : "")}";
                            note = $"applies {number}{(label((int)number) is { } nl ? " " + nl : "")} by cycle";
                        }
                        else if (!isNumber && (evName.Contains(query, StringComparison.OrdinalIgnoreCase) || act.Type.ToString(CultureInfo.InvariantCulture) == query
                                                || spLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                            matched = Summary(act, kind, spParams, label);
                        else continue;
                        hits.Add(new Hit(category, anim, act, animName, act.StartTime / snap, act.EndTime / snap, evName, matched, note, spId));
                    }
                }
            }
            return hits;
        }

        /// <summary>Name of the first field (a key field of the kind, or any field for "Any event") equal to <paramref name="value"/>.</summary>
        private static string? FieldEquals(DSAProj.Action act, Kind kind, long value)
        {
            var p = act.Parameters;
            if (p?.Template == null) return act.Type == value && kind.KeyFields.Length == 0 ? "type" : null;
            for (int i = 0; i < p.Template.Count; i++)
            {
                string name = p.Template[i].Name;
                if (kind.KeyFields.Length > 0 && !kind.KeyFields.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (AsLong(p.Values[i]) == value) return name;
            }
            // JumpTable/Blend kinds have no named key field: match any field (e.g. JumpTableID).
            if (kind.KeyFields.Length == 0 && kind.Name != "Any event")
                for (int i = 0; i < p.Template.Count; i++)
                    if (AsLong(p.Values[i]) == value) return p.Template[i].Name;
            return null;
        }

        private static long? AsLong(object? v) => v switch
        {
            sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            float f when f == Math.Floor(f) => (long)f,
            _ => null,
        };

        private static string? KeyValue(DSAProj.Action act, Kind kind)
        {
            var p = act.Parameters;
            if (p?.Template == null) return null;
            for (int i = 0; i < p.Template.Count; i++)
                if (kind.KeyFields.Any(k => string.Equals(k, p.Template[i].Name, StringComparison.OrdinalIgnoreCase)) && AsLong(p.Values[i]) is long v)
                    return v.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>The SpEffect id of a TAE SpEffect event whose cycle chain applies <paramref name="target"/>, else null.</summary>
        private static long? CycleOf(DSAProj.Action act, long target, System.Collections.Concurrent.ConcurrentDictionary<long, ParamData.SpEffectParam>? spParams)
        {
            if (spParams == null || KeyValue(act, Kinds[1]) is not string s || !long.TryParse(s, out long id)) return null;
            long cur = id;
            var seen = new HashSet<long> { id };
            for (int depth = 0; depth < 3; depth++)
            {
                if (!spParams.TryGetValue(cur, out var p) || p.SasCycleSpEffectId <= 0 || !seen.Add(p.SasCycleSpEffectId)) return null;
                if (p.SasCycleSpEffectId == target) return id;
                cur = p.SasCycleSpEffectId;
            }
            return null;
        }

        /// <summary>SpEffect id of a SpEffect-type TAE event, else null.</summary>
        private static int? SpEffectIdOf(DSAProj.Action act) =>
            Array.IndexOf(SpEffectTypes, act.Type) >= 0 && KeyValue(act, Kinds[1]) is string s && int.TryParse(s, out int id) ? id : null;

        /// <summary>"  name  → cycle …" after a SpEffect id: the user's label wins over the SpEffectParam row name.</summary>
        private static string SpName(DSAProj.Action act, Kind kind, System.Collections.Concurrent.ConcurrentDictionary<long, ParamData.SpEffectParam>? spParams, Func<int, string?> label)
        {
            if (kind.Name != "SpEffect" || SpEffectIdOf(act) is not int id) return "";
            string? user = label(id);
            if (spParams == null || !spParams.TryGetValue(id, out var p)) return user != null ? $"  {user}" : spParams == null ? "" : "  (not in SpEffectParam)";
            string name = user != null ? $"  {user}" : string.IsNullOrWhiteSpace(p.Name) ? "" : $"  {p.Name}";
            string cycle = p.SasCycleSpEffectId > 0 ? $"  → cycle {p.SasCycleSpEffectId}{(label(p.SasCycleSpEffectId) is { } cl ? " " + cl : "")}{(p.SasMotionInterval > 0 ? $" every {p.SasMotionInterval:0.##} s" : "")}" : "";
            return name + cycle;
        }

        /// <summary>Short "Field = value" text of an event (its key fields, or the first few fields).</summary>
        private static string Summary(DSAProj.Action act, Kind kind, System.Collections.Concurrent.ConcurrentDictionary<long, ParamData.SpEffectParam>? spParams, Func<int, string?> label)
        {
            var p = act.Parameters;
            if (p?.Template == null) return "";
            var parts = new List<string>();
            for (int i = 0; i < p.Template.Count && parts.Count < 3; i++)
            {
                string name = p.Template[i].Name;
                if (kind.KeyFields.Length > 0 && !kind.KeyFields.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) continue;
                parts.Add($"{name} = {p.Template[i].ValueToString(p.Values[i])}");
            }
            return string.Join(", ", parts) + SpName(act, kind, spParams, label);
        }
    }
}
