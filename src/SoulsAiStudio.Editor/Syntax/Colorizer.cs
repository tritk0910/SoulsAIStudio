// Derived from ImGuiColorTextEditNet (MIT) Editor/TextEditorColor.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
using SoulsAiStudio.Editor.Text;

namespace SoulsAiStudio.Editor.Syntax;

/// <summary>
/// Incremental colorizer: re-lexes from the first dirty line and stops once a line past the dirty range
/// ends in the same lexer state as before (so typing in one line costs one line, opening "--[[" re-colors
/// until the comment closes).
/// </summary>
internal sealed class Colorizer
{
    readonly TextBuffer _buffer;
    readonly List<LuaLexState> _endStates = new();
    int _dirtyFrom = int.MaxValue;
    int _dirtyTo = -1;

    public Colorizer(TextBuffer buffer)
    {
        _buffer = buffer;
        buffer.Reset += OnReset;
        buffer.LinesInserted += OnLinesInserted;
        buffer.LinesRemoved += OnLinesRemoved;
        buffer.LinesChanged += Invalidate;
        OnReset();
    }

    public bool IsDone => _dirtyFrom == int.MaxValue;

    /// <summary>End-of-line lexer state for a line (valid once colorized).</summary>
    public LuaLexState EndState(int line) => _endStates[line];

    public void InvalidateAll() => Invalidate(0, _buffer.LineCount - 1);

    void OnReset()
    {
        _endStates.Clear();
        for (int i = 0; i < _buffer.LineCount; i++)
            _endStates.Add(default);
        _dirtyFrom = int.MaxValue;
        _dirtyTo = -1;
        InvalidateAll();
    }

    void OnLinesInserted(int at, int count)
    {
        for (int i = 0; i < count; i++)
            _endStates.Insert(at, default);
        if (_dirtyFrom != int.MaxValue)
        {
            if (_dirtyFrom >= at) _dirtyFrom += count;
            if (_dirtyTo >= at) _dirtyTo += count;
        }
    }

    void OnLinesRemoved(int at, int count)
    {
        _endStates.RemoveRange(at, count);
        if (_dirtyFrom != int.MaxValue)
        {
            if (_dirtyFrom >= at + count) _dirtyFrom -= count;
            else if (_dirtyFrom > at) _dirtyFrom = at;
            if (_dirtyTo >= at + count) _dirtyTo -= count;
            else if (_dirtyTo >= at) _dirtyTo = Math.Max(at - 1, _dirtyFrom);
        }
    }

    public void Invalidate(int first, int last)
    {
        _dirtyFrom = Math.Min(_dirtyFrom, Math.Max(0, first));
        _dirtyTo = Math.Max(_dirtyTo, last);
    }

    /// <summary>Colorizes up to <paramref name="maxLines"/> lines. Returns true when everything is up to date.</summary>
    public bool Run(int maxLines)
    {
        if (_dirtyFrom == int.MaxValue)
            return true;

        int count = _buffer.LineCount;
        int line = _dirtyFrom;
        while (line < count && maxLines-- > 0)
        {
            var start = line > 0 ? _endStates[line - 1] : LuaLexState.Normal;
            var l = _buffer[line];
            var end = LuaLexer.ColorizeLine(l.Span, l.ColorSpan, start);
            bool same = _endStates[line] == end;
            _endStates[line] = end;
            line++;
            if (line > _dirtyTo && same)
            {
                line = count; // settled
                break;
            }
        }

        if (line >= count)
        {
            _dirtyFrom = int.MaxValue;
            _dirtyTo = -1;
            return true;
        }
        _dirtyFrom = line;
        return false;
    }
}
