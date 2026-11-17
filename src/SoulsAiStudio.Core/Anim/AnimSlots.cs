using System.Globalization;
using System.Text;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Lua.Syntax;

namespace SoulsAiStudio.Core.Anim;

/// <summary>How a TAE animation entry gets its motion (TAE <c>AnimFileHeader</c>).</summary>
public enum AnimHeaderKind
{
    /// <summary>Standard header: plays the HKX file with its own id.</summary>
    Own,
    /// <summary>Standard header with ImportsHKX: plays the HKX of <see cref="AnimEntryInfo.RefId"/> ("Override HKX").</summary>
    OverrideHkx,
    /// <summary>ImportOtherAnim: uses the whole entry (events + motion) of <see cref="AnimEntryInfo.RefId"/>.</summary>
    ImportOther,
}

/// <param name="Id">Full anim id (category * 1,000,000 + sub id).</param>
/// <param name="RefId">Referenced anim id for <see cref="AnimHeaderKind.OverrideHkx"/> / <see cref="AnimHeaderKind.ImportOther"/>, else -1.</param>
/// <param name="Name">The entry's AnimFileName (a label: "a000_003001.hkt" or e.g. "x slash").</param>
public sealed record AnimEntryInfo(int Id, AnimHeaderKind Kind, int RefId, string Name = "");

/// <summary>Another entry's reference that a swap re-targets (it follows the motion/entry it pointed to).</summary>
/// <param name="EntryId">Id of the referencing entry after the swap.</param>
public sealed record AnimRefChange(int EntryId, AnimHeaderKind Kind, int OldRef, int NewRef);

/// <summary>What swapping two anim ids changes in an anibnd.</summary>
/// <param name="EntryMoves">Entries that change id (old → new); one or two.</param>
/// <param name="HkxMoves">HKX files that change id (old → new); zero, one or two.</param>
/// <param name="NameChanges">AnimFileName labels of the auto form "aXXX_YYYYYY.hkt" that follow the new id (entry id after the swap → new name).</param>
public sealed record AnimSwapPlan(
    int A, int B,
    IReadOnlyList<(int From, int To)> EntryMoves,
    IReadOnlyList<(int From, int To)> HkxMoves,
    IReadOnlyList<AnimRefChange> RefChanges,
    IReadOnlyList<(int EntryId, string OldName, string NewName)> NameChanges,
    IReadOnlyList<string> Warnings)
{
    /// <summary>a → b, b → a, anything else unchanged.</summary>
    public int Map(int id) => id == A ? B : id == B ? A : id;
}

/// <summary>Result of checking an .hkx file before adding it to an anibnd.</summary>
public sealed record HkxCheck(bool IsSekiroTagfile, string? CompendiumRef, IReadOnlyList<string> Problems)
{
    public bool Ok => IsSekiroTagfile && Problems.Count == 0;
}

/// <summary>
/// Swapping and adding animation slots in an anibnd (TAE entries + HKX files), planned without DSAS types so it can be
/// tested. A swap renames both ids at once: entries, HKX files and every Override HKX / ImportOtherAnim reference to
/// either id follow the content, so anims that borrowed a motion still play the same motion afterwards.
/// </summary>
public static class AnimSlots
{
    /// <summary>Binder id of an anim HKX in a Sekiro (and later) anibnd: 1,000,000,000 + full anim id.</summary>
    public const int HkxBinderBase = 1_000_000_000;

    public static int HkxBinderId(int animId) => HkxBinderBase + animId;

    /// <summary><c>a000_003001</c> for 3001, <c>a100_003001</c> for 100003001.</summary>
    public static string IdName(int animId) =>
        $"a{animId / 1_000_000:D3}_{animId % 1_000_000:D6}";

    public static AnimSwapPlan PlanSwap(IReadOnlyList<AnimEntryInfo> entries, IReadOnlySet<int> hkxIds, int a, int b)
    {
        var warnings = new List<string>();
        if (a == b) throw new ArgumentException("Pick two different animations.");
        bool hasA = entries.Any(e => e.Id == a), hasB = entries.Any(e => e.Id == b);
        if (!hasA && !hasB) throw new ArgumentException($"Neither {IdName(a)} nor {IdName(b)} exists.");
        if (entries.Count(e => e.Id == a) > 1 || entries.Count(e => e.Id == b) > 1)
            warnings.Add("One of the ids has more than one entry (an existing id conflict): all of them are swapped.");
        if (a / 1_000_000 != b / 1_000_000)
            warnings.Add($"{IdName(a)} and {IdName(b)} are in different categories.");

        int Map(int id) => id == a ? b : id == b ? a : id;
        var moves = new List<(int, int)>();
        if (hasA) moves.Add((a, b));
        if (hasB) moves.Add((b, a));
        var hkx = new List<(int, int)>();
        if (hkxIds.Contains(a)) hkx.Add((a, b));
        if (hkxIds.Contains(b)) hkx.Add((b, a));

        var refs = entries.Where(e => e.Kind != AnimHeaderKind.Own && (e.RefId == a || e.RefId == b))
            .Select(e => new AnimRefChange(Map(e.Id), e.Kind, e.RefId, Map(e.RefId)))
            .OrderBy(r => r.EntryId).ToList();

        var names = new List<(int, string, string)>();
        foreach (var e in entries.Where(e => e.Id == a || e.Id == b))
            if (RenameAutoName(e.Name, e.Id, Map(e.Id)) is { } nn) names.Add((Map(e.Id), e.Name, nn));

        // An entry that would end up playing a missing HKX.
        foreach (var e in entries.Where(e => e.Id == a || e.Id == b))
        {
            int newId = Map(e.Id);
            if (e.Kind == AnimHeaderKind.Own && !hkxIds.Contains(e.Id))
                warnings.Add($"{IdName(e.Id)} has no HKX file of its own (it already plays nothing / a missing motion); it stays that way at {IdName(newId)}.");
        }
        return new AnimSwapPlan(a, b, moves, hkx, refs, names, warnings);
    }

    /// <summary>"a000_003001.hkt" (the auto label of id 3001) → "a000_003012.hkt"; custom labels → null (kept).</summary>
    public static string? RenameAutoName(string name, int oldId, int newId)
    {
        string oldStem = IdName(oldId);
        if (!name.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = name[oldStem.Length..];
        if (rest.Length > 0 && rest[0] != '.') return null; // "a000_0030010" etc.
        return IdName(newId) + rest;
    }

    /// <summary>Why a new slot can't use <paramref name="id"/> (entry or HKX already there), or null.</summary>
    public static string? WhyIdTaken(IReadOnlyList<AnimEntryInfo> entries, IReadOnlySet<int> hkxIds, int id)
    {
        if (id < 0) return "Invalid id.";
        if (entries.Any(e => e.Id == id)) return $"{IdName(id)} already exists.";
        if (hkxIds.Contains(id)) return $"The anibnd already has an HKX file for {IdName(id)} (with no TAE entry).";
        return null;
    }

    /// <summary>The first free id after <paramref name="after"/> in the same category (no entry and no HKX).</summary>
    public static int NextFreeId(IReadOnlyList<AnimEntryInfo> entries, IReadOnlySet<int> hkxIds, int after)
    {
        int cat = after / 1_000_000;
        for (int id = after + 1; id / 1_000_000 == cat; id++)
            if (WhyIdTaken(entries, hkxIds, id) == null) return id;
        return after;
    }

    // ------------------------------------------------------------------ HKX checks

    /// <summary>
    /// Checks an .hkx file for a Sekiro anibnd: a Havok 2016 tagfile (TAG0, SDK 20160200) whose type compendium
    /// reference (TCRF) is the anibnd's own compendium or one the anibnd's anims already use.
    /// Whether it matches the character's skeleton is checked by loading it (see the app).
    /// </summary>
    public static HkxCheck CheckHkx(byte[] hkx, IReadOnlyCollection<string> knownCompendiumIds)
    {
        var problems = new List<string>();
        bool tag = hkx.Length > 0x20 && Ascii(hkx, 4, 4) == "TAG0";
        if (!tag) problems.Add("Not a Havok tagfile (TAG0): Sekiro anims are Havok 2016 tagfiles. Packfiles from other games must be converted first.");
        else if (IndexOf(hkx, "SDKV"u8) is int v and >= 0 && v + 12 <= hkx.Length && Ascii(hkx, v + 4, 8) != "20160200")
            problems.Add($"Havok version {Ascii(hkx, v + 4, 8)}, Sekiro uses 20160200.");
        string? cref = null;
        if (tag && IndexOf(hkx, "TCRF"u8) is int r and >= 0 && r + 12 <= hkx.Length)
        {
            cref = Convert.ToHexString(hkx, r + 4, 8).ToLowerInvariant();
            if (knownCompendiumIds.Count > 0 && !knownCompendiumIds.Contains(cref))
                problems.Add($"It needs type compendium {cref}, which this anibnd does not have or use. It was probably saved for another file; the game may fail to load it.");
        }
        return new HkxCheck(tag, cref, problems);
    }

    /// <summary>Compendium ids an anibnd provides (TCID of its .compendium) or its anims already reference (TCRF).</summary>
    public static HashSet<string> CompendiumIds(IEnumerable<byte[]> compendiumFiles, IEnumerable<byte[]> hkxFiles)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in compendiumFiles)
            if (IndexOf(c, "TCID"u8) is int i and >= 0 && i + 12 <= c.Length) set.Add(Convert.ToHexString(c, i + 4, 8).ToLowerInvariant());
        foreach (var h in hkxFiles)
            if (IndexOf(h, "TCRF"u8) is int i and >= 0 && i + 12 <= h.Length) set.Add(Convert.ToHexString(h, i + 4, 8).ToLowerInvariant());
        return set;
    }

    private static string Ascii(byte[] b, int at, int n) => at + n <= b.Length ? Encoding.ASCII.GetString(b, at, n) : "";

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);

    // ------------------------------------------------------------------ Lua

    /// <summary>A literal anim id in Lua that a swap rewrites.</summary>
    public sealed record LuaAnimLiteral(int Line, int Column, int Length, int Value, string Context);

    /// <summary>
    /// Literal anim ids in a battle script: the anim argument of <c>AddSubGoal</c> (also through a numeric local such
    /// as <c>local animationId = 3010</c>, then the local's literal) and of <c>SetCoolTime(ai, goal, anim, ...)</c>.
    /// </summary>
    public static IReadOnlyList<LuaAnimLiteral> FindLuaAnimLiterals(string src)
    {
        var t = LuaLexer.Tokenize(src).Where(x => x.Kind != LuaTokenKind.Comment).ToList();
        var result = new List<LuaAnimLiteral>();
        var localLit = new Dictionary<string, LuaToken>(StringComparer.Ordinal);
        for (int j = 0; j + 7 < t.Count; j++)
        {
            if (t[j].Kind == LuaTokenKind.Identifier && t[j + 1].Is(src, "=") && t[j + 2].Kind == LuaTokenKind.Number
                && !(j > 0 && (t[j - 1].Is(src, ".") || t[j - 1].Is(src, ":") || t[j - 1].Is(src, ",")))
                && t[j + 3].Text(src) is not ("+" or "-" or "*" or "/" or "%" or "^" or ".." or "," or "."))
                localLit[t[j].Text(src).ToString()] = t[j + 2];

            // : AddSubGoal ( GOAL , time , anim
            if (t[j].Is(src, ":") && t[j + 1].Is(src, "AddSubGoal") && t[j + 2].Is(src, "(") && t[j + 3].Kind == LuaTokenKind.Identifier && t[j + 4].Is(src, ","))
            {
                int k = j + 5;
                if (t[k].Is(src, "-")) k++;
                if (!t[k + 1].Is(src, ",")) continue;
                var arg = t[k + 2];
                if (arg.Kind == LuaTokenKind.Number) Add(arg, "AddSubGoal");
                else if (arg.Kind == LuaTokenKind.Identifier && !t[k + 3].Is(src, ".") && !t[k + 3].Is(src, ":") && !t[k + 3].Is(src, "(")
                         && localLit.TryGetValue(arg.Text(src).ToString(), out var lit)) Add(lit, "AddSubGoal (local)");
            }
            // SetCoolTime ( ai , goal , anim ,
            if (t[j].Is(src, "SetCoolTime") && t[j + 1].Is(src, "("))
            {
                int depth = 0, comma = 0, k = j + 2;
                for (; k < t.Count; k++)
                {
                    var s = t[k].Text(src);
                    if (s is "(" or "{" or "[") depth++;
                    else if (s is ")" or "}" or "]") { if (depth-- == 0) break; }
                    else if (s is "," && depth == 0 && ++comma == 2) { k++; break; }
                }
                if (comma == 2 && k + 1 < t.Count && t[k].Kind == LuaTokenKind.Number && t[k + 1].Is(src, ",")) Add(t[k], "SetCoolTime");
            }
        }
        return result.DistinctBy(r => (r.Line, r.Column)).OrderBy(r => r.Line).ThenBy(r => r.Column).ToList();

        void Add(LuaToken tok, string ctx)
        {
            if (int.TryParse(tok.Text(src), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                result.Add(new LuaAnimLiteral(tok.Line, tok.Column, tok.Length, v, ctx));
        }
    }

    /// <summary>
    /// Which aXXX category a script's anim ids (as written, e.g. 3001) most likely refer to: the one whose TAE has the
    /// most of them. <c>Sure</c> is false on a tie (e.g. Isshin: every id of the spear phase 540310 exists in both a000
    /// and a100), then the first tied category is returned and the user has to choose.
    /// </summary>
    public static (int Category, bool Sure) GuessLuaCategory(IEnumerable<int> luaIds, IReadOnlyCollection<int> taeIds)
    {
        var ids = luaIds.Where(v => v < 1_000_000).Distinct().ToList();
        var cats = taeIds.Select(t => t / 1_000_000).Distinct().OrderBy(c => c).ToList();
        if (cats.Count == 0) return (0, false);
        if (cats.Count == 1) return (cats[0], true);
        var set = taeIds as ISet<int> ?? taeIds.ToHashSet();
        var scored = cats.Select(c => (Cat: c, Hits: ids.Count(v => set.Contains(c * 1_000_000 + v))))
            .OrderByDescending(x => x.Hits).ThenBy(x => x.Cat).ToList();
        return (scored[0].Cat, scored[0].Hits > scored[1].Hits);
    }

    /// <summary>
    /// One edit (a single undo step) that swaps the Lua anim ids <paramref name="a"/> ↔ <paramref name="b"/> (ids as
    /// written in the script). Null when the script uses neither.
    /// </summary>
    public static TextEdit? SwapLuaAnimIds(string src, int a, int b, out int count)
    {
        var hits = FindLuaAnimLiterals(src).Where(l => l.Value == a || l.Value == b).ToList();
        count = hits.Count;
        if (hits.Count == 0) return null;
        int first = hits[0].Line, last = hits[^1].Line;
        int start = TextEdits.Offset(src, first, 0);
        int endLineStart = TextEdits.Offset(src, last, 0);
        int end = src.IndexOf('\n', endLineStart);
        if (end < 0) end = src.Length;
        var sb = new StringBuilder();
        int pos = start;
        foreach (var h in hits)
        {
            int at = TextEdits.Offset(src, h.Line, h.Column);
            sb.Append(src, pos, at - pos);
            sb.Append((h.Value == a ? b : a).ToString(CultureInfo.InvariantCulture));
            pos = at + h.Length;
        }
        sb.Append(src, pos, end - pos);
        int lastLen = end - endLineStart;
        return new TextEdit(first, 0, last, lastLen, sb.ToString().Replace("\r", ""));
    }
}
