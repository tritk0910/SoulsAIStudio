using SoulsAiStudio.Core.Params;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Params;

public class SpEffectLabelsTests
{
    [Fact]
    public void MissingFile_IsCreatedWithDefaults()
    {
        using var w = TempWorkDir.Create("labels");
        string path = w.Combine("sub/spEffectLabels.json");
        var labels = SpEffectLabels.Load(path);
        Assert.True(File.Exists(path));
        Assert.Equal("Strong parry (right)", labels.Get(200210));
        Assert.Equal("Strong parry (left)", labels.Get(200211));
        Assert.Equal("Weak parry (left)", labels.Get(200215));
        Assert.Equal("Weak parry (right)", labels.Get(200216));
        Assert.Equal(4, labels.Count);
        Assert.Null(labels.LastError);
    }

    [Fact]
    public void SetAndRemove_PersistAcrossReload()
    {
        using var w = TempWorkDir.Create("labels");
        string path = w.Combine("spEffectLabels.json");
        var labels = SpEffectLabels.Load(path);
        int v = labels.Version;
        Assert.True(labels.Set(5060040, "  Owl shuriken\nbranch  "));
        Assert.False(labels.Set(5060040, "Owl shuriken branch"));
        Assert.True(labels.Set(1, "Tiếng Việt"));
        Assert.True(labels.Version > v);

        var again = SpEffectLabels.Load(path);
        Assert.Equal("Owl shuriken branch", again.Get(5060040));
        Assert.Equal("Tiếng Việt", again.Get(1));

        Assert.True(again.Set(1, "   ")); // blank = remove
        Assert.Null(again.Get(1));
        Assert.False(again.Remove(1));
        Assert.Null(SpEffectLabels.Load(path).Get(1));
    }

    [Fact]
    public void DeletedDefault_StaysDeleted()
    {
        using var w = TempWorkDir.Create("labels");
        string path = w.Combine("spEffectLabels.json");
        Assert.True(SpEffectLabels.Load(path).Remove(200210));
        var again = SpEffectLabels.Load(path);
        Assert.Null(again.Get(200210));
        Assert.Equal(3, again.Count);
    }

    [Fact]
    public void EmptyLabelsObject_IsNotReseeded()
    {
        using var w = TempWorkDir.Create("labels");
        string path = w.Combine("spEffectLabels.json");
        File.WriteAllText(path, "{ \"version\": 1, \"labels\": {} }");
        Assert.Equal(0, SpEffectLabels.Load(path).Count);
    }

    [Fact]
    public void CorruptFile_LoadsEmpty_AndIsKeptOnNextSave()
    {
        using var w = TempWorkDir.Create("labels");
        string path = w.Combine("spEffectLabels.json");
        File.WriteAllText(path, "{ not json");
        var labels = SpEffectLabels.Load(path);
        Assert.Equal(0, labels.Count);
        Assert.NotNull(labels.LastError);
        Assert.Equal("{ not json", File.ReadAllText(path)); // loading never rewrites it

        labels.Set(42, "x");
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
        Assert.Equal("x", SpEffectLabels.Load(path).Get(42));
    }

    [Fact]
    public void Clean_MakesSafeSingleLine()
    {
        Assert.Equal("50pct chance a b", SpEffectLabels.Clean(" 50% chance\r\na\tb "));
        Assert.Equal(SpEffectLabels.MaxLabelLength, SpEffectLabels.Clean(new string('x', 500)).Length);
        Assert.Equal("", SpEffectLabels.Clean(null));
    }

    [Fact]
    public void BadEntries_AreSkipped()
    {
        using var w = TempWorkDir.Create("labels");
        string path = w.Combine("spEffectLabels.json");
        File.WriteAllText(path, "{ \"labels\": { \"abc\": \"x\", \"7\": 5, \"8\": \"\", \"9\": \"ok\" } }");
        var labels = SpEffectLabels.Load(path);
        Assert.Equal(1, labels.Count);
        Assert.Equal("ok", labels.Get(9));
    }
}
