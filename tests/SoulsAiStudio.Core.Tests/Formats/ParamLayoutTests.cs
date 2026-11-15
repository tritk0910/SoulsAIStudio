using System.Reflection;
using SoulsAiStudio.Core.Formats.Params;
using SoulsAiStudio.Core.Tests.TestSupport;
using SoulsFormats;
using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.Formats;

/// <summary>
/// Field offsets from the Paramdex XML must match what SoulsFormats decodes with ApplyParamdef, on the real Sekiro
/// SpEffectParam (game install, read-only).
/// </summary>
public class ParamLayoutTests(ITestOutputHelper output)
{
    private static string DefPath(string name) =>
        Path.Combine(TestPaths.RepoRoot ?? "", "Resources", "Paramdex", "SDT", "Defs", name + ".xml");

    [Fact]
    public void SpEffect_CycleFields_HaveOffsets()
    {
        var layout = ParamLayout.FromXml(DefPath("SpEffect"));
        var cycle = layout.Require("cycleOccurrenceSpEffectId");
        var interval = layout.Require("motionInterval");
        Assert.Equal(PARAMDEF.DefType.s32, cycle.Type);
        Assert.Equal(-1, cycle.BitOffset);
        Assert.Equal(PARAMDEF.DefType.f32, interval.Type);
        Assert.True(cycle.Offset > interval.Offset);
        Assert.Equal(0, cycle.Offset % 4);
        output.WriteLine($"cycle @0x{cycle.Offset:X}, interval @0x{interval.Offset:X}, row size 0x{layout.RowSize:X}");
        Assert.Equal(SekiroParamOffsets.SpEffectCycleOccurrenceSpEffectId, cycle.Offset);
        Assert.Equal(SekiroParamOffsets.SpEffectMotionInterval, interval.Offset);
        Assert.Equal(SekiroParamOffsets.SpEffectRowSize, layout.RowSize);
    }

    [RequiresSekiroInstallFact]
    public void SpEffect_RawOffsets_MatchApplyParamdef_OnGameParam()
    {
        if (!OodleSupport.EnsureLoaded()) return; // gameparam is DCX_KRAK
        string bndPath = Path.Combine(TestPaths.SekiroInstallDir, "param", "gameparam", "gameparam.parambnd.dcx");
        if (!File.Exists(bndPath)) return;
        var bnd = BND4.Read(bndPath);
        var file = bnd.Files.First(f => f.Name.EndsWith("SpEffectParam.param", StringComparison.OrdinalIgnoreCase));
        byte[] raw = file.Bytes.ToArray();
        var param = PARAM.Read(raw);
        var def = PARAMDEF.XmlDeserialize(DefPath("SpEffect"));
        param.ApplyParamdef(def);
        var layout = ParamLayout.FromDef(def);
        var dataOffset = typeof(PARAM.Row).GetField("DataOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;

        string[] fields = ["cycleOccurrenceSpEffectId", "motionInterval", "effectEndurance", "effectTargetSelf", "effectTargetEnemy"];
        int checkedRows = 0, nonDefaultCycle = 0;
        foreach (var row in param.Rows)
        {
            long start = (long)dataOffset.GetValue(row)!;
            foreach (string name in fields)
            {
                var f = layout.Require(name);
                object expected = row[name].Value;
                object actual = f.Type switch
                {
                    PARAMDEF.DefType.s32 => BitConverter.ToInt32(raw, (int)(start + f.Offset)),
                    PARAMDEF.DefType.f32 => BitConverter.ToSingle(raw, (int)(start + f.Offset)),
                    PARAMDEF.DefType.u8 when f.BitOffset >= 0 => (byte)((raw[start + f.Offset] >> f.BitOffset) & ((1 << f.BitSize) - 1)),
                    PARAMDEF.DefType.u8 => raw[start + f.Offset],
                    _ => throw new NotSupportedException(f.Type.ToString()),
                };
                Assert.True(Convert.ToDouble(expected).Equals(Convert.ToDouble(actual)), $"row {row.ID} {name}: expected {expected}, raw {actual}");
            }
            if ((int)row["cycleOccurrenceSpEffectId"].Value > 0) nonDefaultCycle++;
            checkedRows++;
        }
        output.WriteLine($"{checkedRows} rows, {nonDefaultCycle} with a cycle SpEffect");
        Assert.True(checkedRows > 1000);
        Assert.True(nonDefaultCycle > 0);
    }
}
