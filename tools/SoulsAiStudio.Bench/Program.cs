// Souls AI Studio perf baseline bench.
// READ-ONLY against the Sekiro install, the mod folder and test-data/original. Writes only to a temp dir
// (deleted at the end) and, with --out, to the given markdown file.
//
// Usage: dotnet run -c Release --project tools/SoulsAiStudio.Bench -- [--quick] [--out docs/perf/bench-results.md]
// Env overrides: SAS_REPO, SAS_SEKIRO, SAS_MOD, SAS_PARAMDEX (folder with SDT/Defs/*.xml)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SoulsAiStudio.Bench;
using SoulsFormats;
using static SoulsAiStudio.Bench.Harness;

var argsList = args.ToList();
if (argsList.Contains("--quick")) Harness.Scale = 0.34;
string? outPath = null;
int oi = argsList.IndexOf("--out");
if (oi >= 0 && oi + 1 < argsList.Count) outPath = argsList[oi + 1];

string repo = Environment.GetEnvironmentVariable("SAS_REPO") ?? FindRepoRoot();
string sekiro = Environment.GetEnvironmentVariable("SAS_SEKIRO") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Sekiro";
string mod = Environment.GetEnvironmentVariable("SAS_MOD") ?? Path.Combine(sekiro, @"mods\Severance-dev");
string paramdex = Environment.GetEnvironmentVariable("SAS_PARAMDEX") ?? @"C:\Users\erizo\Downloads\modding\DSMapStudio-1.11\Assets\Paramdex";
string scriptDir = Path.Combine(repo, @"test-data\original\Severance-dev\script");
string tempDir = Path.Combine(Path.GetTempPath(), $"sas-bench-{Environment.ProcessId}");
Directory.CreateDirectory(tempDir);

var machine = MachineInfo();
Console.WriteLine(machine);

// Some luabnd (e.g. m25_00_00_00) are DCX_KRAK -> need Oodle. Pre-load the game's own oo2core (read-only use);
// SoulsFormats' LoadLibrary("oo2core_6_win64") then resolves to the already-loaded module.
foreach (var dir in new[] { AppContext.BaseDirectory, sekiro })
{
    var oo = Directory.Exists(dir) ? Directory.GetFiles(dir, "oo2core_*_win64.dll").FirstOrDefault() : null;
    if (oo != null) { System.Runtime.InteropServices.NativeLibrary.Load(oo); Console.WriteLine($"Oodle: {oo}"); break; }
}

try
{
    var luabndPaths = new[] { "m10_00_00_00", "m11_02_00_00", "m25_00_00_00", "aicommon" }
        .Select(n => Path.Combine(scriptDir, n + ".luabnd.dcx")).ToArray();

    // ---------------------------------------------------------------- A. luabnd read
    const string A = "A. luabnd read (test-data/original, read-only)";
    Console.WriteLine($"\n== {A}");
    foreach (var p in luabndPaths)
    {
        string n = Path.GetFileName(p);
        var raw = File.ReadAllBytes(p);
        var bnd0 = BND4.Read(p);
        var luaCount = bnd0.Files.Count(f => f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
        long unpacked = bnd0.Files.Sum(f => (long)f.Bytes.Length);
        Fact(A, $"{n}", $"{raw.Length / 1024.0:F0} KB dcx -> {unpacked / 1024.0:F0} KB unpacked, {bnd0.Files.Count} entries ({luaCount} .lua), compression {bnd0.Compression.Type}");
        Run(A, $"{n}: File.ReadAllBytes (OS cache warm)", 3, 30, () => File.ReadAllBytes(p).Length);
        Run(A, $"{n}: DCX.Decompress (in-memory)", 3, 30, () => DCX.Decompress(raw).Length);
        Run(A, $"{n}: BND4.Read(path)", 3, 30, () => BND4.Read(p).Files.Count);
        Run(A, $"{n}: BND4.Read + decode all .lua -> string", 3, 30, () =>
        {
            var b = BND4.Read(p);
            int chars = 0;
            foreach (var f in b.Files)
                if (f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    if (!IsBytecode(f.Bytes)) chars += Encoding.UTF8.GetString(f.Bytes).Length;
            return chars;
        });
    }
    Run(A, "ALL 4 luabnd: read + decode (sequential)", 2, 20, () => luabndPaths.Sum(DecodeAllLua));
    Run(A, "ALL 4 luabnd: read + decode (Parallel.ForEach)", 2, 20, () =>
    {
        int total = 0;
        Parallel.ForEach(luabndPaths, p => Interlocked.Add(ref total, DecodeAllLua(p)));
        return total;
    });
    var modLuabnds = Directory.Exists(Path.Combine(mod, "script"))
        ? Directory.GetFiles(Path.Combine(mod, "script"), "*.luabnd.dcx") : Array.Empty<string>();
    if (modLuabnds.Length > 0)
    {
        Fact(A, "mod script/*.luabnd.dcx count", modLuabnds.Length);
        Run(A, $"ALL {modLuabnds.Length} mod luabnd: read + decode (Parallel)", 1, 10, () =>
        {
            int total = 0;
            Parallel.ForEach(modLuabnds, p => Interlocked.Add(ref total, DecodeAllLua(p)));
            return total;
        }, "whole-mod scan proxy");
    }
    Fact(A, "memory after A", PeakWorkingSetMb());

    // ---------------------------------------------------------------- B. luabnd save
    const string B = "B. luabnd save (writes to %TEMP% only)";
    Console.WriteLine($"\n== {B}");
    foreach (var (mapName, luaName) in new[] { ("m10_00_00_00", "506300_battle.lua"), ("m11_02_00_00", "540300_battle.lua"), ("m25_00_00_00", "500000_battle.lua") })
    {
        string src = Path.Combine(scriptDir, mapName + ".luabnd.dcx");
        var bnd = BND4.Read(src);
        var entry = bnd.Files.First(f => f.Name.EndsWith(luaName, StringComparison.OrdinalIgnoreCase));
        string original = Encoding.UTF8.GetString(entry.Bytes);
        string target = Path.Combine(tempDir, mapName + ".luabnd.dcx");
        File.Copy(src, target, overwrite: true);
        // test-data/original files are read-only; File.Copy keeps the attribute and File.Move(overwrite) then fails.
        // (App note: Save must handle/clear ReadOnly on the destination or report it clearly.)
        File.SetAttributes(target, FileAttributes.Normal);
        string backupDir = Path.Combine(tempDir, "backups");
        string projLua = Path.Combine(tempDir, "lua", luaName);
        Directory.CreateDirectory(Path.GetDirectoryName(projLua)!);

        Run(B, $"{mapName}: BND4.Write() to byte[] ({bnd.Compression.Type} compress)", 2, 20, () => bnd.Write().Length);
        int counter = 0;
        Run(B, $"{mapName}: FULL SAVE [{bnd.Compression.Type}] (lua text + backup copy + repack + tmp write + atomic rename)", 2, 20, () =>
        {
            counter++;
            string edited = original + $"\n-- sas-bench edit {counter}\n";
            var luaBytes = Encoding.UTF8.GetBytes(edited);
            // 1. project lua/ plain text (atomic)
            File.WriteAllBytes(projLua + ".tmp", luaBytes);
            File.Move(projLua + ".tmp", projLua, overwrite: true);
            // 2. backup of the current luabnd
            string bdir = Path.Combine(backupDir, counter.ToString("D4"));
            Directory.CreateDirectory(bdir);
            File.Copy(target, Path.Combine(bdir, mapName + ".luabnd.dcx"));
            // 3. repack + atomic replace
            entry.Bytes = luaBytes;
            bnd.Write(target + ".tmp");
            File.Move(target + ".tmp", target, overwrite: true);
            return luaBytes.Length;
        });
        // round-trip check (not timed): unmodified repack must keep entry bytes identical
        entry.Bytes = Encoding.UTF8.GetBytes(original);
        string rt = Path.Combine(tempDir, mapName + ".rt.luabnd.dcx");
        bnd.Write(rt);
        var a = BND4.Read(src); var b = BND4.Read(rt);
        bool same = a.Files.Count == b.Files.Count && a.Files.Zip(b.Files).All(z => z.First.ID == z.Second.ID && z.First.Name == z.Second.Name && z.First.Bytes.AsSpan().SequenceEqual(z.Second.Bytes));
        Fact(B, $"{mapName}: unmodified repack entries identical", same);
        Fact(B, $"{mapName}: repacked size vs original", $"{new FileInfo(rt).Length} vs {new FileInfo(src).Length} bytes");
    }
    Fact(B, "memory after B", PeakWorkingSetMb());

    // ---------------------------------------------------------------- C. gameparam
    const string C = "C. gameparam + paramdefs";
    Console.WriteLine($"\n== {C}");
    string sdtDefs = Path.Combine(paramdex, @"SDT\Defs");
    var wanted = new[] { "NpcParam", "NpcThinkParam", "BehaviorParam", "AtkParam_Npc", "SpEffectParam", "Bullet" };
    Dictionary<string, PARAM> appliedParams = new();
    foreach (var gp in new[] { Path.Combine(sekiro, @"param\gameparam\gameparam.parambnd.dcx"), Path.Combine(mod, @"param\gameparam\gameparam.parambnd.dcx") })
    {
        if (!File.Exists(gp)) { Fact(C, gp, "MISSING"); continue; }
        string tag = gp.StartsWith(mod, StringComparison.OrdinalIgnoreCase) ? "mod" : "vanilla";
        var pb = BND4.Read(gp);
        Fact(C, $"{tag} gameparam", $"{new FileInfo(gp).Length / 1024.0:F0} KB dcx, {pb.Files.Count} params, {pb.Files.Sum(f => (long)f.Bytes.Length) / (1024.0 * 1024):F1} MB unpacked, {pb.Compression.Type}");
        Run(C, $"{tag}: BND4.Read(gameparam.parambnd.dcx)", 2, 15, () => BND4.Read(gp).Files.Count);
        Run(C, $"{tag}: PARAM.Read ALL params (no defs)", 2, 10, () => pb.Files.Where(f => f.Name.EndsWith(".param")).Sum(f => PARAM.Read(f.Bytes).Rows.Count));
    }
    string modGp = Path.Combine(mod, @"param\gameparam\gameparam.parambnd.dcx");
    string gpUse = File.Exists(modGp) ? modGp : Path.Combine(sekiro, @"param\gameparam\gameparam.parambnd.dcx");
    if (Directory.Exists(sdtDefs) && File.Exists(gpUse))
    {
        var defFiles = Directory.GetFiles(sdtDefs, "*.xml");
        Fact(C, "paramdef source", $"{sdtDefs} ({defFiles.Length} xml) - NOT in repo");
        var allDefs = Run(C, $"PARAMDEF.XmlDeserialize ALL {defFiles.Length} SDT defs", 1, 5, () =>
        {
            var list = new List<PARAMDEF>();
            foreach (var f in defFiles)
                try { list.Add(PARAMDEF.XmlDeserialize(f)); } catch { }
            return list;
        });
        var defByType = allDefs.GroupBy(d => d.ParamType).ToDictionary(g => g.Key, g => g.ToList());
        var pb = BND4.Read(gpUse);
        var needed = pb.Files.Where(f => wanted.Contains(Path.GetFileNameWithoutExtension(f.Name))).ToList();
        Fact(C, "params used", string.Join(", ", needed.Select(f => Path.GetFileName(f.Name))));
        foreach (var f in needed)
        {
            string pname = Path.GetFileNameWithoutExtension(f.Name);
            var p0 = PARAM.Read(f.Bytes);
            defByType.TryGetValue(p0.ParamType, out var cands);
            bool carefully = cands != null && p0.ApplyParamdefCarefully(cands);
            int fields = p0.AppliedParamdef?.Fields.Count ?? 0;
            Fact(C, $"{pname}", $"{p0.ParamType}, {p0.Rows.Count} rows, {f.Bytes.Length / 1024.0:F0} KB, def fields {fields}, ApplyParamdefCarefully={carefully}");
            if (cands == null) continue;
            var def = p0.AppliedParamdef ?? cands[0];
            var pr = Run(C, $"{pname}: PARAM.Read + ApplyParamdef ({p0.Rows.Count} rows)", 2, 10, () =>
            {
                var p = PARAM.Read(f.Bytes);
                if (!p.ApplyParamdefCarefully(cands)) p.ApplyParamdef(def);
                return p;
            });
            appliedParams[pname] = pr;
        }
        Run(C, "6 AI params: Read + ApplyParamdef + ID index (Parallel)", 1, 10, () =>
        {
            var dict = new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<int, PARAM.Row>>();
            Parallel.ForEach(needed, f =>
            {
                var p = PARAM.Read(f.Bytes);
                if (defByType.TryGetValue(p.ParamType, out var cs) && !p.ApplyParamdefCarefully(cs)) p.ApplyParamdef(cs[0]);
                var idx = new Dictionary<int, PARAM.Row>(p.Rows.Count);
                foreach (var r in p.Rows) idx[r.ID] = r;
                dict[f.Name] = idx;
            });
            return dict.Count;
        }, "what 'open project' needs");

        // Alternative: no ApplyParamdef (which boxes every cell of every row). Compute byte offsets of a few
        // needed scalar fields from the paramdef once, then read them straight from row bytes.
        var light = new LightParamReader();
        var lightPlans = needed.Select(f =>
        {
            var p = PARAM.Read(f.Bytes);
            if (!p.ApplyParamdefCarefully(defByType[p.ParamType])) p.ApplyParamdef(defByType[p.ParamType][0]);
            return (File: f, Plan: light.Plan(p.AppliedParamdef, 12));
        }).ToList();
        bool okAll = true;
        foreach (var (f, plan) in lightPlans)
        {
            // validate against ApplyParamdef values
            var p = PARAM.Read(f.Bytes);
            if (!p.ApplyParamdefCarefully(defByType[p.ParamType])) p.ApplyParamdef(defByType[p.ParamType][0]);
            var lp = light.Read(f.Bytes, p, plan);
            int mism = 0;
            for (int r = 0; r < p.Rows.Count; r++)
                for (int k = 0; k < plan.Count; k++)
                {
                    var cellVal = Convert.ToDouble(p.Rows[r].Cells[plan[k].FieldIndex].Value);
                    if (Math.Abs(cellVal - lp.Values[r * plan.Count + k]) > 1e-6) mism++;
                }
            okAll &= mism == 0;
            Fact(C, $"light reader {Path.GetFileNameWithoutExtension(f.Name)}", $"{plan.Count} fields x {p.Rows.Count} rows, mismatches vs ApplyParamdef: {mism}");
        }
        Run(C, "6 AI params: LIGHT read (PARAM.Read + 12 needed fields/row via offsets, no ApplyParamdef)", 2, 15, () =>
        {
            int n = 0;
            foreach (var (f, plan) in lightPlans) { var p = PARAM.Read(f.Bytes); n += light.Read(f.Bytes, p, plan).Values.Length; }
            return n;
        }, okAll ? "values verified == ApplyParamdef" : "VALUE MISMATCH - see facts");
    }
    else Fact(C, "paramdefs", $"NOT FOUND at {sdtDefs} - set SAS_PARAMDEX");
    Fact(C, "memory after C", PeakWorkingSetMb());

    // ---------------------------------------------------------------- D. chr anibnd / chrbnd
    const string D = "D. chr anibnd (TAE+HKX) / chrbnd (FLVER)";
    Console.WriteLine($"\n== {D}");
    object? keepAlive = null;
    foreach (var (chr, who) in new[] { ("c5060", "Owl (506300)"), ("c5400", "Isshin (540300)"), ("c5000", "True Monk (500000)") })
    {
        string? ani = FirstExisting(Path.Combine(mod, "chr", chr + ".anibnd.dcx"), Path.Combine(sekiro, "chr", chr + ".anibnd.dcx"));
        string? chrb = FirstExisting(Path.Combine(mod, "chr", chr + ".chrbnd.dcx"), Path.Combine(sekiro, "chr", chr + ".chrbnd.dcx"));
        if (ani == null) { Fact(D, chr, "anibnd missing"); continue; }
        var anibnd = BND4.Read(ani);
        var taes = anibnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)).ToList();
        var hkxs = anibnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).ToList();
        Fact(D, $"{chr} {who} anibnd", $"{(ani.StartsWith(mod, StringComparison.OrdinalIgnoreCase) ? "mod" : "game")} {new FileInfo(ani).Length / (1024.0 * 1024):F1} MB dcx -> {anibnd.Files.Sum(f => (long)f.Bytes.Length) / (1024.0 * 1024):F1} MB unpacked; {anibnd.Files.Count} entries, {hkxs.Count} hkx ({hkxs.Sum(f => (long)f.Bytes.Length) / (1024.0 * 1024):F1} MB), {taes.Count} tae ({taes.Sum(f => (long)f.Bytes.Length) / 1024.0:F0} KB)");
        int iters = new FileInfo(ani).Length > 10_000_000 ? 5 : 8;
        Run(D, $"{chr}: BND4.Read(anibnd)", 1, iters, () => BND4.Read(ani).Files.Count);
        Run(D, $"{chr}: BND4Reader (lazy) + read only .tae entries + TAE.Read", 1, iters, () =>
        {
            using var rd = new BND4Reader(ani);
            int c = 0;
            foreach (var h in rd.Files.Where(h => h.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
                c += TAE.Read(rd.ReadFile(h)).Animations.Count;
            return c;
        }, "AI only needs TAE (+ HKX on demand)");
        if (taes.Count > 0)
        {
            int anims = 0, events = 0, failed = 0;
            foreach (var t in taes)
            {
                try { var tae = TAE.Read(t.Bytes); anims += tae.Animations.Count; events += tae.Animations.Sum(a => a.Events.Count); }
                catch { failed++; }
            }
            Fact(D, $"{chr} TAE", $"{anims} animations, {events} events, {failed} tae failed to parse (SoulsFormats.TAE, no template)");
            Run(D, $"{chr}: TAE.Read all {taes.Count} tae (raw events, no template)", 2, 15, () =>
            {
                int c = 0;
                foreach (var t in taes) { try { c += TAE.Read(t.Bytes).Animations.Count; } catch { } }
                return c;
            });
        }
        if (chrb != null)
        {
            var cb = BND4.Read(chrb);
            var flv = cb.Files.FirstOrDefault(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase));
            Fact(D, $"{chr} chrbnd", $"{(chrb.StartsWith(mod, StringComparison.OrdinalIgnoreCase) ? "mod" : "game")} {new FileInfo(chrb).Length / (1024.0 * 1024):F1} MB dcx, {cb.Files.Count} entries, flver {(flv?.Bytes.Length ?? 0) / (1024.0 * 1024):F1} MB");
            Run(D, $"{chr}: BND4.Read(chrbnd)", 1, 8, () => BND4.Read(chrb).Files.Count);
            if (flv != null)
            {
                var fl = FLVER2.Read(flv.Bytes);
                Fact(D, $"{chr} FLVER2", $"{fl.Dummies.Count} dummy polys, {fl.Meshes.Count} meshes, {fl.Nodes.Count} nodes");
                Run(D, $"{chr}: FLVER2.Read (full mesh)", 2, 10, () => FLVER2.Read(flv.Bytes).Dummies.Count);
            }
        }
        if (chr == "c5400") keepAlive = anibnd;
    }
    Fact(D, "memory after D", PeakWorkingSetMb());

    // ---------------------------------------------------------------- E. Lua text + lexing
    const string E = "E. Lua corpus stats + naive lexer";
    Console.WriteLine($"\n== {E}");
    var corpus = new List<(string Name, string Text, int Bytes)>();
    int bytecodeCount = 0; // HavokScript bytecode (ESC "Lua" header) = vanilla scripts not yet decompiled
    foreach (var dir in Directory.GetDirectories(scriptDir, "*-luabnd-dcx"))
        foreach (var f in Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            if (IsBytecode(bytes)) { bytecodeCount++; continue; }
            corpus.Add(($"{Path.GetFileName(dir).Replace("-luabnd-dcx", "")}/{Path.GetFileName(f)}", Encoding.UTF8.GetString(bytes), bytes.Length));
        }
    // aicommon is not unpacked in test-data: pull it from the bnd in memory
    foreach (var f in BND4.Read(Path.Combine(scriptDir, "aicommon.luabnd.dcx")).Files.Where(f => f.Name.EndsWith(".lua")))
        if (IsBytecode(f.Bytes)) bytecodeCount++; else corpus.Add(($"aicommon/{Path.GetFileName(f.Name)}", Encoding.UTF8.GetString(f.Bytes), f.Bytes.Length));

    // Encoding: many vanilla-derived scripts carry Shift-JIS (cp932) Japanese comments. Decoding them as UTF-8
    // is LOSSY (U+FFFD) and would corrupt the file on save. Measure strict-UTF-8-then-cp932 detection.
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    var sjis = Encoding.GetEncoding(932);
    var rawBytes = new List<byte[]>();
    foreach (var dir in Directory.GetDirectories(scriptDir, "*-luabnd-dcx"))
        foreach (var f in Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories)) { var rb = File.ReadAllBytes(f); if (!IsBytecode(rb)) rawBytes.Add(rb); }
    int sjisFiles = 0, replacementFiles = 0;
    foreach (var b in rawBytes)
    {
        try { strictUtf8.GetString(b); }
        catch (DecoderFallbackException)
        {
            sjisFiles++;
            var back = sjis.GetBytes(sjis.GetString(b));
            if (!back.AsSpan().SequenceEqual(b)) replacementFiles++;
        }
    }
    Fact(E, "bytecode .lua skipped (unpacked dirs + aicommon)", bytecodeCount);
    Fact(E, "encoding", $"{sjisFiles}/{rawBytes.Count} plain-text .lua are NOT valid UTF-8; cp932 round-trip not byte-identical in {replacementFiles} of them");
    Run(E, $"decode {rawBytes.Count} unpacked .lua with detection (strict UTF-8 -> cp932 fallback)", 2, 15, () =>
    {
        int chars = 0;
        foreach (var b in rawBytes)
        {
            try { chars += strictUtf8.GetString(b).Length; }
            catch (DecoderFallbackException) { chars += sjis.GetString(b).Length; }
        }
        return chars;
    });

    long totalBytes = corpus.Sum(c => (long)c.Bytes);
    long totalLines = corpus.Sum(c => (long)CountLines(c.Text));
    long nonAscii = corpus.Sum(c => (long)c.Text.Count(ch => ch > 127));
    Fact(E, "corpus", $"{corpus.Count} files, {totalLines:N0} lines, {totalBytes / 1024.0:F0} KB, non-ASCII chars {nonAscii}");
    var battle = corpus.Where(c => c.Name.EndsWith("_battle.lua")).ToList();
    Fact(E, "battle files", $"{battle.Count} files, {battle.Sum(c => CountLines(c.Text)):N0} lines, {battle.Sum(c => c.Bytes) / 1024.0:F0} KB");
    foreach (var big in corpus.OrderByDescending(c => c.Bytes).Take(5))
        Fact(E, $"largest: {big.Name}", $"{CountLines(big.Text):N0} lines, {big.Bytes / 1024.0:F0} KB");

    var samples = new[] { "m10_00_00_00/506300_battle.lua", "m11_02_00_00/540300_battle.lua", "m25_00_00_00/500000_battle.lua" };
    var toks = new List<Token>(64 * 1024);
    foreach (var sname in samples)
    {
        var s = corpus.FirstOrDefault(c => c.Name == sname);
        if (s.Text == null) { Fact(E, sname, "MISSING"); continue; }
        toks.Clear(); LuaLexer.Lex(s.Text, toks);
        var outline = Outline.Build(s.Text, toks);
        int lines = CountLines(s.Text);
        Fact(E, $"{sname}", $"{lines:N0} lines, {s.Bytes / 1024.0:F0} KB, {toks.Count:N0} tokens, {toks.Count(t => t.Kind == Tk.Error)} lex errors, {outline.Count} named functions, {outline.Sum(o => o.AddSubGoalCount)} AddSubGoal");
        Run(E, $"{sname}: lex whole file", 5, 50, () => { toks.Clear(); LuaLexer.Lex(s.Text, toks); return toks.Count; });
        Run(E, $"{sname}: lex + outline (navigator proxy)", 5, 50, () => { var t = new List<Token>(); LuaLexer.Lex(s.Text, t); return Outline.Build(s.Text, t).Count; });
        Run(E, $"{sname}: split lines + lex per line w/ state (editor cache rebuild)", 5, 50, () => LexPerLine(s.Text).Count);
        var perLine = LexPerLine(s.Text);
        Run(E, $"{sname}: re-lex ONE edited line (incremental)", 50, 2000, () =>
        {
            var st = LexState.Initial; var tl = perLine[lines / 2];
            tl.Tokens.Clear(); LuaLexer.LexRange(tl.Text.AsSpan(), 0, lines / 2, ref st, tl.Tokens); return tl.Tokens.Count;
        });
        // ImGui editor frame simulation (managed side only): per visible token, ImGui.NET marshals string->UTF8
        Run(E, $"{sname}: SIM frame NAIVE (all {lines} lines, re-lex + UTF8 marshal per token)", 5, 50, () => SimFrame(s.Text, null, 0, lines));
        Run(E, $"{sname}: SIM frame CACHED tokens, ALL lines marshal", 5, 50, () => SimFrame(s.Text, perLine, 0, lines));
        Run(E, $"{sname}: SIM frame CACHED tokens, VISIBLE 60 lines only", 50, 2000, () => SimFrame(s.Text, perLine, lines / 2, 60));
    }
    var allText = corpus.Select(c => c.Text).ToArray();
    Run(E, $"WHOLE corpus ({corpus.Count} files): lex sequential", 2, 15, () => { int n = 0; var t = new List<Token>(); foreach (var s in allText) { t.Clear(); LuaLexer.Lex(s, t); n += t.Count; } return n; });
    Run(E, $"WHOLE corpus: lex + outline Parallel", 2, 15, () => { int n = 0; Parallel.ForEach(allText, s => { var t = new List<Token>(); LuaLexer.Lex(s, t); Interlocked.Add(ref n, Outline.Build(s, t).Count); }); return n; });
    Fact(E, "memory after E", PeakWorkingSetMb());

    // ---------------------------------------------------------------- F. retained memory of a "project"
    const string F = "F. retained memory (simulated open project)";
    Console.WriteLine($"\n== {F}");
    keepAlive = null; appliedParams.Clear();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long baseHeap = GC.GetTotalMemory(true);
    var project = new List<object>();
    foreach (var p in luabndPaths.Take(3)) { var b = BND4.Read(p); project.Add(b.Files.Where(f => f.Name.EndsWith(".lua") && !IsBytecode(f.Bytes)).Select(f => Encoding.UTF8.GetString(f.Bytes)).ToList()); }
    long afterLua = GC.GetTotalMemory(true);
    foreach (var s in battle) { project.Add(LexPerLine(s.Text)); }
    long afterTok = GC.GetTotalMemory(true);
    if (File.Exists(gpUse) && Directory.Exists(sdtDefs))
    {
        var pb = BND4.Read(gpUse);
        var defs = Directory.GetFiles(sdtDefs, "*.xml").Select(f => { try { return PARAMDEF.XmlDeserialize(f); } catch { return null; } }).Where(d => d != null).GroupBy(d => d!.ParamType).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var f in pb.Files.Where(f => wanted.Contains(Path.GetFileNameWithoutExtension(f.Name))))
        {
            var p = PARAM.Read(f.Bytes);
            if (defs.TryGetValue(p.ParamType, out var cs) && !p.ApplyParamdefCarefully(cs!)) p.ApplyParamdef(cs![0]!);
            project.Add(p);
        }
    }
    long afterParams = GC.GetTotalMemory(true);
    string? isshinAni = FirstExisting(Path.Combine(mod, @"chr\c5400.anibnd.dcx"), Path.Combine(sekiro, @"chr\c5400.anibnd.dcx"));
    if (isshinAni != null)
    {
        var ab = BND4.Read(isshinAni);
        project.Add(ab);
        foreach (var t in ab.Files.Where(f => f.Name.EndsWith(".tae"))) { try { project.Add(TAE.Read(t.Bytes)); } catch { } }
    }
    long afterAni = GC.GetTotalMemory(true);
    Fact(F, "3 map luabnd as strings", $"{(afterLua - baseHeap) / (1024.0 * 1024):F1} MB");
    Fact(F, "per-line token cache for all battle files", $"{(afterTok - afterLua) / (1024.0 * 1024):F1} MB");
    Fact(F, "6 AI params with defs applied (cells boxed)", $"{(afterParams - afterTok) / (1024.0 * 1024):F1} MB");
    Fact(F, "Isshin anibnd raw bytes + parsed TAE", $"{(afterAni - afterParams) / (1024.0 * 1024):F1} MB");
    Fact(F, "total retained", $"{(afterAni - baseHeap) / (1024.0 * 1024):F1} MB");
    GC.KeepAlive(project);
    Fact(F, "process at end", PeakWorkingSetMb());

    // ---------------------------------------------------------------- G. DSAS hot-spot quantification
    const string G = "G. DSAS hot-spot quantification";
    Console.WriteLine($"\n== {G}");
    Run(G, "Directory.GetFiles(Sekiro root, *, AllDirectories) [= DSAS GetFilesInDir(null)]", 1, 3, () => Directory.GetFiles(sekiro, "*", SearchOption.AllDirectories).Length);
    Fact(G, "files under Sekiro root (incl. mods)", Directory.GetFiles(sekiro, "*", SearchOption.AllDirectories).Length);
    Run(G, "Directory.GetFiles(Sekiro/chr, *.anibnd.dcx)", 1, 10, () => Directory.GetFiles(Path.Combine(sekiro, "chr"), "*.anibnd.dcx").Length);

    // ---------------------------------------------------------------- H. decompile (import-time, one-off)
    const string H = "H. DSLuaDecompiler (external process, import-time only)";
    string dec = Environment.GetEnvironmentVariable("SAS_DECOMPILER") ?? @"C:\Users\erizo\Downloads\modding\DSLuaDecompiler\DSLuaDecompiler.exe";
    if (File.Exists(dec))
    {
        Console.WriteLine($"\n== {H}");
        string decDir = Path.Combine(tempDir, "decomp");
        Directory.CreateDirectory(decDir);
        var bcFiles = BND4.Read(Path.Combine(sekiro, @"script\m10_00_00_00.luabnd.dcx")).Files
            .Where(f => f.Name.EndsWith("_battle.lua") && IsBytecode(f.Bytes)).Take(10).ToList();
        var inPaths = bcFiles.Select(f => { var pth = Path.Combine(decDir, Path.GetFileName(f.Name)); File.WriteAllBytes(pth, f.Bytes); return pth; }).ToList();
        int RunDec(IEnumerable<string> files)
        {
            var psi = new ProcessStartInfo(dec) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var f in files) psi.ArgumentList.Add(f);
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(Path.Combine(decDir, "out"));
            using var pr = Process.Start(psi)!;
            pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd(); pr.WaitForExit();
            return pr.ExitCode;
        }
        Fact(H, "vanilla m10 battle bytecode files used", $"{inPaths.Count}, {bcFiles.Sum(f => f.Bytes.Length) / 1024} KB");
        if (inPaths.Count > 0)
        {
            Run(H, "decompile 1 file (process start + decompile)", 1, 3, () => RunDec(inPaths.Take(1)));
            Run(H, $"decompile {inPaths.Count} files in ONE process", 1, 3, () => RunDec(inPaths));
        }
    }
}
finally
{
    try { Directory.Delete(tempDir, recursive: true); } catch { }
}

if (outPath != null)
{
    string full = Path.IsPathRooted(outPath) ? outPath : Path.Combine(repo, outPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, Harness.ToMarkdown(machine));
    Console.WriteLine($"\nWrote {full}");
}
return 0;

// ===================================================================== helpers
static int DecodeAllLua(string path)
{
    var b = BND4.Read(path);
    int chars = 0;
    foreach (var f in b.Files)
        if (f.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            if (!IsBytecode(f.Bytes)) chars += Encoding.UTF8.GetString(f.Bytes).Length;
    return chars;
}

static bool IsBytecode(byte[] b) => b.Length > 4 && b[0] == 0x1B && b[1] == (byte)'L' && b[2] == (byte)'u' && b[3] == (byte)'a';

static int CountLines(string s)
{
    int n = 1;
    foreach (var c in s.AsSpan()) if (c == '\n') n++;
    return n;
}

static List<LineTokens> LexPerLine(string text)
{
    var result = new List<LineTokens>(4096);
    var st = LexState.Initial;
    int line = 1, pos = 0;
    var span = text.AsSpan();
    while (pos <= text.Length)
    {
        int nl = span.Slice(pos).IndexOf('\n');
        int end = nl < 0 ? text.Length : pos + nl;
        string lineText = text.Substring(pos, end - pos);
        var lt = new LineTokens(lineText, new List<Token>(8));
        LuaLexer.LexRange(lineText.AsSpan(), 0, line, ref st, lt.Tokens);
        result.Add(lt);
        line++;
        if (nl < 0) break;
        pos = end + 1;
    }
    return result;
}

// Managed-side cost of drawing a code editor frame: for each token on each drawn line, ImGui.NET's
// Text/AddText(string) encodes UTF-16 -> UTF-8 into a buffer before P/Invoke. Native glyph/vertex cost NOT included.
static float SimFrame(string text, List<LineTokens>? cached, int firstLine, int lineCount)
{
    Span<byte> buf = stackalloc byte[1024];
    float x = 0, sum = 0;
    if (cached == null)
    {
        var lines = LexPerLine(text); // naive: re-tokenize every frame
        cached = lines;
    }
    int last = Math.Min(cached.Count, firstLine + lineCount);
    for (int l = firstLine; l < last; l++)
    {
        var lt = cached[l];
        x = 0;
        foreach (var t in lt.Tokens)
        {
            var s = lt.Text.AsSpan(t.Start, Math.Min(t.Length, 300));
            int nb = Encoding.UTF8.GetBytes(s, buf);
            x += nb * 7.0f;
            sum += x + (int)t.Kind;
        }
    }
    return sum;
}

static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

static string MachineInfo()
{
    var sb = new StringBuilder();
#if DEBUG
    const string cfg = "DEBUG (numbers not representative!)";
#else
    const string cfg = "Release";
#endif
    sb.Append($"OS {RuntimeInformation.OSDescription}; ");
    sb.Append($"{RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs; ");
    sb.Append($"GC {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}; ");
    sb.Append($"{cfg}; date {DateTime.Now:yyyy-MM-dd HH:mm}");
    return sb.ToString();
}

public sealed record LineTokens(string Text, List<Token> Tokens);
