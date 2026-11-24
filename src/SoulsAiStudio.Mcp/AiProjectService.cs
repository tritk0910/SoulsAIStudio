using System.Text.Json;
using System.Text.RegularExpressions;
using SoulsAiStudio.Core.Formats;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Lua.Decompile;
using SoulsAiStudio.Core.Lua.Syntax;
using SoulsAiStudio.Core.Project;

namespace SoulsAiStudio.Mcp;

/// <summary>
/// Read-only facade over SoulsAiStudio.Core for the MCP tools. It loads a .sasproj, resolves scripts through
/// <see cref="ScriptCatalog"/> (mod first, then game), decompiles bytecode into a temp dir, and runs the analyzers.
/// It never writes to the project, the mod folder or the game folder.
/// </summary>
public sealed class AiProjectService : IDisposable
{
    public const int MaxLinesPerRead = 400;
    public const int MaxCharsPerRead = 60_000;
    public const int MaxItems = 200;
    public const int DefaultHandlerChars = 20_000;
    public const int DefaultComboNodes = 100;

    private readonly object _gate = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "SoulsAiStudio.Mcp", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Loaded> _texts = new(StringComparer.OrdinalIgnoreCase);
    private SasProject? _project;
    private ScriptCatalog? _catalog;
    private LuaDecompiler? _decompiler;

    private sealed record Loaded(string Text, bool WasBytecode, string[] Lines);

    public bool HasProject => _project != null;

    public string OpenProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"Project file not found: {path}");
        var project = SasProject.Load(path);
        // DCX_KRAK scripts (m25) need oo2core from the user's game folder (never shipped with the app).
        OodleLoader.TryLoad(project.GameDir);
        string exe = LuaDecompiler.ResolveExePath(project.DecompilerPath, FindDecompilerNearRepo());
        lock (_gate)
        {
            _project = project;
            _catalog = new ScriptCatalog(project.GameDir, project.ModDir);
            // Decompile cache lives in a temp dir, never in the project's cache folder.
            _decompiler = new LuaDecompiler(exe, Path.Combine(_tempDir, "cache"));
            _texts.Clear();
        }
        return $"Opened '{project.Name}': {project.Scripts.Count} scripts, {project.Characters.Count} characters.";
    }

    private static string? FindDecompilerNearRepo()
    {
        var env = Environment.GetEnvironmentVariable("SAS_DECOMPILER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, "SoulsAIStudio", "Tools", "DSLuaDecompiler", "DSLuaDecompiler.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private (SasProject P, ScriptCatalog C, LuaDecompiler D) Require()
    {
        lock (_gate)
        {
            if (_project == null || _catalog == null || _decompiler == null)
                throw new InvalidOperationException("No project is open. Call open_project(path) or start the server with --project <file.sasproj>.");
            return (_project, _catalog, _decompiler);
        }
    }

    // ---- listing -----------------------------------------------------------------------------------------------

    public EntityList ListEntities()
    {
        var (p, _, _) = Require();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<EntityInfo>();
        foreach (var ch in p.Characters)
        {
            var scripts = p.Scripts.Where(s => string.Equals(s.Character, ch.ChrId, StringComparison.OrdinalIgnoreCase))
                .Select(s => { assigned.Add(s.Key); return s.FileName; }).ToList();
            entities.Add(new EntityInfo(ch.ChrId, ch.DisplayName, scripts));
        }
        var rest = p.Scripts.Where(s => !assigned.Contains(s.Key)).Select(s => s.FileName).ToList();
        return new EntityList(p.Name, entities, rest);
    }

    public ScriptList ListScripts(string? filter = null)
    {
        var (p, _, _) = Require();
        var all = p.Scripts.Select(ToInfo)
            .Where(s => string.IsNullOrWhiteSpace(filter)
                || s.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.Character.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.Luabnd.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var page = all.Take(MaxItems).ToList();
        return new ScriptList(all.Count, page.Count, all.Count > page.Count, page);
    }

    private static ScriptInfo ToInfo(ProjectScript s)
    {
        string file = s.FileName;
        string? npc = file.Split('_')[0] is { Length: 6 } id && id.All(char.IsDigit) ? id : null;
        string kind = file.Contains("_battle", StringComparison.OrdinalIgnoreCase) ? "battle"
            : file.Contains("_logic", StringComparison.OrdinalIgnoreCase) ? "logic" : "other";
        return new ScriptInfo(s.Key, file, s.Luabnd, npc, kind, s.Character, s.DisplayName);
    }

    /// <summary>Resolves a script by key, file name (with or without .lua), entry path or substring. Throws on none/ambiguous.</summary>
    public ProjectScript ResolveScript(string script)
    {
        var (p, _, _) = Require();
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("script is required.");
        string q = script.Trim();
        var exact = p.Scripts.Where(s => s.Key.Equals(q, StringComparison.OrdinalIgnoreCase)
            || s.EntryPath.Equals(q, StringComparison.OrdinalIgnoreCase)
            || s.FileName.Equals(q, StringComparison.OrdinalIgnoreCase)
            || s.FileName.Equals(q + ".lua", StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var cands = exact.Count > 1 ? exact : p.Scripts.Where(s => s.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cands.Count == 1) return cands[0];
        if (cands.Count == 0) throw new ArgumentException($"No script matches '{script}'. Use list_scripts to see the available scripts.");
        throw new ArgumentException($"'{script}' is ambiguous; use the full key. Matches: "
            + string.Join("; ", cands.Take(10).Select(s => s.Key)) + (cands.Count > 10 ? "; ..." : ""));
    }

    // ---- text --------------------------------------------------------------------------------------------------

    private async Task<(ProjectScript S, Loaded L)> LoadAsync(string script, CancellationToken ct)
    {
        var (_, catalog, decompiler) = Require();
        var s = ResolveScript(script);
        lock (_gate) { if (_texts.TryGetValue(s.Key, out var hit)) return (s, hit); }

        LuabndArchive archive = await Task.Run(() => catalog.Open(s.Luabnd), ct);
        LuabndEntry entry = archive.Find(s.EntryPath)
            ?? throw new FileNotFoundException($"'{s.EntryPath}' is not in {s.Luabnd}.");
        bool bytecode = entry.IsBytecode;
        string text = bytecode ? await decompiler.DecompileAsync(entry.Bytes, entry.FileName, ct) : entry.GetText();
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var loaded = new Loaded(text, bytecode, text.Split('\n'));
        lock (_gate) _texts[s.Key] = loaded;
        return (s, loaded);
    }

    public async Task<LuaText> ReadLuaAsync(string script, int? startLine = null, int? endLine = null, CancellationToken ct = default)
    {
        var (s, l) = await LoadAsync(script, ct);
        int total = l.Lines.Length;
        if (total > 0 && l.Lines[^1].Length == 0) total--; // trailing newline is not a line
        int start = startLine ?? 1;
        if (start < 1) throw new ArgumentException($"startLine must be >= 1 (got {start}).");
        if (start > Math.Max(total, 1)) throw new ArgumentException($"startLine {start} is beyond the end of the file ({total} lines).");
        if (endLine is < 1) throw new ArgumentException($"endLine must be >= 1 (got {endLine}).");
        if (endLine != null && endLine < start) throw new ArgumentException($"endLine ({endLine}) is before startLine ({start}).");
        var notes = new List<string>();
        int end = endLine ?? start + MaxLinesPerRead - 1;
        if (end > total)
        {
            if (endLine != null) notes.Add($"endLine {endLine} is past the end of the file; clamped to {total}.");
            end = Math.Max(total, 1);
        }
        bool truncated = false;
        if (end - start + 1 > MaxLinesPerRead)
        {
            end = start + MaxLinesPerRead - 1; truncated = true;
            notes.Add($"Range capped at {MaxLinesPerRead} lines.");
        }
        string text = total == 0 ? "" : string.Join('\n', l.Lines.Skip(start - 1).Take(end - start + 1));
        if (text.Length > MaxCharsPerRead)
        {
            int cut = text.LastIndexOf('\n', MaxCharsPerRead - 1);
            if (cut > 0) // end at the last COMPLETE line
            {
                text = text[..cut];
                end = start + text.Count(c => c == '\n');
                notes.Add($"Output capped at {MaxCharsPerRead} chars; endLine is the last complete line.");
            }
            else // a single line longer than the cap: nothing complete to return
            {
                text = text[..MaxCharsPerRead];
                end = start;
                notes.Add($"Line {start} alone is longer than {MaxCharsPerRead} chars; it is cut mid-line.");
            }
            truncated = true;
        }
        int? next = end < total ? end + 1 : null;
        if (next != null && endLine == null) truncated = true; // default window stopped before EOF
        if (next != null && notes.Count == 0) notes.Add("More lines follow; pass startLine=nextStartLine.");
        return new LuaText(s.Key, l.WasBytecode, total, start, end, truncated, text,
            notes.Count == 0 ? null : string.Join(' ', notes), next);
    }

    // ---- analysis ----------------------------------------------------------------------------------------------

    public async Task<GoalList> ListGoalsAsync(string script, CancellationToken ct = default)
    {
        var (s, l) = await LoadAsync(script, ct);
        var all = GoalOutline.Build(l.Text);
        var page = all.Take(MaxItems).Select(ToDto).ToList();
        return new GoalList(s.Key, all.Count, page.Count, all.Count > page.Count, page);
    }

    private static GoalInfo ToDto(GoalFunction g) =>
        new(g.QualifiedName, g.Parameters, g.Line + 1, g.EndLine + 1, g.Group.ToString(), g.AddSubGoalCount, g.AnimIds.Take(40).ToList());

    private static readonly JsonSerializerOptions SizeJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Every <c>== S</c> arm in source order (<see cref="SpEffectInterrupts.Table.AllArms"/>), so two arms with the same id
    /// (Isshin has two <c>== 5032</c>) both show up and are flagged <c>duplicateId</c>. Output is paged by <paramref name="offset"/>
    /// (arm index) and capped at <paramref name="maxChars"/> serialized characters; at least one arm is always returned.
    /// </summary>
    public async Task<HandlerTable> GetSpEffectHandlersAsync(string script, int? spEffectId = null, bool includeBodies = false,
        CancellationToken ct = default, int? offset = null, int? maxChars = null)
    {
        var (s, l) = await LoadAsync(script, ct);
        int off = offset ?? 0;
        if (off < 0) throw new ArgumentException($"offset must be >= 0 (got {off}).");
        int budget = maxChars ?? DefaultHandlerChars;
        if (budget < 1000) throw new ArgumentException($"maxChars must be >= 1000 (got {budget}).");
        var table = SpEffectInterrupts.Build(l.Text);
        var counts = table.AllArms.GroupBy(h => h.SpEffectId).ToDictionary(g => g.Key, g => g.Count());
        var all = table.AllArms.Where(h => spEffectId == null || h.SpEffectId == spEffectId).ToList();
        if (off > all.Count) throw new ArgumentException($"offset {off} is beyond the {all.Count} matching arms.");
        bool bodies = includeBodies || spEffectId != null; // full bodies are big; default is a summary

        var ids = table.ObservedTargets.Keys.Concat(table.Observed.Keys).Distinct().OrderBy(x => x);
        // The observed list is the same on every page: it is returned (and counted against maxChars) on the first page only.
        var observed = off > 0 ? [] : ids.Select(id => new ObservedInfo(id, ComboDiagnostics.ObservedOn(table, id), table.Handlers.ContainsKey(id))).ToList();
        var handlers = new List<HandlerInfo>();
        int used = off > 0 ? 0 : JsonSerializer.Serialize(observed, SizeJson).Length, idx = off;
        for (; idx < all.Count && handlers.Count < MaxItems; idx++)
        {
            var h = all[idx];
            var info = new HandlerInfo(h.SpEffectId, h.Line + 1, h.EndLine + 1, NormalizeCondition(h.Condition),
                h.Chance, h.ClearsQueue, h.Replans, h.DependsOnDistance, h.NonRandomCondition,
                ComboDiagnostics.ObservedOn(table, h.SpEffectId), AnimsOf(h.Body), bodies ? ConvertNodes(h.Body) : null,
                ClearModeOf(h.Body), counts[h.SpEffectId], counts[h.SpEffectId] > 1);
            int size = JsonSerializer.Serialize(info, SizeJson).Length;
            if (handlers.Count > 0 && used + size > budget) break;
            used += size;
            handlers.Add(info);
        }
        bool truncated = idx < all.Count;
        var dups = counts.Where(kv => kv.Value > 1 && (spEffectId == null || kv.Key == spEffectId)).Select(kv => kv.Key).OrderBy(x => x).ToList();
        var notes = new List<string>();
        if (truncated)
            notes.Add($"Truncated at {handlers.Count} of {all.Count} arms (maxChars={budget}). Pass offset={idx} for the next page, "
                + "or spEffectId=<id> to get one handler's full body."
                + (bodies ? " Drop includeBodies to get the compact summary." : ""));
        if (dups.Count > 0)
            notes.Add("duplicateIds lists SpEffect ids that have more than one arm (each arm is returned separately, with its own line); "
                + "Core's handler dictionary keeps only the first.");
        if (off > 0) notes.Add("observed is only returned with the first page (offset 0).");
        return new HandlerTable(s.Key, all.Count, truncated, handlers, observed, off, truncated ? idx : null, dups,
            notes.Count == 0 ? null : string.Join(' ', notes));
    }

    /// <summary>
    /// Combo tree of one goal. Pass <paramref name="spEffectId"/> to return just the arm(s) for that SpEffect inside the goal
    /// (the way to read a big <c>Goal.Interrupt</c>); output is capped at <paramref name="maxNodes"/> nodes (depth-first).
    /// </summary>
    public async Task<ComboSequenceResult> GetComboSequenceAsync(string script, string goal, CancellationToken ct = default,
        int? spEffectId = null, int? maxNodes = null)
    {
        var (s, l) = await LoadAsync(script, ct);
        string name = (goal ?? "").Trim();
        if (name.StartsWith("Goal.", StringComparison.OrdinalIgnoreCase)) name = name[5..];
        var goals = GoalOutline.Build(l.Text);
        var g = goals.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Goal '{goal}' not found in {s.FileName}. Use list_goals.");
        int cap = maxNodes ?? DefaultComboNodes;
        if (cap < 1) throw new ArgumentException($"maxNodes must be >= 1 (got {cap}).");
        var nodes = ComboSequence.Build(l.Text, g);
        int steps = CountSteps(nodes);
        IReadOnlyList<ComboNode> shown = nodes;
        string? note = null;
        if (spEffectId != null)
        {
            var arms = SpEffectInterrupts.Build(l.Text).AllArms
                .Where(h => h.SpEffectId == spEffectId && h.Line >= g.Line && h.Line <= g.EndLine).ToList();
            if (arms.Count == 0) throw new ArgumentException($"{g.QualifiedName} has no '== {spEffectId}' handler arm.");
            shown = arms.Select(h => (ComboNode)new ComboBranch(h.Line, [new ComboArm(h.Condition, h.Chance, h.Body, h.Line)])).ToList();
            steps = CountSteps(shown);
            note = $"Only the {arms.Count} arm(s) for SpEffect {spEffectId} are shown (each as a one-arm branch).";
        }
        int budget = cap;
        bool truncated = false;
        var dto = ConvertNodes(shown, ref budget, ref truncated);
        if (truncated)
            note = (note == null ? "" : note + " ") + $"Truncated at maxNodes={cap} (depth-first)."
                + (spEffectId == null ? " For a big goal such as Goal.Interrupt pass spEffectId=<id> to get one handler arm, or raise maxNodes." : " Raise maxNodes for more.");
        return new ComboSequenceResult(s.Key, g.QualifiedName, g.Line + 1, g.EndLine + 1, dto, steps, truncated, cap - budget, note);
    }

    public async Task<DiagnosticsResult> DiagnoseComboAsync(string script, CancellationToken ct = default)
    {
        var (s, l) = await LoadAsync(script, ct);
        var tokens = LuaLexer.Tokenize(l.Text);
        var goals = GoalOutline.Build(l.Text, tokens);
        var table = SpEffectInterrupts.Build(l.Text, tokens, goals);
        var f = new List<Finding>();

        var notObserved = new HashSet<int>();
        foreach (var h in table.AllArms) // every arm, duplicates included
        {
            if (ComboDiagnostics.ObservedOn(table, h.SpEffectId).Count == 0 && notObserved.Add(h.SpEffectId))
                f.Add(new("Warning", "handler-not-observed", h.Line + 1,
                    $"Handler for SpEffect {h.SpEffectId} but no AddObserveSpecialEffectAttribute registers it; Goal.Interrupt never sees it."));
            CheckBranches(h.Body, $"handler {h.SpEffectId}", f);
            if (!h.Replans)
            {
                string mode = ClearModeOf(h.Body);
                if (mode == "none")
                    f.Add(new("Info", "handler-no-clear", h.Line + 1,
                        $"Handler for SpEffect {h.SpEffectId} adds subgoals without ClearSubGoal(): they are appended to the running combo, not replacing it."));
                else if (mode == "some")
                    f.Add(new("Info", "handler-partial-clear", h.Line + 1,
                        $"Handler for SpEffect {h.SpEffectId}: only some branches clear the queue (ClearSubGoal) before adding subgoals; the others append to the running combo."));
            }
        }
        foreach (var g in table.AllArms.GroupBy(a => a.SpEffectId).Where(g => g.Count() > 1))
            f.Add(new("Info", "duplicate-handler", g.First().Line + 1,
                $"SpEffect {g.Key} has {g.Count()} handler arms (lines {string.Join(", ", g.Select(a => a.Line + 1))}); in an if/elseif chain only the first matching arm runs."));
        foreach (var id in table.ObservedTargets.Keys.Concat(table.Observed.Keys).Distinct().OrderBy(x => x))
            if (!table.Handlers.ContainsKey(id))
                f.Add(new("Info", "observed-no-handler", 0, $"SpEffect {id} is observed but no interrupt handler compares to it (may be handled elsewhere)."));
        foreach (var g in goals.Where(g => g.AddSubGoalCount > 0))
            CheckBranches(ComboSequence.Build(l.Text, tokens, g), g.QualifiedName, f);

        int total = f.Count;
        var page = f.Take(MaxItems).ToList();
        return new DiagnosticsResult(s.Key, total, total > page.Count, page,
            "Static analysis of this Lua file only: TAE/param sources of SpEffects are not consulted, so 'no source' cannot be concluded. Findings are hints, not errors.");
    }

    private static void CheckBranches(IReadOnlyList<ComboNode> nodes, string where, List<Finding> f)
    {
        foreach (var b in ComboSequence.Branches(nodes))
        {
            var explicitArms = b.Arms.Where(a => a.Line >= 0).ToList();
            if (explicitArms.Count == 0 || !explicitArms.All(a => a.Probability != null || a.Condition == "else")) continue; // not a pure random chain
            double sum = explicitArms.Sum(a => a.Probability ?? 0);
            bool hasElse = explicitArms.Any(a => a.Condition == "else");
            if (!hasElse && sum > 100.5)
                f.Add(new("Warning", "random-overlap", b.Line + 1, $"{where}: random arm probabilities add up to {sum:0.#}% (overlap)."));
            else if (!hasElse && sum < 99.5)
                f.Add(new("Info", "random-gap", b.Line + 1, $"{where}: random thresholds cover {sum:0.#}% of 1..100; {100 - sum:0.#}% takes no branch."));
        }
    }

    // ---- conversion --------------------------------------------------------------------------------------------

    private static IReadOnlyList<ComboNodeDto> ConvertNodes(IReadOnlyList<ComboNode> nodes)
    {
        int budget = int.MaxValue; bool t = false;
        return ConvertNodes(nodes, ref budget, ref t);
    }

    /// <summary>Depth-first conversion that stops once <paramref name="budget"/> nodes were emitted (sets <paramref name="truncated"/>).</summary>
    private static IReadOnlyList<ComboNodeDto> ConvertNodes(IReadOnlyList<ComboNode> nodes, ref int budget, ref bool truncated)
    {
        var r = new List<ComboNodeDto>();
        foreach (var n in nodes)
        {
            if (budget <= 0) { truncated = true; break; }
            budget--;
            switch (n)
            {
                case ComboStep st: r.Add(new ComboNodeDto("step", st.Line + 1, st.AnimId, st.GoalType)); break;
                case ComboClear c: r.Add(new ComboNodeDto("clear", c.Line + 1)); break;
                case ComboBranch b:
                    var arms = new List<ComboArmDto>();
                    foreach (var a in b.Arms)
                        arms.Add(new ComboArmDto(NormalizeCondition(a.Condition), a.Probability, a.Line < 0 ? 0 : a.Line + 1, ConvertNodes(a.Body, ref budget, ref truncated)));
                    r.Add(new ComboNodeDto("branch", b.Line + 1, Arms: arms));
                    break;
                default: r.Add(new ComboNodeDto("unknown", n.Line + 1)); break;
            }
        }
        return r;
    }

    /// <summary>
    /// "all" = every subgoal is preceded by a ClearSubGoal on its path; "none" = steps but no clear anywhere;
    /// "some" = a clear exists but at least one path adds a subgoal without it; "n/a" = no steps.
    /// </summary>
    public static string ClearModeOf(IReadOnlyList<ComboNode> body)
    {
        bool anyStep = false, anyUncleared = false, anyClear = false;
        bool Walk(IReadOnlyList<ComboNode> nodes, bool cleared)
        {
            foreach (var n in nodes)
            {
                if (n is ComboClear) { cleared = true; anyClear = true; }
                else if (n is ComboStep) { anyStep = true; if (!cleared) anyUncleared = true; }
                else if (n is ComboBranch b)
                {
                    bool all = true; // after the branch: cleared only if every arm (incl. the implicit empty one) cleared
                    foreach (var a in b.Arms) all &= Walk(a.Body, cleared);
                    cleared = all;
                }
            }
            return cleared;
        }
        Walk(body, false);
        if (!anyStep) return "n/a";
        if (!anyUncleared) return "all";
        return anyClear ? "some" : "none";
    }

    private static readonly Regex SpacedColon = new(@"\s*:\s*", RegexOptions.Compiled);
    private static readonly Regex SpacedDot = new(@"(?<=[A-Za-z0-9_\)])\s*\.(?!\.)\s*(?=[A-Za-z_])", RegexOptions.Compiled);

    /// <summary>Display-only: the lexer joins tokens with spaces (<c>arg1 : GetNinsatsuNum ( ) &lt;= 1</c>); tidy that to <c>arg1:GetNinsatsuNum() &lt;= 1</c>.</summary>
    public static string NormalizeCondition(string c)
    {
        if (string.IsNullOrEmpty(c)) return c;
        c = SpacedColon.Replace(c, ":");
        c = SpacedDot.Replace(c, ".");
        c = Regex.Replace(c, @"\(\s+", "(");
        c = Regex.Replace(c, @"\s+\)", ")");
        c = Regex.Replace(c, @"\s+,", ",");
        c = Regex.Replace(c, @"(?<=[A-Za-z0-9_\]])\s+\(", "("); // call: name (  -> name(
        return c;
    }

    private static List<int> AnimsOf(IReadOnlyList<ComboNode> nodes)
    {
        var r = new List<int>();
        void Walk(IReadOnlyList<ComboNode> l)
        {
            foreach (var n in l)
            {
                if (n is ComboStep s) { if (!r.Contains(s.AnimId)) r.Add(s.AnimId); }
                else if (n is ComboBranch b) foreach (var a in b.Arms) Walk(a.Body);
            }
        }
        Walk(nodes);
        return r;
    }

    private static int CountSteps(IReadOnlyList<ComboNode> nodes) =>
        nodes.Sum(n => n switch
        {
            ComboStep => 1,
            ComboBranch b => b.Arms.Sum(a => CountSteps(a.Body)),
            _ => 0,
        });

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
    }
}
