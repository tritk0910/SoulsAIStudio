using System.Collections.Frozen;

namespace SoulsAiStudio.Bench;

public enum Tk : byte { Name, Keyword, Number, String, LongString, Comment, LongComment, Op, Error }

public struct Token
{
    public Tk Kind;
    public int Start;
    public int Length;
    public int Line;
}

/// <summary>Carry-over state for line-by-line lexing (inside a long comment / long string).</summary>
public struct LexState
{
    public int LongLevel;   // -1 = not inside a long bracket
    public bool LongIsComment;
    public static LexState Initial => new() { LongLevel = -1 };
}

/// <summary>
/// Naive hand-written Lua 5.1 / HavokScript lexer used ONLY to estimate a lexing time budget.
/// Not the product parser. Handles names, keywords, numbers (dec/hex/exp), short strings with escapes,
/// long strings/comments with levels, line comments, multi-char ops (incl. HavokScript bitwise | & ~ << >>).
/// </summary>
public static class LuaLexer
{
    private static readonly FrozenSet<string> Keywords = new[]
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while", "goto",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KwLookup =
        Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool IsKeyword(ReadOnlySpan<char> s) => KwLookup.Contains(s);

    /// <summary>Lex a whole file.</summary>
    public static void Lex(string src, List<Token> tokens)
    {
        var st = LexState.Initial;
        LexRange(src.AsSpan(), 0, 1, ref st, tokens);
    }

    /// <summary>
    /// Lex a span. <paramref name="baseOffset"/> is added to token starts; <paramref name="line"/> is the
    /// line number of the first char. Long brackets that are not closed leave state set for the next call.
    /// </summary>
    public static int LexRange(ReadOnlySpan<char> s, int baseOffset, int line, ref LexState st, List<Token> tokens)
    {
        int i = 0, n = s.Length;

        if (st.LongLevel >= 0)
        {
            int start = i;
            int end = FindLongClose(s, i, st.LongLevel, ref line);
            var kind = st.LongIsComment ? Tk.LongComment : Tk.LongString;
            if (end < 0) { tokens.Add(new Token { Kind = kind, Start = baseOffset + start, Length = n - start, Line = line }); return line; }
            tokens.Add(new Token { Kind = kind, Start = baseOffset + start, Length = end - start, Line = line });
            st.LongLevel = -1;
            i = end;
        }

        while (i < n)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }

            int start = i;
            int tokLine = line;

            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i += 2;
                int lvl = LongOpenLevel(s, i);
                if (lvl >= 0)
                {
                    i += lvl + 2;
                    int end = FindLongClose(s, i, lvl, ref line);
                    if (end < 0) { st.LongLevel = lvl; st.LongIsComment = true; i = n; }
                    else i = end;
                    tokens.Add(new Token { Kind = Tk.LongComment, Start = baseOffset + start, Length = i - start, Line = tokLine });
                }
                else
                {
                    while (i < n && s[i] != '\n') i++;
                    tokens.Add(new Token { Kind = Tk.Comment, Start = baseOffset + start, Length = i - start, Line = tokLine });
                }
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                i++;
                while (i < n && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
                var kind = IsKeyword(s.Slice(start, i - start)) ? Tk.Keyword : Tk.Name;
                tokens.Add(new Token { Kind = kind, Start = baseOffset + start, Length = i - start, Line = tokLine });
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
            {
                if (c == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < n && char.IsAsciiHexDigit(s[i])) i++;
                }
                else
                {
                    while (i < n && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                    if (i < n && (s[i] == 'e' || s[i] == 'E'))
                    {
                        i++;
                        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
                        while (i < n && char.IsAsciiDigit(s[i])) i++;
                    }
                }
                tokens.Add(new Token { Kind = Tk.Number, Start = baseOffset + start, Length = i - start, Line = tokLine });
                continue;
            }

            if (c == '"' || c == '\'')
            {
                i++;
                bool closed = false;
                while (i < n)
                {
                    char d = s[i];
                    if (d == '\\') { i += 2; continue; }
                    if (d == '\n') break;
                    i++;
                    if (d == c) { closed = true; break; }
                }
                if (i > n) i = n;
                tokens.Add(new Token { Kind = closed ? Tk.String : Tk.Error, Start = baseOffset + start, Length = i - start, Line = tokLine });
                continue;
            }

            if (c == '[')
            {
                int lvl = LongOpenLevel(s, i);
                if (lvl >= 0)
                {
                    i += lvl + 2;
                    int end = FindLongClose(s, i, lvl, ref line);
                    if (end < 0) { st.LongLevel = lvl; st.LongIsComment = false; i = n; }
                    else i = end;
                    tokens.Add(new Token { Kind = Tk.LongString, Start = baseOffset + start, Length = i - start, Line = tokLine });
                    continue;
                }
            }

            // operators
            i++;
            if (i < n)
            {
                char d = s[i];
                if ((c == '=' || c == '~' || c == '<' || c == '>' || c == '!') && d == '=') i++;
                else if (c == '.' && d == '.') { i++; if (i < n && s[i] == '.') i++; }
                else if ((c == '<' && d == '<') || (c == '>' && d == '>') || (c == ':' && d == ':')) i++;
            }
            tokens.Add(new Token { Kind = Tk.Op, Start = baseOffset + start, Length = i - start, Line = tokLine });
        }
        return line;
    }

    /// <summary>If s[i] starts "[" "="* "[", returns the level, else -1.</summary>
    private static int LongOpenLevel(ReadOnlySpan<char> s, int i)
    {
        if (i >= s.Length || s[i] != '[') return -1;
        int j = i + 1, lvl = 0;
        while (j < s.Length && s[j] == '=') { lvl++; j++; }
        return (j < s.Length && s[j] == '[') ? lvl : -1;
    }

    /// <summary>Returns index just after the closing "]" "="*lvl "]", or -1 if not found.</summary>
    private static int FindLongClose(ReadOnlySpan<char> s, int i, int lvl, ref int line)
    {
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\n') line++;
            else if (c == ']')
            {
                int j = i + 1, k = 0;
                while (j < n && s[j] == '=' && k < lvl) { k++; j++; }
                if (k == lvl && j < n && s[j] == ']') return j + 1;
            }
            i++;
        }
        return -1;
    }
}

/// <summary>Goal-navigator-like outline built from tokens (block depth + Goal.X = function headers).</summary>
public sealed record OutlineItem(string Name, int Line, int EndLine, int AddSubGoalCount, int FirstAnimId);

public static class Outline
{
    public static List<OutlineItem> Build(string src, List<Token> toks)
    {
        var items = new List<OutlineItem>();
        var stack = new Stack<(string? Name, int Line, int Subgoals, int Anim)>();
        var span = src.AsSpan();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != Tk.Keyword && t.Kind != Tk.Name) continue;
            var text = span.Slice(t.Start, t.Length);
            if (t.Kind == Tk.Keyword)
            {
                if (text.SequenceEqual("function"))
                {
                    string? name = null;
                    // "A.B = function" form
                    if (i >= 4 && Is(span, toks[i - 1], "=") && toks[i - 2].Kind == Tk.Name && Is(span, toks[i - 3], ".") && toks[i - 4].Kind == Tk.Name)
                        name = string.Concat(span.Slice(toks[i - 4].Start, toks[i - 4].Length), ".", span.Slice(toks[i - 2].Start, toks[i - 2].Length));
                    // "function A.B(" / "function A:B(" / "function A(" form
                    else if (i + 1 < toks.Count && toks[i + 1].Kind == Tk.Name)
                    {
                        int j = i + 1;
                        int s0 = toks[j].Start, e0 = toks[j].Start + toks[j].Length;
                        while (j + 2 < toks.Count && (Is(span, toks[j + 1], ".") || Is(span, toks[j + 1], ":")) && toks[j + 2].Kind == Tk.Name)
                        { j += 2; e0 = toks[j].Start + toks[j].Length; }
                        name = src.Substring(s0, e0 - s0);
                    }
                    stack.Push((name, t.Line, 0, -1));
                }
                else if (text.SequenceEqual("if") || text.SequenceEqual("do") || text.SequenceEqual("repeat"))
                    stack.Push((null, t.Line, 0, -1));
                else if (text.SequenceEqual("end") || text.SequenceEqual("until"))
                {
                    if (stack.Count == 0) continue;
                    var top = stack.Pop();
                    if (top.Name != null)
                        items.Add(new OutlineItem(top.Name, top.Line, t.Line, top.Subgoals, top.Anim));
                    // bubble sub-goal counts to the enclosing named function
                    if (top.Name == null && stack.Count > 0)
                    {
                        var parent = stack.Pop();
                        stack.Push((parent.Name, parent.Line, parent.Subgoals + top.Subgoals, parent.Anim < 0 ? top.Anim : parent.Anim));
                    }
                }
            }
            else if (text.SequenceEqual("AddSubGoal") && stack.Count > 0)
            {
                // AddSubGoal ( GOAL , life , animId
                int anim = -1;
                if (i + 5 < toks.Count && toks[i + 5].Kind == Tk.Number && int.TryParse(span.Slice(toks[i + 5].Start, toks[i + 5].Length), out var a))
                    anim = a;
                var top = stack.Pop();
                stack.Push((top.Name, top.Line, top.Subgoals + 1, top.Anim < 0 ? anim : top.Anim));
            }
        }
        return items;
    }

    private static bool Is(ReadOnlySpan<char> src, Token t, string s) => t.Kind == Tk.Op && src.Slice(t.Start, t.Length).SequenceEqual(s);
}
