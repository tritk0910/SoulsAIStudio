# SoulsAiStudio.Mcp

Read-only MCP server (stdio) that exposes the headless `SoulsAiStudio.Core` analysis of Sekiro Lua AI to AI clients
(Claude Code, etc.). It never writes: no save/repack tools, nothing is created in the game folder, the mod folder or the
project (bytecode is decompiled into a temp-dir cache that is deleted on exit).

## Run

```
dotnet run --project src/SoulsAiStudio.Mcp -- --project "C:\path\to\MyProject.sasproj"
```

Without `--project`, call the `open_project` tool first. stdout is the MCP protocol; logs go to stderr.
Bytecode scripts need `DSLuaDecompiler.exe`: taken from the project's `DecompilerPath`, else `SoulsAIStudio/Tools/DSLuaDecompiler`
found by walking up from the server's folder, else `%SAS_DECOMPILER%`.

## Register in Claude Code

```
claude mcp add souls-ai -- dotnet run --project "C:\path\to\repo\src\SoulsAiStudio.Mcp" -- --project "C:\path\to\MyProject.sasproj"
```

(For faster startup, `dotnet build -c Release` once and register `dotnet <repo>\src\SoulsAiStudio.Mcp\bin\Release\net9.0\SoulsAiStudio.Mcp.dll --project ...`.)

## Tools

All line numbers are 1-based. `script` accepts the script key (`m10_00_00_00|out\bin\506300_battle.lua`), the file name
(`506300_battle.lua`, `.lua` optional) or a unique substring.

| Tool | Purpose |
|---|---|
| `open_project(path)` | Open a `.sasproj` (only if `--project` was not given). |
| `list_entities()` | Characters (`c####`) with their scripts. |
| `list_scripts(filter?)` | Project scripts: key, file, luabnd, npc id, kind. Max 200. |
| `read_lua(script, startLine?, endLine?)` | Source text; max 400 lines / 60k chars per call; decompiles bytecode. `startLine` beyond EOF, `< 1`, or after `endLine` is an error; `endLine` past EOF is clamped (stated in `note`). If the 60k-char cap cuts the range, `endLine` is the last complete line and `nextStartLine` says where to continue. |
| `list_goals(script)` | `Goal.*` outline: line range, group, AddSubGoal count, literal anim ids. |
| `get_spEffect_handlers(script, spEffectId?, includeBodies?, offset?, maxChars?)` | Every `== S` arm of `Goal.Interrupt`, in source order, plus the observed SpEffects (first page only). **Duplicate arms are all listed** (e.g. Isshin has two `== 5032`, lines 1979 and 1995), each flagged `duplicateId`/`armsForThisId`, ids in `duplicateIds`; Core's `SpEffectInterrupts.Table.Handlers` still keeps only the first per id, the new `Table.AllArms` has all. `clearMode` is `all`/`some`/`none`/`n/a` (is every subgoal preceded by `ClearSubGoal()` on its path); `clearsQueue` is the older "first statement is the clear". Summary by default; full bodies with `spEffectId` or `includeBodies`. Output is capped at `maxChars` (default 20000, min 1000, at least one arm always returned): `truncated` + `nextOffset` + a `note` tell you how to page or narrow with `spEffectId`. |
| `get_combo_sequence(script, goal, spEffectId?, maxNodes?)` | Combo tree of one goal: steps, ClearSubGoal, random/condition branches. Capped at `maxNodes` nodes (default 100, depth-first; `truncated`, `returnedNodes`, `note`; `stepCount` is always the full count). For a big goal such as `Goal.Interrupt` pass `spEffectId` to get just that SpEffect's arm(s). |
| `diagnose_combo(script)` | Static hints (handler not observed, observed without handler, `handler-no-clear` only when **no** path clears, `handler-partial-clear` when only some branches clear, `duplicate-handler`, random overlap/gap). Walks every arm incl. duplicates. Lua-only: TAE/param sources are not consulted. |

Condition strings are tidied for display only (`arg1 : GetNinsatsuNum ( ) <= 1` becomes `arg1:GetNinsatsuNum() <= 1`); Core's strings are unchanged.

Integer arguments are declared as JSON values and validated by the tools, so `startLine: 1.5` or `"abc"` fails with `startLine must be an integer (got 1.5)`. The SDK only shows the generic "An error occurred invoking ..." for binding errors of typed parameters, so this is why they are not plain `int?`. The one remaining typed parameter, `includeBodies` (bool), still gets the generic SDK message if given a non-boolean.

Failures come back as tool errors with the reason (unknown script, no project open, ...).
