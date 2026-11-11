// Derived from ImGuiColorTextEditNet (MIT) Coordinates.cs — see THIRD_PARTY_ImGuiColorTextEditNet.md.
namespace SoulsAiStudio.Editor.Text;

/// <summary>
/// A position in the text: 0-based line, and 0-based column = UTF-16 index inside the line
/// (tabs are NOT expanded; tab stops are a rendering concern only).
/// </summary>
public readonly struct Coordinates : IEquatable<Coordinates>, IComparable<Coordinates>
{
    public readonly int Line;
    public readonly int Column;

    public Coordinates(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public static implicit operator Coordinates((int Line, int Column) x) => new(x.Line, x.Column);

    public override string ToString() => $"{Line}:{Column}";

    public static bool operator ==(Coordinates x, Coordinates y) => x.Line == y.Line && x.Column == y.Column;
    public static bool operator !=(Coordinates x, Coordinates y) => !(x == y);
    public static bool operator <(Coordinates x, Coordinates y) => x.Line != y.Line ? x.Line < y.Line : x.Column < y.Column;
    public static bool operator >(Coordinates x, Coordinates y) => y < x;
    public static bool operator <=(Coordinates x, Coordinates y) => !(y < x);
    public static bool operator >=(Coordinates x, Coordinates y) => !(x < y);

    public static Coordinates Min(Coordinates a, Coordinates b) => a <= b ? a : b;
    public static Coordinates Max(Coordinates a, Coordinates b) => a >= b ? a : b;

    public bool Equals(Coordinates other) => this == other;
    public override bool Equals(object? obj) => obj is Coordinates c && c == this;
    public override int GetHashCode() => HashCode.Combine(Line, Column);
    public int CompareTo(Coordinates other) => this < other ? -1 : this > other ? 1 : 0;
}
