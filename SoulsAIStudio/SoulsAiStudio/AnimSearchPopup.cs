#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DSAnimStudio;
using ImGuiNET;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// Animations panel toolbar "Find": searches every animation of the loaded character by id (a000_003004, 3004), by its
    /// name in the list (e.g. "slash up") or by its animation file name (e.g. a000_021004.hkt, the HKX it plays). Clicking a
    /// result selects it (DSAS scrolls the list to it).
    /// </summary>
    public sealed class AnimSearchPopup
    {
        public const string PopupId = "sas_anim_search";
        private const int MaxResults = 200;

        private sealed record Row(DSAProj.AnimCategory Category, DSAProj.Animation Anim, string Id, string Name, string File);

        private string _query = "";
        private string _searched = "\u0001";
        private object? _searchedProj;
        private List<Row> _rows = new();
        private int _total;
        private bool _focus;

        public void Open()
        {
            _focus = true;
            _searched = "\u0001"; // anims may have changed since last time
            ImGui.OpenPopup(PopupId);
        }

        /// <summary>Draws the popup if open (call every frame from the toolbar, inside the same window).</summary>
        public void Draw(DSAnimStudio.TaeEditor.TaeEditorScreen? screen)
        {
            var proj = screen?.FileContainer?.Proj;
            ImGui.SetNextWindowSizeConstraints(new Vector2(380, 0), new Vector2(700, 520));
            if (!ImGui.BeginPopup(PopupId)) return;
            try
            {
                if (screen == null || proj == null) { ImGui.TextDisabled("No character loaded."); return; }
                if (_focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
                ImGui.SetNextItemWidth(-1);
                bool enter = ImGui.InputTextWithHint("##sas_anim_q", "Anim id, name or file (e.g. 3004, slash, a000_021004)", ref _query, 128, ImGuiInputTextFlags.EnterReturnsTrue);
                if (ImGui.IsItemActive()) DSAnimStudio.ImguiOSD.OSD.AuxFocus = Math.Max(DSAnimStudio.ImguiOSD.OSD.AuxFocus, 2); // no graph shortcuts while typing
                if (_query != _searched || !ReferenceEquals(proj, _searchedProj)) Search(proj);

                if (_query.Trim().Length == 0) { ImGui.TextDisabled("Type to search the animations of this character."); return; }
                ImGui.TextDisabled(_total == 0 ? "No animation matches." : _total > _rows.Count ? $"{_total} matches (first {_rows.Count} shown)" : $"{_total} match{(_total == 1 ? "" : "es")}");
                if (enter && _rows.Count > 0) { Select(screen, _rows[0]); ImGui.CloseCurrentPopup(); return; }

                if (_rows.Count == 0) return;
                var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
                float h = Math.Min(400, (_rows.Count + 1) * ImGui.GetTextLineHeightWithSpacing() + 8);
                if (!ImGui.BeginTable("##sas_anim_results", 2, flags, new Vector2(0, h))) return;
                try
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Animation");
                    ImGui.TableSetupColumn("Name / file", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    bool picked = false;
                    foreach (var r in _rows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        bool sel = ReferenceEquals(screen.SelectedAnim, r.Anim);
                        if (ImGui.Selectable($"{r.Id}##{r.Category.CategoryID}_{r.Id}", sel, ImGuiSelectableFlags.SpanAllColumns)) { Select(screen, r); picked = true; }
                        ImGui.TableNextColumn();
                        if (r.Name.Length > 0) { ImGui.TextUnformatted(r.Name); if (r.File.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(r.File.Replace("%", "%%")); } }
                        else ImGui.TextUnformatted(r.File);
                    }
                    if (picked) ImGui.CloseCurrentPopup();
                }
                finally { ImGui.EndTable(); }
            }
            finally { ImGui.EndPopup(); }
        }

        private void Search(DSAProj proj)
        {
            _searched = _query;
            _searchedProj = proj;
            _rows = new List<Row>();
            _total = 0;
            string q = _query.Trim();
            if (q.Length == 0) return;
            foreach (var category in proj.SAFE_GetAllAnimCategories())
            {
                var anims = new List<DSAProj.Animation>();
                category.SafeAccessAnimations(list => anims.AddRange(list));
                foreach (var anim in anims)
                {
                    if (anim.IS_DUMMY_ANIM) continue;
                    string id = anim.SplitID.GetFormattedIDString(proj);
                    string name = anim.Info.DisplayName ?? "";
                    string file = anim.SAFE_GetHeaderClone()?.AnimFileName ?? "";
                    if (!Hit(id, q) && !Hit(name, q) && !Hit(file, q)
                        && !(q.All(char.IsDigit) && Hit(anim.SplitID.SubID.ToString(), q))) continue;
                    _total++;
                    if (_rows.Count < MaxResults) _rows.Add(new Row(category, anim, id, name, file));
                }
            }
        }

        private static bool Hit(string text, string q) => text.Contains(q, StringComparison.OrdinalIgnoreCase);

        private static void Select(DSAnimStudio.TaeEditor.TaeEditorScreen screen, Row r)
        {
            screen.RequestHighlightAnimation(r.Anim);
            screen.SelectNewAnimRef(r.Category, r.Anim, scrollOnCenter: true);
        }
    }
}
