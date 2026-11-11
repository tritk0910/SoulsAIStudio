using System.Text;

namespace SoulsAiStudio.Core.IO;

/// <summary>
/// Encoding rules for Lua script bytes. Sekiro plain-text scripts are ASCII/UTF-8 without BOM; compiled
/// HavokScript chunks start with the Lua signature <c>1B 4C 75 61</c>.
/// </summary>
public static class LuaText
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool IsBytecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a';

    public static string Decode(ReadOnlySpan<byte> data)
    {
        if (IsBytecode(data))
            throw new InvalidOperationException("Lua bytecode cannot be decoded as text; decompile it first.");
        // Tolerate a BOM if some external editor added one.
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data[3..];
        return Utf8NoBom.GetString(data);
    }

    public static byte[] Encode(string text) => Utf8NoBom.GetBytes(text);
}
