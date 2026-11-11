using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Tests.Lua;

public class LuaLexerTests
{
    private static List<(LuaTokenKind Kind, string Text)> Lex(string src) =>
        LuaLexer.Tokenize(src).Where(t => t.Kind != LuaTokenKind.EndOfFile).Select(t => (t.Kind, t.Text(src).ToString())).ToList();

    private static (LuaTokenKind, string) Single(string src)
    {
        var toks = Lex(src);
        Assert.True(toks.Count == 1, $"expected 1 token for «{src}», got: {string.Join(" | ", toks)}");
        return toks[0];
    }

    [Theory]
    [InlineData("[[abc]]")]
    [InlineData("[==[ a ]] still ]=] inside ]==]")]
    [InlineData("[=[\nline1\nline2]=]")]
    [InlineData("[[]]")]
    public void LongStrings_WithLevels(string s) => Assert.Equal((LuaTokenKind.String, s), Single(s));

    [Theory]
    [InlineData("--[[ block ]]")]
    [InlineData("--[==[ a ]] b ]=] c ]==]")]
    [InlineData("--[=[\nGoal.Fake = function() end\n]=]")]
    [InlineData("-- line comment")]
    [InlineData("--[ not long ]")]
    [InlineData("--[=x not long")]
    public void Comments_WithLevels(string s) => Assert.Equal((LuaTokenKind.Comment, s), Single(s));

    [Fact]
    public void LineComment_StopsAtNewline_LongCommentSpansLines()
    {
        var toks = Lex("--[[ a\nb ]] x -- c\ny");
        Assert.Equal([(LuaTokenKind.Comment, "--[[ a\nb ]]"), (LuaTokenKind.Identifier, "x"), (LuaTokenKind.Comment, "-- c"), (LuaTokenKind.Identifier, "y")], toks);
    }

    [Theory]
    [InlineData("\"a\\\"b\"")]          // "a\"b"
    [InlineData("'it\\'s'")]             // 'it\'s'
    [InlineData("\"back\\\\\"")]         // "back\\"
    [InlineData("\"single ' inside\"")]
    [InlineData("'double \" inside'")]
    [InlineData("\"\"")]
    [InlineData("\"\\n\\t\\065\"")]
    public void QuotedStrings_WithEscapes(string s) => Assert.Equal((LuaTokenKind.String, s), Single(s));

    [Fact]
    public void EscapedBackslashBeforeQuote_ClosesString()
    {
        // "a\\" then identifier b — the \\ is one escaped backslash, so the quote after it closes the string.
        Assert.Equal([(LuaTokenKind.String, "\"a\\\\\""), (LuaTokenKind.Identifier, "b")], Lex("\"a\\\\\" b"));
    }

    [Theory]
    [InlineData("0x1F")]
    [InlineData("0XfF")]
    [InlineData("1e-3")]
    [InlineData("1E+10")]
    [InlineData("2e5")]
    [InlineData(".5")]
    [InlineData("3.")]
    [InlineData("3.14159")]
    [InlineData("0")]
    [InlineData("9999")]
    public void Numbers(string s) => Assert.Equal((LuaTokenKind.Number, s), Single(s));

    [Fact]
    public void NumberFollowedByConcat_IsSplit()
    {
        // `1 .. x` is fine; also common: a .. 5 .. b
        Assert.Equal([(LuaTokenKind.Identifier, "a"), (LuaTokenKind.Operator, ".."), (LuaTokenKind.Number, "5"), (LuaTokenKind.Operator, ".."), (LuaTokenKind.Identifier, "b")],
            Lex("a .. 5 .. b"));
    }

    [Fact]
    public void FieldAccessDot_NotNumber()
    {
        Assert.Equal([(LuaTokenKind.Identifier, "t"), (LuaTokenKind.Operator, "."), (LuaTokenKind.Identifier, "x")], Lex("t.x"));
        // `t[1].x` and a vararg
        Assert.Contains((LuaTokenKind.Operator, "..."), Lex("function f(...) end"));
    }

    [Theory]
    [InlineData("==")]
    [InlineData("~=")]
    [InlineData("<=")]
    [InlineData(">=")]
    [InlineData("..")]
    [InlineData("...")]
    public void MultiCharOperators(string s) => Assert.Equal((LuaTokenKind.Operator, s), Single(s));

    [Fact]
    public void Keywords_vs_Identifiers()
    {
        var toks = Lex("local do_it = endx and nil or not_ until");
        Assert.Equal(LuaTokenKind.Keyword, toks[0].Kind);
        Assert.Equal(LuaTokenKind.Identifier, toks[1].Kind); // do_it
        Assert.Equal(LuaTokenKind.Identifier, toks[3].Kind); // endx
        Assert.Equal(LuaTokenKind.Keyword, toks[4].Kind);    // and
        Assert.Equal(LuaTokenKind.Identifier, toks[7].Kind); // not_
        Assert.Equal(LuaTokenKind.Keyword, toks[8].Kind);    // until
    }

    [Fact]
    public void LinesAndColumns_AreZeroBased_AndSurviveMultilineTokens()
    {
        string src = "a\n  --[[x\ny]] b\r\n\tc";
        var t = LuaLexer.Tokenize(src);
        Assert.Equal((0, 0), (t[0].Line, t[0].Column));   // a
        Assert.Equal((1, 2), (t[1].Line, t[1].Column));   // comment
        Assert.Equal((2, 4), (t[2].Line, t[2].Column));   // b (after "y]] ")
        Assert.Equal((3, 1), (t[3].Line, t[3].Column));   // c (after CRLF + tab)
        Assert.Equal(LuaTokenKind.EndOfFile, t[^1].Kind);
        Assert.Equal(src.Length, t[^1].Start);
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("'abc\nnext")]
    [InlineData("[[never closed")]
    [InlineData("--[==[ never closed ]=]")]
    [InlineData("\"trailing backslash\\")]
    [InlineData("0x")]
    [InlineData("@#$%")]
    [InlineData("")]
    public void MalformedInput_NeverThrows_AndCoversInput(string src)
    {
        LuaDecompilerTests.AssertLexesWithoutGaps(src);
    }

    [Fact]
    public void ShebangLessHavokPatterns_TableConstructorsAndCalls()
    {
        var toks = Lex("arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 1, 3012, TARGET_ENE_0, 9999, 0, 0):TimingSetNumber(13, 0, AI_TIMING_SET__ACTIVATE)");
        Assert.Equal(2, toks.Count(x => x == (LuaTokenKind.Operator, ":")));
        Assert.Contains((LuaTokenKind.Number, "3012"), toks);
    }
}
