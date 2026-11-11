# Review: commit 12932e2, "Project system, start screen, wizard, Scripts panel and interim Lua editor"

This is a read-only review against CLAUDE.md §6/§7 and `docs/review/dsas-architecture.md`. I did not look at `src/SoulsAiStudio.Editor`. Line numbers refer to the commit.

**Overall:** the layering is right. Core has no UI, there are only 5 one-line `[SAS]` hooks, `#nullable` is on, writes are atomic, backups happen before writes, and the tests use real data. The problems are concentrated in the **safety edges**: guarding the game folder, concurrent saves, and losing unsaved work on exit. Fix those before anyone uses the build on a real mod.

Legend: **[MUST]** fix before the next user-facing build · **[SHOULD]** fix soon · **[NICE]** nice to have.

---

## Must-fix

### M1. A blocked TAE save still writes into the game folder and marks the work as saved
`SoulsAIStudio/TaeEditor/TaeFileContainer.cs:715` (hook) and `TaeEditor/TaeEditorScreen.cs:1230-1260` (caller)

**Problem.** `SasHooks.WriteBinder` runs at the very end of the save. By that point DSAS has already done the following:
- written `_DSAS_WORKSPACE.json` (`SaveProjectJson()`, `TaeEditorScreen.cs:1235`)
- copied `<anibnd>.dsasbak` next to the anibnd (`:1246-1252`)
- serialized `<anibnd>.dsaproj` (`TaeFileContainer.cs:695`)
- called `SAFE_ClearAllModified()` (`:706`)

All three files land in the game folder. The editor then shows "no unsaved changes" even though the anibnd was never written. The `IOException` the hook throws also escapes on the LoadingTaskMan thread.

**Fix.** Add one more `[SAS]` line at the top of `SaveCurrentFile`:

```csharp
if (!SasHooks.CanSaveContainer(NewFileContainerName)) return;
```

It should show the dialog and return before any DSAS write. Keep `WriteBinder` as defense in depth.

### M2. A mod folder equal to (or containing) the game folder lets saves overwrite game files
`src/SoulsAiStudio.Core/Project/ProjectSession.cs:101-107`, `NewProjectWizard.cs:211-219`, `SasProject.cs:44-52`

**Problem.** `EnsureInsideMod` only checks that the path is inside `ModDir`. If the user chooses the Sekiro folder itself as the "Mod folder" (an easy mistake with the Browse dialog), `ModPackedPath` becomes `Sekiro\script\<map>.luabnd.dcx`, the check passes, and the game file is overwritten.

A hand-edited `.sasproj` has the same hole. On top of that, `ModDir = ""` makes `ScriptCatalog` resolve `script\` relative to the current working directory.

**Fix.** Add `SasProject.Validate()` in Core and call it from both `Create` and `Load`. It must reject:
- an empty or non-rooted `GameDir`/`ModDir`
- `ModDir == GameDir`
- a `ModDir` that is an ancestor of `GameDir`
- a `ModDir` that contains `sekiro.exe` or `Data0.bdt`

Unit-test every case.

### M3. Concurrent saves to the same luabnd lose edits
`ProjectSession.cs:69-94` and `LuaEditorPanel.cs:94-99`

**Problem.** The `Saving` guard is per tab. Two scripts in the same luabnd are a normal case: `506300_battle.lua` and `_logic.lua` both live in `m10_00_00_00`. Saving them back to back starts two `Task.Run` jobs. Each one loads the archive, changes only its own entry, and writes the packed `.luabnd.dcx`, so the last writer wins.

With an unpacked folder, the next save repairs the packed file. **Without one (a packed-only mod), the first edit is lost for good**, while its tab still shows "Saved".

**Fix.** Serialize writes per luabnd inside `ProjectSession`, using a `SemaphoreSlim` per luabnd key or a single save queue. Add a test that runs two saves concurrently.

### M4. Unsaved Lua is lost silently on app exit
`Main.cs:1314` (`WinForm_FormClosing`) only checks DSAS documents. `Program.cs` then calls `Process.Kill()`.

**Problem.** Dirty `OpenScript` buffers are thrown away with no prompt. Switching projects is guarded (`SasApp.ConfirmDiscardUnsaved`), but quitting is not.

**Fix.** Add a `[SAS]` line in `WinForm_FormClosing`, for example `if (SasHooks.HasUnsavedWork()) { e.Cancel = true; SasHooks.RequestExitPrompt(); }`. Also bring the planned autosave to `cache/autosave/` forward (CLAUDE.md P0 #5).

### M5. The game-folder guard fails open
`SasApp.cs:168-181` (`WhyWriteIsBlocked`)

**Problem.** The guard only knows the game folder from `Project.GameDir`, `Settings.LastGameDir`, or the default Steam path (`SasSettings.GuessGameDir`). Without an open project, it does not protect:
- a Steam library on another drive (`D:\SteamLibrary\...\Sekiro`)
- a fresh user profile
- a second Sekiro copy

In those cases nothing is blocked. The rule also allows **any** `<game>\mods\*`, which is fine, but it lives in the untested UI assembly.

**Fix.** Move the rule to Core as `WritePolicy` with unit tests. Replace the "known game dirs" check with a **marker check**: walk up from the target path, and block if an ancestor contains `sekiro.exe` (or `Data0.bdt`/`Data1.bdt`) and the path is not under that ancestor's `mods\`. Keep the known-dirs check as an extra signal. Use the same policy in `ProjectSession.EnsureInsideMod` so there is one rule, not two.

### M6. An exception between `ImGui.Begin` and `ImGui.End` corrupts the ImGui stack
`LuaEditorPanel.cs:111-119`, `ScriptsPanel.cs:262-281` (plus `BeginChild`/`TreeNode`), `NewProjectWizard.cs:58-93`, `StartScreen.cs:348-383`, `SasApp.cs:213-218`

**Problem.** `SasHooks.BuildWindows` catches the exception, but `End`/`EndChild`/`PopStyleColor`/`TreePop` are skipped. The next frame then hits a native ImGui assertion (abort) or a broken layout, inside DSAS's `lock(_lock_StaticWindowList)`. One realistic trigger is `_app.Project!` at `ScriptsPanel.cs:265` during `CloseProject`.

**Fix.** Wrap each window body as `try { ... } finally { ImGui.End(); }`, and do the same for the child, style and tree scopes. A small `using var _ = ImScope.Window(...)` helper keeps this readable.

---

## Should-fix

### S1. If the save fails partway, the unpacked folder and the packed file disagree
`ProjectSession.cs:90-93`

**Problem.** The order is: back up, write the unpacked `.lua`, then write the packed file. If the packed write fails (file locked, AV scan, disk full, `ReadOnlyFileException`), the unpacked file has the new text but the game still loads the old packed file. The UI reports a failure, and a retry does recover. But in the meantime, "what the editor shows" and "what the game runs" differ, and nothing says so.

**Fix.**
- Build `archive.ToPackedBytes()` first, so serialization errors happen before any write.
- Write the packed file first (it is what the game reads), then the unpacked file.
- On failure, return a `SaveResult { Written, Failed }` and show "partially saved" with the paths.

### S2. Backup targets and the files actually written can diverge
`ProjectSession.cs:85-91` vs `LuabndArchive.cs:125-135`

**Problem.** The backup list is built from `doc.Script.EntryPath`. `SaveUnpackedChanges` writes `entry.RelativePath`. `Find` also matches on the bare file name, so the two paths can differ and the written file goes un-backed-up.

**Fix.** Ask the archive for the paths first, e.g. `archive.PlannedUnpackedWrites(folder)`, then back up and check exactly those paths.

### S3. Repacking silently overwrites a newer packed file with an older unpacked folder
`ProjectSession.cs:79-81`

**Problem.** When an unpacked folder exists, the packed file is always rebuilt from it. That matches the spec (unpacked is the working copy). But if the user repacked or edited the `.luabnd.dcx` with another tool after unpacking, those changes are lost. They are only kept in `backups/`.

**Fix.** If the packed file's mtime is newer than every file in the unpacked folder, show a warning or require a confirmation. This also feeds the planned file watcher.

### S4. The decompiler can hang forever, leaves orphan processes, and may mangle encoding
`src/SoulsAiStudio.Core/Lua/Decompile/LuaDecompiler.cs:275-294`

**Problem.**
- There is no timeout.
- On cancel, `WaitForExitAsync(ct)` throws but the process keeps running.
- stdout is decoded with the console code page because `StandardOutputEncoding` is not set. Any non-ASCII string literal will be corrupted, and that text then gets saved as the plain-text source.

**Fix.**
- Add a timeout with a linked CTS, and call `proc.Kill(entireProcessTree: true)` in the catch.
- Prefer `-o <file>` and read the result as UTF-8 bytes. Otherwise set `StandardOutputEncoding = UTF8` and verify it against a known file.
- Add the decompiler exe's version or mtime to the cache key.

### S5. UI-visible state is mutated off the UI thread
`ProjectSession.cs:96-97`

**Problem.** `doc.SavedText` and `doc.LoadedFrom` are set in the continuation after `await Task.Run`, which runs on the thread pool because the MonoGame loop has no `SynchronizationContext`. The UI thread reads `IsDirty` at the same time. Reference writes are atomic, so this is benign today, but it breaks the "UI state changes on the UI thread" rule and will bite once the real editor tracks versions.

**Fix.** Have `SaveAsync` return a result. Apply `SavedText`/`LoadedFrom` in `LuaEditorPanel.PollTasks`, where `Loading` is already handled correctly.

**Verdict on task polling in general:** `IsCompleted`, then `.Result`, on the UI thread is safe and simple. Keep it. A small `PendingOp<T>` helper (`Poll(onOk, onErr)`) would remove the repeated pattern in four places.

### S6. `WriteBinder` runs on the DSAS loading thread
`SasHooks.cs:238, 273-283`

**Problem.** On that thread:
- `_app ??= new SasApp()` is not thread-safe and has a constructor with side effects (it parses args and may open a project).
- It reads `Session` and `Project` while the UI thread may replace them.
- It can call `BackupService` concurrently with a Lua save. The service is not thread-safe, and `Prune` deletes directories.

**Fix.** Initialize `SasApp` eagerly from the first `BuildMenus` (or use `Lazy<T>`). Make `WritePolicy` (M5) pure, with no `SasApp` state. Put a lock inside `BackupService.BackupBeforeSave`.

### S7. `BackupBeforeExternalWrite` compares paths without a trailing separator
`SasApp.cs:186`

**Problem.** `mods\Severance-dev2\...` matches `mods\Severance-dev`, so the backup ends up under `_external\...`.

**Fix.** Reuse the separator-terminated check from `EnsureInsideMod` (better still, the M5 policy).

### S8. Closing a tab or project while it is saving or loading drops the result and the error
`LuaEditorPanel.cs:88-92, 154-158`

**Problem.** A closed `OpenScript` is no longer polled, so a failed save is never reported.

**Fix.** Disallow closing while `Saving != null`, or move in-flight operations to a session-level list that is still polled.

### S9. `SasApp` is drifting toward a god class
`SasApp.cs`

**Problem.** In 223 lines it already holds: settings and recent files, command-line parsing, project lifecycle, menu building, the write-safety policy, external-write backups, notifications, and the error popup. The file watcher, param index, viewport bridge and undo stack are coming next.

**Fix.** Split it now while it is cheap:
- `WritePolicy` (Core, tested) — M5
- `ProjectController` (open, activate, close, and the dirty prompt)
- `StartupArgs`
- `ErrorSink` / `Notifier`
- `SasMenu`

`SasApp` then only composes these and runs the per-frame `BuildWindows`.

---

## Nice-to-have

### N1. GoalOutline end-of-function detection
`src/SoulsAiStudio.Core/Lua/Analysis/GoalOutline.cs:228-260`

The logic is **correct** for the Lua 5.1 constructs:
- `function`/`if`/`while`/`for`/`repeat` each open one level.
- A loop's own `do` is skipped by `IsLoopDo`, which scans back past `in`/operators and balanced brackets.
- A bare `do` block opens one level.
- `end`/`until` close a level.
- `while f(function() ... end) do` works, because the brackets are balanced before `while` is reached.

Gaps:
- While the user is typing, an unclosed function makes `EndLine` run to EOF. `GoalAtCursor` (`LastOrDefault`) still picks the innermost goal, so this is acceptable. A parser with error recovery fixes it later.
- `function Goal:Name(...)` (colon syntax) is not matched, so add it.
- `ScanAddSubGoals` (`:266-281`) misses the anim ID when an earlier argument is negative (`-1` lexes as two tokens), and it picks up the wrong ID for expressions like `3000 + x`. Parse arguments by comma at bracket depth 0 instead of using fixed offsets.

Tests to add: a nested `repeat ... until`, a bare `do ... end`, `for k, v in pairs(t) do`, a `while` condition containing an anonymous function, and an unterminated function.

### N2. Lexer allocations
`LuaLexer.cs:28`

`IsKeyword` allocates a string for every identifier. Use `.NET 9 HashSet<string>.GetAlternateLookup<ReadOnlySpan<char>>()`. The number lexer also eats `1..x` as a number. That is rare, but use a proper number-then-dot check.

### N3. Interim editor
`LuaEditorPanel.cs:171-179`

`InputTextMultiline(ref string)` copies and re-encodes the whole buffer every frame. A paste larger than the remaining capacity is truncated silently, because the ref-string overload has no `CallbackResize`. That is acceptable for an interim editor, but add a warning when the text is longer than about 70% of capacity.

`HandleShortcuts` uses MonoGame `Keyboard.GetState`. Use `ImGui.GetIO().KeyCtrl` together with `ImGui.IsKeyPressed` so it stays consistent with ImGui focus.

### N4. Wizard validation and UX
`NewProjectWizard.cs:205-219`

- `SafeName` can produce an empty name, which yields a `.sasproj` with no stem.
- The scan does not check that `script\` exists in the game folder.
- Store `GameDir`/`ModDir` as relative paths where possible (CLAUDE.md P0 #1). At least make `ModDir` relative to `GameDir` when it is nested inside it.

### N5. Consistency
- `SasSettings` serializes in PascalCase, while `SasProject` and `recent.json` use camelCase.
- The DSAS-folder files use block namespaces, which is fine to match DSAS. Prefer file-scoped namespaces for any new non-DSAS-style files.
- `ProjectScript.FileName` and `ScriptCandidate.FileName` assume `\` separators. Normalize `EntryPath` on load.

### N6. `RecentProjects.Remove` does not normalize the path
`RecentProjects.cs:135-138`

`Add` stores the full path, but `Remove` compares the raw argument. Call `Path.GetFullPath` there too.

### N7. Missing tests
- `ModDir == GameDir` is rejected (M2).
- Concurrent saves to the same luabnd (M3).
- The packed write failing after the unpacked write (S1).
- `WritePolicy` cases (M5): another drive, `mods\` allowed, sibling-prefix folder names.
- Decompiler timeout and cancel, using a fake exe.

### N8. Suppressing the DSAS welcome dialog depends on frame order
`SasApp.cs:144`

Clearing `OSD.RequestShowWelcome` every frame from `BuildMenus` depends on the menu bar being built before the welcome check (`OSD.cs` ~405 vs ~419). That holds today. Add a comment on the hook line in `MenuBar.cs` so an upstream reorder is noticed during a merge.

---

## Conformance summary

| Rule | Status |
|---|---|
| Core has no UI references | OK |
| `[SAS]` one-line hooks | OK (5). M1 and M4 each add one more. |
| Never write to the game folder | **Violated through the DSAS save path (M1)**, plus gaps M2 and M5 |
| Atomic save, backup first | OK for Lua. Partial-failure handling: S1, S2. |
| Nullable, `async` loads off the UI thread | OK. Off-thread state mutation: S5, S6. |
| Undo | Not in scope yet. The interim editor uses ImGui's built-in undo. |
| Testability | Core is good. Safety logic (`WhyWriteIsBlocked`) sits in the untested UI assembly (M5). |
