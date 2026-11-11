using System.Numerics;
using SoulsAiStudio.Editor.Syntax;

namespace SoulsAiStudio.Editor.Rendering;

/// <summary>Draw target for <see cref="EditorView"/>. Implementations must not allocate per call.</summary>
internal interface IDrawSink
{
    void Rect(Vector2 min, Vector2 max, uint color);
    void Line(Vector2 a, Vector2 b, uint color, float thickness);
    void Circle(Vector2 center, float radius, uint color);
    void Text(Vector2 pos, uint color, ReadOnlySpan<char> text);
    void PushClip(Vector2 min, Vector2 max);
    void PopClip();
}

/// <summary>Font metrics for layout. Implementations must not allocate per call.</summary>
internal interface IGlyphMetrics
{
    float LineHeight { get; }
    float Advance(char c);
}

/// <summary>Fixed-advance metrics (tests / fallback).</summary>
internal sealed class FixedGlyphMetrics : IGlyphMetrics
{
    public float LineHeight { get; set; } = 17f;
    public float CharWidth { get; set; } = 8f;
    public float Advance(char c) => CharWidth;
}

/// <summary>Draws nothing (headless frame cost / allocation tests).</summary>
internal sealed class NullDrawSink : IDrawSink
{
    public int Calls;
    public void Rect(Vector2 min, Vector2 max, uint color) => Calls++;
    public void Line(Vector2 a, Vector2 b, uint color, float thickness) => Calls++;
    public void Circle(Vector2 center, float radius, uint color) => Calls++;
    public void Text(Vector2 pos, uint color, ReadOnlySpan<char> text) => Calls++;
    public void PushClip(Vector2 min, Vector2 max) { }
    public void PopClip() { }
}

/// <summary>Editor colors, ImGui U32 format (0xAABBGGRR).</summary>
public sealed class EditorPalette
{
    /// <summary>Converts 0xRRGGBB (+ alpha 0..255) to ImGui's ABGR U32.</summary>
    public static uint Rgb(uint rrggbb, byte alpha = 255) =>
        (uint)alpha << 24 | (rrggbb & 0xFF) << 16 | (rrggbb & 0xFF00) | (rrggbb >> 16 & 0xFF);

    /// <summary>Indexed by <see cref="LuaTokenKind"/>.</summary>
    public readonly uint[] Tokens = new uint[(int)LuaTokenKind.Count];

    public uint Background, GutterBackground, LineNumber, LineNumberCurrent;
    public uint CurrentLineFill, Selection, Cursor, HighlightLine;
    public uint FindMatch, FindMatchCurrent, Error, Warning;
    public uint FieldBackground, FieldBorder, FieldBorderActive;

    /// <summary>Dark palette matching docs/ux/ui-spec.md §4.1.</summary>
    public static EditorPalette Dark()
    {
        var p = new EditorPalette();
        p.Tokens[(int)LuaTokenKind.Default] = Rgb(0xD7DAE0);
        p.Tokens[(int)LuaTokenKind.Keyword] = Rgb(0x569CD6);
        p.Tokens[(int)LuaTokenKind.Literal] = Rgb(0x569CD6);
        p.Tokens[(int)LuaTokenKind.Number] = Rgb(0xB5CEA8);
        p.Tokens[(int)LuaTokenKind.String] = Rgb(0xCE9178);
        p.Tokens[(int)LuaTokenKind.Comment] = Rgb(0x6A9955);
        p.Tokens[(int)LuaTokenKind.Identifier] = Rgb(0x9CDCFE);
        p.Tokens[(int)LuaTokenKind.Constant] = Rgb(0x4FC1FF);
        p.Tokens[(int)LuaTokenKind.Function] = Rgb(0xDCDCAA);
        p.Tokens[(int)LuaTokenKind.Operator] = Rgb(0xB0B4BA);
        p.Background = Rgb(0x25262A);
        p.GutterBackground = Rgb(0x222327);
        p.LineNumber = Rgb(0x5C6068);
        p.LineNumberCurrent = Rgb(0xD7DAE0);
        p.CurrentLineFill = Rgb(0x2E3035);
        p.Selection = Rgb(0x264F78);
        p.Cursor = Rgb(0xE0E0E0);
        p.HighlightLine = Rgb(0x4C9AFF, 40);
        p.FindMatch = Rgb(0xE0A04F, 70);
        p.FindMatchCurrent = Rgb(0xE0A04F, 150);
        p.Error = Rgb(0xE5534B);
        p.Warning = Rgb(0xE5B454);
        p.FieldBackground = Rgb(0x1E1F22);
        p.FieldBorder = Rgb(0x3A3C42);
        p.FieldBorderActive = Rgb(0x4C9AFF);
        return p;
    }
}
