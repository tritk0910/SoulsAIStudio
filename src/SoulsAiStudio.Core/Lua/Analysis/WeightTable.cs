using System.Globalization;
using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

// ------------------------------------------------------------------ model

/// <summary>What a condition compares that the app can compute from the scenario sliders.</summary>
public enum WeightVarKind { Distance, HpRate, Random, Selector }

/// <summary>A boolean condition of an <c>if</c> arm, parsed far enough to evaluate it for a scenario.</summary>
public abstract record WCond;
public sealed record WCondOr(WCond A, WCond B) : WCond;
public sealed record WCondAnd(WCond A, WCond B) : WCond;
public sealed record WCondNot(WCond A) : WCond;
public sealed record WCondConst(bool Value) : WCond;
/// <summary><c>var op N</c> on a variable the scenario sets (<c>getDist &gt;= 3</c>, <c>sp == 200210</c>, <c>r &lt;= 50</c>).</summary>
/// <param name="ValueStart">Source offset of the literal (for editing <c>sp == N</c>); -1 when the literal is on the left.</param>
public sealed record WCondCompare(WeightVarKind Kind, string Var, string Op, double Value, int ValueStart = -1, int ValueLength = 0, int ValueLine = 0, int ValueColumn = 0) : WCond;
/// <summary>Anything else (<c>GetNumber(5) &gt;= 60</c>, <c>HasSpecialEffectId(...)</c>): a user toggle, false unless ticked.</summary>
public sealed record WCondAtom(string Key) : WCond;

/// <summary>A statement of a weight-table function the evaluator cares about.</summary>
public abstract record WNode(int Line);

/// <summary>
/// <c>table[Slot] = value</c>. <see cref="Literal"/> is set for a plain number (editable); <see cref="Cooldowns"/> for
/// <c>SetCoolTime(ai, goal, anim, sec, table[Slot], min)</c> (weight drops to <c>min</c> for <c>sec</c> after <c>anim</c>).
/// Other expressions keep the previous weight.
/// </summary>
public sealed record WAssign(int Line, int Slot, double? Literal, int ValueStart, int ValueLength, int ValueColumn, (int Anim, double Seconds)? Cooldown) : WNode(Line);

public sealed record WReturn(int Line) : WNode(Line);

/// <summary>One arm of an if chain. <see cref="Cond"/> is null for <c>else</c>.</summary>
/// <param name="Line">Line of the <c>if</c>/<c>elseif</c>/<c>else</c> keyword.</param>
/// <param name="HeaderEndLine">Line of the arm's <c>then</c> (or of <c>else</c>): new statements go below it.</param>
/// <param name="Text">Condition source text ("else" for the else arm).</param>
public sealed record WArm(WCond? Cond, string Text, int Line, int HeaderEndLine, IReadOnlyList<WNode> Body);

/// <summary>An if/elseif/else chain. <see cref="CloseLine"/> = line of the <c>else</c> keyword when there is one, else of <c>end</c>.</summary>
public sealed record WIf(int Line, IReadOnlyList<WArm> Arms, int CloseLine, int Indent) : WNode(Line);

/// <summary><c>funcs[Slot] = REGIST_FUNC(ai, goal, self.Function)</c>.</summary>
public sealed record WRegistration(int Slot, string Function, int Line);

/// <summary>A condition the app cannot compute (shown as a checkbox). <see cref="Hint"/> explains locals (<c>x = ai:GetSpRate(...)</c>).</summary>
public sealed record WAtomInfo(string Key, int Line, string? Hint);

/// <summary>A row of the evaluated table: the weight a slot ends up with and the statement that set it.</summary>
/// <param name="Chance">0–100: chance this slot is picked (averaged over the random rolls).</param>
public sealed record WeightRow(int Slot, double Weight, WAssign? Source, double Chance, string? Function, IReadOnlyList<(int Anim, double Seconds)> Cooldowns);

/// <param name="CaseArm">Innermost arm taken for every random roll: where "Add" inserts a new weight (null = none).</param>
/// <param name="CasePath">Arms taken from the top (for the "Case:" breadcrumb).</param>
public sealed record WeightEvaluation(IReadOnlyList<WeightRow> Rows, double TotalWeight, WArm? CaseArm, IReadOnlyList<WArm> CasePath, bool Returned);

/// <summary>The values the user sets in the panel.</summary>
public sealed class WeightScenario
{
    public double Distance { get; set; } = 2;
    public double HpRate { get; set; } = 1;
    public double? Selector { get; set; }
    public HashSet<string> TrueAtoms { get; } = new(StringComparer.Ordinal);
}

/// <summary>A text replacement in 0-based line/column coordinates (columns = UTF-16 index in the line).</summary>
public sealed record TextEdit(int StartLine, int StartColumn, int EndLine, int EndColumn, string Text);

/// <summary>
/// The weight table of <c>Goal.Activate</c> / <c>Goal.Kengeki_Activate</c> (CLAUDE.md P1 #9 "Act table view"): these
/// functions set <c>act[N] = weight</c> under if/elseif conditions (distance, the parry SpEffect returned by
/// <c>ReturnKengekiSpecialEffect</c>, HP, timers…), register <c>funcs[N] = REGIST_FUNC(..., self.ActNN)</c>, then
/// <c>Common_Battle_Activate</c> / <c>Common_Kengeki_Activate</c> picks one slot at random by weight. This class reads
/// that structure from tokens, evaluates it for a scenario (distance, HP, selector SpEffect, ticked conditions, every
/// random roll) and makes the text edits for changing a weight, adding/removing a slot and adding a selector case.
/// Not modelled: what <c>Common_*_Activate</c> in aicommon does on top (it is shown as such in the UI).
/// </summary>
public sealed class WeightTable
{
    public required GoalFunction Goal { get; init; }
    public required string TableVar { get; init; }
    public required string FuncVar { get; init; }
    /// <summary>Name of the aicommon call that picks the slot (<c>Common_Battle_Activate</c>…).</summary>
    public required string Picker { get; init; }
    /// <summary>Variable holding <c>ReturnKengekiSpecialEffect(ai)</c> (null in Goal.Activate).</summary>
    public string? SelectorVar { get; init; }
    public required IReadOnlyList<WNode> Body { get; init; }
    /// <summary>Slot → registered function (last registration wins, like Lua).</summary>
    public required IReadOnlyDictionary<int, WRegistration> Slots { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>Literals compared with <see cref="SelectorVar"/>, in source order (no 0).</summary>
    public required IReadOnlyList<int> SelectorValues { get; init; }
    public required IReadOnlyList<WAtomInfo> Atoms { get; init; }
    /// <summary>Distance thresholds used in conditions (sorted), for slider ticks.</summary>
    public required IReadOnlyList<double> DistanceThresholds { get; init; }
    public required bool UsesHp { get; init; }
    public required bool UsesRandom { get; init; }
    /// <summary>Random variables and their GetRandam_Int ranges.</summary>
    public required IReadOnlyDictionary<string, (int Min, int Max)> RandomVars { get; init; }

    public bool UsesDistance => DistanceThresholds.Count > 0;

    /// <summary>Weight tables of every function in the file that calls a <c>Common_*_Activate(ai, goal, table, funcs, ...)</c>.</summary>
    public static IReadOnlyList<WeightTable> BuildAll(string src)
    {
        var tokens = LuaLexer.Tokenize(src);
        var outline = GoalOutline.Build(src, tokens);
        var t = tokens.Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var result = new List<WeightTable>();
        foreach (var g in outline)
            if (Build(src, t, g) is { } wt) result.Add(wt);
        return result;
    }

    /// <summary>The weight table of <c>Goal.&lt;goalName&gt;</c> in the current text (outline rebuilt, so positions are fresh).</summary>
    public static WeightTable? BuildFor(string src, string goalName)
    {
        var tokens = LuaLexer.Tokenize(src);
        var goal = GoalOutline.Build(src, tokens).FirstOrDefault(g => g.Name == goalName);
        return goal == null ? null : Build(src, tokens.Where(x => x.Kind != LuaTokenKind.Comment).ToList(), goal);
    }

    public static WeightTable? Build(string src, GoalFunction goal)
    {
        var t = LuaLexer.Tokenize(src).Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        return Build(src, t, goal);
    }

    /// <param name="t">Tokens without comments.</param>
    public static WeightTable? Build(string src, List<LuaToken> t, GoalFunction goal)
    {
        int start = t.FindIndex(x => x.Line == goal.Line && x.Is(src, goal.Name));
        if (start < 0) return null;
        int end = start;
        while (end < t.Count && t[end].Line <= goal.EndLine) end++; // exclusive

        // The picker call: Common_<X>_Activate(ai, goal, table, funcs, ...)
        string? tableVar = null, funcVar = null, picker = null;
        for (int i = start; i < end - 1; i++)
        {
            if (t[i].Kind != LuaTokenKind.Identifier || !t[i + 1].Is(src, "(")) continue;
            var name = t[i].Text(src);
            if (!name.StartsWith("Common_") || !name.EndsWith("_Activate")) continue;
            var args = SplitArgs(src, t, i + 1);
            if (args.Count >= 4 && args[2].Count == 1 && args[3].Count == 1
                && t[args[2][0]].Kind == LuaTokenKind.Identifier && t[args[3][0]].Kind == LuaTokenKind.Identifier)
            {
                tableVar = t[args[2][0]].Text(src).ToString();
                funcVar = t[args[3][0]].Text(src).ToString();
                picker = name.ToString();
            }
        }
        if (tableVar == null || funcVar == null || picker == null) return null;

        // Locals the scenario sets, and initializer text of every other local (hints for the toggles).
        var vars = new Dictionary<string, WeightVarKind>(StringComparer.Ordinal);
        var randRanges = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        string? selector = null;
        for (int i = start; i < end - 2; i++)
        {
            if (t[i].Kind != LuaTokenKind.Identifier || !t[i + 1].Is(src, "=") || (i > 0 && (t[i - 1].Is(src, ".") || t[i - 1].Is(src, ":") || t[i - 1].Is(src, ",")))) continue;
            string name = t[i].Text(src).ToString();
            int v = i + 2;
            // obj : Method ( args )
            if (v + 3 < end && t[v].Kind == LuaTokenKind.Identifier && t[v + 1].Is(src, ":") && t[v + 3].Is(src, "("))
            {
                string method = t[v + 2].Text(src).ToString();
                var args = SplitArgs(src, t, v + 3);
                string a0 = args.Count > 0 ? ArgText(src, t, args[0]) : "";
                if (method == "GetDist" && a0 == "TARGET_ENE_0") vars[name] = WeightVarKind.Distance;
                else if (method == "GetHpRate" && a0 == "TARGET_SELF") vars[name] = WeightVarKind.HpRate;
                else if (method == "GetRandam_Int" && args.Count == 2 && TryInt(src, t, args[0], out int lo) && TryInt(src, t, args[1], out int hi) && hi >= lo)
                {
                    vars[name] = WeightVarKind.Random;
                    randRanges[name] = (lo, hi);
                }
            }
            // ReturnKengekiSpecialEffect ( ai )
            else if (t[v].Is(src, "ReturnKengekiSpecialEffect") && v + 1 < end && t[v + 1].Is(src, "("))
            {
                vars[name] = WeightVarKind.Selector;
                selector = name;
            }
            if (!vars.ContainsKey(name) && !hints.ContainsKey(name))
            {
                int k = v;
                while (k < end && t[k].Line == t[v].Line) k++;
                if (k > v) hints[name] = src[t[v].Start..(t[k - 1].Start + t[k - 1].Length)];
            }
        }

        // Body after the parameter list.
        int bodyStart = start;
        while (bodyStart < end && !t[bodyStart].Is(src, ")")) bodyStart++;
        bodyStart++;
        var parser = new Parser(src, t, end - 1, tableVar, vars, hints);
        int pos = bodyStart;
        var body = parser.Block(ref pos, ["end"]);

        // Registrations: funcs [ N ] = REGIST_FUNC ( a , b , x . Name )
        var slots = new Dictionary<int, WRegistration>();
        var warnings = new List<string>();
        for (int i = start; i + 6 < end; i++)
        {
            if (!t[i].Is(src, funcVar) || !t[i + 1].Is(src, "[") || t[i + 2].Kind != LuaTokenKind.Number || !t[i + 3].Is(src, "]")
                || !t[i + 4].Is(src, "=") || !t[i + 5].Is(src, "REGIST_FUNC") || !t[i + 6].Is(src, "(")) continue;
            if (!int.TryParse(t[i + 2].Text(src), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)) continue;
            var args = SplitArgs(src, t, i + 6);
            if (args.Count < 3) continue;
            var a = args[2];
            string fn = a.Count >= 3 && t[a[^2]].Is(src, ".") ? t[a[^1]].Text(src).ToString() : ArgText(src, t, a);
            if (slots.TryGetValue(slot, out var prev))
                warnings.Add($"{funcVar}[{slot}] is registered twice: {prev.Function} (line {prev.Line + 1}) is replaced by {fn} (line {t[i].Line + 1}).");
            slots[slot] = new WRegistration(slot, fn, t[i].Line);
        }

        var assigned = new SortedSet<int>();
        foreach (var a in AllAssigns(body)) if (a.Literal is > 0) assigned.Add(a.Slot);
        foreach (int s in assigned)
            if (!slots.ContainsKey(s)) warnings.Add($"{tableVar}[{s}] gets a weight but no function is registered for slot {s}.");

        var table = new WeightTable
        {
            Goal = goal,
            TableVar = tableVar,
            FuncVar = funcVar,
            Picker = picker,
            SelectorVar = selector,
            Body = body,
            Slots = slots,
            Warnings = warnings,
            SelectorValues = parser.SelectorValues.Where(v => v != 0).Distinct().ToList(),
            Atoms = parser.Atoms.Values.OrderBy(a => a.Line).ToList(),
            DistanceThresholds = parser.DistanceValues.Distinct().OrderBy(x => x).ToList(),
            UsesHp = parser.UsesHp,
            UsesRandom = parser.UsesRandom,
            RandomVars = randRanges,
        };
        return table;
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>
    /// Weights for a scenario. Random variables are enumerated (every roll of each <c>GetRandam_Int</c> used in a
    /// condition, up to 20000 combinations) and the chances averaged, so <c>if r &lt;= 80 then act[17] = 100</c> counts 80%.
    /// </summary>
    public WeightEvaluation Evaluate(WeightScenario s)
    {
        var rv = RandomVars.Where(kv => UsedRandom.Contains(kv.Key)).ToList();
        long combos = 1;
        var used = new List<KeyValuePair<string, (int Min, int Max)>>();
        foreach (var kv in rv)
        {
            long n = kv.Value.Max - kv.Value.Min + 1;
            if (combos * n > 20000) continue;
            combos *= n;
            used.Add(kv);
        }

        var chance = new Dictionary<(int Slot, WAssign? Src), double>();
        var weightOf = new Dictionary<(int Slot, WAssign? Src), double>();
        var cooldowns = new Dictionary<int, List<(int, double)>>();
        double totalSum = 0;
        List<WArm>? common = null;
        bool anyReturned = false;
        var rolls = new Dictionary<string, double>(StringComparer.Ordinal);

        for (long c = 0; c < combos; c++)
        {
            long rest = c;
            foreach (var kv in used)
            {
                long n = kv.Value.Max - kv.Value.Min + 1;
                rolls[kv.Key] = kv.Value.Min + rest % n;
                rest /= n;
            }
            var w = new Dictionary<int, (double W, WAssign? Src)>();
            var path = new List<WArm>();
            bool returned = Walk(Body, s, rolls, w, path, cooldowns);
            anyReturned |= returned;
            common = common == null ? path : common.Zip(path).TakeWhile(p => ReferenceEquals(p.First, p.Second)).Select(p => p.First).ToList();
            if (returned) continue;
            double sum = w.Values.Where(x => x.W > 0).Sum(x => x.W);
            totalSum += sum / combos;
            foreach (var (slot, (wt, src)) in w)
            {
                if (wt <= 0) continue;
                var key = (slot, src);
                weightOf[key] = wt;
                chance[key] = chance.GetValueOrDefault(key) + wt / sum * 100.0 / combos;
            }
        }

        var rows = chance.Select(kv => new WeightRow(kv.Key.Slot, weightOf[kv.Key], kv.Key.Src, kv.Value,
                Slots.TryGetValue(kv.Key.Slot, out var r) ? r.Function : null,
                cooldowns.TryGetValue(kv.Key.Slot, out var cd) ? cd.Distinct().ToList() : Array.Empty<(int, double)>()))
            .OrderBy(r => r.Slot).ThenBy(r => r.Source?.Line ?? -1).ToList();
        var casePath = common ?? new List<WArm>();
        var caseArm = casePath.LastOrDefault();
        return new WeightEvaluation(rows, totalSum, caseArm, casePath, anyReturned && rows.Count == 0);
    }

    private HashSet<string>? _usedRandom;
    private HashSet<string> UsedRandom => _usedRandom ??= CollectRandom(Body);

    private static HashSet<string> CollectRandom(IReadOnlyList<WNode> body)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void C(WCond? c)
        {
            switch (c)
            {
                case WCondOr o: C(o.A); C(o.B); break;
                case WCondAnd a: C(a.A); C(a.B); break;
                case WCondNot n: C(n.A); break;
                case WCondCompare { Kind: WeightVarKind.Random } cmp: set.Add(cmp.Var); break;
            }
        }
        void N(IReadOnlyList<WNode> nodes)
        {
            foreach (var n in nodes)
                if (n is WIf f) foreach (var a in f.Arms) { C(a.Cond); N(a.Body); }
        }
        N(body);
        return set;
    }

    /// <returns>True when a <c>return</c> was reached (the function stops; Goal.Activate's
    /// <c>if Kengeki_Activate() then return end</c> or Kengeki's <c>if sp == 0 then return false end</c>).</returns>
    private static bool Walk(IReadOnlyList<WNode> nodes, WeightScenario s, Dictionary<string, double> rolls,
        Dictionary<int, (double, WAssign?)> w, List<WArm> path, Dictionary<int, List<(int, double)>> cooldowns)
    {
        foreach (var n in nodes)
            switch (n)
            {
                case WAssign a:
                    if (a.Literal is double v) w[a.Slot] = (v, a);
                    else if (a.Cooldown is { } cd)
                    {
                        if (!cooldowns.TryGetValue(a.Slot, out var l)) cooldowns[a.Slot] = l = new();
                        l.Add(cd);
                    }
                    break;
                case WReturn:
                    return true;
                case WIf f:
                    foreach (var arm in f.Arms)
                        if (arm.Cond == null || Eval(arm.Cond, s, rolls))
                        {
                            path.Add(arm);
                            if (Walk(arm.Body, s, rolls, w, path, cooldowns)) return true;
                            break;
                        }
                    break;
            }
        return false;
    }

    public static bool Eval(WCond c, WeightScenario s, IReadOnlyDictionary<string, double>? rolls = null) => c switch
    {
        WCondOr o => Eval(o.A, s, rolls) || Eval(o.B, s, rolls),
        WCondAnd a => Eval(a.A, s, rolls) && Eval(a.B, s, rolls),
        WCondNot n => !Eval(n.A, s, rolls),
        WCondConst k => k.Value,
        WCondAtom at => s.TrueAtoms.Contains(at.Key),
        WCondCompare cmp => Compare(cmp.Kind switch
        {
            WeightVarKind.Distance => s.Distance,
            WeightVarKind.HpRate => s.HpRate,
            WeightVarKind.Selector => s.Selector ?? 0,
            _ => rolls != null && rolls.TryGetValue(cmp.Var, out double r) ? r : 1,
        }, cmp.Op, cmp.Value),
        _ => false,
    };

    private static bool Compare(double x, string op, double v) => op switch
    {
        "==" => Math.Abs(x - v) < 1e-9,
        "~=" => Math.Abs(x - v) >= 1e-9,
        "<" => x < v,
        "<=" => x <= v,
        ">" => x > v,
        ">=" => x >= v,
        _ => false,
    };

    public static IEnumerable<WAssign> AllAssigns(IReadOnlyList<WNode> nodes)
    {
        foreach (var n in nodes)
            if (n is WAssign a) yield return a;
            else if (n is WIf f)
                foreach (var arm in f.Arms)
                    foreach (var x in AllAssigns(arm.Body)) yield return x;
    }

    /// <summary>All if chains in document order.</summary>
    public static IEnumerable<WIf> AllIfs(IReadOnlyList<WNode> nodes)
    {
        foreach (var n in nodes)
            if (n is WIf f)
            {
                yield return f;
                foreach (var arm in f.Arms)
                    foreach (var x in AllIfs(arm.Body)) yield return x;
            }
    }

    /// <summary>The selector arm (<c>sp == N</c> alone) for a SpEffect id, and the chain it belongs to.</summary>
    public (WIf Chain, WArm Arm, WCondCompare Compare)? SelectorArm(int id)
    {
        foreach (var f in AllIfs(Body))
            foreach (var arm in f.Arms)
                if (SelectorCompare(arm) is { } c && (int)c.Value == id) return (f, arm, c);
        return null;
    }

    /// <summary>The if chain whose arms compare the selector (where a new SpEffect case goes).</summary>
    public WIf? SelectorChain() =>
        AllIfs(Body).Where(f => f.Arms.Count(a => SelectorCompare(a) is { Value: not 0 }) > 0)
            .OrderByDescending(f => f.Arms.Count(a => SelectorCompare(a) != null)).FirstOrDefault();

    /// <summary><c>sp == N</c> when that is the arm's whole condition.</summary>
    public static WCondCompare? SelectorCompare(WArm arm) =>
        arm.Cond is WCondCompare { Kind: WeightVarKind.Selector, Op: "==", ValueStart: >= 0 } c ? c : null;

    // ------------------------------------------------------------------ edits

    public static string FormatNumber(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Changes the literal of <c>table[N] = w</c>.</summary>
    public static TextEdit SetWeight(WAssign a, double weight) =>
        new(a.Line, a.ValueColumn, a.Line, a.ValueColumn + a.ValueLength, FormatNumber(weight));

    /// <summary>Changes <c>sp == N</c> to another SpEffect id.</summary>
    public static TextEdit SetSelectorValue(WCondCompare c, int id) =>
        new(c.ValueLine, c.ValueColumn, c.ValueLine, c.ValueColumn + c.ValueLength, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Removes <c>table[N] = w</c>: deletes the line when the statement is alone on it, else sets the weight to 0.
    /// </summary>
    public TextEdit RemoveAssign(string src, WAssign a)
    {
        var (lineStart, lineEnd) = LineSpan(src, a.Line);
        string compact = string.Concat(src[lineStart..lineEnd].Where(ch => !char.IsWhiteSpace(ch))).TrimEnd(';');
        if (a.Literal != null && compact == $"{TableVar}[{a.Slot}]={src.Substring(a.ValueStart, a.ValueLength)}")
            return new TextEdit(a.Line, 0, a.Line + 1, 0, "");
        return SetWeight(a, 0);
    }

    /// <summary>Inserts <c>table[slot] = w</c> in <paramref name="arm"/>, after its last direct weight (or right below its header).</summary>
    public TextEdit AddAssign(string src, WArm arm, int slot, double weight)
    {
        var lastDirect = arm.Body.OfType<WAssign>().Where(a => a.Literal != null).LastOrDefault();
        int afterLine = lastDirect?.Line ?? arm.HeaderEndLine;
        string indent = lastDirect != null ? IndentOf(src, lastDirect.Line) : IndentOf(src, arm.Line) + "    ";
        return new TextEdit(afterLine + 1, 0, afterLine + 1, 0, $"{indent}{TableVar}[{slot}] = {FormatNumber(weight)}\n");
    }

    /// <summary>
    /// Adds <c>elseif sp == id then table[slot] = w</c> to the selector chain, before its <c>else</c> (or <c>end</c>).
    /// Null when there is no selector chain or its closing keyword does not start its line.
    /// </summary>
    public TextEdit? AddSelectorCase(string src, int id, int slot, double weight)
    {
        if (SelectorVar == null || SelectorChain() is not { } chain) return null;
        var (ls, le) = LineSpan(src, chain.CloseLine);
        string closeLine = src[ls..le].TrimStart();
        if (!closeLine.StartsWith("else", StringComparison.Ordinal) && !closeLine.StartsWith("end", StringComparison.Ordinal)) return null;
        string indent = IndentOf(src, chain.Line);
        string text = $"{indent}elseif {SelectorVar} == {id.ToString(CultureInfo.InvariantCulture)} then\n{indent}    {TableVar}[{slot}] = {FormatNumber(weight)}\n";
        return new TextEdit(chain.CloseLine, 0, chain.CloseLine, 0, text);
    }

    /// <summary>
    /// Weight that gives <paramref name="row"/> a chance of <paramref name="percent"/> when the other weights stay
    /// (w / (w + others) = p). Null for 100% or more (would need the others at 0).
    /// </summary>
    public static double? WeightForChance(WeightEvaluation e, WeightRow row, double percent)
    {
        if (percent >= 100 || percent < 0) return null;
        double others = e.Rows.Where(r => r != row).Sum(r => r.Weight);
        if (others <= 0) return null;
        return Math.Round(percent / (100 - percent) * others);
    }

    private static string IndentOf(string src, int line)
    {
        var (s, e) = LineSpan(src, line);
        int i = s;
        while (i < e && (src[i] == ' ' || src[i] == '\t')) i++;
        return src[s..i];
    }

    /// <summary>Offsets of a 0-based line (end excludes the '\n').</summary>
    private static (int Start, int End) LineSpan(string src, int line)
    {
        int s = 0;
        for (int l = 0; l < line && s >= 0; l++)
        {
            s = src.IndexOf('\n', s);
            if (s >= 0) s++;
        }
        if (s < 0) s = src.Length;
        int e = src.IndexOf('\n', s);
        return (s, e < 0 ? src.Length : e);
    }

    // ------------------------------------------------------------------ token helpers

    /// <summary>Token index lists of the call arguments; <paramref name="open"/> is the index of '('.</summary>
    private static List<List<int>> SplitArgs(string src, List<LuaToken> t, int open)
    {
        var args = new List<List<int>>();
        var cur = new List<int>();
        int depth = 0;
        for (int i = open + 1; i < t.Count; i++)
        {
            if (t[i].Kind == LuaTokenKind.Operator)
            {
                var s = t[i].Text(src);
                if (s is "(" or "{" or "[") depth++;
                else if (s is ")" or "}" or "]")
                {
                    if (depth == 0) { if (cur.Count > 0) args.Add(cur); return args; }
                    depth--;
                }
                else if (s is "," && depth == 0) { args.Add(cur); cur = new(); continue; }
            }
            cur.Add(i);
        }
        return args;
    }

    private static string ArgText(string src, List<LuaToken> t, List<int> arg) =>
        arg.Count == 0 ? "" : src[t[arg[0]].Start..(t[arg[^1]].Start + t[arg[^1]].Length)];

    private static bool TryInt(string src, List<LuaToken> t, List<int> arg, out int v)
    {
        v = 0;
        string s = string.Concat(arg.Select(i => t[i].Text(src).ToString()));
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser(string src, List<LuaToken> t, int last, string tableVar,
        Dictionary<string, WeightVarKind> vars, Dictionary<string, string> hints)
    {
        public readonly List<int> SelectorValues = new();
        public readonly List<double> DistanceValues = new();
        public readonly Dictionary<string, WAtomInfo> Atoms = new(StringComparer.Ordinal);
        public bool UsesHp, UsesRandom;

        private bool Is(int i, string s) => i <= last && t[i].Is(src, s);
        private bool IsKw(int i, string s) => i <= last && t[i].Kind == LuaTokenKind.Keyword && t[i].Is(src, s);

        public List<WNode> Block(ref int i, string[] stopAt)
        {
            var nodes = new List<WNode>();
            while (i <= last && t[i].Kind != LuaTokenKind.EndOfFile)
            {
                var tok = t[i];
                if (tok.Kind == LuaTokenKind.Keyword && stopAt.Any(s => tok.Is(src, s))) return nodes;
                if (IsKw(i, "if")) { nodes.Add(If(ref i)); continue; }
                if (IsKw(i, "function") || IsKw(i, "while") || IsKw(i, "for") || IsKw(i, "repeat") || IsKw(i, "do"))
                {
                    SkipBlock(ref i);
                    continue;
                }
                if (IsKw(i, "return"))
                {
                    // "return Common_Kengeki_Activate(...)" is the pick itself, not an early exit.
                    bool pick = i + 1 <= last && t[i + 1].Kind == LuaTokenKind.Identifier
                                && t[i + 1].Text(src).StartsWith("Common_") && t[i + 1].Text(src).EndsWith("_Activate");
                    if (!pick) nodes.Add(new WReturn(tok.Line));
                    i++;
                    continue;
                }
                if (TryAssign(ref i, out var a)) { nodes.Add(a); continue; }
                i++;
            }
            return nodes;
        }

        /// <summary><c>table [ N ] = value</c></summary>
        private bool TryAssign(ref int i, out WAssign a)
        {
            a = null!;
            if (!Is(i, tableVar) || !Is(i + 1, "[") || i + 5 > last || t[i + 2].Kind != LuaTokenKind.Number || !Is(i + 3, "]") || !Is(i + 4, "=")) return false;
            if (i > 0 && (t[i - 1].Is(src, ".") || t[i - 1].Is(src, ":"))) return false;
            if (!int.TryParse(t[i + 2].Text(src), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)) return false;
            int v = i + 5;
            var vt = t[v];
            // plain number, alone (not "60 - 3")
            if (vt.Kind == LuaTokenKind.Number && !(v + 1 <= last && t[v + 1].Line == vt.Line && t[v + 1].Text(src) is "+" or "-" or "*" or "/" or "%" or "^" or ".."))
            {
                double.TryParse(vt.Text(src), NumberStyles.Float, CultureInfo.InvariantCulture, out double lit);
                a = new WAssign(t[i].Line, slot, lit, vt.Start, vt.Length, vt.Column, null);
                i = v + 1;
                return true;
            }
            if (vt.Is(src, "SetCoolTime") && Is(v + 1, "("))
            {
                var args = SplitArgs(src, t, v + 1);
                (int, double)? cd = null;
                if (args.Count >= 4 && TryInt(src, t, args[2], out int anim)
                    && double.TryParse(ArgText(src, t, args[3]), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                    cd = (anim, sec);
                a = new WAssign(t[i].Line, slot, null, vt.Start, vt.Length, vt.Column, cd);
                i = v + 1;
                return true;
            }
            a = new WAssign(t[i].Line, slot, null, vt.Start, vt.Length, vt.Column, null);
            i = v + 1;
            return true;
        }

        private WIf If(ref int i)
        {
            int line = t[i].Line, indent = t[i].Column;
            var arms = new List<WArm>();
            int armLine = line;
            i++; // 'if'
            int closeLine;
            while (true)
            {
                int condStart = i;
                while (i <= last && !IsKw(i, "then")) i++;
                int thenLine = i <= last ? t[i].Line : armLine;
                var cond = ParseCondition(condStart, i);
                string text = condStart < i ? src[t[condStart].Start..(t[i - 1].Start + t[i - 1].Length)] : "";
                i++; // 'then'
                var body = Block(ref i, ["elseif", "else", "end"]);
                arms.Add(new WArm(cond, text, armLine, thenLine, body));
                if (IsKw(i, "elseif")) { armLine = t[i].Line; i++; continue; }
                if (IsKw(i, "else"))
                {
                    int elseLine = t[i].Line;
                    closeLine = elseLine;
                    i++;
                    arms.Add(new WArm(null, "else", elseLine, elseLine, Block(ref i, ["end"])));
                    i++; // 'end'
                    break;
                }
                closeLine = i <= last ? t[i].Line : thenLine;
                i++; // 'end'
                break;
            }
            return new WIf(line, arms, closeLine, indent);
        }

        private void SkipBlock(ref int i)
        {
            int depth = 0, pendingLoopDo = 0;
            for (; i <= last; i++)
            {
                if (t[i].Kind != LuaTokenKind.Keyword) continue;
                var s = t[i].Text(src);
                if (s is "while" or "for") { depth++; pendingLoopDo++; }
                else if (s is "function" or "if" or "repeat") depth++;
                else if (s is "do") { if (pendingLoopDo > 0) pendingLoopDo--; else depth++; }
                else if (s is "end" or "until")
                {
                    if (--depth == 0) { i++; return; }
                }
            }
        }

        // ---- conditions: or → and → not → comparison → operand

        private WCond ParseCondition(int from, int to)
        {
            if (from >= to) return new WCondConst(false);
            int p = from;
            var c = Or(ref p, to);
            return p == to ? c : AtomOf(from, to); // leftovers: treat the whole thing as one toggle
        }

        private WCond Or(ref int p, int to)
        {
            var a = And(ref p, to);
            while (p < to && IsKw(p, "or")) { p++; a = new WCondOr(a, And(ref p, to)); }
            return a;
        }

        private WCond And(ref int p, int to)
        {
            var a = Not(ref p, to);
            while (p < to && IsKw(p, "and")) { p++; a = new WCondAnd(a, Not(ref p, to)); }
            return a;
        }

        private WCond Not(ref int p, int to)
        {
            if (p < to && IsKw(p, "not")) { p++; return new WCondNot(Not(ref p, to)); }
            return Comparison(ref p, to);
        }

        private static bool IsRelOp(string s) => s is "==" or "~=" or "<" or "<=" or ">" or ">=";

        private WCond Comparison(ref int p, int to)
        {
            int start = p;
            // "( boolean )" not followed by a comparison
            if (p < to && Is(p, "("))
            {
                int close = Matching(p, to);
                if (close > p && (close + 1 >= to || !IsRelOp(t[close + 1].Text(src).ToString())))
                {
                    int q = p + 1;
                    var inner = Or(ref q, close);
                    p = close + 1;
                    return q == close ? inner : AtomOf(start, p);
                }
            }
            int l0 = p;
            Operand(ref p, to);
            int l1 = p;
            if (p < to && t[p].Kind == LuaTokenKind.Operator && IsRelOp(t[p].Text(src).ToString()))
            {
                string op = t[p].Text(src).ToString();
                p++;
                int r0 = p;
                Operand(ref p, to);
                int r1 = p;
                return Compare(l0, l1, op, r0, r1, start, p);
            }
            if (l1 - l0 == 1 && t[l0].Kind == LuaTokenKind.Keyword)
            {
                if (t[l0].Is(src, "true")) return new WCondConst(true);
                if (t[l0].Is(src, "false") || t[l0].Is(src, "nil")) return new WCondConst(false);
            }
            return AtomOf(l0, l1);
        }

        private WCond Compare(int l0, int l1, string op, int r0, int r1, int whole0, int whole1)
        {
            if (Var(l0, l1) is { } lv && Number(r0, r1) is { } rn)
                return Known(lv, op, rn.Value, rn.Tok);
            if (Number(l0, l1) is { } ln && Var(r0, r1) is { } rv)
                return Known(rv, Flip(op), ln.Value, -1);
            return AtomOf(whole0, whole1);
        }

        private WCond Known((WeightVarKind Kind, string Name) v, string op, double value, int tok)
        {
            switch (v.Kind)
            {
                case WeightVarKind.Distance: DistanceValues.Add(value); break;
                case WeightVarKind.HpRate: UsesHp = true; break;
                case WeightVarKind.Random: UsesRandom = true; break;
                case WeightVarKind.Selector: SelectorValues.Add((int)value); break;
            }
            return tok >= 0
                ? new WCondCompare(v.Kind, v.Name, op, value, t[tok].Start, t[tok].Length, t[tok].Line, t[tok].Column)
                : new WCondCompare(v.Kind, v.Name, op, value);
        }

        private static string Flip(string op) => op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };

        /// <summary>A local the scenario sets, or an inline <c>ai:GetDist(TARGET_ENE_0)</c> / <c>ai:GetHpRate(TARGET_SELF)</c>.</summary>
        private (WeightVarKind, string)? Var(int a, int b)
        {
            if (b - a == 1 && t[a].Kind == LuaTokenKind.Identifier && vars.TryGetValue(t[a].Text(src).ToString(), out var k))
                return (k, t[a].Text(src).ToString());
            if (b - a == 6 && t[a].Kind == LuaTokenKind.Identifier && Is(a + 1, ":") && Is(a + 3, "(") && Is(a + 5, ")"))
            {
                if (Is(a + 2, "GetDist") && Is(a + 4, "TARGET_ENE_0")) return (WeightVarKind.Distance, "GetDist");
                if (Is(a + 2, "GetHpRate") && Is(a + 4, "TARGET_SELF")) return (WeightVarKind.HpRate, "GetHpRate");
            }
            return null;
        }

        /// <summary>A number literal (optionally negative); Tok = index of the number token.</summary>
        private (double Value, int Tok)? Number(int a, int b)
        {
            bool neg = b - a == 2 && Is(a, "-");
            int n = neg ? a + 1 : a;
            if (b - n != 1 || t[n].Kind != LuaTokenKind.Number) return null;
            if (!double.TryParse(t[n].Text(src), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
            return (neg ? -v : v, neg ? -1 : n);
        }

        /// <summary>Skips an operand: everything up to and/or/then/a comparison/an unmatched ')' at depth 0.</summary>
        private void Operand(ref int p, int to)
        {
            int depth = 0;
            for (; p < to; p++)
            {
                var tok = t[p];
                if (tok.Kind == LuaTokenKind.Keyword && depth == 0 && (tok.Is(src, "and") || tok.Is(src, "or"))) return;
                if (tok.Kind != LuaTokenKind.Operator) continue;
                var s = tok.Text(src).ToString();
                if (s is "(" or "[" or "{") depth++;
                else if (s is ")" or "]" or "}") { if (depth == 0) return; depth--; }
                else if (depth == 0 && IsRelOp(s)) return;
            }
        }

        private int Matching(int open, int to)
        {
            int depth = 0;
            for (int k = open; k < to; k++)
            {
                if (t[k].Is(src, "(")) depth++;
                else if (t[k].Is(src, ")") && --depth == 0) return k;
            }
            return -1;
        }

        private WCond AtomOf(int a, int b)
        {
            if (b <= a) return new WCondConst(false);
            string text = string.Join(" ", src[t[a].Start..(t[b - 1].Start + t[b - 1].Length)]
                .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            if (!Atoms.ContainsKey(text))
            {
                // Hint: what the locals in it are ("f2_local9 = f2_arg1:GetSpRate(TARGET_SELF)").
                var locals = Enumerable.Range(a, b - a).Where(k => t[k].Kind == LuaTokenKind.Identifier)
                    .Select(k => t[k].Text(src).ToString()).Distinct()
                    .Where(hints.ContainsKey).Select(n => $"{n} = {hints[n]}").ToList();
                Atoms[text] = new WAtomInfo(text, t[a].Line, locals.Count > 0 ? string.Join("\n", locals) : null);
            }
            return new WCondAtom(text);
        }
    }
}

/// <summary>Applies <see cref="TextEdit"/>s to a string (tests and previews; the editor applies them with undo).</summary>
public static class TextEdits
{
    public static string Apply(string src, TextEdit e)
    {
        int a = Offset(src, e.StartLine, e.StartColumn), b = Offset(src, e.EndLine, e.EndColumn);
        return src[..a] + e.Text + src[b..];
    }

    public static int Offset(string src, int line, int column)
    {
        int s = 0;
        for (int l = 0; l < line; l++)
        {
            int n = src.IndexOf('\n', s);
            if (n < 0) return src.Length;
            s = n + 1;
        }
        return Math.Min(src.Length, s + column);
    }
}
