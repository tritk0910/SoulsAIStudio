#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using ImGuiNET;
using Microsoft.Xna.Framework.Input;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.App
{
    /// <summary>A script open in the editor, with its analysis snapshot.</summary>
    public sealed class OpenScript
    {
        public OpenScript(ProjectScript script) => Script = script;
        public ProjectScript Script { get; }
        public Task<ScriptDocument>? Loading;
        public ScriptDocument? Doc;
        public string Text = "";
        public string? Error;
        public bool IsDirty => Doc != null && !string.Equals(Text, Doc.SavedText, StringComparison.Ordinal);

        // Analysis snapshot (built off the UI thread, debounced).
        public IReadOnlyList<GoalFunction> Outline = Array.Empty<GoalFunction>();
        public Task<IReadOnlyList<GoalFunction>>? OutlineTask;
        public string OutlineSource = "";
        public DateTime LastEdit = DateTime.MinValue;

        public int CursorLine;
        public int? PendingJumpLine;
        public Task<IReadOnlyList<string>>? Saving;

        public GoalFunction? GoalAtCursor =>
            Outline.LastOrDefault(g => g.Line <= CursorLine && CursorLine <= g.EndLine);
    }

    /// <summary>
    /// Lua editor panel with goal navigator breadcrumb (CLAUDE.md §5b).
    /// INTERIM text widget: ImGui InputTextMultiline. It is replaced by the SoulsAiStudio.Editor widget
    /// (ImGuiColorTextEditNet port) once that lands; the panel/navigator logic stays.
    /// </summary>
    public sealed unsafe class LuaEditorPanel
    {
        private static readonly TimeSpan AnalysisDebounce = TimeSpan.FromMilliseconds(250);
        private readonly SasApp _app;
        private readonly List<OpenScript> _open = new();
        private OpenScript? _active;
        private OpenScript? _cbTarget;
        private OpenScript? _selectTab; // tab to force-select next frame (ImGui keeps its own selection otherwise)
        private string _navFilter = "";
        private bool _ctrlSWasDown;
        private readonly ImGuiInputTextCallback _callback;
        public bool IsOpen = true;

        public LuaEditorPanel(SasApp app)
        {
            _app = app;
            _callback = OnInputCallback;
        }

        public ProjectScript? ActiveScript => _active?.Script;
        public OpenScript? Active => _active;
        public bool AnyDirty => _open.Any(o => o.IsDirty);
        public bool ActiveIsDirty => _active?.IsDirty == true;

        public void Open(ProjectScript script)
        {
            var existing = _open.FirstOrDefault(o => o.Script.Key == script.Key);
            if (existing == null)
            {
                existing = new OpenScript(script) { Loading = _app.Session!.OpenAsync(script) };
                _open.Add(existing);
            }
            _active = existing;
            _selectTab = existing;
            IsOpen = true;
        }

        public void JumpTo(int line)
        {
            if (_active == null) return;
            _active.PendingJumpLine = line;
        }

        public void CloseAll()
        {
            _open.Clear();
            _active = null;
        }

        public void SaveActive()
        {
            var o = _active;
            if (o?.Doc == null || o.Saving != null || !o.IsDirty) return;
            o.Saving = _app.Session!.SaveAsync(o.Doc, o.Text);
        }

        /// <returns>True when the panel has focus.</returns>
        public bool Build()
        {
            foreach (var o in _open) PollTasks(o);
            if (!IsOpen) return false;

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(340, 60), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(vp.WorkSize.X * 0.55f, vp.WorkSize.Y - 120), ImGuiCond.FirstUseEver);
            bool focused = false;
            if (ImGui.Begin("Lua Editor##sas_editor", ref IsOpen, ImGuiWindowFlags.NoCollapse))
            {
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                if (focused) HandleShortcuts();
                BuildTabs();
                if (_active != null) BuildActive(_active);
                else ImGui.TextDisabled("Open a script from the Scripts panel.");
            }
            ImGui.End();
            return focused;
        }

        private void HandleShortcuts()
        {
            var kb = Keyboard.GetState();
            bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            bool down = ctrl && kb.IsKeyDown(Keys.S);
            if (down && !_ctrlSWasDown) SaveActive();
            _ctrlSWasDown = down;
        }

        private void BuildTabs()
        {
            if (!ImGui.BeginTabBar("##sas_tabs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll)) return;
            OpenScript? close = null;
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
                    if (o.IsDirty) _app.Notify($"{o.Script.FileName} has unsaved changes — save or revert first.");
                    else close = o;
                }
            }
            ImGui.EndTabBar();
            _selectTab = null;
            if (close != null)
            {
                _open.Remove(close);
                if (_active == close) _active = _open.FirstOrDefault();
            }
        }

        private void BuildActive(OpenScript o)
        {
            if (o.Error != null) { ImGui.TextColored(Theme.Error, o.Error); return; }
            if (o.Doc == null) { ImGui.TextDisabled(o.Loading != null ? "Loading (decompiling if needed)..." : "Not loaded."); return; }

            BuildBreadcrumb(o);
            BuildStatusLine(o);

            _cbTarget = o;
            if (o.PendingJumpLine != null) ImGui.SetKeyboardFocusHere();
            uint capacity = (uint)Math.Max(Encoding.UTF8.GetByteCount(o.Text) * 2, 1 << 16);
            var flags = ImGuiInputTextFlags.AllowTabInput | ImGuiInputTextFlags.CallbackAlways;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.11f, 0.11f, 0.13f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.86f, 0.86f, 0.86f, 1f));
            if (ImGui.InputTextMultiline("##sas_code", ref o.Text, capacity, new Vector2(-1, -1), flags, _callback))
            {
                o.LastEdit = DateTime.UtcNow;
            }
            ImGui.PopStyleColor(2);
            _cbTarget = null;
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
                        if (ImGui.Selectable($"  {g.QualifiedName}##nav{g.Line}", g == goal)) o.PendingJumpLine = g.Line;
                        ImGui.SameLine(240);
                        ImGui.TextDisabled($"function ({g.Parameters})");
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"Ln {o.CursorLine + 1}");
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
                    o.Text = o.Doc.SavedText;
                    o.LastEdit = DateTime.UtcNow - AnalysisDebounce; // analyze immediately
                }
                else o.Error = "Could not open: " + load.Exception?.GetBaseException().Message;
            }

            if (o.Saving is { IsCompleted: true } save)
            {
                o.Saving = null;
                if (save.IsCompletedSuccessfully) _app.Notify($"Saved {o.Script.FileName} → {save.Result.Count} file(s) in the mod folder (backup taken).");
                else _app.ReportError($"Saving {o.Script.FileName} failed", save.Exception?.GetBaseException());
            }

            if (o.OutlineTask is { IsCompleted: true } ot)
            {
                o.OutlineTask = null;
                if (ot.IsCompletedSuccessfully) o.Outline = ot.Result;
            }
            else if (o.OutlineTask == null && o.Doc != null && !ReferenceEquals(o.OutlineSource, o.Text)
                     && DateTime.UtcNow - o.LastEdit >= AnalysisDebounce)
            {
                string snapshot = o.Text;
                o.OutlineSource = snapshot;
                o.OutlineTask = Task.Run(() => GoalOutline.Build(snapshot));
            }
        }

        /// <summary>Applies pending jumps and tracks the cursor line (runs while the text box is active).</summary>
        private int OnInputCallback(ImGuiInputTextCallbackData* data)
        {
            var o = _cbTarget;
            if (o == null) return 0;
            var span = new ReadOnlySpan<byte>(data->Buf, data->BufTextLen);

            if (o.PendingJumpLine is int line)
            {
                o.PendingJumpLine = null;
                int offset = 0;
                for (int l = 0; l < line && offset < span.Length; l++)
                {
                    int nl = span[offset..].IndexOf((byte)'\n');
                    if (nl < 0) { offset = span.Length; break; }
                    offset += nl + 1;
                }
                data->CursorPos = data->SelectionStart = data->SelectionEnd = offset; // ImGui scrolls to the cursor
            }

            int cursor = Math.Clamp(data->CursorPos, 0, span.Length);
            int count = 0;
            foreach (byte b in span[..cursor]) if (b == (byte)'\n') count++;
            o.CursorLine = count;
            return 0;
        }
    }
}
