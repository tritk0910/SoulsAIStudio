#nullable enable
using System;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// In-game Lua hot reload (CLAUDE.md §12): status-line button + popup that puts / removes the <see cref="HotReloadStub"/>
    /// block in <c>Goal.Parry</c> and <c>Goal.ShootReaction</c>. While the block is in the script, every Save also writes the plain .lua copy and a new
    /// version file into <c>&lt;mod&gt;\script\sas_hotreload\</c> (ProjectSession), which the block reloads in the fight.
    /// </summary>
    public sealed partial class LuaEditorPanel
    {
        private const string HotReloadPopup = "sas_hot_reload";

        private void BuildHotReloadButton(OpenScript o)
        {
            ImGui.SameLine();
            bool on = o.HotReload;
            if (on) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Ok);
            bool click = ImGui.SmallButton(on ? "Hot Reload: ON##sas_hot"
                                              : "Hot Reload##sas_hot");
            if (on) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(on ? "This script reloads itself in a running fight after each Save.\nRemove it before sharing the mod (it holds paths of this PC)."
                                    : "Reload this script in a running fight after each Save,\nwithout reloading the area (adds a block to Goal.Parry and Goal.ShootReaction).");
            if (click) ImGui.OpenPopup(HotReloadPopup);
            if (ImGui.BeginPopup(HotReloadPopup))
            {
                try { BuildHotReloadPopup(o); }
                finally { ImGui.EndPopup(); }
            }
        }

        private void BuildHotReloadPopup(OpenScript o)
        {
            var session = _app.Session;
            ImGui.TextUnformatted($"In-game hot reload: {o.Script.FileName}");
            ImGui.Separator();
            if (session == null || _app.Project?.IsReadOnly != false)
            {
                ImGui.TextDisabled("Needs a project with a mod folder.");
                return;
            }
            var (copy, _) = session.HotReloadPaths(o.Script);
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34);
            ImGui.TextUnformatted("A block at the top of Goal.Parry and Goal.ShootReaction reloads this script with loadfile from a copy the app writes " +
                                  "at each Save, so your changes reach a running fight without reloading the area.");
            ImGui.Spacing();
            ImGui.TextDisabled("1. Add the block, then Save (Ctrl+S).");
            ImGui.TextDisabled("2. Once: rest at an idol / reload the area, so the game loads the luabnd that has the block.");
            ImGui.TextDisabled("3. From then on: edit, Save, then make the enemy parry or react to a shot (attack it, throw a shuriken): " +
                               "the new script is loaded once per Save.");
            ImGui.Spacing();
            ImGui.TextDisabled("Copy: " + copy.Replace("%", "%%"));
            ImGui.PopTextWrapPos();
            ImGui.Separator();

            if (!o.HotReload)
            {
                if (ImGui.Button("Add to Goal.Parry + ShootReaction")) { ApplyHotReload(o, add: true); ImGui.CloseCurrentPopup(); }
            }
            else
            {
                if (ImGui.Button("Remove")) { ApplyHotReload(o, add: false); ImGui.CloseCurrentPopup(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Takes the block out; the next Save also deletes its copy in the mod.");
                ImGui.SameLine();
                if (ImGui.Button("Go to block")) { GoToHotReload(o); ImGui.CloseCurrentPopup(); }
                ImGui.Spacing();
                ImGui.TextColored(Theme.Warn, "Remove it before sharing the mod: it holds paths of this PC.");
            }
        }

        private void ApplyHotReload(OpenScript o, bool add)
        {
            var session = _app.Session;
            if (session == null) return;
            string old = o.Editor.Text;
            HotReloadEdit edit;
            if (add)
            {
                var (copy, version) = session.HotReloadPaths(o.Script);
                edit = HotReloadStub.Insert(old, copy, version, HotReloadStub.GlobalName(o.Script.FileName));
            }
            else edit = HotReloadStub.Remove(old);
            if (edit.Error != null)
            {
                DSAnimStudio.ImguiOSD.DialogManager.DialogOK("Hot reload", edit.Error);
                return;
            }
            if (edit.Text == old) return;
            if (!SpEffectLuaLink.ApplyChangedLines(o, old, edit.Text))
            {
                _app.Notify("The script is read-only here (open a project with a mod folder to edit).");
                return;
            }
            o.HotReload = HotReloadStub.Has(edit.Text); // now, not after the debounced analysis
            if (edit.Line >= 0) o.Editor.GoToLine(edit.Line);
            _app.Notify(add ? $"{o.Script.FileName}: hot reload block in Goal.Parry / ShootReaction (unsaved) — Save, then reload the area once."
                            : $"{o.Script.FileName}: hot reload block removed (unsaved) — Save to take it out of the mod.");
        }

        private static void GoToHotReload(OpenScript o)
        {
            var lines = o.Editor.Text.Split('\n');
            int i = Array.FindIndex(lines, l => l.TrimStart().StartsWith(HotReloadStub.BeginMarker, StringComparison.Ordinal));
            if (i >= 0) o.Editor.GoToLine(i);
        }

        /// <summary>Appended to the "Saved" notification.</summary>
        private static string HotReloadSavedHint(string savedText) =>
            HotReloadStub.Has(savedText) ? " Hot reload: loaded the next time the enemy parries / reacts to a shot." : "";
    }
}
