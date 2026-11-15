using SoulsAiStudio.Core.Combo;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

/// <summary>Per-script anim category (e.g. Isshin's spear phase 540310 uses a100): Lua "3000" → a100_003000.</summary>
public class AnimIdsTests
{
    [Theory]
    [InlineData(3000, 0, 3000)]
    [InlineData(3000, 100, 100_003000)]
    [InlineData(999_999, 100, 100_999_999)]
    [InlineData(100_003000, 100, 100_003000)] // already a full id
    [InlineData(1_003000, 100, 1_003000)]     // explicit other category stays
    [InlineData(-1, 100, -1)]
    public void WithCategory(int lua, int category, int expected) => Assert.Equal(expected, AnimIds.WithCategory(lua, category));

    [Theory]
    [InlineData(100_003000, "a100_003000")]
    [InlineData(3000, "a000_003000")]
    public void Format(int id, string expected) => Assert.Equal(expected, AnimIds.Format(id));

    [Theory]
    [InlineData("a100", 100)]
    [InlineData("100", 100)]
    [InlineData(" A050 ", 50)]
    [InlineData("", 0)]
    [InlineData("a1000", null)]
    [InlineData("x1", null)]
    [InlineData("-1", null)]
    public void ParseCategory(string text, int? expected) => Assert.Equal(expected, AnimIds.ParseCategory(text));

    [Fact]
    public void ComboScript_FormatsCategoryIds_AndParsesThemBack()
    {
        string text = ComboScript.Format([(100_003000, ComboCut.ComboAttackQueued), (100_003016, ComboCut.NoCancel)]);
        Assert.Contains("a100_003000", text);
        Assert.Contains("a100_003016", text);
        var parsed = ComboScript.Parse(text);
        Assert.Equal([100_003000, 100_003016], parsed.Steps.Select(s => s.AnimId));
    }

    [Fact]
    public void ProjectScript_AnimCategory_RoundTrips()
    {
        using var work = TempWorkDir.Create("animcat");
        string game = Path.Combine(work.Path, "game"), mod = Path.Combine(work.Path, "mod");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mod);
        string path = Path.Combine(work.Path, "p", "p.sasproj");
        var p = SasProject.Create(path, "p", game, mod,
            [new ProjectScript { Luabnd = "m11_02_00_00", EntryPath = @"out\bin\540310_battle.lua", AnimCategory = 100 }]);
        var loaded = SasProject.Load(path);
        Assert.Equal(100, loaded.Scripts[0].AnimCategory);
        Assert.Equal(100_003000, loaded.Scripts[0].ToTaeAnimId(3000));
        Assert.Equal(3000, new ProjectScript().ToTaeAnimId(3000));
    }
}
