using System.Numerics;
using System.Text.Unicode;
using ImGuiNET;

namespace SoulsAiStudio.Editor.Rendering;

/// <summary>Draws into an ImGui draw list through native calls with a reusable UTF-8 buffer (no per-call allocation).</summary>
internal sealed unsafe class ImGuiDrawSink : IDrawSink
{
    byte[] _utf8 = new byte[1024];
    ImDrawListPtr _drawList;

    public void Begin(ImDrawListPtr drawList) => _drawList = drawList;

    public void Rect(Vector2 min, Vector2 max, uint color) => _drawList.AddRectFilled(min, max, color);

    public void Line(Vector2 a, Vector2 b, uint color, float thickness) => _drawList.AddLine(a, b, color, thickness);

    public void Circle(Vector2 center, float radius, uint color) => _drawList.AddCircleFilled(center, radius, color, 8);

    public void PushClip(Vector2 min, Vector2 max) => _drawList.PushClipRect(min, max, true);

    public void PopClip() => _drawList.PopClipRect();

    public void Text(Vector2 pos, uint color, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;
        int needed = text.Length * 3;
        if (_utf8.Length < needed)
            _utf8 = new byte[Math.Max(needed, _utf8.Length * 2)]; // grows once for unusually long runs
        Utf8.FromUtf16(text, _utf8, out _, out int written, replaceInvalidSequences: true);
        fixed (byte* p = _utf8)
            ImGuiNative.ImDrawList_AddText_Vec2(_drawList.NativePtr, pos, color, p, p + written);
    }

    /// <summary>ImGui.TextUnformatted for a span without allocating.</summary>
    public void TextUnformatted(ReadOnlySpan<char> text)
    {
        int needed = text.Length * 3;
        if (_utf8.Length < needed)
            _utf8 = new byte[Math.Max(needed, _utf8.Length * 2)];
        Utf8.FromUtf16(text, _utf8, out _, out int written, replaceInvalidSequences: true);
        fixed (byte* p = _utf8)
            ImGuiNative.igTextUnformatted(p, p + written);
    }
}

/// <summary>
/// Glyph advances from the current ImGui font (host may PushFont a monospace font around Render),
/// cached per font/size in a flat BMP table.
/// </summary>
internal sealed class ImGuiGlyphMetrics : IGlyphMetrics
{
    readonly float[] _advance = new float[65536];
    IntPtr _font;
    float _fontSize = -1;
    float _scale = 1;
    ImFontPtr _fontPtr;

    public float LineHeight { get; private set; } = 17f;

    /// <summary>Call once per frame inside the ImGui frame, before layout.</summary>
    public unsafe void Update()
    {
        var font = ImGui.GetFont();
        float size = ImGui.GetFontSize();
        var ptr = (IntPtr)font.NativePtr;
        if (ptr != _font || size != _fontSize)
        {
            _font = ptr;
            _fontPtr = font;
            _fontSize = size;
            _scale = size / font.FontSize;
            Array.Fill(_advance, -1f);
        }
        LineHeight = MathF.Floor(ImGui.GetTextLineHeightWithSpacing());
    }

    public float Advance(char c)
    {
        float a = _advance[c];
        if (a < 0)
        {
            a = _fontPtr.GetCharAdvance(c) * _scale;
            _advance[c] = a;
        }
        return a;
    }
}
