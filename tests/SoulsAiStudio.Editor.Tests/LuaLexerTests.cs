using SoulsAiStudio.Editor.Syntax;

namespace SoulsAiStudio.Editor.Tests;

public class LuaLexerTests
{
    static List<(string Text, LuaTokenKind Kind)> Lex(string line, ref LuaLexState state)
    {
        var tokens = LuaLexer.TokenizeLine(line, ref state);
        return tokens.Select(t => (line.Substring(t.Start, t.Length), t.Kind)).ToList();
    }

    static List<(string Text, LuaTokenKind Kind)> Lex(string line)
    {
        var s = LuaLexState.Normal;
        var r = Lex(line, ref s);
        Assert.True(s.IsNormal);
        return r;
    }

    static LuaTokenKind KindOf(string line, string word)
    {
        var s = LuaLexState.Normal;
        var kinds = new byte[line.Length];
        LuaLexer.ColorizeLine(line, kinds, s);
        int i = line.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, word);
        for (int k = i; k < i + word.Length; k++)
            Assert.Equal(kinds[i], kinds[k]);
        return (LuaTokenKind)kinds[i];
    }

    [Fact]
    public void Keywords_Literals_Identifiers_Functions()
    {
        const string line = "local x = nil if not true then return Foo(x) end";
        Assert.Equal(LuaTokenKind.Keyword, KindOf(line, "local"));
        Assert.Equal(LuaTokenKind.Identifier, KindOf(line, "x"));
        Assert.Equal(LuaTokenKind.Literal, KindOf(line, "nil"));
        Assert.Equal(LuaTokenKind.Keyword, KindOf(line, "not"));
        Assert.Equal(LuaTokenKind.Literal, KindOf(line, "true"));
        Assert.Equal(LuaTokenKind.Function, KindOf(line, "Foo"));
        Assert.Equal(LuaTokenKind.Operator, KindOf(line, "="));
        Assert.Equal(LuaTokenKind.Keyword, KindOf(line, "end"));
    }

    [Fact]
    public void Sekiro_Constants()
    {
        const string line = "arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0)";
        Assert.Equal(LuaTokenKind.Constant, KindOf(line, "GOAL_COMMON_ComboRepeat"));
        Assert.Equal(LuaTokenKind.Constant, KindOf(line, "TARGET_ENE_0"));
        Assert.Equal(LuaTokenKind.Function, KindOf(line, "AddSubGoal"));
        Assert.Equal(LuaTokenKind.Number, KindOf(line, "3002"));
        Assert.Equal(LuaTokenKind.Constant, KindOf("if arg1:IsInterupt(INTERUPT_ActivateSpecialEffect) then", "INTERUPT_ActivateSpecialEffect"));
        Assert.Equal(LuaTokenKind.Constant, KindOf("x = AI_TIMING_SET__ACTIVATE", "AI_TIMING_SET__ACTIVATE"));
        Assert.Equal(LuaTokenKind.Constant, KindOf("x = SOME_FLAG_2", "SOME_FLAG_2"));
        Assert.Equal(LuaTokenKind.Identifier, KindOf("x = f2_local6", "f2_local6"));
        Assert.Equal(LuaTokenKind.Identifier, KindOf("x = Goal.Act01", "Goal"));
    }

    [Fact]
    public void Strings_WithEscapes_AndUnterminated()
    {
        var t = Lex("x = \"a \\\" b\" .. 'c'");
        Assert.Contains(("\"a \\\" b\"", LuaTokenKind.String), t);
        Assert.Contains(("'c'", LuaTokenKind.String), t);
        var u = Lex("x = \"open");
        Assert.Equal(("\"open", LuaTokenKind.String), u[^1]);
    }

    [Fact]
    public void Numbers()
    {
        foreach (var n in new[] { "3", "3.5", ".5", "1e10", "2.5E-3", "0x1F", "0xA.8p1" })
            Assert.Equal(LuaTokenKind.Number, KindOf($"x = {n} + y", n));
    }

    [Fact]
    public void LineComment_AndVietnamese()
    {
        var t = Lex("x = 1 -- Nhánh rẽ khi trúng đòn: ơ ư đ ă ạ ế ỹ");
        Assert.Equal(("-- Nhánh rẽ khi trúng đòn: ơ ư đ ă ạ ế ỹ", LuaTokenKind.Comment), t[^1]);
    }

    [Fact]
    public void LongComment_SpansLines()
    {
        var s = LuaLexState.Normal;
        var a = Lex("x = 1 --[[ start", ref s);
        Assert.Equal(new LuaLexState(2, 0), s);
        Assert.Equal(LuaTokenKind.Comment, a[^1].Kind);
        var b = Lex("still comment ]=] ignored", ref s);
        Assert.Equal(new LuaLexState(2, 0), s);
        Assert.Single(b);
        var c = Lex("end ]] y = 2", ref s);
        Assert.True(s.IsNormal);
        Assert.Equal(("end ]]", LuaTokenKind.Comment), c[0]);
        Assert.Contains(("y", LuaTokenKind.Identifier), c);
    }

    [Fact]
    public void LongBracketLevels_MustMatch()
    {
        var s = LuaLexState.Normal;
        Lex("--[==[ a ]] b ]=] c", ref s);
        Assert.Equal(new LuaLexState(2, 2), s);
        var r = Lex("]==] local", ref s);
        Assert.True(s.IsNormal);
        Assert.Equal(("local", LuaTokenKind.Keyword), r[^1]);

        s = LuaLexState.Normal;
        Lex("x = [[ long", ref s);
        Assert.Equal(new LuaLexState(1, 0), s);
        var e = Lex("string]] .. y", ref s);
        Assert.True(s.IsNormal);
        Assert.Equal(("string]]", LuaTokenKind.String), e[0]);
    }

    [Fact]
    public void IndexingBracket_IsNotLongString()
    {
        var t = Lex("x = t[i] + a[ [=[s]=] ]");
        Assert.Contains(("[=[s]=]", LuaTokenKind.String), t);
        Assert.Equal(LuaTokenKind.Identifier, KindOf("x = t[i]", "i"));
    }
}
