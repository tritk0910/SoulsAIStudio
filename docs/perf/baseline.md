# Performance baseline and budget: Souls AI Studio

Status: baseline measured on 2026-11-11. Raw numbers are in [`bench-results.md`](bench-results.md), which the bench regenerates. All file access was **read-only** against the Sekiro install, the mod folder and `test-data/original/`. Writes only went to `%TEMP%\sas-bench-<pid>`, which is deleted at the end.

```
dotnet build -c Release tools/SoulsAiStudio.Bench
dotnet run  -c Release --no-build --project tools/SoulsAiStudio.Bench -- --out docs/perf/bench-results.md   # ~4 min
#   --quick                 ~1/3 of the iterations
#   env SAS_SEKIRO / SAS_MOD / SAS_PARAMDEX / SAS_DECOMPILER / SAS_REPO override the default paths
```

## 0. Machine

| | |
|---|---|
| CPU | Intel Core i5-13500H (12C/16T, 2.6 GHz base, hybrid P/E cores), laptop |
| RAM | 15.7 GB |
| Disk | Repo and Sekiro on C: = GIGABYTE AG4512G NVMe SSD (D: = Samsung 980 NVMe) |
| OS / runtime | Windows 11 (10.0.26300), bench targets net9.0 but runs on **.NET 10.0.12** (`RollForward=LatestMajor`), workstation GC, Release, x64 |
| Cache state | The OS file cache was **warm** for every row except the "first" column. The "first" column also includes JIT. A cold NVMe read of a 17 MB anibnd adds roughly 10–40 ms. |

The numbers are noisy (laptop, hybrid cores, power management). Use the **median** for budgets and the p95 for tail. A shared CI machine will be 1.5–3× slower.

## 1. Measured numbers (medians, Release)

### 1.1 Lua containers (luabnd)
| File | dcx → unpacked | Compression | BND4.Read | Read + decode all text .lua |
|---|---|---|---:|---:|
| m10_00_00_00 (Owl) | 152 KB → 945 KB, 56 lua | DCX_DFLT | 2.0 ms | 2.0 ms |
| m11_02_00_00 (Isshin) | 224 KB → 1632 KB, 56 lua | DCX_DFLT | 3.6 ms | 3.9 ms |
| m25_00_00_00 (True Monk) | 109 KB → 834 KB, 67 lua | **DCX_KRAK (Oodle)** | 1.4 ms | 1.8 ms |
| aicommon | 114 KB → 549 KB, 105 lua | DCX_DFLT | 1.8 ms | 1.7 ms |
| All 4, sequential / `Parallel.ForEach` | | | | 9.1 / **4.7 ms** |
| All 7 mod `script/*.luabnd.dcx` (parallel) | | | | 7.5 ms |

- **Most `.lua` entries are HavokScript bytecode** (`ESC "Lua"` header): 272 entries across the three unpacked maps plus aicommon. Only **11 battle files are plain text** in the samples (the modded ones), and they are **valid UTF-8 / ASCII**. The bytecode needs DSLuaDecompiler at import.
- Text corpus: 12 files, 28,661 lines, 1.36 MB. The largest file is `540310_battle.lua` at **3,024 lines / 146 KB**. The Owl file `506300_battle.lua` has 2,732 lines, 24.7k tokens, 87 named functions and 396 `AddSubGoal`.

### 1.2 Save: repack luabnd (to %TEMP%)
| Map | `BND4.Write()` to byte[] | **Full save** = lua text (tmp+rename) + backup copy of old luabnd + repack + tmp write + atomic rename |
|---|---:|---:|
| m10 (DFLT) | 14.4 ms | **18.0 ms** (p95 22) |
| m11_02 (DFLT) | 21.9 ms | **32.8 ms** (p95 49) |
| m25 (**KRAK**, Oodle via the game's `oo2core_6_win64.dll`) | 373 ms | **380 ms** (p95 422) |

- An unmodified repack gives byte-identical entries for all three maps (ID, name and bytes). The m25 KRAK output is the same size as the original. The DFLT outputs differ by under 1 KB, which is a zlib-level difference.
- The `test-data/original` files carry the **ReadOnly** attribute. `File.Copy` keeps that attribute, so a later `File.Move(overwrite)` fails with `UnauthorizedAccessException`. **Save must handle ReadOnly destinations.**

### 1.3 GameParam and paramdefs
| Operation | Median | Alloc/op |
|---|---:|---:|
| Mod `gameparam.parambnd.dcx` (880 KB **KRAK** → 15 MB, 138 params): BND4.Read | 17 ms | 46 MB |
| PARAM.Read of all 138 params, no defs (mod) | 58 ms | 109 MB |
| `PARAMDEF.XmlDeserialize` of all 159 SDT defs (Paramdex from DSMapStudio 1.11, **not in the repo**) | 259 ms (first run 0.2–3 s) | 78 MB |
| SpEffectParam (3,925 rows × 408 fields): Read + ApplyParamdef | **684 ms** | 162 MB |
| AtkParam_Npc (2,808 × 221) / NpcParam (1,289 × 352) / Bullet / BehaviorParam / NpcThinkParam | 175 / 98 / 66 / 22 / 17 ms | |
| **6 AI params: Read + ApplyParamdef + ID index (parallel)** | **1,297 ms** | **328 MB**, ~**197 MB retained** |
| **6 AI params: LIGHT read** (PARAM.Read, then 12 needed fields per row via paramdef offsets, no ApplyParamdef) | **29 ms** | 53 MB, values verified equal to ApplyParamdef |

All six defs pass `ApplyParamdefCarefully` against the mod gameparam (type, data version and row size match).

### 1.4 Character files
| chr | anibnd (mod) dcx → unpacked | BND4.Read anibnd | TAE.Read (raw events, no template) | chrbnd BND4.Read | FLVER2.Read (full mesh) |
|---|---|---:|---:|---:|---:|
| c5060 Owl | 4.7 → 11.0 MB; 164 hkx, 1 tae (202 anims, 6,576 events) | 44 ms | 2.0 ms | 15 ms | 312 ms (99 dummies) |
| c5400 Isshin | **17.1 → 54.6 MB**; 328 hkx, 1 tae (420 anims, 16,667 events) | **209 ms** (alloc 256 MB) | 10 ms | 40 ms | 166 ms (143 dummies) |
| c5000 True Monk | 7.0 → 12.5 MB; 208 hkx, 1 tae (268 anims, 9,182 events) | 67 ms | 4.5 ms | 24 ms | 194 ms (99 dummies) |

- Reading only the TAE entry with the lazy `BND4Reader` costs about the same (41 / 273 / 50 ms). **DCX decompression of the whole anibnd dominates**, and the BND has to be fully inflated to reach the TAE.
- The `c5063` / `c5403` anibnd files are 416-byte stubs. The real models are c5060 (Owl), c5400 (Isshin) and c5000 (True Monk). The mod has no `c5060.chrbnd`, so it falls back to the game copy.
- SoulsFormats' `TAE.Template` reads the `event_template` XML schema. DSAS's `Res/TAE.Template.SDT.xml` is an `action_template` for SoulsAssetPipeline's TAE, so the two templates are not interchangeable. The cost of applying a template was **not** measured. Expect roughly 2–5× the raw TAE parse.

### 1.5 Lua lexing (naive hand-written lexer in the bench, a proxy for the real parser)
| Operation (≈2.7–2.8k-line battle file) | Median |
|---|---:|
| Lex the whole file (≈25k tokens) | 0.3–0.6 ms |
| Lex + navigator outline (functions, `AddSubGoal` count, first anim) | 0.5–0.7 ms |
| Split lines + lex each line with carry state (rebuild the editor colour cache) | 0.8–2.0 ms |
| Re-lex **one** edited line | **< 1 µs** |
| Whole text corpus (12 files), sequential | 4.9 ms |

### 1.6 Editor frame simulation (managed side only: per drawn token, UTF-16→UTF-8 marshal + width calc; native ImGui glyph/vertex cost excluded)
| Mode (Owl 2,732 lines) | Median |
|---|---:|
| Naive: re-tokenise everything every frame + draw all lines | 2.3 ms (≈14% of a 16.6 ms frame, managed part only) |
| Cached tokens, draw **all** lines | 0.49 ms |
| Cached tokens, **visible 60 lines** (1080p, ~17 px line height) | **0.003 ms** |

### 1.7 Memory (retained, simulated open project)
| Item | Retained managed |
|---|---:|
| 3 map luabnd text as strings | 2.5 MB |
| Per-line token cache for all 11 text battle files | 12 MB |
| **6 AI params with `ApplyParamdef` (boxed `Cell` per field)** | **197 MB** |
| Isshin anibnd raw bytes + parsed TAE | 58 MB |
| Peak working set of the bench process (repeated loads, GC churn) | 1.5 GB |

### 1.8 Other
- `DSLuaDecompiler.exe` (external process, .NET 7): **315 ms** for one file (mostly process start) and **632 ms for 10 files** in one process, so about 35 ms per file. Output correctness was not checked here.
- The Sekiro root plus mods holds **19,135 files**. A recursive enumeration takes 58 ms warm and **339 ms cold** (see DSAS H2 below).

## 2. DSAS load-path hot spots (relevant to us)

For architecture and threading, see [`docs/review/dsas-architecture.md`](../review/dsas-architecture.md). In short: the MonoGame loop runs on the UI thread (§1.1), and `_lock_CurrentDocument` is held for the whole Draw (§4.1, `Main.cs:2539`). TAE save is a non-atomic `File.WriteAllBytes` at `TaeFileContainer.cs:715` (§1.6). Params use hand-written offset readers (§1.8). The additional hot spots below are perf-specific:

| # | Where | What | Impact for us |
|---|---|---|---|
| H1 | `NewAnimationContainer.cs:1352-1354` → `ScanAllAnimations` `:579-605` → `FindAnimation` `:395-430` | On anibnd load, **every** HKX is parsed (`HKX.Read` / `GenFakeFromTagFile`, `:1117-1190`) and cached in `AnimationCache`. This runs inside `DoLoadingTaskSynchronous`. | For Isshin that is 328 HKX / 53 MB decoded up front: seconds of CPU and hundreds of MB. We only need TAE plus the one anim being previewed. **Do not reuse this path for Lua→anim jumps.** Load anims on demand. |
| H2 | `zzz_GameDataIns.cs:347-360` `FindFileInGameData` → `GetFilesInDir(null)` `:450-480` | Each call recursively enumerates the **whole install** plus the mod (19k files) and builds a list, then does a LINQ `EndsWith` scan. | 58 ms warm / ~340 ms cold **per call**. Never call it per entity. Build one index in Core `VirtualFileSystem`. |
| H3 | `zzz_GameDataIns.cs:989-1004` `FileExists` | "Unoptimized but effective": **reads the whole file** to test whether it exists. | An existence check on a 17 MB anibnd costs a full read. Use `File.Exists` in our VFS. |
| H4 | `zzz_GameDataIns.cs:286-324` `_readFileCache` | Unbounded `Dictionary<string, byte[]>` of **compressed** file bytes, never evicted (only `ClearReadFileCache`). It lowercases keys on read (`:308`) but **not on add** (`:322`), so mixed-case paths never hit. | Memory grows with every file opened, and the hit rate is lower than intended. A file watcher reload must also invalidate this cache, or DSAS keeps serving stale bytes. |
| H5 | `zzz_GameDataIns.cs:419-428` `ReadBinder` | Whole-file `ReadAllBytes`, then a full `DCX.Decompress`, then BND parse, all synchronous. | Same cost as §1.4. Only acceptable off the UI thread. |
| H6 | `zzz_LoadingTaskManIns.cs:247-269`, `:303-313` | `DoLoadingTaskSynchronous` starts a worker `Thread` and then **spin-waits on the caller** with `Thread.Sleep(16)` plus a 20 ms "fluff" sleep. | When the caller is the UI thread, the UI freezes for the whole load. That is how DSAS behaves today. |
| H7 | `zzz_ParamManagerIns.cs:295-330` `GetParam` | Lazy per param, but every miss walks all 138 BND entries with `ToUpper()` on each name, under `_lock_Params`. | Cheap (hand-written readers, no ApplyParamdef), but it is a lock that our background analysis must not take. |
| H8 | `TaeEditorScreen.cs:1281-1300` | With `ExtensiveBackupsEnabled`, every save does `Directory.GetFiles` on the output folder to find the next `.dsasautobak` index. | In `chr/` that is O(files in the dir) on every save. Our backup goes to `project/backups/`. |
| H9 | `Main.cs:2305-2330` | The frame is capped at `TargetFPS` when focused and drops to **10 FPS when unfocused** (60 during blocking tasks). | Our budgets are per frame at TargetFPS (60). A file-watcher reload that arrives while the window is unfocused is picked up up to 100 ms later, which is fine. |

## 3. Proposed PERF BUDGET

Target: 1920×1080, 60 FPS (16.6 ms/frame), on this machine class. "p95" means the p95 of a bench or automated test run.

| Feature | Budget | Measured basis / rationale |
|---|---|---|
| **Open project → Lua editor interactive** (luabnd + aicommon decoded, file shown, outline ready) | **< 300 ms** p95 | 4.7 ms for 4 luabnd (parallel), ~1 ms lex + outline. The bulk of the budget is UI setup. |
| **Open project → everything loaded** (params, TAE of every entity, chrbnd dummies, one viewport model) | **< 3 s** warm, < 6 s cold, **all of it on background tasks**. The UI stays at 60 FPS with progress shown. | 29 ms params (light), 50–270 ms per anibnd, 150–330 ms per FLVER, HKX on demand only. |
| AI param load (NpcParam, NpcThinkParam, BehaviorParam, AtkParam_Npc, SpEffectParam, Bullet) | **< 100 ms**, **< 60 MB retained** | LIGHT read 29 ms vs 1.3 s / 197 MB with `ApplyParamdef`. **Using `ApplyParamdef` on the open path is forbidden.** Apply it lazily to one row or param for the inspector only. |
| Paramdef XML load | < 300 ms, once, background. Cache only the needed defs. | 259 ms for all 159. Load only the 6, or cache the parsed field offsets in `cache/`. |
| **Re-parse + re-analyse a 3,000-line Lua after an edit** (lex, parse AST, symbols, diagnostics, combo graph for one file) | **< 50 ms** p95 total, **off the UI thread**, debounced **250 ms** after the last keystroke, cancellable (a newer edit cancels the running analysis). Sub-budgets: lex < 2 ms, parse < 15 ms, analysis + combo < 30 ms. | The lexer takes 0.3–0.6 ms, and a real parser with trivia/positions is typically 5–20× a lexer. The result is applied with one pointer swap on the UI thread. |
| **Goal navigator / breadcrumb update** | Build < 5 ms (background, part of the analysis above). **UI-thread apply + cursor→function lookup < 0.2 ms** (binary search over function ranges). | Outline proxy 0.5–0.7 ms including lex. |
| **Lua editor frame cost** (ImGuiColorTextEditNet, visible lines only, ~60 lines at 1080p) | **< 1.0 ms CPU per frame** for the editor widget, 3,000-line file, with or without a selection, with diagnostics squiggles. The **whole app frame is < 16.6 ms** with an idle viewport. | The managed side of 60 visible lines costs 3 µs. Native ImGui text for about 60×80 glyphs is well under 1 ms. The risk is per-frame O(N) work, not drawing (see R2). |
| Keystroke → recoloured line | < 0.1 ms on the UI thread (re-colour the edited line, plus following lines only while the long-comment/string state changes) | Re-lexing one line takes < 1 µs. |
| **Save one Lua** (write lua/ text + backup + repack luabnd + atomic replace) | **DFLT: < 100 ms** p95. **KRAK (m25): < 600 ms** p95, run as a background task with "Saving…" state and input still enabled. | 18–33 ms DFLT, 380 ms KRAK. Post-save re-analysis falls under the 50 ms analysis budget. |
| File watcher: external change → reloaded + re-analysed | < 300 ms (including a 200 ms debounce for editor write bursts) | Decode plus analysis is under 60 ms. |
| Import: decompile bytecode Lua (one-off) | < 1 s for the entity's battle+logic. Batch **all** files in **one** process (each process start costs about 300 ms). | 315 ms for 1 file, 632 ms for 10. |
| Memory: steady state with 3 bosses open (viewport on one) | **< 800 MB working set**, < 400 MB managed heap | Raw anibnd bytes are about 55 MB per Isshin-class boss. Keep TAE and drop the HKX bytes not in use. Do not keep boxed param cells. |

## 4. Top perf risks
1. **Param loading through `ApplyParamdef`** (1.3 s, ~200 MB retained for the 6 AI params). Combo/SpEffect source indexing needs SpEffect, AtkParam, Bullet and BehaviorParam, so the obvious SoulsFormats code is slow. **Mitigation:** an offset-based reader for only the `GameProfile` field list (prototype `tools/SoulsAiStudio.Bench/LightParamReader.cs`, 29 ms, values verified). Core needs public access to row data offsets and field offsets, either via SoulsFormats internals or its own offset computation.
2. **Per-frame O(N) work in the editor or panels.** Examples: rebuilding colour/diagnostic spans, `string.Split` of the buffer, re-running the outline in `Draw`, or calling `GetText()` each frame for the breadcrumb. With visible-lines rendering, drawing is cheap. A naive full re-tokenise plus draw-all costs about 2.3 ms managed per frame, before native cost. **Rule:** `Draw` only reads immutable snapshots that the analysis thread produced.
3. **DSAS synchronous loads on the UI thread** (H1 full HKX scan, H5 `ReadBinder`, H6 spin-wait). Jumping from Lua to an anim on a large boss (Isshin anibnd 17 MB → 55 MB inflate, 200+ ms, plus HKX decode) freezes the UI if routed through DSAS as-is. **Mitigation:** Core keeps an inflated per-entity anibnd cache (TAE parsed once) and DSAS is fed one HKX on demand from a background task.
4. Other risks:
   - KRAK save at about 380 ms per m25 save.
   - Paramdef XML parse on the first run (up to 3 s with JIT plus a cold cache).
   - H2/H3 `FindFileInGameData`/`FileExists` called from new code.
   - GC churn (bench peak WS 1.5 GB) if large binders are re-read on every reload instead of being cached and invalidated by the watcher.

## 5. Perf tests and regression checks the tester should add
Put these in an xUnit `[Trait("Category","Perf")]` set that is skipped by default and run nightly or on demand. Assert at **2× budget** to absorb machine noise, and log actual values to a CSV for trend tracking.

1. **Luabnd open:** decode the 3 sample maps plus aicommon in < 50 ms total. Unmodified repack keeps entries byte-identical, including the KRAK map (correctness gate on the perf path).
2. **Save:** full save of m10 (DFLT) < 200 ms and of m25 (KRAK) < 1.2 s. Saving onto a **ReadOnly** destination copy either succeeds (attribute cleared) or fails with a clear error. It must never leave a half-written `.tmp` file in place of the original.
3. **Param load:** the 6 AI params via the product loader < 200 ms and < 120 MB retained (`GC.GetTotalMemory(true)` delta). Values for a fixed set of IDs (Owl NpcParam/NpcThinkParam, the AtkParam behind the Owl kick, SpEffect 3506000 / 5060040) equal the `ApplyParamdef` values.
4. **Analysis after edit:** for each of the 11 text battle files, a full lex + parse + analyse takes < 100 ms (budget 50 × 2). An edit-then-analyse loop of 50 random single-line edits on `540310_battle.lua` (3,024 lines) has p95 < 100 ms. Each cancelled analysis must not publish its result.
5. **Navigator:** cursor→function lookup over all lines of the Owl file averages < 0.05 ms. The outline count matches the parser's function count (87 named functions for 506300).
6. **Editor frame:** a headless editor model test checks that one `Render`-equivalent pass over the visible range allocates **0 bytes** (`GC.GetAllocatedBytesForCurrentThread` delta) and takes < 1 ms for a 3,000-line file. An in-app debug overlay shows the editor ms per frame and GC count, with a manual test step: scroll the Isshin file top to bottom at 1080p and keep 60 FPS.
7. **No UI-thread I/O:** a debug-build guard (`Debug.Assert(!IsUiThread)`) in Core `VirtualFileSystem.Read*` and `Formats.*`. One test opens a project with a mocked UI thread id and must not trip it.
8. **Anibnd:** loading the TAE for c5400 < 600 ms, and memory retained per entity < 80 MB (HKX bytes not decoded until requested).
9. **Watcher:** touching a Lua file externally makes the reloaded and re-analysed snapshot visible in < 1 s (budget 300 ms × ~3 for timer jitter).
10. **Bench as smoke test:** CI or nightly runs `tools/SoulsAiStudio.Bench --quick --out docs/perf/bench-results.md` and diffs medians against the last committed run (flag a > 30% regression).

## 6. Notes and blockers
- **Paramdefs are not in the repo.** The bench used `DSMapStudio-1.11/Assets/Paramdex/SDT/Defs` (159 XML, XmlVersion 3, loads with the repo's SoulsFormats). Copy the 6 needed defs into `Resources/Paramdex/SDT/` and note the license.
- **Oodle:** m25 luabnd and the gameparam are DCX_KRAK. SoulsFormats needs `oo2core_6_win64.dll` loadable. The bench pre-loads it from the Sekiro root with `NativeLibrary.Load`, and the app should do the same instead of redistributing the DLL.
- CLAUDE.md assumes DCX_DFLT for luabnd. That is wrong for m25 (DCX_KRAK, `compressionLevel` 6 in `_witchy-bnd4.xml`), so the witchy-respecting repack must support KRAK.
- `tools/SoulsAiStudio.Bench` is **not** in the `.sln`. Adding it under a `tools` solution folder is optional. Building it in Release also builds `SoulsAssetPipeline/SoulsFormats` into its own `bin/Release` (build artifacts only).
