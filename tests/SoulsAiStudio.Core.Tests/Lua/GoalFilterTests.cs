using SoulsAiStudio.Core.Lua.Analysis;

namespace SoulsAiStudio.Core.Tests.Lua;

public class GoalFilterTests
{
    [Theory]
    [InlineData("Kengeki09", "ken09")]
    [InlineData("Kengeki09", "k9")]
    [InlineData("Kengeki09", "keng")]
    [InlineData("Kengeki09", "KEN 9")]
    [InlineData("Kengeki09", "geki0")]       // plain substring
    [InlineData("Act01", "act1")]
    [InlineData("Act15", "act1")]
    [InlineData("Act05", "a5")]
    [InlineData("Kengeki_Activate", "kact")]
    [InlineData("Kengeki_Activate", "ken_act")]
    [InlineData("ShootReaction", "sr")]
    [InlineData("Interrupt", "int")]
    [InlineData("Anything", "")]
    public void Matches(string name, string query) => Assert.True(GoalFilter.Matches(name, query));

    [Theory]
    [InlineData("Kengeki10", "ken09")]
    [InlineData("Kengeki19", "ken09")]
    [InlineData("Act05", "act50")]
    [InlineData("Act50", "act05")]
    [InlineData("Act09", "ken")]
    [InlineData("Kengeki09", "9ken")]        // order matters
    [InlineData("Activate", "act1")]
    public void DoesNotMatch(string name, string query) => Assert.False(GoalFilter.Matches(name, query));

    [Fact]
    public void Segments_SplitCaseDigitsUnderscore() =>
        Assert.Equal(["Kengeki", "Activate", "Act", "05", "Shoot", "Reaction"], GoalFilter.Segments("Kengeki_Activate Act05 ShootReaction"));
}
