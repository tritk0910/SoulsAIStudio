using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>A node in a goal's combo sequence: either an anim step or a branch point.</summary>
public abstract record ComboNode(int Line);

/// <summary><c>:AddSubGoal(GOAL_X, time, animId, ...)</c> with a literal anim id.</summary>
public sealed record ComboStep(int Line, int AnimId, string GoalType) : ComboNode(Line);

/// <summary>Clears the queued subgoals (<c>:ClearSubGoal()</c>) — later steps replace earlier ones.</summary>
public sealed record ComboClear(int Line) : ComboNode(Line);

/// <summary>One arm of an if/elseif/else chain.</summary>
/// <param name="Condition">Source text of the condition ("else" for the else arm).</param>
/// <param name="Probability">0–100 when the arm is decided by a random roll (<c>r &lt;= N</c>), else null.</param>
/// <param name="Line">0-based line of the arm's <c>if</c> / <c>elseif</c> / <c>else</c> keyword (-1 for the implicit empty arm).</param>
public sealed record ComboArm(string Condition, double? Probability, IReadOnlyList<ComboNode> Body, int Line = -1);

/// <summary>An if/elseif/else chain. Without an else arm, an implicit empty arm is added ("none of the above").</summary>
public sealed record ComboBranch(int Line, IReadOnlyList<ComboArm> Arms) : ComboNode(Line);

/// <summary>
/// Turns a goal function's body into a tree of <see cref="ComboStep"/>s and <see cref="ComboBranch"/>es, so a combo can
/// be played along a chosen path (CLAUDE.md P2). Random arms get probabilities from thresholds on variables assigned
/// from <c>GetRandam_Int(1, 100)</c> (e.g. <c>if r &lt;= 25 … elseif r &lt;= 50 …</c> → 25 / 25 / …).
/// Loops and nested functions are skipped; non-literal anim ids are ignored.
/// </summary>
public static class ComboSequence
{
    public static IReadOnlyList<ComboNode> Build(string src, GoalFunction goal) =>
        Build(src, LuaLexer.Tokenize(src), goal);

    public static IReadOnlyList<ComboNode> Build(string src, IReadOnlyList<LuaToken> all, GoalFunction goal)
    {
        var t = all.Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var randomVars = FindRandomVars(src, t);
        int start = t.FindIndex(x => x.Line == goal.Line && x.Is(src, goal.Name));
        if (start < 0) return [];
        int i = start;
        while (i < t.Count && !t[i].Is(src, ")")) i++; // end of parameter list
        i++;
        var parser = new Parser(src, t, randomVars);
        return parser.Block(ref i, stopAt: ["end"]);
    }

    /// <summary>Flattens a path: <paramref name="choose"/> picks the arm index for each branch.</summary>
    public static List<ComboStep> Flatten(IReadOnlyList<ComboNode> nodes, Func<ComboBranch, int> choose)
    {
        var result = new List<ComboStep>();
        void Walk(IReadOnlyList<ComboNode> list)
        {
            foreach (var n in list)
                switch (n)
                {
                    case ComboStep s: result.Add(s); break;
                    case ComboClear: result.Clear(); break;
                    case ComboBranch b:
                        int k = Math.Clamp(choose(b), 0, b.Arms.Count - 1);
                        Walk(b.Arms[k].Body);
                        break;
                }
        }
        Walk(nodes);
        return result;
    }

    /// <summary>
    /// True when the chosen path runs a <c>ClearSubGoal()</c> (also one nested in a branch arm): in an interrupt handler
    /// that drops the whole current queue, so the handler's steps replace the rest of the combo instead of being appended.
    /// </summary>
    public static bool ClearsOnPath(IReadOnlyList<ComboNode> nodes, Func<ComboBranch, int> choose)
    {
        foreach (var n in nodes)
            switch (n)
            {
                case ComboClear: return true;
                case ComboBranch b when b.Arms.Count > 0:
                    if (ClearsOnPath(b.Arms[Math.Clamp(choose(b), 0, b.Arms.Count - 1)].Body, choose)) return true;
                    break;
            }
        return false;
    }

    /// <summary>True when any arm (any path) contains a <c>ClearSubGoal()</c>.</summary>
    public static bool ContainsClear(IReadOnlyList<ComboNode> nodes) =>
        nodes.Any(n => n is ComboClear || n is ComboBranch b && b.Arms.Any(a => ContainsClear(a.Body)));

    /// <summary>All branches in document order (for building a branch-choice UI).</summary>
    public static IEnumerable<ComboBranch> Branches(IReadOnlyList<ComboNode> nodes)
    {
        foreach (var n in nodes)
            if (n is ComboBranch b)
            {
                yield return b;
                foreach (var arm in b.Arms)
                    foreach (var inner in Branches(arm.Body)) yield return inner;
            }
    }

    private static HashSet<string> FindRandomVars(string src, List<LuaToken> t)
    {
        var vars = new HashSet<string>(StringComparer.Ordinal);
        for (int j = 0; j + 4 < t.Count; j++)
        {
            // name = obj : GetRandam_Int (
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 3].Is(src, ":")
                && t[j + 4].Is(src, "GetRandam_Int"))
                vars.Add(t[j].Text(src).ToString());
        }
        return vars;
    }

    private sealed class Parser(string src, List<LuaToken> t, HashSet<string> randomVars)
    {
        /// <summary>Locals last assigned a numeric literal (<c>local animationId = 3010</c>), in source order.</summary>
        private readonly Dictionary<string, int> _numericLocals = new(StringComparer.Ordinal);

        private bool Is(int i, string s) => i < t.Count && t[i].Is(src, s);
        private bool IsKw(int i, string s) => i < t.Count && t[i].Kind == LuaTokenKind.Keyword && t[i].Is(src, s);

        public List<ComboNode> Block(ref int i, string[] stopAt)
        {
            var nodes = new List<ComboNode>();
            while (i < t.Count && t[i].Kind != LuaTokenKind.EndOfFile)
            {
                var tok = t[i];
                if (tok.Kind == LuaTokenKind.Keyword && stopAt.Any(s => tok.Is(src, s))) return nodes;

                if (IsKw(i, "if")) { nodes.Add(If(ref i)); continue; }
                if (IsKw(i, "function") || IsKw(i, "while") || IsKw(i, "for") || IsKw(i, "repeat") || IsKw(i, "do"))
                {
                    SkipBlock(ref i);
                    continue;
                }
                if (TryNumericAssignment(i)) { i++; continue; }
                if (Is(i, ":") && Is(i + 1, "ClearSubGoal")) { nodes.Add(new ComboClear(t[i].Line)); i += 2; continue; }
                if (Is(i, ":") && Is(i + 1, "AddSubGoal") && Is(i + 2, "(") && TryStep(i, out var step))
                {
                    nodes.Add(step);
                    i += 3;
                    continue;
                }
                i++;
            }
            return nodes;
        }

        private bool TryStep(int i, out ComboStep step)
        {
            step = null!;
            int k = i + 3;
            if (k + 1 >= t.Count || t[k].Kind != LuaTokenKind.Identifier || !Is(k + 1, ",")) return false;
            string goalType = t[k].Text(src).ToString();
            k += 2;
            if (Is(k, "-")) k++;
            if (!Is(k + 1, ",") || k + 2 >= t.Count) return false;
            var argTok = t[k + 2];
            int anim;
            if (argTok.Kind == LuaTokenKind.Number) { if (!int.TryParse(argTok.Text(src), out anim)) return false; }
            else if (argTok.Kind == LuaTokenKind.Identifier && !Is(k + 3, ".") && !Is(k + 3, ":") && !Is(k + 3, "(")
                     && _numericLocals.TryGetValue(argTok.Text(src).ToString(), out anim)) { }
            else return false;
            step = new ComboStep(argTok.Line, anim, goalType);
            return true;
        }

        /// <summary>Records <c>[local] name = &lt;integer&gt;</c> when the value is a lone literal (not part of an expression).</summary>
        private bool TryNumericAssignment(int i)
        {
            if (t[i].Kind != LuaTokenKind.Identifier || !Is(i + 1, "=") || i + 2 >= t.Count || t[i + 2].Kind != LuaTokenKind.Number) return false;
            if (i > 0 && (Is(i - 1, ".") || Is(i - 1, ":") || Is(i - 1, ","))) return false;
            var after = i + 3 < t.Count ? t[i + 3].Text(src) : default;
            if (after is "+" or "-" or "*" or "/" or "%" or "^" or ".." or "," or ".") return false;
            if (!int.TryParse(t[i + 2].Text(src), out int value)) return false;
            _numericLocals[t[i].Text(src).ToString()] = value;
            return true;
        }

        private ComboBranch If(ref int i)
        {
            int line = t[i].Line;
            var arms = new List<(string Cond, List<ComboNode> Body, int Line)>();
            bool hasElse = false;
            int armLine = line;
            i++; // 'if'
            while (true)
            {
                int condStart = i;
                while (i < t.Count && !IsKw(i, "then")) i++;
                string cond = TextOf(condStart, i);
                i++; // 'then'
                var body = Block(ref i, ["elseif", "else", "end"]);
                arms.Add((cond, body, armLine));
                if (IsKw(i, "elseif")) { armLine = t[i].Line; i++; continue; }
                if (IsKw(i, "else"))
                {
                    int elseLine = t[i].Line;
                    i++;
                    arms.Add(("else", Block(ref i, ["end"]), elseLine));
                    hasElse = true;
                }
                i++; // 'end'
                break;
            }
            if (!hasElse) arms.Add(("(no branch taken)", [], -1));
            return new ComboBranch(line, Probabilities(arms));
        }

        /// <summary>Thresholds on one random var, in order: r &lt;= a, r &lt;= b, … else → a, b-a, …, 100-last.</summary>
        private List<ComboArm> Probabilities(List<(string Cond, List<ComboNode> Body, int Line)> arms)
        {
            var thresholds = new List<double?>();
            string? var = null;
            foreach (var (cond, _, _) in arms)
            {
                var parts = cond.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && randomVars.Contains(parts[0]) && (var == null || var == parts[0])
                    && parts[1] is "<=" or "<" && double.TryParse(parts[2], out double n))
                {
                    var = parts[0];
                    thresholds.Add(parts[1] == "<" ? n - 1 : n);
                }
                else thresholds.Add(null);
            }

            bool allRandom = var != null && thresholds.Take(arms.Count - 1).All(x => x != null)
                             && (thresholds[^1] != null || arms[^1].Cond is "else" or "(no branch taken)");
            var result = new List<ComboArm>();
            double prev = 0;
            for (int a = 0; a < arms.Count; a++)
            {
                double? p = null;
                if (allRandom)
                {
                    double upTo = thresholds[a] ?? 100;
                    p = Math.Clamp(upTo - prev, 0, 100);
                    prev = Math.Max(prev, upTo);
                }
                result.Add(new ComboArm(arms[a].Cond, p, arms[a].Body, arms[a].Line));
            }
            return result;
        }

        private void SkipBlock(ref int i)
        {
            // 'do' after while/for belongs to the loop header; count openers/closers like GoalOutline does.
            int depth = 0, pendingLoopDo = 0;
            for (; i < t.Count; i++)
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

        private string TextOf(int from, int to) =>
            string.Join(" ", Enumerable.Range(from, Math.Max(0, to - from)).Select(k => t[k].Text(src).ToString()));
    }
}
