using System.Text;

namespace SoulsAiStudio.Core.Lua.Syntax;

/// <summary>
/// Conservative "prettier" for Lua: re-indents by block structure, normalizes spacing between tokens, trims trailing
/// whitespace, keeps at most one blank line in a row and ends the file with exactly one newline. Comments and the
/// content of multi-line strings/comments are never touched. Safety net: the result is re-tokenized and must give
/// the same token sequence as the input, and a script with a syntax error is not formatted at all.
/// </summary>
public static class LuaFormatter
{
    /// <summary>
    /// Formats <paramref name="src"/>. Returns null when it can't be done safely (syntax error, or the formatted text
    /// would not have the same tokens); otherwise the formatted text (equal to the input when already formatted).
    /// </summary>
    public static string? Format(string src, int indentSize = 4)
    {
        if (src.Length == 0) return src;
        if (LuaSyntaxChecker.Check(src) != null) return null;

        bool crlf = src.Contains("\r\n", StringComparison.Ordinal);
        string text = src.Replace("\r\n", "\n");
        bool bom = text.Length > 0 && text[0] == '﻿';
        if (bom) text = text[1..];

        var all = LuaLexer.Tokenize(text);
        string output = Layout(text, all, indentSize);

        // Safety net: same tokens before and after.
        if (!SameTokens(text, all, output, LuaLexer.Tokenize(output))) return null;

        if (crlf) output = output.Replace("\n", "\r\n");
        if (bom) output = "﻿" + output;
        return output;
    }

    private static bool SameTokens(string a, List<LuaToken> ta, string b, List<LuaToken> tb)
    {
        if (ta.Count != tb.Count) return false;
        for (int i = 0; i < ta.Count; i++)
        {
            var x = a.AsSpan(ta[i].Start, ta[i].Length);
            var y = b.AsSpan(tb[i].Start, tb[i].Length);
            if (ta[i].Kind == LuaTokenKind.Comment && x.IndexOf((char)10) < 0) { x = x.TrimEnd(); y = y.TrimEnd(); }
            if (ta[i].Kind != tb[i].Kind || !x.SequenceEqual(y)) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------ layout

    private static readonly HashSet<string> BinaryOps = ["+", "-", "*", "/", "%", "^", "..", "==", "~=", "<", "<=", ">", ">=", "="];

    /// <summary>An open block/bracket: the line it was opened on, and whether that line was itself a continuation line.</summary>
    private readonly record struct Open(int Line, bool Cont = false);

    private static string Layout(string text, List<LuaToken> all, int indentSize)
    {
        var lines = text.Split('\n');
        // Tokens by the line they start on (EndOfFile excluded); lines that begin inside a multi-line string/comment.
        var byLine = new List<LuaToken>?[lines.Length];
        var verbatim = new bool[lines.Length];
        foreach (var t in all)
        {
            if (t.Kind == LuaTokenKind.EndOfFile) continue;
            (byLine[t.Line] ??= []).Add(t);
            if (t.Kind is LuaTokenKind.String or LuaTokenKind.Comment)
            {
                int extra = 0;
                foreach (char c in text.AsSpan(t.Start, t.Length)) if (c == '\n') extra++;
                for (int k = 1; k <= extra && t.Line + k < lines.Length; k++) verbatim[t.Line + k] = true;
            }
        }

        var sb = new StringBuilder(text.Length + 64);
        var stack = new List<Open>();
        int loopDo = 0;                 // while/for waiting for their `do`
        int blank = 0;                  // blank lines waiting to be written
        bool wroteAny = false;
        bool prevContinues = false;     // previous code line ends with a binary operator / and / or
        bool prevWasContinuation = false;
        // Comment-only lines wait for the next code line: they move by the same amount as it did (their relative
        // indentation, e.g. commented-out code, is kept).
        var pendingComments = new List<(bool BlankBefore, string Text, int OldIndent)>();
        int lastDelta = 0;

        for (int l = 0; l < lines.Length; l++)
        {
            var toks = byLine[l];

            if (verbatim[l])
            {
                FlushComments(lastDelta);
                FlushBlank();
                sb.Append(lines[l]).Append('\n');
                wroteAny = true;
                if (toks != null) foreach (var t in toks) Track(t, l, false);
                prevContinues = false; prevWasContinuation = false;
                continue;
            }

            if (toks == null) { blank++; continue; } // blank (or whitespace-only) line

            if (IsCommentOnly(text, toks))
            {
                pendingComments.Add((blank > 0 && (wroteAny || pendingComments.Count > 0), lines[l].Trim(), IndentWidth(lines[l])));
                blank = 0;
                continue;
            }

            // ---- indent: pop the closers the line starts with, count the distinct lines still open
            int first = 0;
            var mids = new List<Open>();
            while (first < toks.Count)
            {
                var t = toks[first];
                if (t.Kind != LuaTokenKind.Keyword && t.Kind != LuaTokenKind.Operator) break;
                string s = text.Substring(t.Start, t.Length);
                if (s is "end" or "until" or "}" or ")" or "]") { Pop(); first++; }
                else if (s is "else" or "elseif") { mids.Add(Pop() ?? new Open(l)); first++; }
                else break;
            }
            int depth = DistinctLines(stack);
            bool startsContinuation = first < toks.Count && IsContinuationStart(text, toks[first]);
            bool continuation = (prevContinues || startsContinuation)
                                && first < toks.Count && toks[first].Kind != LuaTokenKind.Comment;
            if (continuation) depth++;
            stack.AddRange(mids);

            // ---- build the line
            lastDelta = depth * indentSize - IndentWidth(lines[l]);
            FlushComments(lastDelta);
            FlushBlank();
            sb.Append(' ', depth * indentSize);
            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                if (i > 0)
                {
                    var p = toks[i - 1];
                    int prevEnd = p.Start + p.Length;
                    sb.Append(Gap(text, toks, i, t.Start > prevEnd));
                }
                // a multi-line token continues on the following lines (kept verbatim): emit only its first line here
                var span = text.AsSpan(t.Start, t.Length);
                int nl = span.IndexOf('\n');
                sb.Append(nl >= 0 ? span[..nl] : span);
            }
            sb.Append('\n');
            wroteAny = true;

            for (int i = first; i < toks.Count; i++) Track(toks[i], l, continuation);
            prevContinues = LastEndsExpression(text, toks);
            prevWasContinuation = continuation;
        }

        FlushComments(lastDelta);
        return sb.ToString().TrimEnd('\n') + "\n";

        void FlushComments(int delta)
        {
            foreach (var c in pendingComments)
            {
                if (c.BlankBefore) sb.Append('\n');
                sb.Append(' ', Math.Max(0, c.OldIndent + delta)).Append(c.Text).Append('\n');
                wroteAny = true;
            }
            pendingComments.Clear();
        }

        void FlushBlank()
        {
            if (blank > 0 && wroteAny) sb.Append('\n'); // at most one blank line; none at the start of the file
            blank = 0;
        }

        Open? Pop()
        {
            if (stack.Count == 0) return null;
            var o = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return o;
        }

        void Track(LuaToken t, int line, bool cont)
        {
            string s = text.Substring(t.Start, t.Length);
            if (t.Kind == LuaTokenKind.Keyword)
            {
                if (s is "while" or "for") { stack.Add(new Open(line, cont)); loopDo++; }
                else if (s == "do") { if (loopDo > 0) loopDo--; else stack.Add(new Open(line, cont)); }
                else if (s is "function" or "if" or "repeat") stack.Add(new Open(line, cont));
                else if (s is "end" or "until") Pop();
                else if (s is "else" or "elseif") { var o = Pop(); if (o != null) stack.Add(o.Value); }
            }
            else if (t.Kind == LuaTokenKind.Operator)
            {
                if (s is "(" or "{" or "[") stack.Add(new Open(line, cont));
                else if (s is ")" or "}" or "]") Pop();
            }
        }
    }

    /// <summary>The line holds only single-line comments (a multi-line comment is laid out like code).</summary>
    private static bool IsCommentOnly(string text, List<LuaToken> toks) =>
        toks.All(t => t.Kind == LuaTokenKind.Comment && text.AsSpan(t.Start, t.Length).IndexOf((char)10) < 0);

    private static int IndentWidth(string line)
    {
        int w = 0;
        foreach (char c in line)
        {
            if (c == ' ') w++;
            else if (c == '	') w += 4;
            else break;
        }
        return w;
    }

    private static int DistinctLines(List<Open> stack)
    {
        int n = 0, prev = -1;
        foreach (var o in stack)
            if (o.Line != prev) { n += o.Cont ? 2 : 1; prev = o.Line; }
        return n;
    }

    private static bool IsContinuationStart(string text, LuaToken t)
    {
        var s = text.AsSpan(t.Start, t.Length);
        if (t.Kind == LuaTokenKind.Keyword) return s is "and" or "or";
        return t.Kind == LuaTokenKind.Operator && s is "." or ":" or ".." or "+" or "*" or "/" or "%" or "^" or "==" or "~=" or "<" or "<=" or ">" or ">=";
    }

    /// <summary>The line's last code token is a binary operator, <c>=</c>, <c>and</c> or <c>or</c>: the expression goes on.</summary>
    private static bool LastEndsExpression(string text, List<LuaToken> toks)
    {
        for (int i = toks.Count - 1; i >= 0; i--)
        {
            var t = toks[i];
            if (t.Kind == LuaTokenKind.Comment) continue;
            string s = text.Substring(t.Start, t.Length);
            if (t.Kind == LuaTokenKind.Keyword) return s is "and" or "or";
            return t.Kind == LuaTokenKind.Operator && BinaryOps.Contains(s);
        }
        return false;
    }

    // ------------------------------------------------------------------------------------------------ spacing

    private static bool IsWord(LuaToken t, string s) =>
        t.Kind is LuaTokenKind.Identifier or LuaTokenKind.Number or LuaTokenKind.String or LuaTokenKind.Keyword || s == "...";

    /// <summary>The whitespace to print between token i-1 and token i of one line.</summary>
    private static string Gap(string text, List<LuaToken> toks, int i, bool hadSpace)
    {
        var P = toks[i - 1];
        var C = toks[i];
        string p = text.Substring(P.Start, P.Length);
        string c = text.Substring(C.Start, C.Length);
        string orig = hadSpace ? " " : "";

        if (C.Kind == LuaTokenKind.Comment) return hadSpace ? text.Substring(P.Start + P.Length, C.Start - (P.Start + P.Length)) : " ";
        if (P.Kind == LuaTokenKind.Comment) return " ";
        bool pOp = P.Kind == LuaTokenKind.Operator, cOp = C.Kind == LuaTokenKind.Operator;

        if (cOp && c is "," or ";") return "";
        if (pOp && p is "," or ";") return " ";
        if (pOp && p is "." or ":") return "";
        if (cOp && c is "." or ":") return "";
        if (cOp && c is ")" or "]") return "";
        if (pOp && p is "(" or "[") return "";
        if (pOp && p == "{") return c == "}" ? "" : orig;
        if (cOp && c == "}") return orig;

        bool pUnary = pOp && (p == "#" || p == "-" && IsUnaryMinus(text, toks, i - 1));
        if (pUnary) return p == "-" && c == "-" ? " " : "";
        if (P.Kind == LuaTokenKind.Keyword && p == "not") return " ";

        if (cOp && c == "(")
        {
            if (P.Kind is LuaTokenKind.Identifier or LuaTokenKind.String || pOp && p is ")" or "]" or "}") return "";
            if (P.Kind == LuaTokenKind.Keyword) return p == "function" ? orig : " ";
        }
        if (cOp && c == "[" && (P.Kind == LuaTokenKind.Identifier || pOp && p is ")" or "]" or "}")) return "";
        if (cOp && c == "{" && (P.Kind == LuaTokenKind.Identifier || pOp && p is ")" or "]")) return orig;

        bool cBinary = cOp && BinaryOps.Contains(c) && !(c == "-" && IsUnaryMinus(text, toks, i));
        bool pBinary = pOp && BinaryOps.Contains(p);
        if (cBinary || pBinary || cOp && c == "-") return " ";

        if (IsWord(P, p) && IsWord(C, c)) return " ";
        if (pOp && p is ")" or "]" or "}" && IsWord(C, c)) return " ";
        if (cOp && c is "{" or "(" or "[" && IsWord(P, p)) return " ";
        return orig; // anything unusual: keep the author's choice (collapsed to 0 or 1 space)
    }

    /// <summary>A <c>-</c> with no operand on its left: first on the line, after an operator other than a closing bracket, or after a keyword other than end/true/false/nil.</summary>
    private static bool IsUnaryMinus(string text, List<LuaToken> toks, int idx)
    {
        if (idx == 0) return true;
        var P = toks[idx - 1];
        string p = text.Substring(P.Start, P.Length);
        return P.Kind switch
        {
            LuaTokenKind.Operator => !(p is ")" or "]" or "}" or "..."),
            LuaTokenKind.Keyword => !(p is "end" or "true" or "false" or "nil"),
            _ => false,
        };
    }
}
