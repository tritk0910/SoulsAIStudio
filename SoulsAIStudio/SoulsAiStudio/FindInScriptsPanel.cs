#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.App
{
    /// <summary>
    /// "Find in Scripts" (Ctrl+Shift+F in the Lua editor, Souls AI menu): searches many AI scripts at once with the find
    /// bar's matching (Match case / Whole word). Scope: the project's scripts, every script of the current character (all
    /// luabnds, like the animation swap), or every luabnd. Texts are read in the background, one script at a time (bytecode
    /// is decompiled, ~300 ms each); scripts open in the editor are searched in their live text. Results are grouped by
    /// file; click a row to open the script and select the match. Replace all / Swap edit the ticked files' editor tabs as
    /// UNSAVED changes (one undo step per file; scripts outside the project are added to it); "Save all" saves them.
    /// </summary>
    public sealed class FindInScriptsPanel
    {
        public const string WindowName = "Find in Scripts##sas_findscripts";
        private const int MaxMatchesPerFile = 2000;
        private const int PreviewBefore = 48, PreviewAfter = 120;

        private enum Scope { Project, Character, AllLuabnds }

        private sealed class Hit
        {
            public int Line, Column, Length;
            public string Number = "", Before = "", Match = "", After = "";
        }

        private sealed class FileResult
        {
            public ProjectScript Script = null!;
            public bool InProject;
            public bool Ticked = true;
            public bool Expanded = true;
            public List<Hit> Hits = new();
            public int OtherCount; // occurrences of the Replace text (for Swap)
            public bool Truncated;
            /// <summary>Editor version the text came from, or -1 when it was read from the file.</summary>
            public long SourceVersion = -1;
            public string Header = "";
        }

        /// <summary>One visible row: a file header (Hit == -1) or a match.</summary>
        private readonly record struct Row(int File, int Hit);

        private readonly SasApp _app;
        public bool IsOpen;
        private string _query = "";
        private string _replace = "";
        private bool _matchCase;
        private bool _wholeWord;
        private Scope _scope = Scope.Project;
        private bool _focusQuery;
        private ProjectSession? _session;

        // search state (written by the background task, read on the UI thread)
        private CancellationTokenSource? _cts;
        private Task? _task;
        private readonly ConcurrentQueue<FileResult> _incoming = new();
        private int _done, _total;
        private volatile string _current = "";
        private volatile string? _searchError;
        private string _status = "";

        private readonly List<FileResult> _results = new();
        private readonly List<Row> _rows = new();
        private bool _rowsDirty;
        private string _searchedQuery = "", _searchedReplace = "";
        private bool _searchedCase, _searchedWord;
        private (int File, int Hit) _selected = (-1, -1);

        public FindInScriptsPanel(SasApp app) => _app = app;

        /// <summary>Opens the window; a non-empty <paramref name="seed"/> (selection / word at the cursor) fills the query.</summary>
        public void Open(string? seed = null)
        {
            IsOpen = true;
            _focusQuery = true;
            if (!string.IsNullOrEmpty(seed) && seed.IndexOfAny(new[] { '\r', '\n' }) < 0) _query = seed;
            ImGui.SetWindowFocus(WindowName);
        }

        public void CancelSearch() => _cts?.Cancel();

        /// <returns>True when the window has focus.</returns>
        public bool Build()
        {
            if (!ReferenceEquals(_session, _app.Session))
            {
                // project closed / switched: results belong to the old one
                _session = _app.Session;
                CancelSearch();
                while (_incoming.TryDequeue(out _)) { }
                _results.Clear();
                _rowsDirty = true;
                _searchedQuery = "";
                _status = "";
                _searchError = null;
            }
            Drain();
            if (!IsOpen) return false;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(900, 560), ImGuiCond.FirstUseEver);
            bool focused = false;
            bool visible = Theme.BeginOpaque(WindowName, ref IsOpen, ImGuiWindowFlags.NoCollapse);
            try
            {
                if (!visible) return false;
                focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
                if (_app.Session == null) { ImGui.TextDisabled("Open a Souls AI project first."); return focused; }
                BuildInputs();
                BuildStatus();
                BuildResults();
            }
            finally { ImGui.End(); }
            if (!IsOpen) CancelSearch();
            return focused;
        }

        // ------------------------------------------------------------------ inputs

        private bool Searching => _task is { IsCompleted: false };

        private void BuildInputs()
        {
            float labelW = ImGui.CalcTextSize("Replace").X + ImGui.GetStyle().ItemSpacing.X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Find");
            ImGui.SameLine(labelW + ImGui.GetStyle().WindowPadding.X);
            ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X - 260));
            if (_focusQuery) { ImGui.SetKeyboardFocusHere(); _focusQuery = false; }
            bool enter = ImGui.InputTextWithHint("##sas_fis_query", "text to find, e.g. 3001 or GOAL_COMMON_ComboRepeat", ref _query, 256,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            ImGui.Checkbox("Aa", ref _matchCase);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Match case");
            ImGui.SameLine();
            ImGui.Checkbox("W", ref _wholeWord);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Match whole word: 3001 does not match inside 30012 or a3001");
            ImGui.SameLine();
            if (Searching)
            {
                if (ImGui.Button("Cancel##sas_fis_cancel")) CancelSearch();
            }
            else
            {
                ImGui.BeginDisabled(_query.Length == 0);
                if (ImGui.Button("Search##sas_fis_go") || enter && _query.Length > 0) StartSearch();
                ImGui.EndDisabled();
            }

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Replace");
            ImGui.SameLine(labelW + ImGui.GetStyle().WindowPadding.X);
            ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X - 260));
            ImGui.InputTextWithHint("##sas_fis_replace", "replacement (Replace all) or the other text (Swap)", ref _replace, 256);
            ImGui.SameLine();
            BuildReplaceButtons();

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("In");
            ImGui.SameLine(labelW + ImGui.GetStyle().WindowPadding.X);
            if (ImGui.RadioButton("Project scripts", _scope == Scope.Project)) _scope = Scope.Project;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The scripts listed in the Project panel.");
            ImGui.SameLine();
            var chr = CharacterAnibnd(out string chrLabel);
            ImGui.BeginDisabled(chr == null);
            if (ImGui.RadioButton($"All scripts of this character{chrLabel}", _scope == Scope.Character)) _scope = Scope.Character;
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(chr == null ? "Open a script of a character (or load a character) first."
                    : "Every AI script of the mod and the game whose character is this one (all luabnds),\nin the project or not.");
            ImGui.SameLine();
            if (ImGui.RadioButton("All luabnds", _scope == Scope.AllLuabnds)) _scope = Scope.AllLuabnds;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every script of every luabnd (mod first, then game). Bytecode scripts are decompiled once\n(~0.3 s each, cached): the first search over everything can take minutes. Cancel anytime.");
            if (_scope == Scope.Character && chr == null) _scope = Scope.Project;
        }

        /// <summary>Anibnd of the active script's character (else of the loaded character), for the character scope.</summary>
        private string? CharacterAnibnd(out string label)
        {
            string key = (ActiveScript()?.Key ?? "") + "|" + (_app.Bridge.LoadedAnibnd ?? "");
            if (key != _chrKey)
            {
                _chrKey = key;
                _chrAnibnd = ResolveCharacterAnibnd(out _chrLabel);
            }
            label = _chrLabel;
            return _chrAnibnd;
        }

        private string _chrKey = "?", _chrLabel = "";
        private string? _chrAnibnd;

        private string? ResolveCharacterAnibnd(out string label)
        {
            label = "";
            string? anibnd = null;
            if (ActiveScript() is { } s && _app.ResolveCharacter(s) is { } files)
            {
                anibnd = files.AnibndPath;
                label = $" ({files.ChrId})";
            }
            else if (_app.Bridge.LoadedAnibnd is { } loaded)
            {
                anibnd = loaded;
                label = $" ({Path.GetFileName(loaded).Split('.')[0]})";
            }
            return anibnd;
        }

        private ProjectScript? ActiveScript() => _app.EditorPanel.ActiveScript;

        private bool SameQueryAsSearch => _query == _searchedQuery && _matchCase == _searchedCase && _wholeWord == _searchedWord;

        private IEnumerable<FileResult> Ticked(bool swap) =>
            _results.Where(r => r.Ticked && (r.Hits.Count > 0 || swap && r.OtherCount > 0));

        private void BuildReplaceButtons()
        {
            bool readOnly = _app.Project?.IsReadOnly == true;
            bool ready = !Searching && _results.Count > 0 && SameQueryAsSearch && !readOnly;
            string why = readOnly ? "Read-only project (no mod folder): scripts can't be changed."
                : Searching ? "Wait for the search to finish."
                : _results.Count == 0 ? "Search first."
                : !SameQueryAsSearch ? "The Find text or options changed: Search again first." : "";

            int files = Ticked(false).Count();
            ImGui.BeginDisabled(!ready || files == 0);
            if (ImGui.Button("Replace all##sas_fis_all")) Apply(swap: false);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(why.Length > 0 ? why : $"Replace every match in the {files} ticked file(s): the changes go into their editor tabs\n" +
                                                        "as unsaved edits (one undo step per file). Save with Save all.");
            ImGui.SameLine();
            bool swapReady = ready && _replace.Length > 0 && _replace == _searchedReplace;
            int swapFiles = Ticked(true).Count();
            ImGui.BeginDisabled(!swapReady || swapFiles == 0);
            if (ImGui.Button("Swap##sas_fis_swap")) Apply(swap: true);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(why.Length > 0 ? why
                    : _replace.Length == 0 ? "Type the other text in Replace, then Search (the search also counts it)."
                    : _replace != _searchedReplace ? "The Replace text changed since the search: Search again (Swap needs its count too)."
                    : $"Exchange the two texts everywhere at once in the {swapFiles} ticked file(s): every Find becomes Replace and every\n" +
                      "Replace becomes Find (e.g. 3001 <-> 3012). Unsaved edits, one undo step per file. Tip: W for anim ids.");
        }

        // ------------------------------------------------------------------ search

        private void StartSearch()
        {
            var session = _app.Session;
            if (session == null || _query.Length == 0) return;
            CancelSearch();
            var cts = _cts = new CancellationTokenSource();
            while (_incoming.TryDequeue(out _)) { }
            _results.Clear();
            _rowsDirty = true;
            _selected = (-1, -1);
            _searchError = null;
            _done = 0;
            _total = 0;
            _current = "";
            _status = "";
            _searchedQuery = _query;
            _searchedReplace = _replace;
            _searchedCase = _matchCase;
            _searchedWord = _wholeWord;

            string query = _query, other = _replace;
            bool mc = _matchCase, ww = _wholeWord;
            var scope = _scope;
            string? anibnd = scope == Scope.Character ? CharacterAnibnd(out _) : null;
            var projectScripts = session.Project.Scripts.ToList();
            var locator = new CharacterLocator(session.Project.GameDir, session.Project.ModDir);
            // Live text of scripts open in the editor (snapshotted on the UI thread).
            var live = _app.OpenScripts.Where(o => o.Doc != null && o.Loading == null)
                .ToDictionary(o => o.Script.Key, o => (Text: o.Editor.Text, o.Editor.Version), StringComparer.OrdinalIgnoreCase);

            _task = Task.Run(async () =>
            {
                try
                {
                    var targets = new List<(ProjectScript Script, bool InProject)>();
                    if (scope == Scope.Project)
                        targets.AddRange(projectScripts.Select(s => (s, true)));
                    else
                    {
                        _current = "listing the luabnds...";
                        var (candidates, errors) = session.Catalog.Scan(cts.Token);
                        if (errors.Count > 0) _searchError = "Some luabnds could not be read: " + string.Join("; ", errors);
                        if (scope == Scope.Character)
                            targets.AddRange(SharedCharacterScripts.Find(candidates, projectScripts, locator, anibnd!)
                                .Select(sh => (sh.Script, sh.InProject)));
                        else
                        {
                            var inProject = projectScripts.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
                            targets.AddRange(candidates.Select(c => inProject.TryGetValue(c.Luabnd + "|" + c.EntryPath, out var ps)
                                ? (ps, true) : (c.ToProjectScript(), false)));
                        }
                    }
                    targets = targets.GroupBy(t => t.Script.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .OrderBy(t => t.Script.Luabnd, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Script.FileName, StringComparer.OrdinalIgnoreCase).ToList();
                    _total = targets.Count;

                    var errorsOpen = new List<string>();
                    foreach (var (script, inProject) in targets) // sequential: the decompiler runs one process at a time
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        _current = $"{script.Luabnd} > {script.FileName}";
                        try
                        {
                            string text;
                            long version = -1;
                            if (live.TryGetValue(script.Key, out var l)) { text = l.Text; version = l.Version; }
                            else text = (await session.OpenAsync(script, cts.Token)).SavedText;
                            var r = Analyze(script, inProject, text, version, query, other, mc, ww);
                            if (r != null && !cts.IsCancellationRequested) _incoming.Enqueue(r);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { errorsOpen.Add($"{script.Luabnd} > {script.FileName}: {ex.GetBaseException().Message}"); }
                        Interlocked.Increment(ref _done);
                    }
                    if (errorsOpen.Count > 0)
                        _searchError = (_searchError != null ? _searchError + "\n" : "") + $"{errorsOpen.Count} script(s) could not be read: "
                                       + string.Join("; ", errorsOpen.Take(5)) + (errorsOpen.Count > 5 ? "; ..." : "");
                }
                catch (OperationCanceledException) { if (ReferenceEquals(_cts, cts)) _searchError = "Search cancelled."; }
                catch (Exception ex) { if (ReferenceEquals(_cts, cts)) _searchError = "Search failed: " + ex.GetBaseException().Message; }
                finally { if (ReferenceEquals(_cts, cts)) _current = ""; }
            });
        }

        private static FileResult? Analyze(ProjectScript script, bool inProject, string text, long version,
            string query, string other, bool mc, bool ww)
        {
            var matches = TextSearch.FindAll(text, query, mc, ww, MaxMatchesPerFile + 1);
            int otherCount = other.Length > 0 && !string.Equals(other, query, mc ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                ? TextSearch.Count(text, other, mc, ww) : 0;
            if (matches.Count == 0 && otherCount == 0) return null;
            var r = new FileResult { Script = script, InProject = inProject, OtherCount = otherCount, SourceVersion = version };
            r.Truncated = matches.Count > MaxMatchesPerFile;
            foreach (var m in matches.Take(MaxMatchesPerFile)) r.Hits.Add(MakeHit(m));
            return r;
        }

        /// <summary>Line preview split around the match (leading blanks trimmed, long lines shortened).</summary>
        private static Hit MakeHit(TextMatch m)
        {
            string line = m.LineText;
            int start = 0;
            while (start < m.Column && (line[start] == ' ' || line[start] == '\t')) start++;
            string before = line[start..m.Column];
            if (before.Length > PreviewBefore) before = "…" + before[^PreviewBefore..];
            int afterStart = m.Column + m.Length;
            string after = line[afterStart..];
            if (after.Length > PreviewAfter) after = after[..PreviewAfter] + "…";
            return new Hit
            {
                Line = m.Line, Column = m.Column, Length = m.Length,
                Number = (m.Line + 1).ToString().PadLeft(5) + ":  ",
                Before = before.Replace('\t', ' '), Match = line.Substring(m.Column, m.Length), After = after.Replace('\t', ' '),
            };
        }

        /// <summary>Moves finished file results to the UI list (UI thread).</summary>
        private void Drain()
        {
            bool any = false;
            while (_incoming.TryDequeue(out var r))
            {
                r.Header = $"{r.Script.Luabnd} > {r.Script.FileName}";
                _results.Add(r);
                any = true;
            }
            if (any)
            {
                _results.Sort((a, b) =>
                {
                    int c = string.Compare(a.Script.Luabnd, b.Script.Luabnd, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : string.Compare(a.Script.FileName, b.Script.FileName, StringComparison.OrdinalIgnoreCase);
                });
                _rowsDirty = true;
                _selected = (-1, -1);
            }
        }

        private string StatusText()
        {
            if (!Searching && _searchedQuery.Length == 0) return _status; // e.g. the last Replace all message
            int matches = 0, files = 0, otherOnly = 0;
            foreach (var r in _results)
            {
                matches += r.Hits.Count;
                if (r.Hits.Count > 0) files++;
                else otherOnly++;
            }
            string found = $"{matches} match{(matches == 1 ? "" : "es")} in {files} file(s)";
            if (otherOnly > 0) found += $" (+{otherOnly} with only the Replace text)";
            return Searching
                ? $"Searching {_done}/{(_total == 0 ? "?" : _total.ToString())}: {_current}   —   {found}"
                : found + ".";
        }

        // ------------------------------------------------------------------ results

        private void BuildStatus()
        {
            string status = StatusText();
            if (status.Length > 0) ImGui.TextDisabled(status.Replace("%", "%%"));
            if (_searchError is { } err) ImGui.TextColored(Theme.Warn, err.Replace("%", "%%"));
            if (_results.Count > 0)
            {
                if (ImGui.SmallButton("Tick all")) foreach (var r in _results) r.Ticked = true;
                ImGui.SameLine();
                if (ImGui.SmallButton("Untick all")) foreach (var r in _results) r.Ticked = false;
                ImGui.SameLine();
                if (ImGui.SmallButton("Expand all")) { foreach (var r in _results) r.Expanded = true; _rowsDirty = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("Collapse all")) { foreach (var r in _results) r.Expanded = false; _rowsDirty = true; }
            }
        }

        private void RebuildRows()
        {
            _rows.Clear();
            for (int f = 0; f < _results.Count; f++)
            {
                _rows.Add(new Row(f, -1));
                if (!_results[f].Expanded) continue;
                for (int h = 0; h < _results[f].Hits.Count; h++) _rows.Add(new Row(f, h));
            }
            _rowsDirty = false;
        }

        private void BuildResults()
        {
            if (_rowsDirty) RebuildRows();
            if (!ImGui.BeginChild("##sas_fis_results", Vector2.Zero, true)) { ImGui.EndChild(); return; }
            try
            {
                float rowH = ImGui.GetFrameHeight();
                var style = ImGui.GetStyle();
                unsafe
                {
                    var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                    clipper.Begin(_rows.Count, rowH + style.ItemSpacing.Y);
                    while (clipper.Step())
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            var row = _rows[i];
                            var r = _results[row.File];
                            ImGui.PushID(i);
                            try
                            {
                                if (row.Hit < 0) FileRow(r);
                                else HitRow(r, row.File, row.Hit, rowH, style);
                            }
                            finally { ImGui.PopID(); }
                        }
                    clipper.End();
                    clipper.Destroy();
                }
            }
            finally { ImGui.EndChild(); }
        }

        private void FileRow(FileResult r)
        {
            if (ImGui.ArrowButton("##exp", r.Expanded ? ImGuiDir.Down : ImGuiDir.Right)) { r.Expanded = !r.Expanded; _rowsDirty = true; }
            ImGui.SameLine();
            ImGui.Checkbox("##tick", ref r.Ticked);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Include this file in Replace all / Swap");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.GroupHeader);
            ImGui.TextUnformatted(r.Header);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            string count = r.Hits.Count > 0 ? $"{r.Hits.Count}{(r.Truncated ? "+" : "")} match{(r.Hits.Count == 1 ? "" : "es")}" : "no match";
            if (r.OtherCount > 0) count += $", {r.OtherCount}x Replace text";
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Dim);
            ImGui.TextUnformatted(count);
            ImGui.PopStyleColor();

            var notes = new List<string>();
            var open = _app.OpenScripts.FirstOrDefault(o => o.Script.Key == r.Script.Key);
            if (open != null && (r.SourceVersion >= 0 ? open.Editor.Version != r.SourceVersion : open.IsDirty)) notes.Add("edited since the search");
            if (!r.InProject && !_app.Project!.Scripts.Any(s => s.Key == r.Script.Key)) notes.Add("not in the project: added on replace");
            if (r.Truncated) notes.Add($"only the first {MaxMatchesPerFile} are listed; Replace all changes every match");
            if (notes.Count > 0)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                ImGui.TextUnformatted("(" + string.Join(", ", notes) + ")");
                ImGui.PopStyleColor();
            }
        }

        private void HitRow(FileResult r, int file, int hit, float rowH, ImGuiStylePtr style)
        {
            var h = r.Hits[hit];
            float x0 = ImGui.GetCursorPosX();
            bool selected = _selected == (file, hit);
            if (ImGui.Selectable("##hit", selected, ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, rowH)))
            {
                _selected = (file, hit);
                GoTo(r, h);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click: open the script and select this match");
            ImGui.SameLine(x0 + ImGui.GetFrameHeight() * 2);
            ImGui.AlignTextToFramePadding(); // texts centered in the row; the row keeps the frame height (clipper)
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Dim);
            ImGui.TextUnformatted(h.Number);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(h.Before);
            ImGui.SameLine(0, 0);
            var p = ImGui.GetCursorScreenPos() + new Vector2(0, style.FramePadding.Y);
            var size = ImGui.CalcTextSize(h.Match);
            ImGui.GetWindowDrawList().AddRectFilled(p, p + size, ImGui.GetColorU32(new Vector4(0.85f, 0.55f, 0.1f, 0.45f)));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
            ImGui.TextUnformatted(h.Match);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(h.After);
        }

        /// <summary>Opens the script's tab and selects the match (re-found on its line if the text moved since the search).</summary>
        private void GoTo(FileResult r, Hit h)
        {
            string query = _searchedQuery;
            bool mc = _searchedCase, ww = _searchedWord;
            void Select(OpenScript o)
            {
                var e = o.Editor;
                if (h.Line < e.LineCount)
                {
                    var onLine = TextSearch.FindAll(e.GetLineText(h.Line), query, mc, ww);
                    if (onLine.Count > 0)
                    {
                        var best = onLine.OrderBy(m => Math.Abs(m.Column - h.Column)).First();
                        e.SelectRange(h.Line, best.Column, best.Column + best.Length);
                        return;
                    }
                }
                e.GoToLine(Math.Min(h.Line, e.LineCount - 1));
            }
            _app.EditorPanel.Open(r.Script, Select, activate: true);
        }

        // ------------------------------------------------------------------ replace / swap

        private void Apply(bool swap)
        {
            string find = _searchedQuery, other = _replace;
            bool mc = _searchedCase, ww = _searchedWord;
            var files = Ticked(swap).ToList();
            int now = 0, nowFiles = 0, later = 0;
            foreach (var r in files)
            {
                bool loaded = _app.OpenScripts.Any(o => o.Script.Key == r.Script.Key && o.Doc != null && o.Loading == null);
                string label = r.Script.FileName;
                bool deferred = !loaded;
                if (deferred) later++;
                // Adds scripts that are not in the project yet; runs now for loaded tabs, after loading otherwise.
                _app.OpenScriptWith(r.Script, o =>
                {
                    int n = swap ? o.Editor.SwapAllText(find, other, mc, ww) : o.Editor.ReplaceAllText(find, other, mc, ww);
                    if (deferred) _app.Notify($"Find in Scripts: {(swap ? "swapped" : "replaced")} {n} in {label} (unsaved).");
                    else { now += n; if (n > 0) nowFiles++; }
                }, activate: false);
            }
            string what = swap ? $"Swapped {find} <-> {other}" : $"Replaced {find} -> {other}";
            string msg = $"{what}: {now} occurrence(s) in {nowFiles} open file(s)"
                         + (later > 0 ? $"; {later} file(s) are opening, their change follows when loaded" : "")
                         + ". Unsaved: check the tabs, then Save all.";
            _app.Notify(msg);
            _status = msg;
            _results.Clear(); // texts changed: the listed positions are stale
            _rowsDirty = true;
            _searchedQuery = "";
            _searchError = null;
        }
    }
}
