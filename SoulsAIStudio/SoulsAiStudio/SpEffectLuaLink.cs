#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Parameters panel, next to the SpEffect id of a TAE SpEffect event: a "Lua" button whose popup lists the loaded
    /// character's AI scripts (in the project) and, for each, whether <c>Goal.Interrupt</c> handles that SpEffect.
    /// Handled: jump to its branch. Not handled: add the branch (and the observe in <c>Goal.Activate</c>) on
    /// <c>TARGET_SELF</c> or <c>TARGET_ENE_0</c>, like the other SpEffects of the script (<see cref="SpEffectHandlerWriter"/>).
    /// </summary>
    public sealed class SpEffectLuaLink
    {
        private static readonly int[] SpEffectEventTypes = { 67, 66, 302, 401, 331, 797 };
        public const string AskWindowName = "Add SpEffect handler##sas_sp_ask";
        public const string ResultWindowName = "SpEffect in Lua##sas_sp_win";
        public bool AskOpen => _askOpen;
        public bool ResultOpen => _winOpen && _popupSpEffect > 0;

        private sealed record Row(ProjectScript Script, int HandlerLine, IReadOnlyList<string> Observed, string? Error);

        private readonly SasApp _app;
        private int _popupSpEffect;
        private Task<List<Row>>? _task;
        private List<Row>? _rows;

        public SpEffectLuaLink(SasApp app) => _app = app;

        /// <summary>True for the SpEffect id field of a TAE SpEffect event (first parameter, read by offset 0).</summary>
        public static bool IsSpEffectField(int eventType, int paramIndex) =>
            paramIndex == 0 && Array.IndexOf(SpEffectEventTypes, eventType) >= 0;

        /// <summary>Draws the button after the field (same line) and its popup.</summary>
        public void Draw(int spEffectId)
        {
            if (_app.Session == null || spEffectId <= 0) return;
            ImGui.SameLine();
            if (ImGui.SmallButton($"Lua##sas_sp_lua_{spEffectId}")) Request(spEffectId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"SpEffect {_app.SpEffectText(spEffectId).Replace("%", "%%")} in the AI scripts of this character:\n" +
                                 "jump to its branch in Goal.Interrupt, or add one (TARGET_SELF / TARGET_ENE_0).");
        }

        /// <summary>Reads (off the UI thread) every project script of the loaded character and looks the SpEffect up.</summary>
        private void Refresh()
        {
            var session = _app.Session;
            if (session == null) return;
            int id = _popupSpEffect;
            var scripts = session.Project.Scripts.Where(s => _app.ResolveCharacter(s) is { } f && _app.IsCharacterLoaded(f)).ToList();
            var live = _app.OpenScripts.Where(o => o.Doc != null && o.Loading == null)
                .ToDictionary(o => o.Script.Key, o => o.Editor.Text, StringComparer.OrdinalIgnoreCase);
            _rows = null;
            _task = Task.Run(async () =>
            {
                var rows = new List<Row>();
                foreach (var s in scripts)
                {
                    try
                    {
                        string text = live.TryGetValue(s.Key, out var t) ? t : (await session.OpenAsync(s)).SavedText;
                        var table = SpEffectInterrupts.Build(text);
                        rows.Add(new Row(s, table.Handlers.TryGetValue(id, out var h) ? h.Line : -1,
                            table.ObservedTargets.TryGetValue(id, out var obs) ? obs : Array.Empty<string>(), null));
                    }
                    catch (Exception ex) { rows.Add(new Row(s, -1, Array.Empty<string>(), ex.GetBaseException().Message)); }
                }
                return rows;
            });
        }

        private void DrawPopup(int id)
        {
            if (_task is { IsCompleted: true } t)
            {
                _rows = t.IsCompletedSuccessfully ? t.Result : new List<Row>();
                _task = null;
            }
            ImGui.TextUnformatted($"SpEffect {_app.SpEffectText(id)}");
            ImGui.Separator();
            if (_rows == null) { ImGui.TextDisabled("Reading the scripts..."); return; }
            if (_rows.Count == 0)
            {
                ImGui.TextDisabled("No AI script of the loaded character in this project.");
                ImGui.TextDisabled("Add its battle script in the Project panel (characters are matched by their anibnd).");
                return;
            }
            bool readOnly = _app.Project?.IsReadOnly == true;
            foreach (var r in _rows)
            {
                ImGui.PushID(r.Script.Key);
                ImGui.TextUnformatted($"{r.Script.FileName}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({r.Script.Luabnd})");
                ImGui.Indent();
                if (r.Error != null) ImGui.TextColored(Theme.Warn, "Could not read: " + r.Error);
                else if (r.HandlerLine >= 0)
                {
                    if (ImGui.Selectable($"Go to Goal.Interrupt branch (line {r.HandlerLine + 1})"))
                    {
                        Open(r.Script, o => o.Editor.GoToLine(r.HandlerLine));
                        _winOpen = false;
                    }
                    if (r.Observed.Count > 0) ImGui.TextDisabled("Observed on " + string.Join(", ", r.Observed));
                    else
                    {
                        ImGui.TextColored(Theme.Warn, "Not observed: the branch never runs.");
                        AddButtons(r, id, readOnly, "Observe");
                    }
                }
                else
                {
                    ImGui.TextDisabled(r.Observed.Count > 0 ? $"Observed on {string.Join(", ", r.Observed)} but Goal.Interrupt has no branch for it."
                                                            : "Not handled in Goal.Interrupt.");
                    AddButtons(r, id, readOnly, "Add");
                }
                ImGui.Unindent();
                ImGui.PopID();
                ImGui.Spacing();
            }
        }

        private void AddButtons(Row r, int id, bool readOnly, string verb)
        {
            ImGui.BeginDisabled(readOnly);
            if (ImGui.Button($"{verb}: TARGET_SELF")) { Add(r.Script, id, SpEffectHandlerWriter.TargetSelf); _winOpen = false; }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(readOnly ? "Read-only project (no mod folder)." : "The SpEffect is put on the enemy itself (e.g. a TAE SpEffect event of its own anim).");
            ImGui.SameLine();
            if (ImGui.Button($"{verb}: TARGET_ENE_0")) { Add(r.Script, id, SpEffectHandlerWriter.TargetEnemy); _winOpen = false; }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(readOnly ? "Read-only project (no mod folder)." : "The SpEffect is put on its target, the player (e.g. by an attack that hits).");
            ImGui.EndDisabled();
        }

        private void Open(ProjectScript script, Action<OpenScript> then)
        {
            _app.OpenScriptWith(script, then, activate: true);
            _app.Layout.FocusEditorTabFrames = 10; // bring the editor tab in front of Graph when they share a node
        }

        /// <summary>
        /// Adds the observe + Goal.Interrupt branch in the script's editor tab (unsaved, one undo step) and puts the caret on
        /// the empty line of the new branch, ready for its AddSubGoal lines.
        /// </summary>
        public void AddTo(ProjectScript script, int id, string target) => Add(script, id, target);

        private int _pending;

        /// <summary>Opens the SpEffect's popup (scripts of the loaded character) from elsewhere, e.g. the Combo panel's menus.</summary>
        public void Request(int spEffectId) => _pending = spEffectId;

        private bool _askOpen, _askFocus, _askNew;
        private string _askText = "";

        /// <summary>Menu entry: asks for any SpEffect id (not tied to a TAE event), then shows the same window as the "Lua" button.</summary>
        public void Ask() { _askOpen = true; _askFocus = true; _askNew = true; _askText = ""; } // fresh field each time (no appending to the last id)

        // Plain windows, not popups: popups can't be dragged, and these sit on top of the viewport.
        private void PlaceNewWindow(ref bool isNew)
        {
            if (!isNew) return;
            isNew = false;
            ImGui.SetNextWindowPos(ImGui.GetMousePos() + new System.Numerics.Vector2(12, 12), ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
        }

        private void DrawAsk()
        {
            if (!_askOpen) return;
            PlaceNewWindow(ref _askNew);
            bool open = true;
            bool shown = Theme.BeginOpaque(AskWindowName, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking); // transient: dragging it must not dock it into the layout
            if (!open) _askOpen = false;
            if (!shown) { ImGui.End(); return; }
            try
            {
                ImGui.TextUnformatted("Add a SpEffect handler to the AI scripts");
                ImGui.TextDisabled("Adds `== id` in Goal.Interrupt and the AddObserveSpecialEffectAttribute in Goal.Activate.");
                if (_askFocus) { ImGui.SetKeyboardFocusHere(); _askFocus = false; }
                ImGui.SetNextItemWidth(160);
                bool enter = ImGui.InputTextWithHint("##sp_ask_id", "SpEffect id, e.g. 5060040", ref _askText, 16,
                    ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
                bool ok = int.TryParse(_askText.Trim(), out int id) && id > 0;
                if (ok) ImGui.TextDisabled(_app.SpEffectDisplayName(id) is { } n ? n : "(no name in SpEffectParam)");
                else ImGui.TextDisabled(" ");
                ImGui.BeginDisabled(!ok);
                if (ImGui.Button("Next") || (enter && ok)) { Request(id); _askOpen = false; }
                ImGui.EndDisabled();
            }
            finally { ImGui.End(); }
        }

        private bool _winOpen, _winNew;

        private void DrawWindow()
        {
            if (!_winOpen || _popupSpEffect <= 0) return;
            PlaceNewWindow(ref _winNew);
            bool open = true;
            bool shown = Theme.BeginOpaque(ResultWindowName, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking); // transient: dragging it must not dock it into the layout
            if (!open) _winOpen = false;
            if (!shown) { ImGui.End(); return; }
            try { DrawPopup(_popupSpEffect); }
            finally { ImGui.End(); }
        }

        /// <summary>Every frame at the top level (SasApp): shows the popup requested with <see cref="Request"/>.</summary>
        public void DrawRequested()
        {
            DrawAsk();
            if (_pending > 0)
            {
                _popupSpEffect = _pending;
                _pending = 0;
                Refresh();
                _winOpen = true;
                _winNew = true;
            }
            DrawWindow();
        }

        private void Add(ProjectScript script, int id, string target) => Open(script, o =>
        {
            string old = o.Editor.Text;
            var edit = SpEffectHandlerWriter.Add(old, id, target);
            if (edit.Error != null)
            {
                DSAnimStudio.ImguiOSD.DialogManager.DialogOK("SpEffect not added", edit.Error);
                return;
            }
            if (edit.Changed && !ApplyChangedLines(o, old, edit.Text))
            {
                _app.Notify("The script is read-only here (open a project with a mod folder to edit).");
                return;
            }
            if (edit.CaretLine >= 0) o.Editor.SelectRange(edit.CaretLine, edit.CaretColumn, edit.CaretColumn);
            _app.Notify(edit.AddedHandler
                ? $"SpEffect {id}: branch added in Goal.Interrupt" + (edit.AddedObserve ? $" and observed on {target}" : "") + " (unsaved)"
                : edit.AddedObserve ? $"SpEffect {id}: now observed on {target} (unsaved)" : $"SpEffect {id} is already handled");
        });

        /// <summary>Replaces only the lines between the first and the last changed line, as one undo step.</summary>
        internal static bool ApplyChangedLines(OpenScript o, string oldText, string newText)
        {
            var a = oldText.Split('\n');
            var b = newText.Split('\n');
            int first = 0;
            while (first < a.Length && first < b.Length && a[first] == b[first]) first++;
            int ea = a.Length - 1, eb = b.Length - 1;
            while (ea >= first && eb >= first && a[ea] == b[eb]) { ea--; eb--; }
            // Old lines [first, ea] become new lines [first, eb]: replace from (first, 0) to (ea + 1, 0).
            string text = string.Join("\n", b.Skip(first).Take(eb - first + 1)) + "\n";
            if (ea + 1 >= a.Length) // change runs to the end of the text: no line after it to anchor on
                return o.Editor.ApplyEdit(first, 0, a.Length - 1, a[^1].Length, string.Join("\n", b.Skip(first)));
            return o.Editor.ApplyEdit(first, 0, ea + 1, 0, text);
        }
    }
}
