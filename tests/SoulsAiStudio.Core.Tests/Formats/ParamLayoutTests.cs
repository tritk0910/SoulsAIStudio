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

    [Fact]
    public void AtkAndBullet_SpEffectFields_HaveOffsets()
    {
        var atk = ParamLayout.FromXml(DefPath("AtkParam"));
        Assert.Equal(SekiroParamOffsets.AtkRowSize, atk.RowSize);
        var bullet = ParamLayout.FromXml(DefPath("BulletParam"));
        Assert.Equal(SekiroParamOffsets.BulletRowSize, bullet.RowSize);
        for (int i = 0; i < SekiroParamOffsets.SpEffectIdCount; i++)
        {
            Assert.Equal(SekiroParamOffsets.AtkSpEffectId0 + 4 * i, atk.Require("spEffectId" + i).Offset);
            Assert.Equal(PARAMDEF.DefType.s32, atk.Require("spEffectId" + i).Type);
            Assert.Equal(SekiroParamOffsets.BulletSpEffectId0 + 4 * i, bullet.Require("spEffectId" + i).Offset);
        }
        Assert.Equal(SekiroParamOffsets.BulletAtkId, bullet.Require("atkId_Bullet").Offset);
        Assert.Equal(SekiroParamOffsets.BulletSpEffectIdForShooter, bullet.Require("spEffectIDForShooter").Offset);
        var beh = ParamLayout.FromXml(DefPath("BehaviorParam"));
        Assert.Equal(0x9, beh.Require("refType").Offset); // what DSAS's BehaviorParam reader uses
        Assert.Equal(0xC, beh.Require("refId").Offset);
    }

    [RequiresSekiroInstallTheory]
    [InlineData("AtkParam_Npc.param", "AtkParam", new[] { "spEffectId0", "spEffectId1", "spEffectId2", "spEffectId3", "spEffectId4" })]
    [InlineData("Bullet.param", "BulletParam", new[] { "atkId_Bullet", "spEffectIDForShooter", "spEffectId0", "spEffectId4" })]
    public void AtkAndBullet_RawOffsets_MatchApplyParamdef_OnGameParam(string fileName, string defName, string[] fields)
    {
        if (!OodleSupport.EnsureLoaded()) return;
        string bndPath = Path.Combine(TestPaths.SekiroInstallDir, "param", "gameparam", "gameparam.parambnd.dcx");
        if (!File.Exists(bndPath)) return;
        var bnd = BND4.Read(bndPath);
        var file = bnd.Files.First(f => f.Name.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase));
        byte[] raw = file.Bytes.ToArray();
        var param = PARAM.Read(raw);
        var def = PARAMDEF.XmlDeserialize(DefPath(defName));
        param.ApplyParamdef(def);
        var layout = ParamLayout.FromDef(def);
        var dataOffset = typeof(PARAM.Row).GetField("DataOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int nonDefault = 0;
        foreach (var row in param.Rows)
        {
            long start = (long)dataOffset.GetValue(row)!;
            foreach (string name in fields)
            {
                int expected = (int)row[name].Value;
                Assert.Equal(expected, BitConverter.ToInt32(raw, (int)(start + layout.Require(name).Offset)));
                if (name == "spEffectId0" && expected > 0) nonDefault++;
            }
        }
        output.WriteLine($"{fileName}: {param.Rows.Count} rows, {nonDefault} with spEffectId0");
        Assert.True(param.Rows.Count > 100);
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
