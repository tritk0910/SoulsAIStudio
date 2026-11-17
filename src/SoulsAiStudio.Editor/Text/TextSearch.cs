namespace SoulsAiStudio.Editor.Text;

/// <summary>One match found by <see cref="TextSearch"/>: 0-based line and UTF-16 column, plus the whole line's text.</summary>
public readonly record struct TextMatch(int Line, int Column, int Length, string LineText);

/// <summary>
/// Searches a whole text (e.g. a script that is not open in an editor) with exactly the find bar's matching
/// (<see cref="FindModel"/>: Match case, Whole word). Thread-safe: every call uses its own model. Allocates.
/// </summary>
public static class TextSearch
{
    /// <summary>Every non-overlapping match of <paramref name="query"/> in <paramref name="text"/> (lines split on '\n', '\r' dropped).</summary>
    /// <param name="maxMatches">Stops after this many matches.</param>
    public static List<TextMatch> FindAll(string text, string query, bool matchCase, bool wholeWord, int maxMatches = int.MaxValue)
    {
        var result = new List<TextMatch>();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
            return result;
        var model = new FindModel();
        model.SetQuery(query, matchCase, wholeWord);
        int qlen = model.Query.Length;
        if (qlen == 0)
            return result;
        Span<int> starts = stackalloc int[256];
        int lineNo = 0, pos = 0;
        while (pos <= text.Length && result.Count < maxMatches)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            int len = end - pos;
            if (len > 0 && text[end - 1] == '\r')
                len--;
            var line = text.AsSpan(pos, len);
            string? lineText = null;
            int col = 0;
            while (result.Count < maxMatches)
            {
                int n = model.MatchesInLine(line[col..], starts);
                if (n == 0)
                    break;
                for (int k = 0; k < n && result.Count < maxMatches; k++)
                {
                    lineText ??= new string(line);
                    result.Add(new TextMatch(lineNo, col + starts[k], qlen, lineText));
                }
                if (n < starts.Length)
                    break;
                col += starts[n - 1] + qlen; // very long line: continue after the last match found
            }
            if (nl < 0)
                break;
            pos = nl + 1;
            lineNo++;
        }
        return result;
    }

    /// <summary>Number of matches (same rules as <see cref="FindAll"/>).</summary>
    public static int Count(string text, string query, bool matchCase, bool wholeWord) =>
        FindAll(text, query, matchCase, wholeWord).Count;
}
