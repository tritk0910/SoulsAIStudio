namespace SoulsAiStudio.Editor.Text;

/// <summary>
/// Single-line text field model used by the find / go-to bars. Fed from <c>IKeyInput</c> like the text
/// area (no native ImGui.InputText), fixed capacity, no allocation while typing.
/// </summary>
internal sealed class LineInput
{
    readonly char[] _chars;
    int _length;
    int _cursor;
    bool _allSelected;

    public LineInput(int capacity = 256, bool digitsOnly = false)
    {
        _chars = new char[capacity];
        DigitsOnly = digitsOnly;
    }

    public bool DigitsOnly { get; }
    public ReadOnlySpan<char> Text => _chars.AsSpan(0, _length);
    public int Cursor => _cursor;
    public bool AllSelected => _allSelected && _length > 0;
    public int Capacity => _chars.Length;

    /// <summary>Incremented whenever <see cref="Text"/> changes.</summary>
    public int Version { get; private set; }

    public void Clear()
    {
        if (_length != 0)
            Version++;
        _length = _cursor = 0;
        _allSelected = false;
    }

    /// <summary>Replaces the content (truncated to capacity / first line), cursor at end, all selected.</summary>
    public void SetText(ReadOnlySpan<char> text)
    {
        int nl = text.IndexOfAny('\r', '\n');
        if (nl >= 0)
            text = text[..nl];
        text = text[..Math.Min(text.Length, _chars.Length)];
        if (!text.SequenceEqual(Text))
            Version++;
        text.CopyTo(_chars);
        _length = _cursor = text.Length;
        _allSelected = true;
    }

    public void SelectAll()
    {
        _allSelected = true;
        _cursor = _length;
    }

    bool DeleteSelectionIfAny()
    {
        if (!AllSelected)
        {
            _allSelected = false;
            return false;
        }
        _length = _cursor = 0;
        _allSelected = false;
        Version++;
        return true;
    }

    /// <summary>Types one char (control chars and, for digit fields, non-digits are ignored).</summary>
    public bool Insert(char c)
    {
        if (c < 32 || c == 127 || (DigitsOnly && (c < '0' || c > '9')))
            return false;
        DeleteSelectionIfAny();
        if (_length >= _chars.Length)
            return false;
        Array.Copy(_chars, _cursor, _chars, _cursor + 1, _length - _cursor);
        _chars[_cursor++] = c;
        _length++;
        Version++;
        return true;
    }

    public void Insert(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c == '\r' || c == '\n')
                break;
            Insert(c);
        }
    }

    public void Backspace()
    {
        if (DeleteSelectionIfAny() || _cursor == 0)
            return;
        Array.Copy(_chars, _cursor, _chars, _cursor - 1, _length - _cursor);
        _cursor--;
        _length--;
        Version++;
    }

    public void Delete()
    {
        if (DeleteSelectionIfAny() || _cursor >= _length)
            return;
        Array.Copy(_chars, _cursor + 1, _chars, _cursor, _length - _cursor - 1);
        _length--;
        Version++;
    }

    public void MoveLeft()
    {
        if (AllSelected)
            _cursor = 0;
        else if (_cursor > 0)
            _cursor--;
        _allSelected = false;
    }

    public void MoveRight()
    {
        if (!AllSelected && _cursor < _length)
            _cursor++;
        else if (AllSelected)
            _cursor = _length;
        _allSelected = false;
    }

    public void Home()
    {
        _cursor = 0;
        _allSelected = false;
    }

    public void End()
    {
        _cursor = _length;
        _allSelected = false;
    }

    public void SetCursor(int index)
    {
        _cursor = Math.Clamp(index, 0, _length);
        _allSelected = false;
    }

    /// <summary>Parses the digits as a positive int (0 if empty/overflow).</summary>
    public int ParseInt()
    {
        int v = 0;
        foreach (var c in Text)
        {
            if (c < '0' || c > '9')
                continue;
            if (v > 100_000_000)
                return 0;
            v = v * 10 + (c - '0');
        }
        return v;
    }
}
