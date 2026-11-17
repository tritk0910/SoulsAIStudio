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
        private const string PopupId = "sas_sp_lua_popup";

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
            if (ImGui.SmallButton($"Lua##sas_sp_lua_{spEffectId}"))
            {
                _popupSpEffect = spEffectId;
                Refresh();
                ImGui.OpenPopup(PopupId);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"SpEffect {_app.SpEffectText(spEffectId).Replace("%", "%%")} in the AI scripts of this character:\n" +
                                 "jump to its branch in Goal.Interrupt, or add one (TARGET_SELF / TARGET_ENE_0).");
            if (_popupSpEffect == spEffectId && ImGui.BeginPopup(PopupId))
            {
                try { DrawPopup(spEffectId); }
                finally { ImGui.EndPopup(); }
            }
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
                        ImGui.CloseCurrentPopup();
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
            if (ImGui.Button($"{verb}: TARGET_SELF")) { Add(r.Script, id, SpEffectHandlerWriter.TargetSelf); ImGui.CloseCurrentPopup(); }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(readOnly ? "Read-only project (no mod folder)." : "The SpEffect is put on the enemy itself (e.g. a TAE SpEffect event of its own anim).");
            ImGui.SameLine();
            if (ImGui.Button($"{verb}: TARGET_ENE_0")) { Add(r.Script, id, SpEffectHandlerWriter.TargetEnemy); ImGui.CloseCurrentPopup(); }
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
        private static bool ApplyChangedLines(OpenScript o, string oldText, string newText)
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
