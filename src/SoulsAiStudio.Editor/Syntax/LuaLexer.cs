namespace SoulsAiStudio.Editor.Syntax;

/// <summary>Token classes produced by <see cref="LuaLexer"/>. Values are stored as color bytes per char.</summary>
public enum LuaTokenKind : byte
{
    Default = 0,
    Keyword,
    Literal,    // nil true false
    Number,
    String,
    Comment,
    Identifier,
    Constant,   // GOAL_COMMON_*, TARGET_*, INTERUPT_*, ALL_CAPS_WITH_UNDERSCORE
    Function,   // identifier followed by '(' / string / '{'
    Operator,
    Count,
}

/// <summary>Lexer state carried from the end of one line to the start of the next (long strings / comments).</summary>
public readonly struct LuaLexState : IEquatable<LuaLexState>
{
    /// <summary>0 = normal, 1 = inside long string, 2 = inside long comment.</summary>
    public readonly byte Mode;

    /// <summary>Number of '=' in the open long bracket.</summary>
    public readonly byte Level;

    public LuaLexState(byte mode, byte level)
    {
        Mode = mode;
        Level = level;
    }

    public static LuaLexState Normal => default;
    public bool IsNormal => Mode == 0;

    public bool Equals(LuaLexState other) => Mode == other.Mode && Level == other.Level;
    public override bool Equals(object? obj) => obj is LuaLexState s && Equals(s);
    public override int GetHashCode() => Mode << 8 | Level;
    public static bool operator ==(LuaLexState a, LuaLexState b) => a.Equals(b);
    public static bool operator !=(LuaLexState a, LuaLexState b) => !a.Equals(b);
    public override string ToString() => Mode == 0 ? "Normal" : $"{(Mode == 1 ? "LongString" : "LongComment")}({Level})";
}

/// <summary>
/// Line-at-a-time Lua 5.1 lexer for syntax coloring. Allocation-free: writes one <see cref="LuaTokenKind"/>
/// byte per char and returns the state to carry into the next line.
/// </summary>
public static class LuaLexer
{
    static readonly string[] Keywords =
    {
        "and", "break", "do", "else", "elseif", "end", "for", "function", "goto", "if", "in",
        "local", "not", "or", "repeat", "return", "then", "until", "while",
    };

    static readonly string[] Literals = { "nil", "true", "false" };

    static readonly string[] DefaultConstantPrefixes = { "GOAL_", "TARGET_", "INTERUPT_", "INTERRUPT_", "AI_", "PLATOON_", "POINT_", "DIST_", "NPC_ATK_", "SP_" };

    static string[] _constantPrefixes = DefaultConstantPrefixes;

    /// <summary>
    /// Identifiers starting with one of these prefixes are colored as <see cref="LuaTokenKind.Constant"/>
    /// (in addition to ALL_CAPS identifiers that contain '_'). Set once at startup (e.g. from GameProfile).
    /// </summary>
    public static IReadOnlyList<string> ConstantPrefixes
    {
        get => _constantPrefixes;
        set => _constantPrefixes = value?.ToArray() ?? DefaultConstantPrefixes;
    }

    /// <summary>Colors one line. <paramref name="kinds"/> must be at least <paramref name="line"/>.Length long.</summary>
    public static LuaLexState ColorizeLine(ReadOnlySpan<char> line, Span<byte> kinds, LuaLexState state)
    {
        int n = line.Length;
        int i = 0;

        if (!state.IsNormal)
        {
            var kind = state.Mode == 1 ? LuaTokenKind.String : LuaTokenKind.Comment;
            int close = FindLongClose(line, 0, state.Level);
            if (close < 0)
            {
                Fill(kinds, 0, n, kind);
                return state;
            }
            Fill(kinds, 0, close, kind);
            i = close;
            state = LuaLexState.Normal;
        }

        while (i < n)
        {
            char c = line[i];

            if (c == ' ' || c == '\t' || c == '\f' || c == '\v')
            {
                kinds[i++] = (byte)LuaTokenKind.Default;
                continue;
            }

            // Comments
            if (c == '-' && i + 1 < n && line[i + 1] == '-')
            {
                int level = LongOpenLevel(line, i + 2);
                if (level >= 0)
                {
                    int bodyStart = i + 2 + level + 2;
                    int close = FindLongClose(line, bodyStart, level);
                    if (close < 0)
                    {
                        Fill(kinds, i, n, LuaTokenKind.Comment);
                        return new LuaLexState(2, (byte)Math.Min(level, 255));
                    }
                    Fill(kinds, i, close, LuaTokenKind.Comment);
                    i = close;
                    continue;
                }
                Fill(kinds, i, n, LuaTokenKind.Comment);
                return LuaLexState.Normal;
            }

            // Long strings
            if (c == '[')
            {
                int level = LongOpenLevel(line, i);
                if (level >= 0)
                {
                    int bodyStart = i + level + 2;
                    int close = FindLongClose(line, bodyStart, level);
                    if (close < 0)
                    {
                        Fill(kinds, i, n, LuaTokenKind.String);
                        return new LuaLexState(1, (byte)Math.Min(level, 255));
                    }
                    Fill(kinds, i, close, LuaTokenKind.String);
                    i = close;
                    continue;
                }
            }

            // Quoted strings
            if (c == '"' || c == '\'')
            {
                int j = i + 1;
                while (j < n)
                {
                    char d = line[j];
                    if (d == '\\')
                    {
                        j += 2;
                        continue;
                    }
                    j++;
                    if (d == c)
                        break;
                }
                j = Math.Min(j, n);
                Fill(kinds, i, j, LuaTokenKind.String);
                i = j;
                continue;
            }

            // Numbers
            if (IsDigit(c) || (c == '.' && i + 1 < n && IsDigit(line[i + 1])))
            {
                int j = ScanNumber(line, i);
                Fill(kinds, i, j, LuaTokenKind.Number);
                i = j;
                continue;
            }

            // Identifiers / keywords
            if (IsIdentStart(c))
            {
                int j = i + 1;
                while (j < n && IsIdentPart(line[j]))
                    j++;
                var word = line[i..j];
                LuaTokenKind kind;
                if (IsOneOf(word, Keywords))
                    kind = LuaTokenKind.Keyword;
                else if (IsOneOf(word, Literals))
                    kind = LuaTokenKind.Literal;
                else if (IsConstant(word))
                    kind = LuaTokenKind.Constant;
                else
                {
                    int k = j;
                    while (k < n && (line[k] == ' ' || line[k] == '\t'))
                        k++;
                    kind = k < n && (line[k] == '(' || line[k] == '"' || line[k] == '\'' || line[k] == '{')
                        ? LuaTokenKind.Function
                        : LuaTokenKind.Identifier;
                }
                Fill(kinds, i, j, kind);
                i = j;
                continue;
            }

            kinds[i++] = (byte)LuaTokenKind.Operator;
        }

        return state;
    }

    /// <summary>Convenience for tests/tools: tokens of one line as (start, length, kind), adjacent same-kind chars merged.</summary>
    public static List<(int Start, int Length, LuaTokenKind Kind)> TokenizeLine(string line, ref LuaLexState state)
    {
        var kinds = new byte[line.Length];
        state = ColorizeLine(line, kinds, state);
        var result = new List<(int, int, LuaTokenKind)>();
        int s = 0;
        for (int i = 1; i <= line.Length; i++)
        {
            if (i == line.Length || kinds[i] != kinds[s])
            {
                if (i > s)
                    result.Add((s, i - s, (LuaTokenKind)kinds[s]));
                s = i;
            }
        }
        return result;
    }

    static void Fill(Span<byte> kinds, int from, int to, LuaTokenKind kind) => kinds[from..to].Fill((byte)kind);

    static bool IsDigit(char c) => c >= '0' && c <= '9';

    static bool IsHex(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c);

    static bool IsOneOf(ReadOnlySpan<char> word, string[] set)
    {
        foreach (var s in set)
            if (word.SequenceEqual(s))
                return true;
        return false;
    }

    static bool IsConstant(ReadOnlySpan<char> word)
    {
        foreach (var p in _constantPrefixes)
            if (word.Length > p.Length && word.StartsWith(p, StringComparison.Ordinal))
                return true;

        bool hasUnderscore = false;
        bool hasLetter = false;
        foreach (var c in word)
        {
            if (c == '_')
                hasUnderscore = true;
            else if (c >= 'A' && c <= 'Z')
                hasLetter = true;
            else if (!IsDigit(c))
                return false;
        }
        return hasUnderscore && hasLetter;
    }

    /// <summary>If a long bracket "[", "="*level, "[" starts at <paramref name="i"/>, returns level; else -1.</summary>
    static int LongOpenLevel(ReadOnlySpan<char> line, int i)
    {
        if (i >= line.Length || line[i] != '[')
            return -1;
        int j = i + 1;
        while (j < line.Length && line[j] == '=')
            j++;
        return j < line.Length && line[j] == '[' ? j - i - 1 : -1;
    }

    /// <summary>Finds "]" + "="*level + "]" at or after <paramref name="from"/>; returns the index just past it, or -1.</summary>
    static int FindLongClose(ReadOnlySpan<char> line, int from, int level)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] != ']')
                continue;
            int j = i + 1;
            int eq = 0;
            while (j < line.Length && line[j] == '=')
            {
                j++;
                eq++;
            }
            if (eq == level && j < line.Length && line[j] == ']')
                return j + 1;
        }
        return -1;
    }

    static int ScanNumber(ReadOnlySpan<char> line, int i)
    {
        int n = line.Length;
        int j = i;
        if (line[j] == '0' && j + 1 < n && (line[j + 1] == 'x' || line[j + 1] == 'X'))
        {
            j += 2;
            while (j < n && (IsHex(line[j]) || line[j] == '.'))
                j++;
            if (j < n && (line[j] == 'p' || line[j] == 'P'))
            {
                j++;
                if (j < n && (line[j] == '+' || line[j] == '-'))
                    j++;
                while (j < n && IsDigit(line[j]))
                    j++;
            }
            return j;
        }

        while (j < n && (IsDigit(line[j]) || line[j] == '.'))
            j++;
        if (j < n && (line[j] == 'e' || line[j] == 'E'))
        {
            int k = j + 1;
            if (k < n && (line[k] == '+' || line[k] == '-'))
                k++;
            if (k < n && IsDigit(line[k]))
            {
                j = k;
                while (j < n && IsDigit(line[j]))
                    j++;
            }
        }
        return j;
    }
}
