#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SoulsAiStudio.Core.Params;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "SpEffect Labels" window (menu Souls AI): the user's own SpEffect names (e.g. 200210 = Strong parry (right)),
    /// shown next to SpEffect ids in the Combo and Find Events windows. Filter, add, edit inline, delete; every change is
    /// saved at once (<see cref="SpEffectLabels"/>). Also hosts the small "Set label" editor opened from right-click menus.
    /// </summary>
    public sealed class SpEffectLabelsWindow
    {
        public const string WindowName = "SpEffect Labels##sas_splabels";
        private const string EditWindowName = "Set SpEffect Label##sas_splabel_edit";

        private readonly SasApp _app;
        public bool IsOpen;
        private string _filter = "";
        private string _newId = "";
        private string _newLabel = "";
        private string? _addError;
        private int _editingId = -1;
        private string _editingText = "";
        private bool _focusEdit;

        // Small "Set label" editor (right-click → Set label…).
        private int? _quickId;
        private string _quickText = "";
        private bool _quickFocus;

        public SpEffectLabelsWindow(SasApp app) => _app = app;

        private SpEffectLabels Labels => _app.SpEffectLabels;

        public void Open()
        {
            IsOpen = true;
            ImGui.SetWindowFocus(WindowName);
        }

        /// <summary>Opens the small editor for one SpEffect id (current label prefilled).</summary>
        public void EditOne(int id)
        {
            _quickId = id;
            _quickText = Labels.Get(id) ?? "";
            _quickFocus = true;
        }

        /// <summary>Right-click menu entry for the last drawn item: "Set label…" for <paramref name="id"/>.</summary>
        public void ContextMenu(string strId, int id)
        {
            if (!ImGui.BeginPopupContextItem(strId)) return;
            try { ContextMenuItems(id); }
            finally { ImGui.EndPopup(); }
        }

        /// <summary>Menu items for one SpEffect (inside an already open popup/menu).</summary>
        public void ContextMenuItems(int id)
        {
            string? current = Labels.Get(id);
            if (ImGui.MenuItem(current == null ? $"Set label for SpEffect {id}…" : $"Edit label of SpEffect {id}…")) EditOne(id);
            if (current != null && ImGui.MenuItem($"Remove label \"{current}\"")) Labels.Remove(id);
            if (ImGui.MenuItem("Open SpEffect Labels window")) Open();
        }

        /// <returns>True when one of its windows has focus.</returns>
        public bool Build()
        {
            bool focused = BuildQuickEdit();
            if (!IsOpen) return focused;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(640, 460), ImGuiCond.FirstUseEver);
            bool visible = Theme.BeginOpaque(WindowName, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return focused;
                focused |= ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                ImGui.TextDisabled("Your names for SpEffect ids, shown in the Combo and Find Events windows. Saved for every project.");
                if (Labels.LastError is { } err) ImGui.TextColored(Theme.Error, err);

                // Add row
                ImGui.SetNextItemWidth(110);
                bool enter = ImGui.InputTextWithHint("##sas_splabel_newid", "SpEffect id", ref _newId, 12, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X - 70));
                enter |= ImGui.InputTextWithHint("##sas_splabel_newlabel", "Label, e.g. Strong parry (right)", ref _newLabel, SpEffectLabels.MaxLabelLength, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                if (ImGui.Button("Add") || enter) Add();
                if (_addError != null) ImGui.TextColored(Theme.Warn, _addError);

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##sas_splabel_filter", "Filter: id, label or SpEffectParam name", ref _filter, 64);

                BuildTable();
            }
            finally { ImGui.End(); }
            return focused;
        }

        private void Add()
        {
            _addError = null;
            if (!int.TryParse(_newId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0) { _addError = "Type a SpEffect id (a number)."; return; }
            if (SpEffectLabels.Clean(_newLabel).Length == 0) { _addError = "Type a label."; return; }
            bool existed = Labels.Get(id) != null;
            Labels.Set(id, _newLabel);
            if (existed) _addError = $"SpEffect {id} already had a label: replaced.";
            _newId = _newLabel = "";
        }

        private void BuildTable()
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("##sas_splabel_table", 4, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Label (click to edit)", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("SpEffectParam name", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
                ImGui.TableHeadersRow();
                int? delete = null;
                string f = _filter.Trim();
                foreach (var (id, label) in Labels.All.ToList())
                {
                    string? paramName = _app.Bridge.SpEffectName(id);
                    if (f.Length > 0 && !id.ToString(CultureInfo.InvariantCulture).Contains(f)
                        && !label.Contains(f, StringComparison.OrdinalIgnoreCase)
                        && paramName?.Contains(f, StringComparison.OrdinalIgnoreCase) != true) continue;
                    ImGui.PushID(id);
                    try
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(id.ToString(CultureInfo.InvariantCulture));
                        ImGui.TableNextColumn();
                        if (_editingId == id)
                        {
                            ImGui.SetNextItemWidth(-1);
                            if (_focusEdit) { ImGui.SetKeyboardFocusHere(); _focusEdit = false; }
                            bool done = ImGui.InputText("##edit", ref _editingText, SpEffectLabels.MaxLabelLength,
                                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                            if (done || ImGui.IsItemDeactivated())
                            {
                                // Escape cancels; Enter or clicking away saves (an empty label keeps the old one).
                                if (!EscapeDown() && SpEffectLabels.Clean(_editingText).Length > 0)
                                    Labels.Set(id, _editingText);
                                _editingId = -1;
                            }
                        }
                        else
                        {
                            if (ImGui.Selectable(label + "##lbl", false)) { _editingId = id; _editingText = label; _focusEdit = true; }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to edit (Enter saves, Esc cancels)");
                        }
                        ImGui.TableNextColumn();
                        if (paramName != null) ImGui.TextDisabled(paramName);
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("x")) delete = id;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Delete the label of SpEffect {id}");
                    }
                    finally { ImGui.PopID(); }
                }
                if (delete is int d) { Labels.Remove(d); if (_editingId == d) _editingId = -1; }
            }
            finally { ImGui.EndTable(); }
            if (Labels.Count == 0) ImGui.TextDisabled("No labels yet: add one above, or right-click a SpEffect in the Combo / Find Events window.");
        }

        /// <summary>Esc via MonoGame (ImGui does not get every key in DSAS, CLAUDE.md §6).</summary>
        private static bool EscapeDown() =>
            Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Escape);

        /// <summary>Small floating editor for one id (a window, not a popup, so it can be opened from any panel/popup).</summary>
        private bool BuildQuickEdit()
        {
            if (_quickId is not int id) return false;
            bool open = true;
            if (_quickFocus) { ImGui.SetNextWindowPos(ImGui.GetMousePos() + new Vector2(8, 8), ImGuiCond.Always); ImGui.SetNextWindowFocus(); }
            ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Always);
            bool focused = false;
            bool visible = Theme.BeginOpaque(EditWindowName, ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                ImGui.Text($"SpEffect {id}");
                if (_app.Bridge.SpEffectName(id) is { } pn) { ImGui.SameLine(); ImGui.TextDisabled($"SpEffectParam: {pn}"); }
                ImGui.SetNextItemWidth(-1);
                if (_quickFocus) { ImGui.SetKeyboardFocusHere(); _quickFocus = false; }
                bool enter = ImGui.InputTextWithHint("##sas_splabel_quick", "Label (empty = no label)", ref _quickText, SpEffectLabels.MaxLabelLength,
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                if (ImGui.Button("Save") || enter) { Labels.Set(id, _quickText); open = false; }
                ImGui.SameLine();
                if (ImGui.Button("Cancel") || (focused && EscapeDown())) open = false;
                if (Labels.Get(id) != null)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Remove label")) { Labels.Remove(id); open = false; }
                }
                ImGui.SameLine();
                if (ImGui.Button("All labels…")) Open();
            }
            finally
            {
                ImGui.End();
                if (!open) _quickId = null;
            }
            return focused;
        }
    }
}
