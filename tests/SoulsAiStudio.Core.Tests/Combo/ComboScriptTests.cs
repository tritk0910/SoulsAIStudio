using SoulsAiStudio.Core.Combo;

namespace SoulsAiStudio.Core.Tests.Combo;

public class ComboScriptTests
{
    [Fact]
    public void Dsas499Script_KeywordCutsPreviousAnim()
    {
        var r = ComboScript.Parse("EnemyComboAtk 3000\nEnemyComboAtk 3001\nNoCancel 3002\n");
        Assert.Empty(r.Diagnostics);
        Assert.Equal([3000, 3001, 3002], r.Steps.Select(s => s.AnimId));
        var own = r.OwnCuts(loop: false);
        Assert.Equal(23, own[0].JumpTableFlag);   // 3000 ends at 3001's keyword
        Assert.Equal(-1, own[1].JumpTableFlag);   // 3001 ends by NoCancel (line of 3002)
        Assert.Equal(-1, own[2].JumpTableFlag);   // last plays to the end
        Assert.Equal(23, r.OwnCuts(loop: true)[2].JumpTableFlag); // loop: cut like the first line
    }

    [Fact]
    public void SpEffectKeyword_FramesComments_AnimIdFormats()
    {
        var r = ComboScript.Parse("""
            # Owl
            enemycomboatk a000_003002   -- lower case + aXX_YYYYYY
            SpEffect 5060030 3048 0 120 // with start/end frames
            SetPlaybackSpeed 2
            Bogus 1
            EnemyComboAtk
            """);
        Assert.Equal([3002, 3048], r.Steps.Select(s => s.AnimId));
        Assert.Equal(5060030, r.OwnCuts(false)[0].SpEffectId);
        Assert.Equal((0f, 120f), (r.Steps[1].StartFrame!.Value, r.Steps[1].EndFrame!.Value));
        Assert.Contains(r.Diagnostics, d => !d.IsError && d.Message.Contains("SetPlaybackSpeed"));
        Assert.Equal(2, r.Diagnostics.Count(d => d.IsError));
        Assert.True(ComboScript.TryParseAnimId("a01_003000", out int id) && id == 1_003_000);
    }

    [Fact]
    public void Format_RoundTrips()
    {
        string text = ComboScript.Format([(3002, ComboCut.ComboAttackQueued), (3048, new ComboCut(-1, 5060030)), (3060, ComboCut.NoCancel)]);
        Assert.Equal("EnemyComboAtk 3002\nEnemyComboAtk 3048\nSpEffect 5060030 3060\n", text);
        var r = ComboScript.Parse(text);
        Assert.Equal(5060030, r.OwnCuts(false)[1].SpEffectId);
    }
}
