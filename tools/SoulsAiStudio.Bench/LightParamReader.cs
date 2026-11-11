using System.Buffers.Binary;
using System.Reflection;
using SoulsFormats;

namespace SoulsAiStudio.Bench;

/// <summary>
/// Bench prototype of a "read only the fields we need" param reader. Avoids PARAM.ApplyParamdef, which
/// creates a boxed Cell object for every field of every row (hundreds of MB for SpEffectParam/AtkParam).
/// Uses reflection on SoulsFormats internals (Row.DataOffset, PARAMDEF.GetFieldsSize) - a product version
/// should expose these properly or re-implement the offset computation.
/// </summary>
public sealed class LightParamReader
{
    public sealed record FieldPlan(int FieldIndex, string Name, int Offset, PARAMDEF.DefType Type);
    public sealed record Result(int[] Ids, double[] Values);

    private static readonly FieldInfo RowDataOffset =
        typeof(PARAM.Row).GetField("DataOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetFieldsSize =
        typeof(PARAMDEF).GetMethod("GetFieldsSize", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static bool IsScalar(PARAMDEF.DefType t) => t is PARAMDEF.DefType.s8 or PARAMDEF.DefType.u8
        or PARAMDEF.DefType.s16 or PARAMDEF.DefType.u16 or PARAMDEF.DefType.s32 or PARAMDEF.DefType.u32 or PARAMDEF.DefType.f32;

    /// <summary>Pick up to <paramref name="max"/> non-bitfield scalar fields spread across the row.</summary>
    public List<FieldPlan> Plan(PARAMDEF def, int max)
    {
        var eligible = new List<FieldPlan>();
        for (int i = 0; i < def.Fields.Count; i++)
        {
            var f = def.Fields[i];
            if (!IsScalar(f.DisplayType) || f.BitSize != -1) continue;
            int off = (int)GetFieldsSize.Invoke(def, new object[] { i, ulong.MaxValue })!;
            eligible.Add(new FieldPlan(i, f.InternalName, off, f.DisplayType));
        }
        if (eligible.Count <= max) return eligible;
        var pick = new List<FieldPlan>();
        for (int k = 0; k < max; k++) pick.Add(eligible[k * eligible.Count / max]);
        return pick;
    }

    public Result Read(byte[] paramFile, PARAM p, List<FieldPlan> plan)
    {
        var ids = new int[p.Rows.Count];
        var vals = new double[p.Rows.Count * plan.Count];
        var span = paramFile.AsSpan();
        for (int r = 0; r < p.Rows.Count; r++)
        {
            var row = p.Rows[r];
            ids[r] = row.ID;
            long baseOff = (long)RowDataOffset.GetValue(row)!;
            for (int k = 0; k < plan.Count; k++)
            {
                var s = span.Slice((int)baseOff + plan[k].Offset);
                vals[r * plan.Count + k] = plan[k].Type switch
                {
                    PARAMDEF.DefType.s8 => (sbyte)s[0],
                    PARAMDEF.DefType.u8 => s[0],
                    PARAMDEF.DefType.s16 => BinaryPrimitives.ReadInt16LittleEndian(s),
                    PARAMDEF.DefType.u16 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                    PARAMDEF.DefType.s32 => BinaryPrimitives.ReadInt32LittleEndian(s),
                    PARAMDEF.DefType.u32 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                    PARAMDEF.DefType.f32 => BinaryPrimitives.ReadSingleLittleEndian(s),
                    _ => double.NaN,
                };
            }
        }
        return new Result(ids, vals);
    }
}
