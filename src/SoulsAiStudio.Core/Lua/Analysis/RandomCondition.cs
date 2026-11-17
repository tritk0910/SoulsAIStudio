namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>
/// Evaluates a branch condition (as written in the Lua, tokens separated by spaces, e.g.
/// <c>interruptspeffect == 3549652 and interruptRand &lt;= 45 and arg1 : IsFinishTimer ( 8 ) == true</c>) against
/// concrete values of the random variables (<c>local r = ai:GetRandam_Int(1, 100)</c>), the way the game does when the
/// function runs: one value per variable per call, shared by every <c>if</c> that reads it.
/// <para>Only <c>and</c>-chains are understood. Parts that are not a random test (timers, SpEffects, HP, distance…)
/// or a SpEffect-id test are reported as <c>unknown</c>; a condition with <c>or</c>/<c>not</c> is unknown as a whole.</para>
/// </summary>
public static class RandomCondition
{
    /// <param name="condition">Arm condition text; <c>else</c> and <c>(no branch taken)</c> are always true.</param>
    /// <param name="randomVars">Variables that hold a <c>GetRandam_Int</c> result.</param>
    /// <param name="matchedVars">Variables whose <c>== N</c> test is already known to be true (the SpEffect id variable of the handler being run).</param>
    /// <param name="roll">Value of a random variable (rolled on first use by the caller, then reused).</param>
    /// <param name="unknown">True when some part could not be decided from the random values.</param>
    /// <returns>False when a random test fails (the arm is certainly not taken), else true.</returns>
    public static bool Evaluate(string condition, IReadOnlySet<string> randomVars, IReadOnlySet<string> matchedVars,
        Func<string, int> roll, out bool unknown)
    {
        unknown = false;
        string c = condition.Trim();
        if (c is "else" or "(no branch taken)" or "")
            return true;
        var tokens = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(x => x is "or" or "not"))
        {
            unknown = true;
            return true;
        }
        var part = new List<string>();
        bool ok = true;
        foreach (string tok in tokens.Append("and"))
        {
            if (tok != "and") { part.Add(tok); continue; }
            if (part.Count == 0) continue;
            switch (Part(part, randomVars, matchedVars, roll))
            {
                case false: ok = false; break;
                case null: unknown = true; break;
            }
            part.Clear();
        }
        return ok;
    }

    /// <summary>true/false for a decided part, null for a part that is not a random or matched-id test.</summary>
    private static bool? Part(List<string> p, IReadOnlySet<string> randomVars, IReadOnlySet<string> matchedVars, Func<string, int> roll)
    {
        // strip balanced outer parentheses: ( r <= 50 )
        while (p.Count >= 2 && p[0] == "(" && p[^1] == ")")
        {
            p.RemoveAt(p.Count - 1);
            p.RemoveAt(0);
        }
        if (p.Count != 3)
            return null;
        string a = p[0], op = p[1], b = p[2];
        if (matchedVars.Contains(a) && op == "==" && double.TryParse(b, out _))
            return true;
        if (randomVars.Contains(a) && double.TryParse(b, out double n))
            return Compare(roll(a), op, n);
        if (randomVars.Contains(b) && double.TryParse(a, out double m))
            return Compare(roll(b), Flip(op), m);
        return null;
    }

    private static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    private static bool? Compare(int v, string op, double n) => op switch
    {
        "<=" => v <= n,
        "<" => v < n,
        ">=" => v >= n,
        ">" => v > n,
        "==" => v == n,
        "~=" => v != n,
        _ => null,
    };
}
