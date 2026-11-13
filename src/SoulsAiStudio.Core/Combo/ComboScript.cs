using System.Globalization;

namespace SoulsAiStudio.Core.Combo;

/// <summary>How an anim of a combo ends (and the next one takes over).</summary>
/// <param name="JumpTableFlag">Cut at the start of this JumpTable (TAE type 0) flag, e.g. 23 = "End If AI ComboAttack Queued"; -1 = play to the end (NoCancel).</param>
/// <param name="SpEffectId">Cut at the start of this SpEffect event instead (Goal.Interrupt style); JumpTableFlag is then ignored.</param>
/// <param name="AtFrame">With SpEffectId: cut at this frame inside the SpEffect event's window instead of its start
/// (a SpEffect can re-trigger the interrupt every interval while active, e.g. once a distance check passes).</param>
public sealed record ComboCut(int JumpTableFlag, int? SpEffectId = null, float? AtFrame = null)
{
    public static readonly ComboCut ComboAttackQueued = new(ComboScript.EnemyComboAtkFlag);
    public static readonly ComboCut NoCancel = new(-1);
}

/// <summary>
/// One anim line of a combo script. <paramref name="EntryCut"/> is the line's keyword: how the PREVIOUS anim ends
/// (DSAS 4.9.9 semantics). <paramref name="StartFrame"/>/<paramref name="EndFrame"/> are the optional 4.9.9 overrides.
/// </summary>
public sealed record ComboScriptStep(int Line, int AnimId, ComboCut EntryCut, float? StartFrame, float? EndFrame);

public sealed record ComboScriptDiagnostic(int Line, string Message, bool IsError);

public sealed record ComboScriptResult(IReadOnlyList<ComboScriptStep> Steps, IReadOnlyList<ComboScriptDiagnostic> Diagnostics)
{
    /// <summary>
    /// How each step itself ends: step i is cut by step i+1's keyword. The last step plays to its end, or with
    /// <paramref name="loop"/> is cut by the first line's keyword (DSAS 4.9.9) so the replay joins seamlessly.
    /// </summary>
    public IReadOnlyList<ComboCut> OwnCuts(bool loop)
    {
        var cuts = new ComboCut[Steps.Count];
        for (int i = 0; i < Steps.Count; i++)
            cuts[i] = i + 1 < Steps.Count ? Steps[i + 1].EntryCut : loop ? Steps[0].EntryCut : ComboCut.NoCancel;
        return cuts;
    }
}

/// <summary>
/// Text combo format of the Combo panel's Manual tab, compatible with DSAS 4.9.9's Combo Viewer:
/// <code>
/// EnemyComboAtk 3002            # 4.9.9: keyword = how the previous anim ends (JumpTable flag 23 here)
/// SpEffect 5060030 3060         # new: previous anim ends at its SpEffect 5060030 event
/// SpEffect 5358@30 3049         # new: … at frame 30, inside that SpEffect event's window
/// NoCancel 3016 [start] [end]   # previous anim plays to its end; optional start/end frames (4.9.9)
/// </code>
/// 4.9.9's Set* commands are recognized and reported as not supported.
/// </summary>
public static class ComboScript
{
    public const int EnemyComboAtkFlag = 23;

    /// <summary>DSAS 4.9.9 keywords (TaeComboMenu.TaeComboAnimType) → JumpTable flag; NoCancel = -1.</summary>
    public static readonly IReadOnlyDictionary<string, int> CancelKeywords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["NoCancel"] = -1, ["PlayerRH"] = 4, ["PlayerMove"] = 11, ["PlayerLH"] = 16, ["PlayerGuard"] = 22,
        ["EnemyComboAtk"] = 23, ["PlayerDodge"] = 26, ["PlayerEstus"] = 30, ["PlayerItem"] = 31,
        ["PlayerWeaponSwitch"] = 32, ["ThrowEscape"] = 68, ["EnemyMove"] = 78, ["EnemyDodge"] = 79, ["EnemyAtk"] = 86,
    };

    public const string SpEffectKeyword = "SpEffect";

    /// <summary>Keyword for a JumpTable flag (the 4.9.9 name if there is one).</summary>
    public static string KeywordFor(ComboCut cut) =>
        cut.SpEffectId is int sp ? $"{SpEffectKeyword} {sp}" + (cut.AtFrame is float at ? FormattableString.Invariant($"@{at:0.##}") : "")
        : CancelKeywords.FirstOrDefault(kv => kv.Value == cut.JumpTableFlag).Key ?? $"JumpTable {cut.JumpTableFlag}";

    public static ComboScriptResult Parse(string text)
    {
        var steps = new List<ComboScriptStep>();
        var diags = new List<ComboScriptDiagnostic>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int ln = 0; ln < lines.Length; ln++)
        {
            string line = StripComment(lines[ln]);
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;

            ComboCut cut;
            int k; // index of the anim token
            if (t[0].Equals(SpEffectKeyword, StringComparison.OrdinalIgnoreCase))
            {
                string spText = t.Length >= 2 ? t[1] : "";
                float? at = null;
                int atIdx = spText.IndexOf('@');
                if (atIdx >= 0)
                {
                    if (!TryFrame(spText[(atIdx + 1)..], out float a))
                    { diags.Add(new(ln, "SpEffect <id>@<frame>: the frame after @ is not a number", true)); continue; }
                    at = a;
                    spText = spText[..atIdx];
                }
                if (!int.TryParse(spText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sp))
                { diags.Add(new(ln, "SpEffect needs an id: SpEffect <id> <anim>  (or SpEffect <id>@<frame> <anim>)", true)); continue; }
                cut = new ComboCut(-1, sp, at);
                k = 2;
            }
            else if (t[0].Equals("JumpTable", StringComparison.OrdinalIgnoreCase))
            {
                if (t.Length < 2 || !int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flag))
                { diags.Add(new(ln, "JumpTable needs a flag: JumpTable <flag> <anim>", true)); continue; }
                cut = new ComboCut(flag);
                k = 2;
            }
            else if (CancelKeywords.TryGetValue(t[0], out int f))
            {
                cut = new ComboCut(f);
                k = 1;
            }
            else if (t[0].StartsWith("Set", StringComparison.OrdinalIgnoreCase))
            {
                diags.Add(new(ln, $"{t[0]} (DSAS 4.9.9) is not supported; line ignored", false));
                continue;
            }
            else
            {
                diags.Add(new(ln, $"Unknown keyword '{t[0]}'. Use EnemyComboAtk, SpEffect <id>, NoCancel, …", true));
                continue;
            }

            if (t.Length <= k || !TryParseAnimId(t[k], out int anim))
            { diags.Add(new(ln, "Missing or invalid anim id (e.g. 3002 or a000_003002)", true)); continue; }

            float? start = null, end = null;
            if (t.Length > k + 1)
            {
                if (TryFrame(t[k + 1], out float s)) start = s;
                else { diags.Add(new(ln, $"'{t[k + 1]}' is not a start frame", true)); continue; }
            }
            if (t.Length > k + 2)
            {
                if (TryFrame(t[k + 2], out float e)) end = e;
                else { diags.Add(new(ln, $"'{t[k + 2]}' is not an end frame", true)); continue; }
            }
            if (t.Length > k + 3) diags.Add(new(ln, "Extra text after the end frame is ignored", false));
            steps.Add(new ComboScriptStep(ln, anim, cut, start, end));
        }
        return new ComboScriptResult(steps, diags);
    }

    /// <summary>Script text for a sequence of anims and how each one ends (inverse of <see cref="ComboScriptResult.OwnCuts"/>).</summary>
    public static string Format(IReadOnlyList<(int AnimId, ComboCut OwnCut)> steps)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < steps.Count; i++)
        {
            var entry = i == 0 ? ComboCut.ComboAttackQueued : steps[i - 1].OwnCut;
            sb.Append(KeywordFor(entry)).Append(' ').Append(steps[i].AnimId).Append('\n');
        }
        return sb.ToString();
    }

    private static string StripComment(string s)
    {
        int cut = s.Length;
        foreach (var marker in new[] { "#", "--", "//" })
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && i < cut) cut = i;
        }
        return s[..cut];
    }

    private static bool TryFrame(string s, out float f) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f >= 0;

    /// <summary><c>3002</c>, <c>1003002</c>, or <c>aXX_YYYYYY</c> (category XX → XX*1,000,000 + YYYYYY).</summary>
    public static bool TryParseAnimId(string s, out int id)
    {
        id = 0;
        if (s.Length > 1 && (s[0] == 'a' || s[0] == 'A'))
        {
            var parts = s[1..].Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int cat)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int sub) && sub < 1_000_000)
            { id = cat * 1_000_000 + sub; return true; }
            return false;
        }
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}
