using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Lua.Analysis;

/// <summary>One local variable / parameter / loop variable: where it is declared and every place it is used.</summary>
public sealed class LuaVariable
{
    public required string Name { get; init; }
    /// <summary>The declaring token (<c>local x</c>, a parameter, a <c>for</c> variable).</summary>
    public required LuaToken Declaration { get; init; }
    /// <summary>Every token naming this variable, the declaration included, in source order.</summary>
    public List<LuaToken> Occurrences { get; } = [];
    /// <summary>Source offset where the variable's scope ends (end of the enclosing block).</summary>
    public int ScopeEnd { get; internal set; } = int.MaxValue;
}

/// <summary>
/// Lexical scope resolution for one Lua script (Lua 5.1: <c>local</c>, <c>local function</c>, parameters, <c>for</c>
/// variables, block scopes). Token based, no AST: good for go-to-definition and rename of locals. Globals and fields
/// (<c>Goal.Activate</c>, <c>ai:GetDist</c>) are not variables here.
/// </summary>
public sealed class LuaScopes
{
    private sealed class Scope
    {
        public readonly Dictionary<string, LuaVariable> Vars = new(StringComparer.Ordinal);
        public readonly List<LuaVariable> Declared = [];
    }

    private readonly string _src;
    private readonly List<LuaToken> _tokens;                 // without comments
    private readonly Dictionary<int, LuaVariable> _byStart = new(); // token start -> variable
    private readonly List<LuaToken> _globalRefs = [];        // identifiers that are not bound to a local

    public IReadOnlyList<LuaVariable> Variables { get; }

    private LuaScopes(string src, List<LuaToken> tokens, List<LuaVariable> vars)
    {
        _src = src; _tokens = tokens; Variables = vars;
        foreach (var v in vars) foreach (var o in v.Occurrences) _byStart[o.Start] = v;
    }

    /// <summary>The variable named by the identifier at (<paramref name="line"/>, <paramref name="column"/>), 0-based; null for anything else.</summary>
    public LuaVariable? VariableAt(int line, int column)
    {
        foreach (var t in _tokens)
        {
            if (t.Line != line) { if (t.Line > line) break; continue; }
            if (t.Kind == LuaTokenKind.Identifier && column >= t.Column && column <= t.Column + t.Length)
                if (_byStart.TryGetValue(t.Start, out var v)) return v;
        }
        return null;
    }

    /// <summary>Why <paramref name="newName"/> can't replace the variable's name, or null when it can.</summary>
    public string? CheckRename(LuaVariable v, string newName)
    {
        if (newName.Length == 0 || !(char.IsLetter(newName[0]) || newName[0] == '_') || !newName.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return "Not a valid Lua name (letters, digits, _; not starting with a digit).";
        if (LuaLexer.IsKeyword(newName)) return $"'{newName}' is a Lua keyword.";
        if (newName == v.Name) return null;
        int lo = v.Declaration.Start, hi = v.ScopeEnd;
        foreach (var o in Variables)
        {
            if (o == v || o.Name != newName) continue;
            // an outer variable of that name used inside our scope would be shadowed by the renamed one
            bool shadowsOuter = o.Declaration.Start < lo && o.ScopeEnd >= lo && o.Occurrences.Any(t => t.Start > lo && t.Start <= hi);
            // one of our uses would be captured by an inner variable of that name
            bool capturesOurs = o.Declaration.Start > lo && o.Declaration.Start <= hi
                                && v.Occurrences.Any(t => t.Start >= o.Declaration.Start && t.Start <= o.ScopeEnd);
            if (shadowsOuter || capturesOurs) return $"'{newName}' is already a variable in this scope.";
        }
        foreach (var g in _globalRefs)
            if (g.Start >= lo && g.Start <= hi && _src.AsSpan(g.Start, g.Length).SequenceEqual(newName))
                return $"'{newName}' is used as a global in this scope; renaming would change its meaning.";
        return null;
    }

    public static LuaScopes Build(string src) => Build(src, LuaLexer.Tokenize(src));

    public static LuaScopes Build(string src, IReadOnlyList<LuaToken> all)
    {
        var t = all.Where(x => x.Kind != LuaTokenKind.Comment && x.Kind != LuaTokenKind.EndOfFile).ToList();
        var vars = new List<LuaVariable>();
        var globals = new List<LuaToken>();
        var scopes = new List<Scope> { new() };                 // chain, innermost last
        var blocks = new List<(int ScopeDepth, bool ExpectDo)>(); // one entry per open block
        var pending = new List<(LuaToken Tok, Scope Target)>();   // `local x = …`: visible once the statement ends
        int pendingLine = -1;
        int bracketDepth = 0;
        var brackets = new Stack<char>();
        int untilLine = -1;

        string S(LuaToken k) => src.Substring(k.Start, k.Length);

        LuaVariable Declare(LuaToken tok, Scope target)
        {
            var v = new LuaVariable { Name = S(tok), Declaration = tok };
            v.Occurrences.Add(tok);
            target.Vars[v.Name] = v;
            target.Declared.Add(v);
            vars.Add(v);
            return v;
        }

        LuaVariable? Lookup(string name)
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
                if (scopes[i].Vars.TryGetValue(name, out var v)) return v;
            return null;
        }

        void PushBlock(bool expectDo = false)
        {
            blocks.Add((scopes.Count, expectDo));
            scopes.Add(new Scope());
        }

        void PopBlock(int endOffset)
        {
            if (blocks.Count == 0) return;
            var (depth, _) = blocks[^1];
            blocks.RemoveAt(blocks.Count - 1);
            FlushPending(force: true);
            while (scopes.Count > depth)
            {
                foreach (var v in scopes[^1].Declared) v.ScopeEnd = endOffset;
                scopes.RemoveAt(scopes.Count - 1);
            }
        }

        void FlushPending(bool force)
        {
            if (pending.Count == 0) return;
            foreach (var (tok, target) in pending) Declare(tok, target);
            pending.Clear();
        }

        for (int i = 0; i < t.Count; i++)
        {
            var tok = t[i];
            string s = S(tok);

            if (untilLine >= 0 && tok.Line > untilLine) { untilLine = -1; PopBlock(tok.Start); }

            // `local x = …` becomes visible at the next statement: a new line, outside brackets, that does not
            // continue the expression.
            if (pending.Count > 0 && bracketDepth == 0 && i > 0 && tok.Line > pendingLine
                && !ContinuesFrom(src, t[i - 1]) && !IsContinuationToken(tok, s))
                FlushPending(false);

            if (tok.Kind == LuaTokenKind.Operator)
            {
                if (s is "(" or "{" or "[") { brackets.Push(s[0]); bracketDepth++; }
                else if (s is ")" or "}" or "]" && bracketDepth > 0) { brackets.Pop(); bracketDepth--; }
                continue;
            }

            if (tok.Kind == LuaTokenKind.Keyword)
            {
                switch (s)
                {
                    case "local":
                        if (i + 1 < t.Count && S(t[i + 1]) == "function" && i + 2 < t.Count && t[i + 2].Kind == LuaTokenKind.Identifier)
                        {
                            // local function f(...) … end : f is visible inside its own body
                            Declare(t[i + 2], scopes[^1]);
                            i = HandleFunction(i + 1, skipName: true);
                        }
                        else
                        {
                            // local a, b = …
                            int j = i + 1;
                            var names = new List<LuaToken>();
                            while (j < t.Count && t[j].Kind == LuaTokenKind.Identifier)
                            {
                                names.Add(t[j]);
                                if (j + 1 < t.Count && S(t[j + 1]) == ",") j += 2; else { j++; break; }
                            }
                            if (j < t.Count && S(t[j]) == "=")
                            {
                                foreach (var n in names) pending.Add((n, scopes[^1]));
                                pendingLine = tok.Line;
                            }
                            else
                                foreach (var n in names) Declare(n, scopes[^1]);
                            i = j - 1;
                        }
                        continue;
                    case "function":
                        i = HandleFunction(i, skipName: false);
                        continue;
                    case "for":
                    {
                        PushBlock(expectDo: true);
                        int j = i + 1;
                        var names = new List<LuaToken>();
                        while (j < t.Count && t[j].Kind == LuaTokenKind.Identifier)
                        {
                            names.Add(t[j]);
                            if (j + 1 < t.Count && S(t[j + 1]) == ",") j += 2; else { j++; break; }
                        }
                        foreach (var n in names) Declare(n, scopes[^1]);
                        i = j - 1;
                        continue;
                    }
                    case "while": PushBlock(expectDo: true); continue;
                    case "if": PushBlock(); continue;
                    case "repeat": PushBlock(); continue;
                    case "do":
                        if (blocks.Count > 0 && blocks[^1].ExpectDo) blocks[^1] = (blocks[^1].ScopeDepth, false);
                        else PushBlock();
                        continue;
                    case "elseif":
                    case "else":
                        if (blocks.Count > 0)
                        {
                            FlushPending(true);
                            foreach (var v in scopes[^1].Declared) v.ScopeEnd = tok.Start;
                            scopes[^1] = new Scope();
                        }
                        continue;
                    case "end": PopBlock(tok.Start); continue;
                    case "until": untilLine = tok.Line; continue; // the condition still sees the block's locals
                    default: continue;
                }
            }

            if (tok.Kind != LuaTokenKind.Identifier) continue;

            // field access (a.b, a:b) and table keys ({ b = 1 }) are not variables
            if (i > 0 && t[i - 1].Kind == LuaTokenKind.Operator && S(t[i - 1]) is "." or ":") continue;
            if (i > 0 && brackets.Count > 0 && brackets.Peek() == '{' && t[i - 1].Kind == LuaTokenKind.Operator && S(t[i - 1]) is "{" or "," or ";"
                && i + 1 < t.Count && S(t[i + 1]) == "=") continue;

            var found = Lookup(s);
            if (found != null) found.Occurrences.Add(tok);
            else globals.Add(tok);
        }

        // unclosed blocks / pending: close at end of file
        FlushPending(true);
        while (blocks.Count > 0) PopBlock(src.Length);

        var result = new LuaScopes(src, t, vars);
        result._globalRefs.AddRange(globals);
        return result;

        // `function a.b:c(p1, p2)` / `function (p1)`: name path is references (the first segment is a variable), params are locals of the body.
        int HandleFunction(int at, bool skipName)
        {
            int j = at + 1;
            if (skipName) j++; // `local function f`: f already declared
            else
            {
                bool first = true;
                while (j < t.Count && (t[j].Kind == LuaTokenKind.Identifier || t[j].Kind == LuaTokenKind.Operator && S(t[j]) is "." or ":"))
                {
                    if (t[j].Kind == LuaTokenKind.Identifier && first)
                    {
                        var v = Lookup(S(t[j]));
                        if (v != null) v.Occurrences.Add(t[j]); else globals.Add(t[j]);
                    }
                    if (t[j].Kind == LuaTokenKind.Identifier) first = false;
                    j++;
                }
            }
            PushBlock();
            if (j < t.Count && S(t[j]) == "(")
            {
                j++;
                while (j < t.Count && S(t[j]) != ")")
                {
                    if (t[j].Kind == LuaTokenKind.Identifier) Declare(t[j], scopes[^1]);
                    j++;
                }
            }
            return j; // at `)`; the loop's i++ moves on
        }
    }

    private static bool ContinuesFrom(string src, LuaToken prev)
    {
        var s = src.AsSpan(prev.Start, prev.Length);
        if (prev.Kind == LuaTokenKind.Keyword) return s is "and" or "or" or "not";
        return prev.Kind == LuaTokenKind.Operator && s is not ")" and not "]" and not "}" and not "...";
    }

    private static bool IsContinuationToken(LuaToken tok, string s) =>
        tok.Kind == LuaTokenKind.Keyword && s is "and" or "or" || tok.Kind == LuaTokenKind.Operator && s is ".." or "+" or "*" or "/" or "%" or "^" or "==" or "~=" or "<" or "<=" or ">" or ">=" or ")" or "}" or "]" or "." or ":" or ",";
}
