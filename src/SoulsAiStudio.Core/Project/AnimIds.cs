namespace SoulsAiStudio.Core.Project;

/// <summary>Sekiro anim ids: full id = category * 1,000,000 + sub id, written <c>aCCC_SSSSSS</c> (e.g. 100003000 = a100_003000).</summary>
public static class AnimIds
{
    public const int CategoryFactor = 1_000_000;

    /// <summary>Puts a bare sub id (&lt; 1,000,000, as written in Lua) in <paramref name="category"/>; full ids and negatives pass through.</summary>
    public static int WithCategory(int animId, int category) =>
        animId is >= 0 and < CategoryFactor && category > 0 ? category * CategoryFactor + animId : animId;

    /// <summary><c>a100_003000</c> for 100003000; <c>a000_003000</c> for 3000.</summary>
    public static string Format(int fullId) =>
        fullId < 0 ? fullId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   : $"a{fullId / CategoryFactor:D3}_{fullId % CategoryFactor:D6}";

    /// <summary>Parses <c>a100</c>, <c>100</c> or empty (= 0). Null if invalid.</summary>
    public static int? ParseCategory(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return 0;
        if (t[0] is 'a' or 'A') t = t[1..];
        return int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int c) && c is >= 0 and < 1000
            ? c : null;
    }
}
