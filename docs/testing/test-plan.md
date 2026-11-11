# Souls AI Studio — Test plan (Core)

Scope: `tests/SoulsAiStudio.Core.Tests` (xUnit, net9.0, RollForward=LatestMajor). Maps every feature in `CLAUDE.md` §5 to concrete test cases on real samples.
Run: `dotnet test tests/SoulsAiStudio.Core.Tests`

## 0. Data sources and gating

| Tag | Source | Gate (auto-skip with reason) |
|---|---|---|
| **TD** | `test-data/original/Severance-dev/script/` (backup, READ-ONLY, gitignored) | `[RequiresTestDataFact/Theory]` |
| **OO** | TD + `oo2core_6_win64.dll` (Oodle, loaded in place from the Sekiro install, `%SAS_OODLE_DLL%` or test bin) — needed for DCX_KRAK (`m25_00_00_00.luabnd.dcx`) | `[RequiresOodleFact/Theory]`; KRAK rows are dropped from luabnd theories and `Krak_OodleAvailable_m25IsReadable` is skipped with the reason |
| **GD** | Sekiro install, read-only: `chr/c5060|c5400|c5000.anibnd.dcx` (Owl / Isshin / True Monk), `chr/*.chrbnd.dcx`, `param/gameparam/gameparam.parambnd.dcx`; mod overrides in `mods/Severance-dev/chr/` (has unpacked `c5060-anibnd-dcx-wanibnd`, `c5400-anibnd-dcx-wanibnd`) | `[RequiresSekiroInstallFact]` |
| **SY** | Synthetic data generated in a temp dir inside the test | none |

Safety rules (enforced by `TestSupport`):
- Anything that writes uses `TempWorkDir` (`%TEMP%/SoulsAiStudio.Tests/<label>-<guid>`, deleted on dispose; `SAS_KEEP_TEMP=1` keeps it) + `CopyFromOriginal`/`CopyScript`.
- `TestPaths.AssertWritable` throws for anything under `test-data/original` or the Sekiro install. Core code under test that takes an output path should be called only with temp paths.
- `Original_ChecksumsUnchanged` re-hashes the backup on every run (fails on changed / missing / new files). Note: paths in `SHA256SUMS.txt` are `./script/...`, i.e. relative to `test-data/original/Severance-dev/`, not to `test-data/original/` as CLAUDE.md §7.1 says.

Samples: Owl `m10_00_00_00/out/bin/506300_battle.lua` (c5060), Isshin `m11_02_00_00/out/bin/540300_battle.lua` (c5400), True Monk `m25_00_00_00/out/bin/500000_battle.lua` (c5000), `aicommon.luabnd.dcx`.

## 1. Baseline already covered (this round)

| Test class | What it pins | Data |
|---|---|---|
| `TestSupport/TestDataGuardTests` | repo root found, checksum guard, TempWorkDir copy/cleanup, protected-path refusal | TD |
| `Formats/LuabndBaselineTests` | header (`07D7R6`, IDs\|Names1\|Names2\|Compression, unicode, extended=4, Flag1), entry counts/IDs/order, plain vs bytecode, unpacked == dcx entries, `_witchy-bnd4.xml` == BND, write→read round trip, repack from folder+xml | TD/OO |
| `Lua/LuaCorpusTests` | 179 `out/bin/*.lua` inventory (10 plain, 169 bytecode), plain text = ASCII/LF/no BOM/trailing `\n`, bytecode header `1B 4C 75 61 50`, Owl `== 3506000` branch present | TD |

Data sources for future tests: `LuaCorpus.PlainText()`, `LuaCorpus.Bytecode()`, `LuaCorpus.All()`, `LuaCorpus.KeyBattleScripts()`, `Samples.LuabndNames()`, `LuabndBaselineTests.Luabnds()/UnpackedMaps()` (KRAK-aware). Oracle: `WitchyBnd4Xml.Load(...).BuildBnd4(dir)`.

## 2. P0 — Foundation

### 2.1 Luabnd read/write (Core/Formats) — TD/OO
- L1 Read each luabnd via Core API → same entries as `LuabndBaselineTests.Baseline` (58/58/69/107 entries; luagnl ID 1000000, **m11 uses 10000000**).
- L2 Open unpacked folder via `_witchy-bnd4.xml` → same entry list/order/IDs/flags/names as the `.dcx`.
- L3 Save unchanged → read back: entries identical; uncompressed BND4 byte-identical; compression type + params preserved (DFLT `0x11000/0x44/0x4C/9/0`, KRAK level 6). **Do not assert .dcx byte-identity for DCX_DFLT** (SoulsFormats zlib ≠ WitchyBND's; KRAK was byte-identical).
- L4 Edit one Lua (Owl 506300) in a temp copy → save → only that entry changed; all other entries byte-identical; written as plain text.
- L5 Save writes `.luabnd.dcx` and leaves an existing `.luabnd.dcx.bak` untouched (create a fake `.bak` in temp).
- L6 Atomic save: inject a failure mid-write (e.g. writer throws / target locked) → old file intact, no partial file left; temp file cleaned.
- L7 Unpacked folder newer than `.dcx` (modify a Lua in temp copy) → app detects divergence and reports which side is newer (decide policy; never silently drop edits).
- L8 Refuses to write into the game root (non-mod) path → error. Use a fake "game" dir in temp; check `IsProtectedPath`-style guard in Core.
- L9 Bytecode entry kept as-is when not edited (no re-encoding).

### 2.2 Project system (.sasproj) — SY (+TD for entity list)
- P1 New → save → load: all fields round-trip (paths relative where possible, entities, open tabs, cursor, bookmarks).
- P2 Relative paths resolve after moving the whole project folder (temp → temp2).
- P3 Missing referenced file on open → project opens with a warning, not crash.
- P4 Recent projects list: MRU order, dedupe, cap, missing entries pruned.
- P5 Wizard logic: given TD script dir as "mod" → lists entities incl. 506300/540300/500000; mod overrides game (fake game dir with an older copy).
- P6 Folder layout created: `lua/`, `backups/`, `cache/`.

### 2.3 Backup — SY/TD
- B1 First import snapshots to `backups/original/`; second import does not overwrite it; retention never deletes it.
- B2 Each save creates `backups/<yyyyMMdd-HHmmss>/<relpath>` for every written file (inject clock).
- B3 Retention N (set 3, save 5×) → newest 3 timestamp dirs kept + `original/`.
- B4 Autosave buffer to `cache/autosave/`; open project → recovery offered; discard clears it.
- B5 Restore single file / whole snapshot → bytes equal the backup; restore itself creates a backup first.
- B6 Lua text diff between backup and current (Owl, edit one line) → exactly one hunk.

### 2.4 File watcher — SY (temp dir, real FS events)
- W1 External change to a watched Lua → reload event once (debounce coalesces burst of writes).
- W2 Buffer dirty + external change → conflict state (Keep mine / Take theirs / Diff), no auto-overwrite.
- W3 App's own save does not trigger a reload loop.
- W4 Delete + recreate (editor "safe write" pattern) handled as change, not removal.
- W5 Watch luabnd/anibnd/gameparam paths in mod folder (temp copy).
- W6 After save → re-analysis (AST, combo graph, diagnostics) invoked.

### 2.5 Decompile (Core/Lua/Decompile) — TD (+ external exe)
- D1 Bytecode detection = `1B 4C 75 61` (169 corpus files; header byte 5 = `0x50`).
- D2 Decompiler path from settings; missing exe → clear error, no crash.
- D3 Integration (skip unless exe present at configured path): decompile `m10 .../506000_logic.lua` or any bytecode file into temp → non-empty text that parses.
- D4 Original bytecode file never modified (hash before/after).

## 3. Lua editor core + Goal navigator (Core/Lua/Parsing, Analysis) — TD

- E1 **Parser round-trip** parse → print == input byte-for-byte on `LuaCorpus.PlainText()` (10 files incl. 506300/540300/500000) and on aicommon plain text (`goal_list.lua`, `147001_battle.lua`). Later: on all decompiled bytecode.
- E2 AST positions: every node's line/col maps back to the same source substring.
- E3 Comments/whitespace preserved; LF kept (corpus is LF-only ASCII).
- E4 Navigator Owl 506300: lists `Goal.Initialize/Activate/Act01..Act50/Interrupt/Parry/ShootReaction/Kengeki_Activate/Kengeki01..` with correct definition lines (assert count of `Act*` and the exact Kengeki set from the file).
- E5 Grouping (Lifecycle / Interrupt / Act / Kengeki / Other) driven by GameProfile rules.
- E6 Badges: per-goal `AddSubGoal` count, main anim ID, handled SpEffects (Interrupt → includes `3506000`).
- E7 Expand `Goal.Interrupt` → branch list includes `== 3506000` and other `INTERUPT_*`.
- E8 Breadcrumb: cursor offset → enclosing function (inside Interrupt body → `Goal.Interrupt`; top level → none).
- E9 Go-to-definition `Goal.Act05` string → definition line; find usages.
- E10 Top-level `AddObserveSpecialEffectAttribute(TARGET_*, id)` calls extracted (Owl), not inside a function.
- E11 Chained call `AddSubGoal(...):TimingSetNumber(...)` parsed as call-on-call; AddSubGoal still recognized.
- E12 aicommon symbols (`GOAL_COMMON_*`, `INTERUPT_*`, `TARGET_*`, `AI_DIR_*`) indexed from `goal_list.lua` / decompiled aicommon for autocomplete.
- E13 Basic diagnostics: unknown function, unknown goal ID; anim ID not in TAE (**GD**).
- E14 Isshin 540300 + True Monk 500000: navigator produces non-empty Act/Interrupt lists (smoke on different authors' styles); `000120_platoon.lua` (bytecode) is a third script kind — loader must not assume only `_battle`/`_logic`.

## 4. P1 — Visual understanding

- V1 Param chain resolve (**GD**): NpcParam → NpcThinkParam → battle goal for 506300/540300/500000 (read `gameparam.parambnd.dcx`; prefer mod `param/` override).
- V2 Attack inspector data (**GD**): `AddSubGoal(GOAL_COMMON_Attack*, animId)` in Owl → anim exists in `c5060.anibnd.dcx` TAE → AttackBehavior events → BehaviorParam → AtkParam_Npc (hitbox dmyPoly, radius, damage, posture). Assert non-empty and stable values for one known anim.
- V3 Act table extraction (TD): Owl `Goal.Activate` weight table → act list with distance conditions; edit weight → regenerated code differs only in that literal (AST edit, format preserved).
- V4 Distance overlay inputs (TD): distance thresholds extracted from conditions match source literals.
- Viewport/rendering: out of Core scope (manual).

## 5. P1.5 — TAE limited editor (Core/Tae) — GD (read install; write only to temp copies)

- T1 Round-trip: load `c5060.anibnd.dcx` (copy to temp) → save without changes → TAE bytes identical (CLAUDE.md requirement); also BND entries identical.
- T2 Move StartTime/EndTime of a SpEffect event, snap to 30 fps; other events untouched.
- T3 Set value only for SpEffect events; attempt on non-whitelisted event → rejected.
- T4 Copy/paste events anim A → B (keep time / at playhead); delete with result; undo/redo restores exact bytes.
- T5 Whitelist comes from GameProfile (swap profile → different allowed set).
- T6 SpEffect ID lookup: unknown ID → warning (needs SpEffectParam, GD).
- T7 Prefer mod override `mods/Severance-dev/chr/c5060...` when present (read only).

## 6. P2 — ComboGraph (Core/Lua/Analysis/Combo)

Lua-only parts (TD):
- C1 Owl `Goal.Interrupt`, branch `interruptSpeffect == 3506000`: detects `GetSpecialEffectActivateInterruptType(0)` data-flow (variable name independent), `IsInterupt(INTERUPT_ActivateSpecialEffect)` guard, `ClearSubGoal` → `ClearsQueue=true`, random split (~33%) with probabilities, `GetNinsatsuNum()` → ConditionBranch (no probability), `:TimingSetNumber` chain kept on the AnimStep.
- C2 Random thresholds → probabilities (`<= 25/50/75/else` → 25% each); nested if inside random branch.
- C3 Coverage diagnostics: thresholds not covering 1..100 / overlapping (SY snippets).
- C4 Observe registration: handler `== S` without top-level `AddObserveSpecialEffectAttribute(target, S)` → Warning; present → none (Owl).
- C5 Rename locals (SY: rewrite Owl branch with different variable names) → identical graph.
- C6 `Replanning()` branch → Replan edge.
- C7 Non-SpEffect `INTERUPT_*` branches → edges (P2 #14).
- C8 Cycle handling: synthetic S1→anim→S2→anim→S1 terminates with back-edge marker.
- C9 Every edge has source file/line pointing at the right `AddSubGoal` / `== S` line.
- C10 Isshin 540300 / True Monk 500000: graph builds without exceptions; snapshot node/edge counts once reviewed.

SpEffect source index (GD):
- S1 `TaeEvent`: Kengeki03 + SpEffect 5060040 (CLAUDE.md §P2 example; verify which entity/anim — c5060 anim 3002 at ~2.0 s) → edge with source TaeEvent and start time.
- S2 `OnHit`: Owl kick anim → TAE AttackBehavior → BehaviorParam → AtkParam_Npc SpEffect on target → edge "on hit", observed on `TARGET_ENE_0`; full source chain string matches.
- S3 Target mismatch (S applied to self but observed on `TARGET_ENE_0`) → Warning (SY param fixture).
- S4 `SpEffectChain` depth limit + cycle guard (SY).
- S5 Handler S with no source → Info (wording "not found in loaded data"), never Error.
- S6 Interrupt timing after JumpTable window → Warning.
- S7 Anim ID in AddSubGoal missing from TAE → diagnostic.
- S8 Path probability: OnHit branches excluded from fixed probability; condition branches mark path "undetermined".

Combo builder (later, GD + temp): one operation updates Lua goal + interrupt handler + observe + TAE event; single undo reverts all three.

## 7. Diagnostics (cross-cutting)

- G1 Every diagnostic has severity, message, file/line, and "where searched" for combo hints.
- G2 Snapshot test of diagnostics for 506300/540300/500000 (approved list in `tests/.../Snapshots/`), so analysis changes show up as diffs.
- G3 No Error-level diagnostics on the unmodified plain-text corpus (the mod runs in-game).

## 8. Needs-data summary

| Area | TD only | Needs GD (Sekiro install, read-only) | External |
|---|---|---|---|
| Luabnd read/write/repack, backup, watcher, project | ✔ (OO for m25) | | |
| Parser, navigator, symbol index, act table | ✔ | anim-ID-in-TAE diagnostic | |
| ComboGraph Lua side (C1–C10) | ✔ | | |
| SpEffect sources TaeEvent / OnHit (S1–S2, S6–S7) | | ✔ anibnd + gameparam | |
| Param chain, attack inspector | | ✔ | |
| TAE editor | | ✔ anibnd (copied to temp) | |
| Decompile integration | bytecode from TD | | DSLuaDecompiler.exe (.NET 7) |
