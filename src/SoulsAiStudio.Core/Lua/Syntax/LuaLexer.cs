namespace SoulsAiStudio.Core.Lua.Syntax;

public enum LuaTokenKind
{
    Identifier, Keyword, Number, String, Comment, Operator, EndOfFile,
}

/// <summary>A token with its absolute offset and 0-based line/column of its first character.</summary>
public readonly record struct LuaToken(LuaTokenKind Kind, int Start, int Length, int Line, int Column)
{
    public ReadOnlySpan<char> Text(string source) => source.AsSpan(Start, Length);
    public bool Is(string source, string text) => Text(source).SequenceEqual(text);
}

/// <summary>
/// Lua 5.1 lexer (HavokScript-compatible for the constructs in Sekiro AI). Lossless with respect to positions:
/// every token knows where it came from, so analysis results can point back into the editor. Comments are
/// emitted as tokens so they can be kept by later rewriting passes.
/// </summary>
public static class LuaLexer
{
    private static readonly HashSet<string> Keywords =
    [
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    ];

    public static bool IsKeyword(ReadOnlySpan<char> s) => Keywords.Contains(s.ToString());

    public static List<LuaToken> Tokenize(string src)
    {
        var tokens = new List<LuaToken>(src.Length / 4);
        int i = 0, line = 0, lineStart = 0;

        void Advance(int to)
        {
            for (; i < to; i++)
                if (src[i] == '\n') { line++; lineStart = i + 1; }
        }

        while (i < src.Length)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c)) { Advance(i + 1); continue; }

            int start = i, tokLine = line, tokCol = i - lineStart;
            LuaTokenKind kind;

            if (c == '-' && Peek(src, i + 1) == '-')
            {
                kind = LuaTokenKind.Comment;
                int level = LongBracketLevel(src, i + 2);
                int end = level >= 0 ? FindLongBracketEnd(src, i + 2, level) : IndexOfOrEnd(src, '\n', i);
                Advance(end);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int end = i + 1;
                while (end < src.Length && (char.IsLetterOrDigit(src[end]) || src[end] == '_')) end++;
                kind = IsKeyword(src.AsSpan(i, end - i)) ? LuaTokenKind.Keyword : LuaTokenKind.Identifier;
                Advance(end);
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(src, i + 1))))
            {
                int end = i;
                if (c == '0' && (Peek(src, i + 1) is 'x' or 'X'))
                {
                    end += 2;
                    while (end < src.Length && Uri.IsHexDigit(src[end])) end++;
                }
                else
                {
                    while (end < src.Length && (char.IsDigit(src[end]) || src[end] == '.')) end++;
                    if (end < src.Length && src[end] is 'e' or 'E')
                    {
                        end++;
                        if (end < src.Length && src[end] is '+' or '-') end++;
                        while (end < src.Length && char.IsDigit(src[end])) end++;
                    }
                }
                kind = LuaTokenKind.Number;
                Advance(end);
            }
            else if (c is '"' or '\'')
            {
                int end = i + 1;
                while (end < src.Length && src[end] != c && src[end] != '\n')
                    end += src[end] == '\\' && end + 1 < src.Length ? 2 : 1;
                if (end < src.Length && src[end] == c) end++;
                kind = LuaTokenKind.String;
                Advance(end);
            }
            else if (c == '[' && LongBracketLevel(src, i) is >= 0 and var lvl)
            {
                kind = LuaTokenKind.String;
                Advance(FindLongBracketEnd(src, i, lvl));
            }
            else
            {
                kind = LuaTokenKind.Operator;
                int len = MatchOperator(src, i);
                Advance(i + len);
            }

            tokens.Add(new LuaToken(kind, start, i - start, tokLine, tokCol));
        }

        tokens.Add(new LuaToken(LuaTokenKind.EndOfFile, src.Length, 0, line, src.Length - lineStart));
        return tokens;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static int IndexOfOrEnd(string s, char c, int from)
    {
        int idx = s.IndexOf(c, from);
        return idx < 0 ? s.Length : idx;
    }

    /// <summary>Returns n for <c>[==...=[</c> (n equals signs) starting at <paramref name="i"/>, else -1.</summary>
    private static int LongBracketLevel(string s, int i)
    {
        if (Peek(s, i) != '[') return -1;
        int j = i + 1, level = 0;
        while (Peek(s, j) == '=') { level++; j++; }
        return Peek(s, j) == '[' ? level : -1;
    }

    private static int FindLongBracketEnd(string s, int openAt, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int idx = s.IndexOf(close, openAt + level + 2, StringComparison.Ordinal);
        return idx < 0 ? s.Length : idx + close.Length;
    }

    private static int MatchOperator(string s, int i)
    {
        if (i + 2 < s.Length && s.AsSpan(i, 3).SequenceEqual("...")) return 3;
        if (i + 1 < s.Length)
        {
            ReadOnlySpan<char> two = s.AsSpan(i, 2);
            if (two is "==" or "~=" or "<=" or ">=" or "..") return 2;
        }
        return 1;
    }
}
