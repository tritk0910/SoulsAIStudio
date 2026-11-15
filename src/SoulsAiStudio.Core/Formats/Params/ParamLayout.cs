using SoulsFormats;

namespace SoulsAiStudio.Core.Formats.Params;

/// <summary>
/// Byte offsets of paramdef fields inside a row, computed with the same rules SoulsFormats uses to read cells
/// (<c>PARAM.Row.ReadCells</c>: value fields in order, bitfields packed into storage of their type). Lets the app read a
/// few fields straight from row bytes instead of <c>ApplyParamdef</c> (slow and memory heavy, see CLAUDE.md perf notes).
/// </summary>
public sealed class ParamLayout
{
    /// <param name="Offset">Byte offset of the field (of its storage unit for bitfields) from the row start.</param>
    /// <param name="BitOffset">Bit position inside the storage unit; -1 for value fields.</param>
    public sealed record FieldInfo(string Name, PARAMDEF.DefType Type, int Offset, int BitOffset, int BitSize);

    private readonly Dictionary<string, FieldInfo> _fields = new(StringComparer.Ordinal);

    public int RowSize { get; }
    public string ParamType { get; }

    private ParamLayout(PARAMDEF def)
    {
        ParamType = def.ParamType;
        int offset = 0, bitOffset = -1, bitLimit = -1, storage = 0;
        foreach (var f in def.Fields)
        {
            var type = f.DisplayType;
            bool isBit = ParamUtil.IsBitType(type) && f.BitSize != -1;
            if (!isBit)
            {
                bitOffset = -1;
                _fields[f.InternalName] = new FieldInfo(f.InternalName, type, offset, -1, -1);
                offset += ValueSize(f);
                continue;
            }
            int limit = ParamUtil.GetBitLimit(type);
            if (bitOffset == -1 || limit != bitLimit || bitOffset + f.BitSize > bitLimit)
            {
                bitOffset = 0;
                bitLimit = limit;
                storage = offset;
                offset += limit / 8;
            }
            _fields[f.InternalName] = new FieldInfo(f.InternalName, type, storage, bitOffset, f.BitSize);
            bitOffset += f.BitSize;
        }
        RowSize = offset;
    }

    private static int ValueSize(PARAMDEF.Field f) => f.DisplayType switch
    {
        PARAMDEF.DefType.fixstr => f.ArrayLength,
        PARAMDEF.DefType.fixstrW => f.ArrayLength * 2,
        PARAMDEF.DefType.dummy8 => f.ArrayLength,
        PARAMDEF.DefType.u8 when f.ArrayLength > 1 => f.ArrayLength,
        _ => ParamUtil.GetValueSize(f.DisplayType),
    };

    public static ParamLayout FromDef(PARAMDEF def) => new(def);

    /// <summary>Loads a Paramdex XML paramdef (e.g. <c>Resources/Paramdex/SDT/Defs/SpEffect.xml</c>).</summary>
    public static ParamLayout FromXml(string path) => new(PARAMDEF.XmlDeserialize(path));

    public FieldInfo? Field(string name) => _fields.TryGetValue(name, out var f) ? f : null;

    public FieldInfo Require(string name) => Field(name) ?? throw new KeyNotFoundException($"{ParamType} has no field '{name}'.");
}
