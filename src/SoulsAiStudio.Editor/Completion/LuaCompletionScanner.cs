using SoulsAiStudio.Editor.Syntax;

namespace SoulsAiStudio.Editor.Completion;

/// <summary>
/// Collects the identifiers of one Lua file for completion, using the coloring lexer (so names inside strings and
/// comments are ignored). Classification:
/// <list type="bullet">
/// <item><c>x:Name</c> → <see cref="CompletionKind.Method"/>; <c>x.Name</c> → <see cref="CompletionKind.Field"/>;</item>
/// <item>names after <c>local</c>, function parameters and <c>for</c> variables → <see cref="CompletionKind.Local"/>;</item>
/// <item>GOAL_/TARGET_/… and ALL_CAPS → <see cref="CompletionKind.Constant"/>; called names → <see cref="CompletionKind.Function"/>;</item>
/// <item>anything else → <see cref="CompletionKind.Variable"/>.</item>
/// </list>
/// Pure and thread-safe: run it on a worker thread. Allocates one string per distinct name.
/// </summary>
public static class LuaCompletionScanner
{
    /// <param name="skipLine">0-based line of the word being typed (excluded so a half-typed word is not suggested); -1 = none.</param>
    /// <param name="skipColumn">Column inside that word (usually the cursor column).</param>
    public static CompletionSet Scan(string text, int skipLine = -1, int skipColumn = -1)
    {
        var globals = new Dictionary<string, CompletionKind>(StringComparer.Ordinal);
        var methods = new HashSet<string>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var gl = globals.GetAlternateLookup<ReadOnlySpan<char>>();
        var ml = methods.GetAlternateLookup<ReadOnlySpan<char>>();
        var fl = fields.GetAlternateLookup<ReadOnlySpan<char>>();

        byte[] kinds = new byte[256];
        var state = LuaLexState.Normal;
        var rest = text.AsSpan();
        int lineNo = 0;
        int declMode = 0; // 0 none, 1 after 'local', 2 function parameters, 3 for-variables
        bool pendingFunction = false;

        while (true)
        {
            int nl = rest.IndexOf('\n');
            var line = nl < 0 ? rest : rest[..nl];
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];
            if (kinds.Length < line.Length)
                kinds = new byte[Math.Max(line.Length, kinds.Length * 2)];
            state = LuaLexer.ColorizeLine(line, kinds, state);

            if (declMode != 2)
                declMode = 0;

            int i = 0;
            while (i < line.Length)
            {
                var kind = (LuaTokenKind)kinds[i];
                int j = i + 1;
                while (j < line.Length && kinds[j] == (byte)kind && (kind != LuaTokenKind.Operator && kind != LuaTokenKind.Default))
                    j++;

                switch (kind)
                {
                    case LuaTokenKind.Keyword:
                    {
                        var w = line[i..j];
                        if (w.SequenceEqual("local"))
                            declMode = 1;
                        else if (w.SequenceEqual("function"))
                            pendingFunction = true; // 'local function f' keeps declMode = 1 for f
                        else if (w.SequenceEqual("for"))
                            declMode = 3;
                        else
                            declMode = 0;
                        break;
                    }
                    case LuaTokenKind.Identifier:
                    case LuaTokenKind.Constant:
                    case LuaTokenKind.Function:
                    {
                        var w = line[i..j];
                        bool skip = lineNo == skipLine && skipColumn >= i && skipColumn <= j;
                        char prev = PrevSignificant(line, i, out int prevIndex);
                        if (skip)
                            break;
                        if (prev == ':' && !(prevIndex > 0 && line[prevIndex - 1] == ':'))
                        {
                            ml.Add(w);
                        }
                        else if (prev == '.' && !(prevIndex > 0 && line[prevIndex - 1] == '.'))
                        {
                            fl.Add(w);
                        }
                        else
                        {
                            var k = declMode != 0 ? CompletionKind.Local
                                : kind == LuaTokenKind.Constant ? CompletionKind.Constant
                                : kind == LuaTokenKind.Function ? CompletionKind.Function
                                : CompletionKind.Variable;
                            if (!gl.TryGetValue(w, out var old) || k < old)
                                gl[w] = k;
                        }
                        break;
                    }
                    case LuaTokenKind.Operator:
                    {
                        char c = line[i];
                        if (c == '(' && pendingFunction)
                        {
                            declMode = 2;
                            pendingFunction = false;
                        }
                        else if (c == ')' && declMode == 2)
                            declMode = 0;
                        else if (c != ',' && !(c == '.' && declMode == 2)) // ',' continues a name list; '...' in params
                            declMode = declMode == 2 ? 2 : 0;
                        break;
                    }
                    case LuaTokenKind.Default:
                        break;
                    default:
                        if (declMode != 2)
                            declMode = 0;
                        break;
                }
                i = j;
            }

            if (nl < 0)
                break;
            rest = rest[(nl + 1)..];
            lineNo++;
        }

        // Keywords/literals never come back as identifiers from the lexer, so no filtering needed.
        var g = new CompletionItem[globals.Count];
        int n = 0;
        foreach (var kv in globals)
            g[n++] = new CompletionItem(kv.Key, kv.Value);
        return new CompletionSet(g,
            methods.Select(m => new CompletionItem(m, CompletionKind.Method)).ToArray(),
            fields.Select(f => new CompletionItem(f, CompletionKind.Field)).ToArray());
    }

    static char PrevSignificant(ReadOnlySpan<char> line, int index, out int at)
    {
        at = index - 1;
        while (at >= 0 && (line[at] == ' ' || line[at] == '\t'))
            at--;
        return at >= 0 ? line[at] : '\0';
    }
}
