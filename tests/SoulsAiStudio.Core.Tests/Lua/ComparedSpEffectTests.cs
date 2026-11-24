using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Tests.Lua;

public class ComparedSpEffectTests
{
    [Fact]
    public void KengekiEffectVariable_ComparedNumbersAreSpEffects()
    {
        const string src = """
            Goal.Kengeki_Activate = function (ai, goal)
                local kengekiEffect = ReturnKengekiSpecialEffect(ai)
                local dist = ai:GetDist(TARGET_ENE_0)
                if kengekiEffect == 200200 then
                    act[1] = 100
                elseif 200210 == kengekiEffect then
                    act[2] = 100
                elseif dist == 7 then
                    act[3] = 1
                end
            end
            """;
        var ids = SpEffectInterrupts.ComparedSpEffectIds(src, LuaLexer.Tokenize(src));
        Assert.Contains(200200, ids);
        Assert.Contains(200210, ids);
        Assert.DoesNotContain(7, ids);
    }
}
