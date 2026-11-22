namespace SoulsAiStudio.Core.Lua.Syntax;

/// <summary>
/// A syntax error: 0-based line / columns of the offending token, and a Lua-style message. For a missing <c>end</c>
/// (reported by Lua at the end of the file) <see cref="HintLine"/> is where it is most likely missing: the first line
/// written at column 0 (e.g. the next <c>Goal.X = function</c>) while a block was still open, and <see cref="Hint"/> says which.
/// </summary>
public sealed record LuaSyntaxError(int Line, int StartColumn, int EndColumn, string Message, int HintLine = -1, string? Hint = null, int HintEndColumn = 0);

/// <summary>
/// Lua 5.1 syntax check (what <c>luac -p</c> reports), over <see cref="LuaLexer"/> tokens: recursive descent over the
/// whole grammar, no AST. Like the Lua compiler it stops at the FIRST error, with Lua's own wording
/// (<c>'end' expected (to close 'function' at line 12) near 'elseif'</c>, <c>unexpected symbol near ')'</c>, ...).
/// Used by the Lua editor to underline a script the game would refuse to load.
/// </summary>
public sealed class LuaSyntaxChecker
{
    private readonly string _src;
    private readonly List<LuaToken> _t;
    private int _p;
    /// <summary>Open constructs (keyword, 0-based line), innermost last.</summary>
    private readonly List<(string Who, int Line)> _open = new();
    /// <summary>First statement found at column 0 while something was open (a likely missing 'end' just before it).</summary>
    private (int Line, string Who, int OpenLine, int Length)? _suspect;

    private sealed class SyntaxException(LuaSyntaxError error) : Exception(error.Message)
    {
        public LuaSyntaxError Error { get; } = error;
    }

    private LuaSyntaxChecker(string src)
    {
        _src = src;
        _t = LuaLexer.Tokenize(src).Where(x => x.Kind != LuaTokenKind.Comment).ToList();
    }

    /// <summary>The first syntax error of <paramref name="src"/>, or null if it compiles.</summary>
    public static LuaSyntaxError? Check(string src)
    {
        var c = new LuaSyntaxChecker(src);
        try
        {
            c.CheckTokens();
            c.Block();
            if (c.Cur.Kind != LuaTokenKind.EndOfFile) c.Fail("'<eof>' expected");
            return null;
        }
        catch (SyntaxException ex) { return ex.Error; }
    }

    // ---------------------------------------------------------------- tokens

    private LuaToken Cur => _t[_p];
    private string Text(LuaToken t) => t.Kind == LuaTokenKind.EndOfFile ? "<eof>" : t.Text(_src).ToString();
    private bool At(string s) => Cur.Kind is LuaTokenKind.Keyword or LuaTokenKind.Operator && Cur.Is(_src, s);
    private bool AtName => Cur.Kind == LuaTokenKind.Identifier;
    private void Next() { if (_p < _t.Count - 1) _p++; }

    private bool Accept(string s)
    {
        if (!At(s)) return false;
        Next();
        return true;
    }

    private void Expect(string s)
    {
        if (!Accept(s)) Fail($"'{s}' expected");
    }

    /// <summary>Closing keyword of a construct opened at <paramref name="line"/> (Lua names the opener when it is on another line).</summary>
    private void ExpectMatch(string what, string who, int line)
    {
        if (_open.Count > 0 && _open[^1].Line == line && _open[^1].Who == who) _open.RemoveAt(_open.Count - 1);
        if (Accept(what)) return;
        if (line == Cur.Line) Fail($"'{what}' expected");
        Fail($"'{what}' expected (to close '{who}' at line {line + 1})");
    }

    private void Name()
    {
        if (!AtName) Fail("<name> expected");
        Next();
    }

    private void Fail(string message) => FailAt(Cur, message + " near '" + Text(Cur) + "'");

    private void FailAt(LuaToken t, string message)
    {
        int hintLine = -1, hintEnd = 0;
        string? hint = null;
        if (_suspect is { } s && t.Line > s.Line && message.StartsWith("'end' expected", StringComparison.Ordinal))
        {
            hintLine = s.Line;
            hint = $"'end' probably missing before this line: '{s.Who}' at line {s.OpenLine + 1} is still open here";
            hintEnd = s.Length;
        }
        throw new SyntaxException(new LuaSyntaxError(t.Line, t.Column, t.Column + Math.Max(1, t.Length), message, hintLine, hint, hintEnd));
    }

    private void Open(string who, int line) => _open.Add((who, line));

    /// <summary>Lexical errors the lexer lets through: unfinished strings / long strings, stray characters.</summary>
    private void CheckTokens()
    {
        foreach (var t in _t)
        {
            var s = t.Text(_src);
            if (t.Kind == LuaTokenKind.String)
            {
                if (s[0] is '"' or '\'')
                {
                    if (s.Length < 2 || s[^1] != s[0] || EndsWithEscape(s)) FailAt(t, "unfinished string near '" + s.ToString().TrimEnd('\r') + "'");
                }
                else if (!s.EndsWith("]") || s.Length < 4) FailAt(t, "unfinished long string near '<eof>'");
            }
            else if (t.Kind == LuaTokenKind.Operator && s.Length == 1 && !"+-*/%^#=<>(){}[];:,.~".Contains(s[0]))
                FailAt(t, $"unexpected symbol near '{s}'");
            else if (t.Kind == LuaTokenKind.Operator && s is "~") FailAt(t, "unexpected symbol near '~'");
        }
        // Unfinished long comment: the lexer runs it to the end of the file.
        var all = LuaLexer.Tokenize(_src);
        foreach (var t in all)
        {
            if (t.Kind != LuaTokenKind.Comment) continue;
            var s = t.Text(_src);
            if (s.Length > 3 && s[2] == '[' && (s[3] == '[' || s[3] == '='))
            {
                int level = 0, j = 3;
                while (j < s.Length && s[j] == '=') { level++; j++; }
                if (j < s.Length && s[j] == '[' && !s.EndsWith("]" + new string('=', level) + "]"))
                    FailAt(t, "unfinished long comment near '<eof>'");
            }
        }
    }

    private static bool EndsWithEscape(ReadOnlySpan<char> s)
    {
        // "abc\" : the closing quote is escaped
        int n = 0;
        for (int i = s.Length - 2; i >= 1 && s[i] == '\\'; i--) n++;
        return n % 2 == 1;
    }

    // ---------------------------------------------------------------- statements

    private bool BlockFollows() =>
        Cur.Kind == LuaTokenKind.EndOfFile || At("end") || At("else") || At("elseif") || At("until");

    private void Block()
    {
        while (!BlockFollows())
        {
            if (At("return"))
            {
                Next();
                if (!BlockFollows() && !At(";")) ExpList();
                Accept(";");
                if (!BlockFollows()) Fail("'end' expected");
                return;
            }
            if (At("break"))
            {
                Next();
                Accept(";");
                if (!BlockFollows()) Fail("'end' expected");
                return;
            }
            if (Cur.Column == 0 && _open.Count > 0 && _suspect == null)
            {
                int eol = _src.IndexOf('\n', Cur.Start);
                int len = (eol < 0 ? _src.Length : eol) - Cur.Start;
                _suspect = (Cur.Line, _open[^1].Who, _open[^1].Line, _src.AsSpan(Cur.Start, len).TrimEnd().Length);
            }
            Statement();
            Accept(";");
        }
    }

    private void Statement()
    {
        int line = Cur.Line;
        if (Accept("do")) { Open("do", line); Block(); ExpectMatch("end", "do", line); return; }
        if (Accept("while")) { Open("while", line); Exp(); Expect("do"); Block(); ExpectMatch("end", "while", line); return; }
        if (Accept("repeat")) { Open("repeat", line); Block(); ExpectMatch("until", "repeat", line); Exp(); return; }
        if (Accept("if"))
        {
            Open("if", line);
            Exp(); Expect("then"); Block();
            while (Accept("elseif")) { Exp(); Expect("then"); Block(); }
            if (Accept("else")) Block();
            ExpectMatch("end", "if", line);
            return;
        }
        if (Accept("for"))
        {
            Open("for", line);
            Name();
            if (Accept("="))
            {
                Exp(); Expect(","); Exp();
                if (Accept(",")) Exp();
            }
            else if (At(",") || At("in"))
            {
                while (Accept(",")) Name();
                Expect("in");
                ExpList();
            }
            else Fail("'=' or 'in' expected");
            Expect("do"); Block(); ExpectMatch("end", "for", line);
            return;
        }
        if (Accept("function"))
        {
            Name();
            while (Accept(".")) Name();
            if (Accept(":")) Name();
            FuncBody(line);
            return;
        }
        if (Accept("local"))
        {
            if (Accept("function")) { Name(); FuncBody(line); return; }
            Name();
            while (Accept(",")) Name();
            if (Accept("=")) ExpList();
            return;
        }
        ExprStatement();
    }

    /// <summary>A call, or an assignment <c>a, b.c, d[e] = ...</c>.</summary>
    private void ExprStatement()
    {
        var kind = SuffixedExp();
        if (At("=") || At(","))
        {
            if (kind == ExpKind.Call) Fail("syntax error");
            while (Accept(","))
                if (SuffixedExp() == ExpKind.Call) Fail("syntax error");
            Expect("=");
            ExpList();
        }
        else if (kind != ExpKind.Call) Fail("syntax error");
    }

    private void FuncBody(int line)
    {
        Open("function", line);
        Expect("(");
        if (!At(")"))
        {
            do
            {
                if (Accept("...")) break;
                Name();
            } while (Accept(","));
        }
        Expect(")");
        Block();
        ExpectMatch("end", "function", line);
    }

    // ---------------------------------------------------------------- expressions

    private enum ExpKind { Other, Name, Index, Call }

    private void ExpList()
    {
        Exp();
        while (Accept(",")) Exp();
    }

    private static readonly Dictionary<string, (int Left, int Right)> Binary = new()
    {
        ["or"] = (1, 1), ["and"] = (2, 2),
        ["<"] = (3, 3), [">"] = (3, 3), ["<="] = (3, 3), [">="] = (3, 3), ["~="] = (3, 3), ["=="] = (3, 3),
        [".."] = (5, 4), // right associative
        ["+"] = (6, 6), ["-"] = (6, 6),
        ["*"] = (7, 7), ["/"] = (7, 7), ["%"] = (7, 7),
        ["^"] = (10, 9), // right associative
    };
    private const int UnaryPriority = 8;

    private void Exp(int limit = 0)
    {
        if (At("not") || At("-") || At("#")) { Next(); Exp(UnaryPriority); }
        else SimpleExp();
        while (Cur.Kind is LuaTokenKind.Operator or LuaTokenKind.Keyword
               && Binary.TryGetValue(Text(Cur), out var pr) && pr.Left > limit)
        {
            Next();
            Exp(pr.Right);
        }
    }

    private void SimpleExp()
    {
        switch (Cur.Kind)
        {
            case LuaTokenKind.Number:
            case LuaTokenKind.String:
                Next();
                return;
        }
        if (Accept("nil") || Accept("true") || Accept("false") || Accept("...")) return;
        if (At("{")) { Table(); return; }
        if (At("function")) { int line = Cur.Line; Next(); FuncBody(line); return; }
        SuffixedExp();
    }

    private ExpKind PrimaryExp()
    {
        if (AtName) { Next(); return ExpKind.Name; }
        if (At("("))
        {
            int line = Cur.Line;
            Next();
            Exp();
            ExpectMatch(")", "(", line);
            return ExpKind.Other;
        }
        Fail("unexpected symbol");
        return ExpKind.Other;
    }

    private ExpKind SuffixedExp()
    {
        var kind = PrimaryExp();
        while (true)
        {
            if (Accept(".")) { Name(); kind = ExpKind.Index; }
            else if (At("["))
            {
                Next(); Exp(); Expect("]");
                kind = ExpKind.Index;
            }
            else if (Accept(":")) { Name(); CallArgs(); kind = ExpKind.Call; }
            else if (At("(") || At("{") || Cur.Kind == LuaTokenKind.String) { CallArgs(); kind = ExpKind.Call; }
            else return kind;
        }
    }

    private void CallArgs()
    {
        if (Cur.Kind == LuaTokenKind.String) { Next(); return; }
        if (At("{")) { Table(); return; }
        if (!At("(")) Fail("function arguments expected");
        int line = Cur.Line;
        Next();
        if (!At(")")) ExpList();
        ExpectMatch(")", "(", line);
    }

    private void Table()
    {
        int line = Cur.Line;
        Expect("{");
        while (!At("}"))
        {
            if (At("["))
            {
                Next(); Exp(); Expect("]"); Expect("="); Exp();
            }
            else if (AtName && _p + 1 < _t.Count && _t[_p + 1].Is(_src, "=") && _t[_p + 1].Kind == LuaTokenKind.Operator)
            {
                Next(); Next(); Exp();
            }
            else Exp();
            if (!Accept(",") && !Accept(";")) break;
        }
        ExpectMatch("}", "{", line);
    }
}
