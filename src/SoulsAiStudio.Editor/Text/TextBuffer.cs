// Derived from ImGuiColorTextEditNet (MIT) Editor/TextEditorText.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
using System.Text;

namespace SoulsAiStudio.Editor.Text;

/// <summary>
/// Line-based text storage. Internally lines are split on '\n'; '\r' is dropped and the file's
/// dominant line ending is remembered so <see cref="GetText()"/> round-trips LF and CRLF files.
/// </summary>
internal sealed class TextBuffer
{
    readonly List<Line> _lines = new() { new Line() };

    /// <summary>Incremented on every content change.</summary>
    public long Version { get; private set; }

    /// <summary>Line terminator used by <see cref="GetText()"/> ("\n" or "\r\n").</summary>
    public string LineEnding { get; set; } = "\n";

    /// <summary>(index, count): lines were inserted starting at index.</summary>
    public event Action<int, int>? LinesInserted;

    /// <summary>(index, count): lines [index, index+count) were removed.</summary>
    public event Action<int, int>? LinesRemoved;

    /// <summary>(first, last) inclusive: content of these lines changed (after insert/remove events).</summary>
    public event Action<int, int>? LinesChanged;

    /// <summary>All text was replaced.</summary>
    public event Action? Reset;

    public int LineCount => _lines.Count;

    public Line this[int index] => _lines[index];

    public ReadOnlySpan<char> GetLineSpan(int index) => _lines[index].Span;

    public int GetLineLength(int index) => _lines[index].Length;

    public void SetText(string? text)
    {
        text ??= "";
        _lines.Clear();
        LineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var span = text.AsSpan();
        while (true)
        {
            int nl = span.IndexOf('\n');
            var lineText = nl < 0 ? span : span[..nl];
            var line = new Line(lineText.Length);
            line.Append(lineText);
            _lines.Add(line);
            if (nl < 0)
                break;
            span = span[(nl + 1)..];
        }

        Version++;
        Reset?.Invoke();
    }

    /// <summary>Whole text using <see cref="LineEnding"/>.</summary>
    public string GetText()
    {
        int size = 0;
        foreach (var l in _lines)
            size += l.Length + LineEnding.Length;

        var sb = new StringBuilder(size);
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0)
                sb.Append(LineEnding);
            sb.Append(_lines[i].Span);
        }
        return sb.ToString();
    }

    /// <summary>Text between two positions, lines joined with '\n'.</summary>
    public string GetText(Coordinates a, Coordinates b)
    {
        a = Sanitize(a);
        b = Sanitize(b);
        if (b < a)
            (a, b) = (b, a);

        if (a.Line == b.Line)
            return new string(_lines[a.Line].Span[a.Column..b.Column]);

        var sb = new StringBuilder();
        sb.Append(_lines[a.Line].Span[a.Column..]);
        for (int i = a.Line + 1; i < b.Line; i++)
        {
            sb.Append('\n');
            sb.Append(_lines[i].Span);
        }
        sb.Append('\n');
        sb.Append(_lines[b.Line].Span[..b.Column]);
        return sb.ToString();
    }

    public Coordinates Sanitize(Coordinates c)
    {
        if (c.Line < 0)
            return new Coordinates(0, 0);
        if (c.Line >= _lines.Count)
        {
            int last = _lines.Count - 1;
            return new Coordinates(last, _lines[last].Length);
        }
        int col = Math.Clamp(c.Column, 0, _lines[c.Line].Length);
        return new Coordinates(c.Line, col);
    }

    public Coordinates EndOfText => new(_lines.Count - 1, _lines[^1].Length);

    /// <summary>Inserts text ('\n' splits lines, '\r' dropped). Returns the position after the inserted text.</summary>
    public Coordinates Insert(Coordinates pos, ReadOnlySpan<char> text)
    {
        pos = Sanitize(pos);
        if (text.IsEmpty)
            return pos;

        var line = _lines[pos.Line];
        int firstNl = text.IndexOf('\n');
        if (firstNl < 0)
        {
            int added = line.Insert(pos.Column, text);
            Version++;
            LinesChanged?.Invoke(pos.Line, pos.Line);
            return new Coordinates(pos.Line, pos.Column + added);
        }

        // Split the current line: keep the head, carry the tail to the last new line.
        int tailLen = line.Length - pos.Column;
        var tail = new Line(tailLen);
        tail.Chars.AddRange(line.Span[pos.Column..]);
        line.Chars.RemoveRange(pos.Column, tailLen);
        line.Append(text[..firstNl]);

        var newLines = new List<Line>();
        var rest = text[(firstNl + 1)..];
        Coordinates end;
        while (true)
        {
            int nl = rest.IndexOf('\n');
            var nlText = nl < 0 ? rest : rest[..nl];
            var newLine = new Line(nlText.Length + (nl < 0 ? tailLen : 0));
            newLine.Append(nlText);
            newLines.Add(newLine);
            if (nl < 0)
            {
                end = new Coordinates(pos.Line + newLines.Count, newLine.Length);
                newLine.Chars.AddRange(tail.Span);
                break;
            }
            rest = rest[(nl + 1)..];
        }

        _lines.InsertRange(pos.Line + 1, newLines);
        Version++;
        LinesInserted?.Invoke(pos.Line + 1, newLines.Count);
        LinesChanged?.Invoke(pos.Line, pos.Line + newLines.Count);
        return end;
    }

    /// <summary>Deletes the text between two positions.</summary>
    public void Delete(Coordinates a, Coordinates b)
    {
        a = Sanitize(a);
        b = Sanitize(b);
        if (b < a)
            (a, b) = (b, a);
        if (a == b)
            return;

        var first = _lines[a.Line];
        if (a.Line == b.Line)
        {
            first.Chars.RemoveRange(a.Column, b.Column - a.Column);
            Version++;
            LinesChanged?.Invoke(a.Line, a.Line);
            return;
        }

        var last = _lines[b.Line];
        first.Chars.RemoveRange(a.Column, first.Length - a.Column);
        first.Chars.AddRange(last.Span[b.Column..]);
        int removeCount = b.Line - a.Line;
        _lines.RemoveRange(a.Line + 1, removeCount);
        Version++;
        LinesRemoved?.Invoke(a.Line + 1, removeCount);
        LinesChanged?.Invoke(a.Line, a.Line);
    }

    /// <summary>Position reached by inserting <paramref name="text"/> at <paramref name="start"/> (no mutation).</summary>
    public static Coordinates EndAfter(Coordinates start, ReadOnlySpan<char> text)
    {
        int line = start.Line;
        int col = start.Column;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                line++;
                col = 0;
            }
            else if (c != '\r')
            {
                col++;
            }
        }
        return new Coordinates(line, col);
    }
}
