using SoulsAiStudio.Core.Lua.Syntax;
using SoulsAiStudio.Core.Tests.TestSupport;
using Xunit.Abstractions;

namespace SoulsAiStudio.Core.Tests.Lua;

public class LuaFormatterTests(ITestOutputHelper output)
{
    private static string F(string s) => LuaFormatter.Format(s) ?? throw new Xunit.Sdk.XunitException("Format returned null");

    [Fact]
    public void ReindentsBlocksAndNormalizesSpacing()
    {
        string src = "Goal.Act01=function(ai,goal)\nlocal a=1+2\nif a>1 then\n  goal:AddSubGoal(GOAL_X,10,3000,TARGET_ENE_0 ,9999,0)\nelse\nreturn\nend\nend\n";
        string expected =
            "Goal.Act01 = function(ai, goal)\n" +
            "    local a = 1 + 2\n" +
            "    if a > 1 then\n" +
            "        goal:AddSubGoal(GOAL_X, 10, 3000, TARGET_ENE_0, 9999, 0)\n" +
            "    else\n" +
            "        return\n" +
            "    end\n" +
            "end\n";
        Assert.Equal(expected, F(src));
    }

    [Fact]
    public void UnaryMinusHashAndConcat()
    {
        Assert.Equal("local a = -1\nlocal b = #t\nlocal c = x - -1\nlocal d = \"a\" .. b .. 1\nlocal e = f(-a, 2)\n",
            F("local a=-1\nlocal b=#t\nlocal c=x - -1\nlocal d=\"a\"..b..1\nlocal e=f( -a,2 )\n"));
    }

    [Fact]
    public void FunctionInsideCallIndentsOnce_AndTablesHang()
    {
        string src = "foo(1, function()\nbar()\nend)\nlocal t = {\n1,\n2,\n}\n";
        Assert.Equal("foo(1, function()\n    bar()\nend)\nlocal t = {\n    1,\n    2,\n}\n", F(src));
    }

    [Fact]
    public void LongStringsAndCommentsKeepContent_BlankLinesCollapse_FinalNewline()
    {
        string src = "\n\nlocal s = [[\n   keep   \n  this  ]]\n--[[ a\n    b ]]\n\n\n\nlocal x = 1   -- note\nlocal y = 2";
        Assert.Equal("local s = [[\n   keep   \n  this  ]]\n--[[ a\n    b ]]\n\nlocal x = 1   -- note\nlocal y = 2\n", F(src));
    }

    [Fact]
    public void ContinuationLinesGetOneExtraIndent()
    {
        string src = "if a == 1\nand b == 2 then\nx()\nend\nlocal s = \"a\" ..\n\"b\" ..\n\"c\"\n";
        Assert.Equal("if a == 1\n        and b == 2 then\n    x()\nend\nlocal s = \"a\" ..\n    \"b\" ..\n    \"c\"\n", F(src));
    }

    [Fact]
    public void KeepsCrlf_AndSyntaxErrorsAreNotFormatted()
    {
        Assert.Equal("if a then\r\n    b()\r\nend\r\n", F("if a then\r\nb()\r\nend\r\n"));
        Assert.Null(LuaFormatter.Format("if a then\nb(\n"));
    }

    [Fact]
    public void Idempotent()
    {
        string once = F("Goal.A=function(a)\nfor i=1,3 do\nwhile x do\nrepeat\ny=y+1\nuntil y>3\nend\nend\nend\n");
        Assert.Equal(once, F(once));
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(PlainFiles))]
    public void RealScripts_FormatSafely_AndAlreadyFormattedOnesBarelyChange(string rel)
    {
        string text = LuaCorpus.ReadText(rel).Replace("\r\n", "\n");
        string? r = LuaFormatter.Format(text);
        Assert.NotNull(r);
        Assert.Equal(r, LuaFormatter.Format(r!)); // idempotent
        var a = text.Split('\n'); var b = r!.Split('\n');
        int diff = a.Zip(b).Count(p => p.First != p.Second) + Math.Abs(a.Length - b.Length);
        output.WriteLine($"{rel}: {a.Length} lines, {diff} differ");
        if (Environment.GetEnvironmentVariable("SAS_FMT_DIFF") == "1")
            foreach (var p in a.Zip(b).Where(p => p.First != p.Second).Take(12))
                output.WriteLine($"  - {p.First}\n  + {p.Second}");
    }

    public static TheoryData<string> PlainFiles()
    {
        var d = new TheoryData<string>();
        foreach (var e in LuaCorpus.Entries.Where(e => !e.IsBytecode)) d.Add(e.RelPath);
        if (d.Count == 0) d.Add(LuaCorpus.NoDataSentinel);
        return d;
    }
}
