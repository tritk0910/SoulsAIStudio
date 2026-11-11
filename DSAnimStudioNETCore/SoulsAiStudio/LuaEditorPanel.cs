#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Editor;

namespace SoulsAiStudio.App
{
    /// <summary>A script open in the editor, with its analysis snapshot.</summary>
    public sealed class OpenScript
    {
        public OpenScript(ProjectScript script, DsasKeyInput keys)
        {
            Script = script;
            Editor = new LuaCodeEditor { KeyInput = keys };
            Editor.TextChanged += () => LastEdit = DateTime.UtcNow;
        }

        public ProjectScript Script { get; }
        public LuaCodeEditor Editor { get; }
        public Task<ScriptDocument>? Loading;
        public ScriptDocument? Doc;
        public string? Error;
        public bool IsDirty => Doc != null && Editor.IsDirty;

        // Analysis snapshot (built off the UI thread, debounced).
        public IReadOnlyList<GoalFunction> Outline = Array.Empty<GoalFunction>();
        public ILookup<int, AnimReference> AnimsByLine = Array.Empty<AnimReference>().ToLookup(a => a.Line);
        public Task<(IReadOnlyList<GoalFunction> Outline, ILookup<int, AnimReference> Anims)>? OutlineTask;
        public long OutlineVersion = -1;
        public DateTime LastEdit = DateTime.MinValue;

        public Task<SaveResult>? Saving;
        public long SavingVersion;
        public string SavingText = "";

        public int CursorLine => Editor.Cursor.Line;

        public GoalFunction? GoalAtCursor
        {
            get
            {
                int line = CursorLine;
                return Outline.LastOrDefault(g => g.Line <= line && line <= g.EndLine);
            }
        }
    }

    /// <summary>Lua editor panel (SoulsAiStudio.Editor widget) with the goal navigator breadcrumb (CLAUDE.md §5b).</summary>
    public sealed class LuaEditorPanel
    {
        private static readonly TimeSpan AnalysisDebounce = TimeSpan.FromMilliseconds(250);
        private readonly SasApp _app;
        private readonly List<OpenScript> _open = new();
        private readonly DsasKeyInput _keys = new();
        private OpenScript? _active;
        private OpenScript? _selectTab; // force-select next frame (ImGui keeps its own tab selection otherwise)
        private string _navFilter = "";
        public bool IsOpen = true;

        /// <summary>Dock node of this window when its tab was visible this frame, else 0.</summary>
        public uint VisibleInDockId { get; private set; }

        public LuaEditorPanel(SasApp app) => _app = app;

        public ProjectScript? ActiveScript => _active?.Script;
        public OpenScript? Active => _active;
        public bool AnyDirty => _open.Any(o => o.IsDirty);
        public bool ActiveIsDirty => _active?.IsDirty == true;
        public IEnumerable<string> DirtyScriptNames => _open.Where(o => o.IsDirty).Select(o => o.Script.FileName);

        public void Open(ProjectScript script)
        {
            var existing = _open.FirstOrDefault(o => o.Script.Key == script.Key);
            if (existing == null)
            {
                existing = new OpenScript(script, _keys) { Loading = _app.Session!.OpenAsync(script) };
                _open.Add(existing);
            }
            _active = _selectTab = existing;
            IsOpen = true;
        }

        public void JumpTo(int line) => _active?.Editor.GoToLine(line);

        public void CloseAll()
        {
            _open.Clear();
            _active = null;
        }

        public void SaveActive()
        {
            var o = _active;
            if (o?.Doc == null || o.Saving != null || !o.IsDirty) return;
            o.SavingVersion = o.Editor.Version;
            o.SavingText = o.Editor.Text;
            o.Saving = _app.Session!.SaveAsync(o.Doc, o.SavingText);
        }

        /// <returns>True when the panel (or the editor inside it) has keyboard focus.</returns>
        public bool Build()
        {
            _keys.NewFrame();
            VisibleInDockId = 0;
            foreach (var o in _open) PollTasks(o);
            if (!IsOpen) return false;

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(340, 60), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(vp.WorkSize.X * 0.55f, vp.WorkSize.Y - 120), ImGuiCond.FirstUseEver);
            bool focused = false;
            bool visible = ImGui.Begin("Lua Editor##sas_editor", ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (visible)
                {
                    VisibleInDockId = ImGui.IsWindowDocked() ? ImGui.GetWindowDockID() : 0;
                    focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                    if (focused && _keys.CtrlS) SaveActive();
                    BuildTabs();
                    if (_active != null) focused |= BuildActive(_active);
                    else ImGui.TextDisabled("Open a script from the Scripts panel.");
                }
            }
            finally { ImGui.End(); } // keep ImGui's stack balanced even if a panel throws
            return focused;
        }

        private void BuildTabs()
        {
            if (!ImGui.BeginTabBar("##sas_tabs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll)) return;
            OpenScript? close = null;
            try
            {
            foreach (var o in _open)
            {
                bool keep = true;
                var flags = o.IsDirty ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None;
                if (o == _selectTab) flags |= ImGuiTabItemFlags.SetSelected;
                if (ImGui.BeginTabItem($"{o.Script.FileName}##{o.Script.Key}", ref keep, flags))
                {
                    _active = o;
                    ImGui.EndTabItem();
                }
                if (!keep)
                {
                    if (o.IsDirty) _app.Notify($"{o.Script.FileName} has unsaved changes — save or undo them first.");
                    else close = o;
                }
            }
            }
            finally { ImGui.EndTabBar(); }
            _selectTab = null;
            if (close != null)
            {
                _open.Remove(close);
                if (_active == close) _active = _open.FirstOrDefault();
            }
        }

        private bool BuildActive(OpenScript o)
        {
            if (o.Error != null) { ImGui.TextColored(Theme.Error, o.Error); return false; }
            if (o.Doc == null) { ImGui.TextDisabled(o.Loading != null ? "Loading (decompiling if needed)..." : "Not loaded."); return false; }

            BuildBreadcrumb(o);
            BuildStatusLine(o);
            o.Editor.SetHighlightLine(o.GoalAtCursor?.Line);
            return o.Editor.Render("##sas_code", new Vector2(-1, -1));
        }

        private void BuildBreadcrumb(OpenScript o)
        {
            var goal = o.GoalAtCursor;
            ImGui.TextDisabled($"{o.Script.Luabnd} ›");
            ImGui.SameLine();
            ImGui.TextDisabled($"{o.Script.FileName} ›");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("##sas_nav", goal?.QualifiedName ?? "(top level)", ImGuiComboFlags.HeightLarge))
            {
                try
                {
                if (ImGui.IsWindowAppearing()) { _navFilter = ""; ImGui.SetKeyboardFocusHere(); }
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##sas_nav_filter", "Filter goals (e.g. keng)", ref _navFilter, 64);
                foreach (var group in o.Outline.GroupBy(g => g.Group).OrderBy(g => g.Key))
                {
                    var items = group.Where(g => _navFilter.Length == 0 || g.Name.Contains(_navFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (items.Count == 0) continue;
                    ImGui.TextColored(Theme.GroupHeader, group.Key.ToString());
                    foreach (var g in items)
                    {
                        if (ImGui.Selectable($"  {g.QualifiedName}##nav{g.Line}", g == goal)) o.Editor.GoToLine(g.Line);
                        ImGui.SameLine(240);
                        ImGui.TextDisabled($"function ({g.Parameters})");
                    }
                }
                }
                finally { ImGui.EndCombo(); }
            }
            ImGui.SameLine();
            var (line, col) = o.Editor.Cursor;
            foreach (var a in o.AnimsByLine[line])
            {
                if (ImGui.SmallButton($"▶ {a.AnimId}##play{a.AnimId}")) _app.PlayAnim(a.AnimId);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{a.GoalType}: show anim a000_{a.AnimId:D6} in the timeline/viewport");
                ImGui.SameLine();
            }
            ImGui.TextDisabled($"Ln {line + 1}, Col {col + 1}   Ctrl+F find · Ctrl+G go to line · Ctrl+S save");
        }

        private static void BuildStatusLine(OpenScript o)
        {
            if (o.Saving != null) ImGui.TextColored(Theme.Warn, "Saving...");
            else if (o.IsDirty) ImGui.TextColored(Theme.Warn, "● Modified — Ctrl+S to save to the mod folder");
            else ImGui.TextColored(Theme.Ok, "Saved");
            ImGui.SameLine();
            ImGui.TextDisabled(o.Doc!.WasBytecode ? "  (decompiled from bytecode — saving writes plain text)" : "");
        }

        private void PollTasks(OpenScript o)
        {
            if (o.Loading is { IsCompleted: true } load)
            {
                o.Loading = null;
                if (load.IsCompletedSuccessfully)
                {
                    o.Doc = load.Result;
                    o.Editor.Text = o.Doc.SavedText; // resets undo, marks clean
                    o.LastEdit = DateTime.UtcNow - AnalysisDebounce; // analyze immediately
                }
                else o.Error = "Could not open: " + load.Exception?.GetBaseException().Message;
            }

            if (o.Saving is { IsCompleted: true } save)
            {
                o.Saving = null;
                if (save.IsCompletedSuccessfully)
                {
                    o.Doc?.MarkSaved(o.SavingText);
                    // Only clean if nothing was typed while saving.
                    if (o.Editor.Version == o.SavingVersion) o.Editor.MarkClean();
                    if (save.Result.Warning != null) _app.ReportError($"{o.Script.FileName}: partial save", new Exception(save.Result.Warning));
                    else _app.Notify($"Saved {o.Script.FileName} → {save.Result.WrittenFiles.Count} file(s) in the mod folder (backup taken).");
                }
                else _app.ReportError($"Saving {o.Script.FileName} failed", save.Exception?.GetBaseException());
            }

            if (o.OutlineTask is { IsCompleted: true } ot)
            {
                o.OutlineTask = null;
                if (ot.IsCompletedSuccessfully) { o.Outline = ot.Result.Outline; o.AnimsByLine = ot.Result.Anims; }
            }
            else if (o.OutlineTask == null && o.Doc != null && o.OutlineVersion != o.Editor.Version
                     && DateTime.UtcNow - o.LastEdit >= AnalysisDebounce)
            {
                o.OutlineVersion = o.Editor.Version;
                string snapshot = o.Editor.Text;
                o.OutlineTask = Task.Run(() =>
                {
                    var tokens = SoulsAiStudio.Core.Lua.Syntax.LuaLexer.Tokenize(snapshot);
                    return (GoalOutline.Build(snapshot, tokens), AnimReferences.Build(snapshot, tokens).ToLookup(a => a.Line));
                });
            }
        }
    }
}
