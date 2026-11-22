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
using SoulsAiStudio.Editor.Completion;

namespace SoulsAiStudio.App
{
    /// <summary>A script open in the editor, with its analysis snapshot.</summary>
    public sealed class OpenScript
    {
        public OpenScript(ProjectScript script, DsasKeyInput keys, ICompletionProvider? completions = null)
        {
            Script = script;
            Editor = new LuaCodeEditor { KeyInput = keys, CompletionProvider = completions };
            Editor.TextChanged += () => LastEdit = DateTime.UtcNow;
        }

        public ProjectScript Script { get; }
        public LuaCodeEditor Editor { get; }
        public Task<ScriptDocument>? Loading;
        public ScriptDocument? Doc;
        public string? Error;
        /// <summary>Cursor line to restore after a reload.</summary>
        public int? RestoreLine;
        public bool IsDirty => Doc != null && Editor.IsDirty;

        // Analysis snapshot (built off the UI thread, debounced).
        public IReadOnlyList<GoalFunction> Outline = Array.Empty<GoalFunction>();
        public ILookup<int, AnimReference> AnimsByLine = Array.Empty<AnimReference>().ToLookup(a => a.Line);
        public Task<(IReadOnlyList<GoalFunction> Outline, ILookup<int, AnimReference> Anims, bool Hot, SoulsAiStudio.Core.Lua.Syntax.LuaSyntaxError? Syntax, IReadOnlySet<int> SpEffects)>? OutlineTask;
        /// <summary>The text has the in-game hot reload block (<see cref="HotReloadStub"/>).</summary>
        public bool HotReload;
        /// <summary>First Lua syntax error of the text (null = compiles), from the debounced analysis.</summary>
        public SoulsAiStudio.Core.Lua.Syntax.LuaSyntaxError? SyntaxError;
        /// <summary>SpEffect ids the script handles in Goal.Interrupt or observes (hover tooltips).</summary>
        public IReadOnlySet<int> SpEffectIds = new HashSet<int>();
        public long OutlineVersion = -1;
        public DateTime LastEdit = DateTime.MinValue;
        /// <summary>Run once on the UI thread right after the text is loaded (e.g. the Lua part of an animation swap).</summary>
        public readonly List<Action<OpenScript>> AfterLoad = new();

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
    public sealed partial class LuaEditorPanel
    {
        private static readonly TimeSpan AnalysisDebounce = TimeSpan.FromMilliseconds(250);
        private readonly SasApp _app;
        private readonly List<OpenScript> _open = new();
        private readonly DsasKeyInput _keys = new();
        /// <summary>Autocomplete names from every open script + Sekiro AI seeds (shared by all editors).</summary>
        private readonly WorkspaceCompletionProvider _completions = new();
        private OpenScript? _active;
        private OpenScript? _selectTab; // force-select next frame (ImGui keeps its own tab selection otherwise)
        private string _navFilter = "";
        public bool IsOpen = true;

        /// <summary>True when the editor had keyboard focus last frame (Space etc. go to the text, not to playback).</summary>
        public bool HadKeyboardFocus { get; private set; }

        /// <summary>Dock node of this window when its tab was visible this frame, else 0.</summary>
        public uint VisibleInDockId { get; private set; }

        public LuaEditorPanel(SasApp app) => _app = app;

        /// <summary>Set SAS_DEBUG=1 to show the editor's internal input state (diagnostics only).</summary>
        private static readonly bool DebugEditor = Environment.GetEnvironmentVariable("SAS_DEBUG") == "1";

        public ProjectScript? ActiveScript => _active?.Script;
        public OpenScript? Active => _active;
        public bool AnyDirty => _open.Any(o => o.IsDirty);
        /// <summary>Scripts open in tabs (e.g. to rewrite anim ids after a swap).</summary>
        public IReadOnlyList<OpenScript> OpenScripts => _open;
        public bool ActiveIsDirty => _active?.IsDirty == true;
        public IEnumerable<string> DirtyScriptNames => _open.Where(o => o.IsDirty).Select(o => o.Script.FileName);

        public void Open(ProjectScript script) => Open(script, null, activate: true);

        /// <summary>
        /// Opens (or finds) a tab. <paramref name="afterLoad"/> runs on the UI thread once its text is loaded: now if it
        /// already is, else right after loading (decompiling) finishes.
        /// </summary>
        public OpenScript Open(ProjectScript script, Action<OpenScript>? afterLoad, bool activate)
        {
            var existing = _open.FirstOrDefault(o => o.Script.Key == script.Key);
            if (existing == null)
            {
                existing = new OpenScript(script, _keys, _completions) { Loading = _app.Session!.OpenAsync(script) };
                var created = existing;
                existing.Editor.NumberHoverText = (n, line) => SpEffectHoverText(created, n, line);
                _open.Add(existing);
            }
            if (afterLoad != null)
            {
                if (existing.Doc != null && existing.Loading == null) afterLoad(existing);
                else existing.AfterLoad.Add(afterLoad);
            }
            if (activate || _active == null) _active = _selectTab = existing;
            IsOpen = true;
            return existing;
        }

        public int DirtyCount => _open.Count(o => o.IsDirty);

        /// <summary>Saves every tab with unsaved edits (saves of one luabnd are serialized by the session).</summary>
        public void SaveAll()
        {
            if (_app.Project?.IsReadOnly == true) { _app.Notify("Read-only project (no mod folder): scripts can't be saved."); return; }
            foreach (var o in _open) Save(o);
        }

        public void JumpTo(int line) => _active?.Editor.GoToLine(line);

        /// <summary>
        /// Reloads open scripts of <paramref name="luabnd"/> from disk (after a repack/unpack changed it). Scripts with
        /// unsaved edits are left alone; their names are returned.
        /// </summary>
        public IReadOnlyList<string> ReloadLuabnd(string luabnd)
        {
            var skipped = new List<string>();
            foreach (var o in _open.Where(o => string.Equals(o.Script.Luabnd, luabnd, StringComparison.OrdinalIgnoreCase)))
            {
                if (o.IsDirty || o.Saving != null) { skipped.Add(o.Script.FileName); continue; }
                if (o.Loading != null) continue;
                o.RestoreLine = o.Doc != null ? o.CursorLine : null;
                o.Loading = _app.Session!.OpenAsync(o.Script);
            }
            return skipped;
        }

        /// <summary>Closes the tab of <paramref name="key"/> if open. False (tab kept) when it has unsaved edits or is saving.</summary>
        public bool TryClose(string key)
        {
            var o = _open.FirstOrDefault(x => x.Script.Key == key);
            if (o == null) return true;
            if (o.IsDirty || o.Saving != null) return false;
            _open.Remove(o);
            _completions.RemoveDocument(key);
            if (_active == o) _active = _open.FirstOrDefault();
            return true;
        }

        public void CloseAll()
        {
            _open.Clear();
            _completions.Clear();
            _active = null;
        }

        public void SaveActive()
        {
            var o = _active;
            if (o?.Doc == null || o.Saving != null || !o.IsDirty) return;
            if (_app.Project?.IsReadOnly == true)
            {
                _app.Notify("Read-only project (no mod folder): scripts can't be saved.");
                return;
            }
            Save(o);
        }

        private void Save(OpenScript o)
        {
            if (o.Doc == null || o.Saving != null || !o.IsDirty) return;
            o.SavingVersion = o.Editor.Version;
            o.SavingText = o.Editor.Text;
            o.Saving = _app.Session!.SaveAsync(o.Doc, o.SavingText);
        }

        /// <summary>
        /// Hovering a number in the editor: the SpEffect's label (Souls AI &gt; SpEffect Labels) or its SpEffectParam name,
        /// when the number is a SpEffect here: in a <c>...SpecialEffect...</c> call on that line, or an id the script
        /// handles in Goal.Interrupt / observes. Other numbers (anim ids, distances...) show nothing.
        /// </summary>
        private string? SpEffectHoverText(OpenScript o, long n, string line)
        {
            if (n <= 0 || n > int.MaxValue) return null;
            int id = (int)n;
            bool spContext = line.Contains("SpecialEffect", StringComparison.Ordinal) || o.SpEffectIds.Contains(id);
            if (!spContext) return null;
            string? name = _app.SpEffectDisplayName(id);
            return name != null
                ? $"SpEffect {id}: {name}"
                : $"SpEffect {id} (no label: right-click it in the Combo panel, or Souls AI > SpEffect Labels)";
        }

        /// <returns>True when the panel (or the editor inside it) has keyboard focus.</returns>
        public bool Build()
        {
            _keys.NewFrame();
            VisibleInDockId = 0;
            foreach (var o in _open) PollTasks(o);
            PollDiskChecks(); // LuaEditorPanel.External.cs
            if (!IsOpen) { ImeGuard.SetEditorFocused(DSAnimStudio.Main.WinForm?.Handle ?? IntPtr.Zero, false, true); return false; }

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(340, 60), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(vp.WorkSize.X * 0.55f, vp.WorkSize.Y - 120), ImGuiCond.FirstUseEver);
            bool focused = false;
            bool visible = Theme.BeginOpaque("Lua Editor##sas_editor", ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (visible)
                {
                    VisibleInDockId = ImGui.IsWindowDocked() ? ImGui.GetWindowDockID() : 0;
                    focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                    if (focused && _keys.CtrlS) SaveActive();
                    BuildTabs();
                    if (_active != null) focused |= BuildActive(_active);
                    else ImGui.TextDisabled("Open a script from the Project panel.");
                }
            }
            finally { ImGui.End(); } // keep ImGui's stack balanced even if a panel throws
            ImeGuard.SetEditorFocused(DSAnimStudio.Main.WinForm?.Handle ?? IntPtr.Zero, focused, _app.Settings.AllowImeInEditor);
            HadKeyboardFocus = focused;
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
                _completions.RemoveDocument(close.Script.Key);
                if (_active == close) _active = _open.FirstOrDefault();
            }
        }

        private bool BuildActive(OpenScript o)
        {
            if (o.Error != null) { ImGui.TextColored(Theme.Error, o.Error); return false; }
            if (o.Doc == null) { ImGui.TextDisabled(o.Loading != null ? "Loading (decompiling if needed)..." : "Not loaded."); return false; }

            BuildDiskBanner(o); // changed on disk while edited (LuaEditorPanel.External.cs)
            BuildBreadcrumb(o);
            BuildStatusLine(o);
            o.Editor.SetHighlightLine(o.GoalAtCursor?.Line);
            if (DebugEditor) ImGui.TextColored(Theme.Warn, o.Editor.DebugState.Replace('\n', ' '));
            // Per-file ID: one shared child window would share its scroll position across every open tab.
            bool focused = o.Editor.Render("##sas_code_" + o.Script.Key, new Vector2(-1, -1));
            if (o.Editor.TryTakeFindInFilesRequest(out string seed)) _app.OpenFindInScripts(seed); // Ctrl+Shift+F
            return focused;
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
                ImGui.InputTextWithHint("##sas_nav_filter", "Filter goals (e.g. ken09, k9, act1)", ref _navFilter, 64);
                foreach (var group in o.Outline.GroupBy(g => g.Group).OrderBy(g => g.Key))
                {
                    var items = group.Where(g => GoalFilter.Matches(g.Name, _navFilter)).ToList();
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
                if (ImGui.SmallButton($"▶ {a.AnimId}##play{a.AnimId}")) _app.PlayLuaAnim(a.AnimId);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{a.GoalType}: play anim {SoulsAiStudio.Core.Project.AnimIds.Format(_app.TaeAnimId(a.AnimId))} in the viewport" +
                                                            "\n(the a### part is the script's Anim category, Project panel)");
                ImGui.SameLine();
            }
            ImGui.TextDisabled($"Ln {line + 1}, Col {col + 1}   Ctrl+F find · Ctrl+H replace · Ctrl+Space suggest · Ctrl+G go to line · Ctrl+S save · (hover: all shortcuts)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Alt+Up / Alt+Down          move line(s)\n" +
                                 "Shift+Alt+Up / Down        copy line(s) up / down\n" +
                                 "Ctrl+L                     select line (again: next line too)\n" +
                                 "Ctrl+Shift+K               delete line(s)\n" +
                                 "Ctrl+Enter / Ctrl+Shift+Enter   new line below / above\n" +
                                 "Ctrl+/                     comment / uncomment (--)\n" +
                                 "Ctrl+D                     select word, then ADD next occurrence (multi-cursor)\n" +
                                 "Ctrl+Shift+L               select all occurrences\n" +
                                 "Alt+Click / Ctrl+Alt+Up / Down   add a cursor (Esc: back to one)\n" +
                                 "Ctrl+Shift+F               find / replace in many scripts\n" +
                                 "Ctrl+] / Ctrl+[  Tab / Shift+Tab   indent / outdent\n" +
                                 "Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z)   undo / redo\n" +
                                 "Ctrl+F / Ctrl+H / F3 / Shift+F3   find / replace / next / previous\n" +
                                 "Ctrl+G go to line · Ctrl+Space suggestions · Ctrl+S save");
        }

        private void BuildStatusLine(OpenScript o)
        {
            if (o.Saving != null) ImGui.TextColored(Theme.Warn, "Saving...");
            else if (o.IsDirty) ImGui.TextColored(Theme.Warn, "● Modified — Ctrl+S to save to the mod folder");
            else ImGui.TextColored(Theme.Ok, "Saved");
            int dirty = DirtyCount;
            if (dirty > 1 || (dirty == 1 && !o.IsDirty))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Save all ({dirty})")) SaveAll();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip((string.Join("\n", DirtyScriptNames.Select(n => "● " + n)) +
                    "\n\nSaves every modified script tab into the mod folder (backup taken).").Replace("%", "%%"));
            }
            BuildHotReloadButton(o); // LuaEditorPanel.HotReload.cs
            BuildSyntaxErrorButton(o); // LuaEditorPanel.Syntax.cs
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
                    if (o.RestoreLine is int line) { o.Editor.GoToLine(line); o.RestoreLine = null; }
                    o.LastEdit = DateTime.UtcNow - AnalysisDebounce; // analyze immediately
                    var after = o.AfterLoad.ToList();
                    o.AfterLoad.Clear();
                    foreach (var a in after)
                    {
                        try { a(o); }
                        catch (Exception ex) { _app.ReportError($"{o.Script.FileName}: after load", ex); }
                    }
                }
                else
                {
                    o.Error = "Could not open: " + load.Exception?.GetBaseException().Message;
                    if (o.AfterLoad.Count > 0) _app.ReportError($"{o.Script.FileName}: could not open, its pending change was not applied", load.Exception?.GetBaseException());
                    o.AfterLoad.Clear();
                }
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
                    else _app.Notify($"Saved {o.Script.FileName} → {save.Result.WrittenFiles.Count} file(s) in the mod folder (backup taken)."
                                     + HotReloadSavedHint(o.SavingText)); // LuaEditorPanel.HotReload.cs
                    WarnIfSavedWithSyntaxError(o, o.SavingText); // LuaEditorPanel.Syntax.cs
                }
                else _app.ReportError($"Saving {o.Script.FileName} failed", save.Exception?.GetBaseException());
            }

            if (o.OutlineTask is { IsCompleted: true } ot)
            {
                o.OutlineTask = null;
                if (ot.IsCompletedSuccessfully)
                {
                    o.Outline = ot.Result.Outline; o.AnimsByLine = ot.Result.Anims; o.HotReload = ot.Result.Hot;
                    ApplySyntaxError(o, ot.Result.Syntax); // LuaEditorPanel.Syntax.cs
                    o.SpEffectIds = ot.Result.SpEffects;
                }
            }
            else if (o.OutlineTask == null && o.Doc != null && o.OutlineVersion != o.Editor.Version
                     && DateTime.UtcNow - o.LastEdit >= AnalysisDebounce)
            {
                o.OutlineVersion = o.Editor.Version;
                string snapshot = o.Editor.Text;
                string key = o.Script.Key;
                o.OutlineTask = Task.Run(() =>
                {
                    _completions.SetDocument(key, LuaCompletionScanner.Scan(snapshot)); // thread-safe
                    var tokens = SoulsAiStudio.Core.Lua.Syntax.LuaLexer.Tokenize(snapshot);
                    var outline = GoalOutline.Build(snapshot, tokens);
                    var sp = SpEffectInterrupts.Build(snapshot, tokens, outline);
                    IReadOnlySet<int> spIds = sp.Handlers.Keys.Concat(sp.ObservedTargets.Keys).ToHashSet();
                    return (outline, AnimReferences.Build(snapshot, tokens).ToLookup(a => a.Line), HotReloadStub.Has(snapshot),
                            SoulsAiStudio.Core.Lua.Syntax.LuaSyntaxChecker.Check(snapshot), spIds);
                });
            }
        }
    }
}
