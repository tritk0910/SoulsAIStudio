// Derived from ImGuiColorTextEditNet (MIT) Line.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
using System.Runtime.InteropServices;

namespace SoulsAiStudio.Editor.Text;

/// <summary>One line of text (without the line terminator) plus one color byte per char.</summary>
internal sealed class Line
{
    public readonly List<char> Chars;
    public readonly List<byte> Colors;

    public Line(int capacity = 0)
    {
        Chars = new List<char>(capacity);
        Colors = new List<byte>(capacity);
    }

    public int Length => Chars.Count;

    public ReadOnlySpan<char> Span => CollectionsMarshal.AsSpan(Chars);

    /// <summary>Color bytes, resized to match <see cref="Length"/> (new entries are unspecified until colorized).</summary>
    public Span<byte> ColorSpan
    {
        get
        {
            if (Colors.Count != Chars.Count)
                CollectionsMarshal.SetCount(Colors, Chars.Count);
            return CollectionsMarshal.AsSpan(Colors);
        }
    }

    /// <summary>Appends text, dropping '\r'.</summary>
    public void Append(ReadOnlySpan<char> text) => Insert(Chars.Count, text);

    /// <summary>Inserts text at <paramref name="index"/>, dropping '\r'. Returns the number of chars inserted.</summary>
    public int Insert(int index, ReadOnlySpan<char> text)
    {
        if (text.IndexOf('\r') < 0)
        {
            Chars.InsertRange(index, text);
            return text.Length;
        }

        int n = 0;
        foreach (var c in text)
        {
            if (c == '\r')
                continue;
            Chars.Insert(index + n, c);
            n++;
        }
        return n;
    }

    public override string ToString() => new(Span);
}
