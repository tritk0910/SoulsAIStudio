using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>Result of <see cref="SpEffectHandlerWriter.Add"/>.</summary>
/// <param name="Text">The new script (unchanged when <paramref name="Error"/> is set or nothing was missing).</param>
/// <param name="CaretLine">0-based line to put the caret on: the empty line of the new arm, or the existing arm.</param>
/// <param name="CaretColumn">0-based column on that line.</param>
public sealed record SpEffectHandlerEdit(string Text, int CaretLine, int CaretColumn, bool AddedObserve, bool AddedHandler, string? Error = null)
{
    public bool Changed => AddedObserve || AddedHandler;
}

/// <summary>
/// Makes a battle script react to a SpEffect, as the existing ones do (Parameters panel "Add to Goal.Interrupt"):
/// <list type="bullet">
/// <item><c>ai:AddObserveSpecialEffectAttribute(TARGET_SELF | TARGET_ENE_0, id)</c> next to the other observes (in <c>Goal.Activate</c>);</item>
/// <item>an arm <c>elseif interruptSpeffect == id then goal:ClearSubGoal() … return true</c> at the end of the SpEffect chain
/// of <c>Goal.Interrupt</c> (before its <c>else</c>, if any), with an empty line for the combo.</item>
/// </list>
/// Only what is missing is added; variable names, indentation and line endings are taken from the script.
/// </summary>
public static class SpEffectHandlerWriter
{
    public const string TargetSelf = "TARGET_SELF";
    public const string TargetEnemy = "TARGET_ENE_0";

    private sealed record Insert(int BeforeLine, string[] Lines, int CaretOffset = -1, int CaretColumn = 0);

    public static SpEffectHandlerEdit Add(string src, int spEffectId, string target)
    {
        var all = LuaLexer.Tokenize(src);
        var t = all.Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var outline = GoalOutline.Build(src, all);
        var table = SpEffectInterrupts.Build(src, all, outline);
        string nl = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = src.Split(nl).ToList();
        var inserts = new List<Insert>();
        int caretLine = -1, caretCol = 0;

        // ---- handler arm in Goal.Interrupt ----
        bool addHandler = !table.Handlers.ContainsKey(spEffectId);
        if (!addHandler)
        {
            caretLine = table.Handlers[spEffectId].Line;
            caretCol = Indent(lines, caretLine).Length;
        }
        else
        {
            var interrupt = outline.FirstOrDefault(g => g.Name == "Interrupt");
            if (interrupt == null) return Fail(src, "This script has no Goal.Interrupt function.");
            var prm = interrupt.Parameters.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (prm.Length < 3) return Fail(src, "Goal.Interrupt has fewer than 3 parameters (self, ai, goal).");
            string ai = prm[1], goal = prm[2];
            var insert = HandlerInsert(src, t, lines, interrupt, ai, goal, spEffectId, table.SpEffectVars);
            if (insert == null) return Fail(src, "Could not find where to add the SpEffect branch in Goal.Interrupt.");
            inserts.Add(insert);
        }

        // ---- observe in Goal.Activate ----
        bool observed = table.ObservedTargets.TryGetValue(spEffectId, out var targets) && targets.Contains(target);
        if (!observed)
        {
            var insert = ObserveInsert(src, t, lines, outline, spEffectId, target);
            if (insert == null) return Fail(src, "This script has no Goal.Activate to register the SpEffect in (AddObserveSpecialEffectAttribute).");
            inserts.Add(insert);
        }

        if (inserts.Count == 0) return new SpEffectHandlerEdit(src, caretLine, caretCol, false, false);

        // Apply bottom-up so earlier line numbers stay valid; then shift the caret by the lines added above it.
        foreach (var ins in inserts.OrderByDescending(i => i.BeforeLine))
            lines.InsertRange(ins.BeforeLine, ins.Lines);
        var caretIns = inserts.FirstOrDefault(i => i.CaretOffset >= 0);
        if (caretIns != null)
        {
            caretLine = caretIns.BeforeLine + caretIns.CaretOffset + inserts.Where(i => i != caretIns && i.BeforeLine <= caretIns.BeforeLine).Sum(i => i.Lines.Length);
            caretCol = caretIns.CaretColumn;
        }
        else if (caretLine >= 0)
            caretLine += inserts.Where(i => i.BeforeLine <= caretLine).Sum(i => i.Lines.Length);
        return new SpEffectHandlerEdit(string.Join(nl, lines), caretLine, caretCol, !observed, addHandler);
    }

    private static SpEffectHandlerEdit Fail(string src, string why) => new(src, -1, 0, false, false, why);

    private static string Indent(List<string> lines, int line) =>
        line >= 0 && line < lines.Count ? new string(lines[line].TakeWhile(c => c is ' ' or '\t').ToArray()) : "";

    /// <summary>Indent unit of the script (a tab, or the smallest step between two consecutive indented lines), default 4 spaces.</summary>
    private static string IndentUnit(List<string> lines)
    {
        if (lines.Any(l => l.StartsWith('\t'))) return "\t";
        int best = int.MaxValue;
        for (int i = 1; i < lines.Count; i++)
        {
            int a = Indent(lines, i - 1).Length, b = Indent(lines, i).Length;
            if (b > a && lines[i].Trim().Length > 0) best = Math.Min(best, b - a);
        }
        return new string(' ', best is > 0 and <= 8 ? best : 4);
    }

    private static Insert? HandlerInsert(string src, List<LuaToken> t, List<string> lines, GoalFunction interrupt,
        string ai, string goal, int id, IReadOnlySet<string> spVarsInFile)
    {
        string unit = IndentUnit(lines);
        int from = t.FindIndex(x => x.Line > interrupt.Line);
        if (from < 0) return null;
        int to = t.FindLastIndex(x => x.Line <= interrupt.EndLine);

        // The SpEffect variable of this function (name = ai:GetSpecialEffectActivateInterruptType(...)).
        string? spVar = null;
        int spVarLine = -1;
        for (int j = from; j + 4 <= to; j++)
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 3].Is(src, ":")
                && t[j + 4].Is(src, "GetSpecialEffectActivateInterruptType") && spVarsInFile.Contains(t[j].Text(src).ToString()))
            { spVar = t[j].Text(src).ToString(); spVarLine = t[j].Line; break; }

        // The chain: first "if spVar == N then" of the function, its last arm ends before its own 'else' or 'end'.
        if (spVar != null)
            for (int k = from; k + 2 <= to; k++)
            {
                if (!t[k].Is(src, "if") || !t[k + 1].Is(src, spVar) || !t[k + 2].Is(src, "==")) continue;
                int depth = 1, stop = -1;
                for (int j = k + 1; j <= to && stop < 0; j++)
                {
                    if (t[j].Kind != LuaTokenKind.Keyword) continue;
                    string s = t[j].Text(src).ToString();
                    if (s is "function" or "if" or "while" or "for" or "repeat") depth++;
                    else if (s == "do" && !IsLoopDo(src, t, j)) depth++;
                    else if (s == "else" && depth == 1) stop = j;
                    else if (s is "end" or "until" && --depth == 0) stop = j;
                }
                if (stop < 0) return null;
                string ifIndent = Indent(lines, t[k].Line);
                string body = Indent(lines, t[k].Line + 1) is { } next && next.Length > ifIndent.Length ? next : ifIndent + unit;
                return new Insert(t[stop].Line, new[]
                {
                    $"{ifIndent}elseif {spVar} == {id} then",
                    $"{body}{goal}:ClearSubGoal()",
                    body,
                    $"{body}return true",
                }, CaretOffset: 2, CaretColumn: body.Length);
            }

        // No chain yet: a whole block, inside an existing "if ai:IsInterupt(INTERUPT_ActivateSpecialEffect) then" if there is one.
        for (int k = from; k + 5 <= to; k++)
            if (t[k].Is(src, "if") && t[k + 2].Is(src, ":") && t[k + 3].Is(src, "IsInterupt") && t[k + 5].Is(src, "INTERUPT_ActivateSpecialEffect"))
            {
                string outer = Indent(lines, t[k].Line), ind = outer + unit;
                var block = new List<string>();
                string v = spVar ?? "interruptSpeffect";
                if (spVar == null) block.Add($"{ind}local {v} = {ai}:GetSpecialEffectActivateInterruptType(0)");
                block.AddRange(new[] { $"{ind}if {v} == {id} then", $"{ind}{unit}{goal}:ClearSubGoal()", $"{ind}{unit}", $"{ind}{unit}return true", $"{ind}end" });
                return new Insert(t[k].Line + 1, block.ToArray(), CaretOffset: block.Count - 3, CaretColumn: ind.Length + unit.Length);
            }

        {
            int at = spVarLine >= 0 ? spVarLine + 1 : interrupt.Line + 1;
            string ind = Indent(lines, at) is { Length: > 0 } i0 ? i0 : Indent(lines, interrupt.Line) + unit;
            var block = new List<string>();
            string v = spVar ?? "interruptSpeffect";
            if (spVar == null) block.Add($"{ind}local {v} = {ai}:GetSpecialEffectActivateInterruptType(0)");
            block.AddRange(new[]
            {
                $"{ind}if {ai}:IsInterupt(INTERUPT_ActivateSpecialEffect) then",
                $"{ind}{unit}if {v} == {id} then", $"{ind}{unit}{unit}{goal}:ClearSubGoal()", $"{ind}{unit}{unit}",
                $"{ind}{unit}{unit}return true", $"{ind}{unit}end", $"{ind}end",
            });
            return new Insert(at, block.ToArray(), CaretOffset: block.Count - 4, CaretColumn: ind.Length + 2 * unit.Length);
        }
    }

    private static Insert? ObserveInsert(string src, List<LuaToken> t, List<string> lines, IReadOnlyList<GoalFunction> outline, int id, string target)
    {
        // Observes are registered in Goal.Activate (Goal.Interrupt only reacts to them): after the last observe of
        // Goal.Activate on the same target (else its last observe at all), with its receiver and indentation.
        // Observes elsewhere in the file (e.g. a stray one in Goal.Interrupt) are never used as the anchor.
        var activate = outline.FirstOrDefault(g => g.Name == "Activate");
        if (activate == null) return null;
        int lastAny = -1, lastSame = -1;
        for (int j = 1; j + 3 < t.Count; j++)
            if (t[j].Line > activate.Line && t[j].Line < activate.EndLine
                && t[j].Is(src, ":") && t[j + 1].Is(src, "AddObserveSpecialEffectAttribute") && t[j + 2].Is(src, "("))
            {
                lastAny = j;
                if (t[j + 3].Is(src, target)) lastSame = j;
            }
        int at = lastSame >= 0 ? lastSame : lastAny;
        if (at >= 0)
        {
            int line = t[at].Line;
            string recv = t[at - 1].Text(src).ToString();
            return new Insert(line + 1, new[] { $"{Indent(lines, line)}{recv}:AddObserveSpecialEffectAttribute({target}, {id})" });
        }
        var prm = activate.Parameters.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (prm.Length < 2) return null;
        string ind = Indent(lines, activate.Line + 1) is { Length: > 0 } i0 ? i0 : Indent(lines, activate.Line) + IndentUnit(lines);
        return new Insert(activate.Line + 1, new[] { $"{ind}{prm[1]}:AddObserveSpecialEffectAttribute({target}, {id})" });
    }

    private static bool IsLoopDo(string src, List<LuaToken> t, int doIdx)
    {
        int parens = 0;
        for (int k = doIdx - 1; k >= 0; k--)
        {
            var s = t[k].Text(src);
            if (s is ")" or "]" or "}") parens++;
            else if (s is "(" or "[" or "{") parens--;
            if (parens > 0) continue;
            if (t[k].Kind == LuaTokenKind.Keyword)
            {
                if (s is "while" or "for") return true;
                if (s is not ("in" or "and" or "or" or "not" or "nil" or "true" or "false")) return false;
            }
        }
        return false;
    }
}
