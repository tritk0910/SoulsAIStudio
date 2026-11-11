// Derived from ImGuiColorTextEditNet (MIT) Editor/TextEditorRenderer.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
// Rewritten: layout/draw is ImGui-free (IDrawSink / IGlyphMetrics), only visible lines are touched,
// and the per-frame path performs no heap allocation.
using System.Numerics;
using SoulsAiStudio.Editor.Syntax;
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Rendering;

/// <summary>Per-frame inputs for <see cref="EditorView.Draw"/>.</summary>
internal struct FrameParams
{
    /// <summary>Screen position of content (0,0), i.e. window position minus scroll.</summary>
    public Vector2 ContentOrigin;
    /// <summary>Visible region of the text window (screen space).</summary>
    public Vector2 WindowPos;
    public Vector2 WindowSize;
    public bool Focused;
    public bool CursorVisible;
    public int HighlightLine; // -1 = none
    public bool ShowFindMatches;
    public Coordinates CurrentMatchStart;
    public Coordinates CurrentMatchEnd;
}

internal sealed class EditorView
{
    const float LeftPad = 6f;
    const int MaxMatchesPerLine = 64;

    readonly EditorCore _core;
    readonly DiagnosticStore _diagnostics;
    readonly FindModel _find;

    public EditorPalette Palette { get; set; } = EditorPalette.Dark();

    public EditorView(EditorCore core, DiagnosticStore diagnostics, FindModel find)
    {
        _core = core;
        _diagnostics = diagnostics;
        _find = find;
    }

    // ------------------------------------------------------------------ layout helpers

    public static int DigitCount(int n)
    {
        int d = 1;
        while (n >= 10)
        {
            n /= 10;
            d++;
        }
        return d;
    }

    public float GutterWidth(IGlyphMetrics m)
    {
        int digits = Math.Max(3, DigitCount(_core.Buffer.LineCount));
        return digits * m.Advance('0') + m.LineHeight + 8f;
    }

    public float TextStartX(IGlyphMetrics m) => GutterWidth(m) + LeftPad;

    float TabWidth(IGlyphMetrics m) => _core.TabSize * m.Advance(' ');

    /// <summary>Pixel offset of <paramref name="column"/> from the line's text start.</summary>
    public float ColumnToX(ReadOnlySpan<char> line, int column, IGlyphMetrics m)
    {
        float x = 0;
        float tab = TabWidth(m);
        int end = Math.Min(column, line.Length);
        for (int i = 0; i < end; i++)
            x = line[i] == '\t' ? (MathF.Floor(x / tab) + 1) * tab : x + m.Advance(line[i]);
        if (column > line.Length)
            x += (column - line.Length) * m.Advance(' ');
        return x;
    }

    /// <summary>Nearest column for a pixel offset from the line's text start.</summary>
    public int XToColumn(ReadOnlySpan<char> line, float x, IGlyphMetrics m)
    {
        float cx = 0;
        float tab = TabWidth(m);
        for (int i = 0; i < line.Length; i++)
        {
            float nx = line[i] == '\t' ? (MathF.Floor(cx / tab) + 1) * tab : cx + m.Advance(line[i]);
            if (x < (cx + nx) * 0.5f)
                return i;
            cx = nx;
        }
        return line.Length;
    }

    /// <summary>Text coordinate under a screen position.</summary>
    public Coordinates ScreenToCoordinates(Vector2 screen, in FrameParams f, IGlyphMetrics m)
    {
        float lineH = m.LineHeight;
        int line = (int)MathF.Floor((screen.Y - f.ContentOrigin.Y) / lineH);
        if (line < 0)
            return new Coordinates(0, 0);
        if (line >= _core.Buffer.LineCount)
            return _core.Buffer.EndOfText;
        var span = _core.Buffer.GetLineSpan(line);
        float localX = screen.X - (f.ContentOrigin.X + TextStartX(m));
        return new Coordinates(line, XToColumn(span, localX, m));
    }

    public float VisibleLineCount(Vector2 windowSize, IGlyphMetrics m) => windowSize.Y / m.LineHeight;

    // ------------------------------------------------------------------ draw

    public void Draw(IDrawSink sink, IGlyphMetrics m, in FrameParams f)
    {
        var buffer = _core.Buffer;
        var pal = Palette;
        float lineH = m.LineHeight;
        int lineCount = buffer.LineCount;

        float scrollY = f.WindowPos.Y - f.ContentOrigin.Y;
        int first = Math.Max(0, (int)MathF.Floor(scrollY / lineH));
        int last = Math.Min(lineCount - 1, (int)MathF.Floor((scrollY + f.WindowSize.Y) / lineH));

        float gutterW = GutterWidth(m);
        float textX0 = f.ContentOrigin.X + gutterW + LeftPad;
        float clipLeft = f.WindowPos.X + gutterW;
        float right = f.WindowPos.X + f.WindowSize.X;
        float spaceW = m.Advance(' ');
        float tabW = TabWidth(m);

        var selStart = _core.SelectionStart;
        var selEnd = _core.SelectionEnd;
        bool hasSel = selStart != selEnd;
        var cursor = _core.Cursor;

        Span<int> matchStarts = stackalloc int[MaxMatchesPerLine];
        int queryLen = _find.Query.Length;

        sink.PushClip(new Vector2(clipLeft, f.WindowPos.Y), new Vector2(right, f.WindowPos.Y + f.WindowSize.Y));

        int diagIndex = _diagnostics.FirstIndexAtOrAfter(first);

        for (int lineNo = first; lineNo <= last; lineNo++)
        {
            var line = buffer[lineNo];
            var chars = line.Span;
            var colors = line.ColorSpan;
            float y = f.ContentOrigin.Y + lineNo * lineH;
            var rowMin = new Vector2(clipLeft, y);
            var rowMax = new Vector2(right, y + lineH);

            if (lineNo == f.HighlightLine)
                sink.Rect(rowMin, rowMax, pal.HighlightLine);
            else if (lineNo == cursor.Line && !hasSel && f.Focused)
                sink.Rect(rowMin, rowMax, pal.CurrentLineFill);

            // find matches
            if (f.ShowFindMatches && queryLen > 0)
            {
                int n = _find.MatchesInLine(chars, matchStarts);
                for (int k = 0; k < n; k++)
                {
                    int s = matchStarts[k];
                    bool current = f.CurrentMatchStart.Line == lineNo && f.CurrentMatchStart.Column == s;
                    float x1 = textX0 + ColumnToX(chars, s, m);
                    float x2 = textX0 + ColumnToX(chars, s + queryLen, m);
                    sink.Rect(new Vector2(x1, y), new Vector2(x2, y + lineH), current ? pal.FindMatchCurrent : pal.FindMatch);
                }
            }

            // selection
            if (hasSel && lineNo >= selStart.Line && lineNo <= selEnd.Line)
            {
                int c1 = lineNo == selStart.Line ? selStart.Column : 0;
                int c2 = lineNo == selEnd.Line ? selEnd.Column : chars.Length;
                float x1 = textX0 + ColumnToX(chars, c1, m);
                float x2 = textX0 + ColumnToX(chars, c2, m);
                if (lineNo != selEnd.Line)
                    x2 += spaceW; // show the selected line break
                if (x2 > x1)
                    sink.Rect(new Vector2(x1, y), new Vector2(x2, y + lineH), pal.Selection);
            }

            // text runs (split on color change and whitespace)
            {
                float x = textX0;
                int runStart = -1;
                float runX = 0;
                byte runColor = 0;
                for (int i = 0; i <= chars.Length; i++)
                {
                    bool atEnd = i == chars.Length;
                    char c = atEnd ? '\0' : chars[i];
                    bool ws = c == ' ' || c == '\t';
                    byte color = atEnd || ws ? (byte)0 : (i < colors.Length ? colors[i] : (byte)0);

                    if (runStart >= 0 && (atEnd || ws || color != runColor))
                    {
                        if (runX < right)
                            sink.Text(new Vector2(runX, y), TokenColor(pal, runColor), chars[runStart..i]);
                        runStart = -1;
                    }
                    if (atEnd)
                        break;
                    if (x > right)
                    {
                        // rest of the line is not visible
                        if (runStart >= 0)
                            sink.Text(new Vector2(runX, y), TokenColor(pal, runColor), chars[runStart..i]);
                        break;
                    }

                    if (ws)
                    {
                        x = c == '\t' ? textX0 + (MathF.Floor((x - textX0) / tabW) + 1) * tabW : x + spaceW;
                        continue;
                    }
                    if (runStart < 0)
                    {
                        runStart = i;
                        runX = x;
                        runColor = color;
                    }
                    x += m.Advance(c);
                }
            }

            // diagnostics squiggles
            while (diagIndex < _diagnostics.Count && _diagnostics[diagIndex].Line < lineNo)
                diagIndex++;
            for (int d = diagIndex; d < _diagnostics.Count && _diagnostics[d].Line == lineNo; d++)
            {
                var diag = _diagnostics[d];
                float x1 = textX0 + ColumnToX(chars, diag.StartCol, m);
                float x2 = textX0 + ColumnToX(chars, Math.Max(diag.EndCol, diag.StartCol + 1), m);
                Squiggle(sink, x1, Math.Min(x2, right), y + lineH - 2f, diag.IsWarning ? pal.Warning : pal.Error);
            }

            // cursor
            if (f.Focused && f.CursorVisible && lineNo == cursor.Line)
            {
                float cx = textX0 + ColumnToX(chars, cursor.Column, m);
                sink.Rect(new Vector2(cx, y), new Vector2(cx + 2f, y + lineH), pal.Cursor);
            }
        }

        sink.PopClip();

        // gutter (fixed, does not scroll horizontally)
        var gMin = f.WindowPos;
        var gMax = new Vector2(f.WindowPos.X + gutterW, f.WindowPos.Y + f.WindowSize.Y);
        sink.PushClip(gMin, gMax);
        sink.Rect(gMin, gMax, pal.GutterBackground);
        Span<char> num = stackalloc char[12];
        float markerX = f.WindowPos.X + lineH * 0.5f + 2f;
        diagIndex = _diagnostics.FirstIndexAtOrAfter(first);
        for (int lineNo = first; lineNo <= last; lineNo++)
        {
            float y = f.ContentOrigin.Y + lineNo * lineH;
            (lineNo + 1).TryFormat(num, out int len);
            float w = 0;
            for (int i = 0; i < len; i++)
                w += m.Advance(num[i]);
            float nx = f.WindowPos.X + gutterW - 6f - w;
            sink.Text(new Vector2(nx, y), lineNo == cursor.Line ? pal.LineNumberCurrent : pal.LineNumber, num[..len]);

            while (diagIndex < _diagnostics.Count && _diagnostics[diagIndex].Line < lineNo)
                diagIndex++;
            bool any = false, error = false;
            for (int d = diagIndex; d < _diagnostics.Count && _diagnostics[d].Line == lineNo; d++)
            {
                any = true;
                if (!_diagnostics[d].IsWarning)
                    error = true;
            }
            if (any)
                sink.Circle(new Vector2(markerX, y + lineH * 0.5f), lineH * 0.22f, error ? pal.Error : pal.Warning);
        }
        sink.PopClip();
    }

    static uint TokenColor(EditorPalette pal, byte kind) =>
        kind < pal.Tokens.Length ? pal.Tokens[kind] : pal.Tokens[0];

    static void Squiggle(IDrawSink sink, float x1, float x2, float y, uint color)
    {
        const float step = 3f;
        float x = x1;
        bool up = true;
        while (x < x2)
        {
            float nx = MathF.Min(x + step, x2);
            sink.Line(new Vector2(x, up ? y : y - 2f), new Vector2(nx, up ? y - 2f : y), color, 1f);
            x = nx;
            up = !up;
        }
    }

    /// <summary>First diagnostic whose range contains <paramref name="pos"/> (or any on the line if <paramref name="anyOnLine"/>).</summary>
    public int DiagnosticAt(Coordinates pos, bool anyOnLine)
    {
        for (int d = _diagnostics.FirstIndexAtOrAfter(pos.Line); d < _diagnostics.Count && _diagnostics[d].Line == pos.Line; d++)
        {
            var diag = _diagnostics[d];
            if (anyOnLine || (pos.Column >= diag.StartCol && pos.Column <= Math.Max(diag.EndCol, diag.StartCol + 1)))
                return d;
        }
        return -1;
    }
}
