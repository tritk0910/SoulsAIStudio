# DSAS architecture review: hook points, §10 answers, conventions

Reviewer: code-review agent (readability / scalability / maintainability). Read-only review of `sas/main` on 2026-11-11.
Paths are relative to the repo root. `DSAS/` stands for `SoulsAIStudio/`. Line numbers come from the current tree, so expect drift after upstream merges.

Size: DSAS has 383 `.cs` files and about 136k lines. It is one WinExe assembly with no nullable annotations. Global state: `zzz_DocumentManager.CurrentDocument` is referenced 612 times, `Main.Config` 244 times and `Main.TAE_EDITOR` 75 times.

---

## 1. Architecture map

### 1.1 Entry point and main loop
| What | Where |
|---|---|
| `Program.Main` creates `Main : Microsoft.Xna.Framework.Game` and runs it synchronously, so the MonoGame loop runs on the WinForms UI thread. On exit it calls `Process.Kill()`. | `DSAS/Program.cs:18-75` |
| `Main` is a god class: about 3000 lines, about 150 `public static` members (config, fonts, DPI, input, render targets, lazy dispatch). | `DSAS/Main.cs` |
| `Main.TAE_EDITOR` resolves to `zzz_DocumentManager.CurrentDocument?.EditorScreen` | `DSAS/Main.cs:208` |
| `Update()` runs `TAE_EDITOR.Update(...)`, then `zzz_DocumentManager.UpdateDocuments()` | `DSAS/Main.cs:2065-2517` (TAE update at ~2469) |
| `Draw()` holds `lock(zzz_DocumentManager._lock_CurrentDocument)`. The order is: `ImGuiDraw.BeforeLayout`, then `OSD.Build` (all ImGui UI), then the 3D pass into `RenderTarget0_Color`, then `TAE_EDITOR.Draw`, then `OSD.PostBuild`, then `ImGuiDraw.AfterLayout`. | `DSAS/Main.cs:2526-2930` (OSD.Build 2599, 3D 2660-2860) |
| Cross-thread helpers: `Main.MainThreadLazyDispatch(act, frameDelay, waitForAllLoadingTasks)` and `Main.WinForm.Invoke` | `DSAS/Main.cs:467-575` |

### 1.2 Document model ("instanced statics")
- `zzz_DocumentManager` is static. It holds the list of open documents, `CurrentDocument` (`zzz_DocumentManager.cs:112-134`), `_lock_CurrentDocument`, and request flags.
- `zzz_DocumentIns` represents one open anibnd/tab (`zzz_DocumentIns.cs:14-27`). It owns `GameData` (VFS), `GameRoot` (game type, paths, loaders), `ParamManager`, `Scene`, `LoadingTaskMan`, `SoundManager`, `FmgManager`, `WorldViewManager`, `EditorScreen` (TaeEditorScreen) and `SpWindowAnimations`.
- Services are per-document instances, but almost everything reaches them through the static `zzz_DocumentManager.CurrentDocument`. That includes the param **row readers** themselves, for example `ParamData.AtkParam.Read` checks `zzz_DocumentManager.CurrentDocument.GameRoot.GameType`.

### 1.3 ImGui UI (`DSAS/ImguiOSD/`)
- **`OSD`** is static (`ImguiOSD/OSD.cs`).
  - `Build()` (`:299`) creates a fullscreen `MainDockSpace` window with `ImGui.DockSpace` (`:359-376`).
  - It then shows the main menu bar (`MenuBar.BuildMenuBar`, `:405-417`).
  - It then **explicitly calls `.Update(...)` on every window**, one line per window (`:438-484`).
  - Windows are `public static` fields on `OSD` (`:172-200`). A reflection scan of those fields (`CreateStaticWindowList`, `:140-156`) is used only to persist which windows are open.
  - The default layout is an embedded ini, `EmbRes/imgui_default_V5.ini`, written to `imgui.ini` (`:20-41`).
- **`Window`** (`ImguiOSD/Window.cs`) is `public abstract partial class`. Subclasses implement `Init()`, `BuildContents(ref bool anyFieldFocused)`, `NewImguiWindowTitle` and `GetSaveOpenStateType()`. DSAS writes each window as a nested class (`Window.Help`, `Window.Parameters`, …), but these members are `protected abstract` on a public class, so **external subclasses work too**.
- **`MenuBar`** is a static partial with one file per menu (`MenuBar.000.File.cs` … `MenuBar.080.SupportMeowmaritus.cs`). The order is set in `MenuBar.cs:40-53`. Helpers: `ClickItem`, `Checkbox`, `EnumSelectorItem` (`MenuBar.cs:84-191`).
- **`DialogManager`** provides modal dialogs (`DialogOK`, text input, …) and `Dialog.SetupWorkspace` (game dir and ModEngine dir picker).
- The **TAE graph is not built from ImGui widgets.** `Window.Graph` hosts a nested dockspace (`Window.Graph.cs:35-60`). The timeline itself is drawn with `ImGuiDebugDrawer` draw-list calls in `TaeEditor/NewGraphDrawer.cs`, and its input is read from MonoGame raw input (`FancyInputHandler`) in `TaeEditor/NewGraphInput.cs`, not from ImGui input.
- The **viewport** is `Window.Viewport` (`ImguiOSD/Window.Viewport.cs`). It only reserves a rectangle, `TaeEditorScreen.ModelViewerBounds => OSD.SpWindowViewport.GetRect()` (`TaeEditorScreen.cs:724`). `Main.Draw` then blits the 3D render target into that rectangle.

### 1.4 TAE editor (`DSAS/TaeEditor/`)
- `TaeEditorScreen` is a god class of 4560 lines. It covers selection, file open/save, anim navigation (`GotoAnimID` `:1981`), highlights (`RequestHighlightAction` `:113`), the undo managers (`:727-815`), keyboard shortcuts (`Update` `:3675`, with copy/paste/delete keys at `:3801-4083`) and saving (`SaveCurrentFile` `:1230`).
- `NewGraph`, `NewGraphInput` (1600 lines) and `NewGraphDrawer` form the timeline. Drag types are `Move/ResizeLeft/ResizeRight/SelectionRect/Scrub` (`NewGraphInput.cs:17-31`), committed through `CurrentAnimUndoMan.NewAction` (`:611-660`). Copy, cut, paste and add are `DoCopy/DoCut/DoPaste/DoAddNewEventAtMouse` (`:1014-1153`). An `isReadOnly` flag already exists, but it is tied only to `IsGhostGraph` (`NewGraph.cs:293,311`).
- `Window.Parameters` is the **action inspector**, despite the name. It edits `act.Parameters[pIndex]` through `CurrentAnimUndoMan.NewAction` (`ImguiOSD/Window.Parameters.cs:650-891`).
- **Undo** (`TaeUndoMan.cs`) uses mementos. Each action captures a `TaeRestorableState_Anim`, a full snapshot of one animation (`TaeUndoableAction_Anim.cs`). There is **one undo stack per animation** (`UndoManDictionary`) plus a `GlobalUndoMan` for project and category operations. `PerformDo` and `PerformUndo` marshal through `Main.WinForm.Invoke` and take `_lock_CurrentDocument`.
- There are **35 mutation call sites** that go through `UndoMan.NewAction`: `NewGraphInput` (11), `TaeEditorScreen` (11), `Window.Parameters` (6), `DialogManager` (2) and `OLD_*` (3).
- Simulation: `TaeActionSimulationEnvironment` (2371 lines) maps events to effects such as hitboxes, SpEffects and FFX. It uses `InternalSimTemplate` (`DSAS/InternalSimTemplate.cs`, embedded XML `EmbRes/InternalSimulationTemplate.xml`), which maps logical field names like `JumpTableID`, `BehaviorJudgeID` and `SpEffectID` to per-game byte offsets.

### 1.5 Project system (`DSAS/DSAProj/`, `TaeProjectJson`)
DSAS has **three separate "project" concepts**. None of them matches `.sasproj`.
1. **`.dsaproj`** (`DSAProj.cs:26`, `EXT = ".dsaproj"`) is a versioned **binary** sidecar written next to the anibnd as `<anibnd>.dsaproj` (`DSAProj.TaeContainerInfo.cs:37-40`). It holds the editable TAE model (`AnimCategory` → `Animation` → `ActionTrack` → `Action`), tags, colours and editor flags. It is the in-memory TAE model that the editor mutates.
2. **Workspace JSON**, `TaeProjectJson` (`TaeEditor/TaeProjectJson.cs`), stores `GameDirectory`, `ModEngineDirectory`, `LoadLooseParams`, `LoadUnpackedGameFiles` and world-view cameras. The current code writes it as `<anibnd dir>/_DSAS_WORKSPACE.json` (`zzz_GameRootIns.cs:555`, `TaeLoadFromArchivesWizard.cs:850`). The `_DSAS_PROJECT.json` files in the user's `Sekiro/chr/` and `mods/*/chr/` were written by an **older DSAS build** under the old name. The current code never reads that name.
3. **Caches**: `_DSAS_CACHE/<GAME>/data*.bhd` holds decrypted BHD headers plus `_CACHE_INFO.json` (`zzz_GameRootIns.cs:303`, `zzz_GameDataIns.cs:116-202`). `_DSAS_TEMP_UNPACK` / `_DSAS_TEMP` hold files temporarily unpacked from the archives (`zzz_GameRootIns.cs:302`).

The recent-files list is stored in the global config (`TaeConfigFile.RecentFilesList`, `TaeEditor/TaeConfigFile.cs:176`).

### 1.6 File loading for Sekiro (SDT)
- **VFS** (`zzz_GameDataIns.ReadFileDirect`, `zzz_GameDataIns.cs:1071-1135`) checks, in order: the in-memory cache, then the **ModEngine dir** (`ModengineRootPath`), then loose interroot files (when "unpacked" is enabled), then the encrypted `Data*.bdt/bhd` archives (EBL, with headers cached in `_DSAS_CACHE`). This is already the "mod first, then game" order that CLAUDE.md asks for. It is document-bound and logs through notifications.
- `GameRoot.InitializeFromBND` and `Init` (`zzz_GameRootIns.cs:402-617`) detect the game type, and `LoadCharacter(anibndID, chrbndID, …)` (`:1029`) loads the models. `Model` (`DSAS/Model.cs:461`) loads the chrbnd: FLVER, textures and dummy polys (`NewDummyPolyManager`). Animation goes through `NewAnimationContainer` / `NewHavokAnimation*` (HKX via SoulsAssetPipeline/Havoc). Sekiro uses delay-load anibnds (`GameTypeUsesDelayloadAnibnd`, `zzz_GameRootIns.cs:189`).
- Oodle (Sekiro DCX_KRAK) is located from the exe directory or the game dir (`Main.cs:43-104`). SoulsFormats loads `oo2core_6_win64` itself (`SoulsFormats/Utilities/Compression/Oodle/Oodle.cs:71-124`).
- **Saving a TAE** (`TaeFileContainer.NewSaveContainer`, `TaeEditor/TaeFileContainer.cs:625-720`) re-serialises every TAE from the DSAProj model (`DSAProj.WriteTaeFilesForAnibnd`, `DSAProj.cs:600`), writes the `.dsaproj`, then calls **`File.WriteAllBytes(Info.GetMainBinderName(), …)`**. That write is not atomic, it is not redirected to the mod folder, and it overwrites whatever path was opened, **including the game dir**. The backup extension is `.dsasbak` (`TaeEditorScreen.cs:241`).

### 1.7 Viewport and renderer
- `GFX` is static. `GFX.DrawScene3D`, `DrawPrimRespectDepth`, `DrawSceneOver3D` and `DrawPrimDisrespectDepth` dispatch through `DoDrawStep` (`GFX.cs:772-870`). That calls `CurrentDocument.Scene.Draw()` for meshes and `DBG.DrawPrimitives()` for overlays (`DBG.cs:362`), which iterates `Scene.Models[*].DrawAllPrimitiveShapes()`.
- There is one viewport and one render target, both owned by `Main` (`Main.cs:1384` `RebuildRenderTarget`).
- Primitive types in `DSAS/DebugPrimitives/` include `DbgPrimWireCapsule`, `WireSphere`, `WireCylinder` and `WireFan`. Immediate-mode helpers live in `ImGuiDebugDrawer` (`DrawLine3D`, `DrawText`, …). A range ring needs no new primitive class: a series of `DrawLine3D` calls is enough.

### 1.8 Params
- **Yes, DSAS loads Sekiro params.** `zzz_ParamManagerIns.LoadParamBND` (`zzz_ParamManagerIns.cs:591-690`) reads `/param/GameParam/GameParam.parambnd.dcx` through the VFS, so the mod copy wins. It then loads `BehaviorParam`, `BehaviorParam_PC`, `AtkParam_Pc`, `AtkParam_Npc`, `NpcParam`, `SpEffectParam`, `EquipParam*`, `WepAbsorpPosParam` and `HitMtrlParam` (`LoadStuffFromParamBND`, `:394-498`).
- **Caveat:** the rows go through hand-written byte-offset readers (`DSAS/ParamData/*.cs`, raw `PARAM_Hack` in `DSAS/PARAMHack/`), **not paramdefs**. Only the hitbox subset is read. AtkParam **skips `spEffectId0..4`** (`ParamData.AtkParam.cs:484-494`). `SpEffectParam.Read` reads **nothing** for SDT (`ParamData.SpEffectParam.cs`). Bullet, NpcThinkParam and the `NpcParam.thinkId` field are not loaded at all. These readers are unusable for our inspector or for the OnHit index.
- Reusable logic:
  - `GetNpcBasicAtkParam` (`:562-588`) uses the NPC BehaviorParam ID formula `2_00000_000 + BehaviorVariationID*1000 + BehaviorJudgeID`, then follows `RefType` (0=Attack, 1=Bullet, 2=SpEffect, `ParamData.BehaviorParam.cs:21`) to `RefID`.
  - `FindNpcParams` matches the chr ID for SDT with `(id % 1_0000_0000) / 1_0000 == chrId` (`:355-392`).
  - The param **file watcher** already exists: `InitAutoReload` / `UpdateAutoParamReload` use a FileSystemWatcher with a poll (`:90-170`).

### 1.9 Hitbox drawing
- In `TaeActionSimulationEnvironment.cs:483-560`, event types `1` (AttackBehavior), `5` (CommonBehavior), `304` (Throw) and `307` (PC) resolve to an AtkParam through `GetAtkParamFromEventBox`, then `GetBehaviorParamFromEvBox` (`:1807-1830`, `EVID_*` constants).
- `NewDummyPolyManager` (`DSAS/NewDummyPolyManager.cs`) then draws them. `HitboxSlot` and `HitPrims` (`:45-70`) handle the drawing, `UpdateHitPrim` / `AddNewHitPrim` (`:241-320`) build capsules and spheres from `AtkParam.Hits[i]` (DmyPoly1/2, Radius), and `DrawAllHitPrims` (`:619`) renders them. `GetPrimDrawMatrix(DummyPolySource)` (`:516`) picks the body or weapon dummy-poly space.
- The active SpEffects in the simulation appear only as display strings (`SimulatedActiveSpEffects`, `:1805`).

### 1.10 Global and static state (what new code will collide with)
`Main.*` (Config, Input, DPI, fonts, `TAE_EDITOR`), `zzz_DocumentManager.CurrentDocument`, `OSD.*` (window instances, focus flags `AnyFieldFocused`/`Focused`), `MenuBar`, `GFX`, `DBG`, `InternalSimTemplate.Instance`, `zzz_NotificationManagerIns`, `ParamManager.AutoParamReloadEnabled`, `GameData.EnableFileCaching`.

---

## 2. Hook points (feature → DSAS hook → risk)

General rule: add one static seam file to DSAS, `DSAS/SoulsAiStudio/SasHooks.cs`. Each DSAS edit is a one-line `SasHooks.X?.Invoke(...)` marked `// [SAS]`.

| Feature (CLAUDE.md) | Hook / reuse in DSAS | Risk |
|---|---|---|
| **New ImGui panels** (Lua editor, goal navigator, combo graph, inspector, entity list) | Subclass `DSAnimStudio.ImguiOSD.Window` from our own namespace. Register with one `// [SAS]` line in `OSD.Build` after `:482`: `SasHooks.BuildWindows(ref actualFocusedWindow, ref currentFocusedWindow, ref anyWindowHovered, ref anyFieldFocused)`. Our registry owns the list, so do **not** add more fields to `OSD`. | **Low.** One line in a file that changes often upstream. Open-state persistence will not pick up our windows (it uses reflection over OSD fields), so persist our own state in `.sasproj`. |
| Menus ("Souls AI" menu, New/Open project) | Add one `// [SAS]` line in `MenuBar.BuildMenuBar` (`MenuBar.cs:53`) that calls `SasHooks.BuildMenus(...)`. Hide DSAS menus behind a single `SasHooks.HideDsasMenu(name)` check rather than deleting them. | Low |
| **Fixed default layout** (§5b) | Ship our own `imgui_default_SAS.ini` and switch `OSD.LoadDefaults` to it (`OSD.cs:20-41`), **or** build the layout in code. ImGui.NET 1.87 does **not** bind `DockBuilder*`, but `cimgui.dll` exports `igDockBuilderSplitNode` and `igDockBuilderDockWindow`, so a 20-line `[DllImport("cimgui")]` adapter works. | Medium. The ini approach is fragile because window titles are the IDs; the DockBuilder approach is safer. |
| **Lua editor keyboard focus** | When the editor has focus it **must** set `anyFieldFocused = true`. Otherwise the TAE shortcuts fire alongside it, for example Ctrl+Z, Ctrl+C/V and Delete (`TaeEditorScreen.cs:3768-4083`, gated on `OSD.AnyFieldFocused`). Also check that Space does not toggle playback (`Graph.UpdatePlaybackCursor(allowPlayPauseInput: Main.Active)`, `:3753`). | **High** if this is missed: it causes silent TAE edits while the user types Lua. |
| **TAE whitelist** (move/resize all, value only for SpEffect, copy/paste/delete, undo) | Add a policy class `SasTaePolicy` (in Core, driven by `GameProfile`) and gate the DSAS mutation sites with `// [SAS] if (!SasHooks.TaePolicy.CanX(act)) …`:<br>• drag/resize: `NewGraphInput.cs:243` (extend the existing `isReadOnly` check to per-action) and `:611-660`<br>• parameter edits: `Window.Parameters.cs` (6 `NewAction` sites, `:650-891`). Show read-only fields when `!CanEditParam(act, fieldName)`.<br>• add new event / change type: `DoAddNewEventAtMouse` (`NewGraphInput.cs:1074`), `ChangeTypeOfEvent` (`TaeEditorScreen.cs:2478`), Insert key (`:3801`), track/anim/category edits (`DialogManager.cs`, `TaeEditorScreen` 11 sites), all denied<br>• copy/paste/delete: keep `DoCopy/DoPaste/DoCut` and Delete (`:4083`) | **Medium.** 35 call sites. Missing one leaks a forbidden edit. Mitigation: a debug assertion in `TaeUndoMan.NewAction` (one `[SAS]` line) that rejects actions not tagged by the policy. |
| Event colours, dimming non-editable events, JumpTable highlight | `NewGraphDrawer.cs` draws boxes (`:269-462`, highlight logic). Add one `SasHooks.GetActionTint(act)` call. `TaeEditorScreen.RequestHighlightAction(act)` (`:113`) already exists. | Low |
| **Undo across Lua + TAE** (combo builder "1 action = 1 undo") | DSAS undo is **per animation** and snapshot-based. Do not try to extend it. Our own `UndoStack` lives in Core: a TAE command calls `TaeEditorScreen.GetUndoManForSpecificAnim(anim).NewAction(...)`, and our composite command records "undo N steps on anim X + Lua edit". | **High.** The design needs a decision before P2. Per-anim stacks plus a global stack plus our Lua stack confuse users. Pick one Edit→Undo entry point that is owned by SAS. |
| Timeline jump from a Lua `AddSubGoal` | `TAE_EDITOR.GotoAnimID(SplitAnimID, scrollOnCenter, …)` (`TaeEditorScreen.cs:1981`) and `HardReset(startPlayback)` (`:3641`) | Low |
| **Attack inspector: params** | **Do not reuse `ParamData`.** It uses byte offsets, skips SpEffect fields, depends on `CurrentDocument`, and cannot be tested. Load the bytes in Core with SoulsFormats `PARAM` plus Paramdex `PARAMDEF` (`PARAM.ApplyParamdef`, `SoulsFormats/Formats/PARAM/PARAM/PARAM.cs:309`). Get the bytes from DSAS's VFS or from our own VFS. Reuse only the **ID formulas** (BehaviorParam `2VVVVVJJJ`, the NpcParam chr match) by porting them into `GameProfile.Sekiro`. | Low/Medium. We end up with two param models in memory, which is acceptable because Sekiro gameparam is small. |
| **Attack inspector: hitbox overlay** | Reuse the DSAS sim: set the playback time and the simulation draws hitboxes from TAE events. For an AtkParam picked from Lua that has no TAE event, call `NewDummyPolyManager.ShowAttackOnDummyPoly(...)` (`:155`) or `HitboxSlot`. The DSAS `AtkParam` must exist in `ParamManager.AtkParam_Npc` (it is loaded for SDT). | Medium. Hitbox state is coupled to the sim's per-frame clear. |
| Range rings / distance overlay | Add one `// [SAS]` line in `DBG.DrawPrimitives` (`DBG.cs:362`) that calls `SasHooks.DrawOverlays()`, then draw with `ImGuiDebugDrawer.DrawLine3D`. | Low |
| **Project (.sasproj)** | **Do not reuse DSAProj.** It is a binary TAE model, not a project. Reuse **`TaeProjectJson` semantics only** (game dir, ModEngine dir, loose params, unpacked files). On project open, push them into DSAS through `GameRoot.SetInterrootPickerPathsManually(projectDir, gameDir, modengineDir, loadLooseParams)` (`zzz_GameRootIns.cs:534`) and then open the anibnd. The `.dsaproj` sidecar will still appear next to the anibnd, so decide whether that is acceptable or redirect `GetDSAProjFileName` into `cache/` (one `[SAS]` edit). | **Medium.** DSAS also writes `_DSAS_WORKSPACE.json`, `_DSAS_CACHE` and `_DSAS_TEMP_UNPACK` into the game dir. This breaks "never write to the game folder" (it is the cache, not game data, but still a write). |
| **Safe save** (atomic, mod folder only, backup) | Gate `TaeFileContainer.NewSaveContainer` at the `File.WriteAllBytes` line (`TaeFileContainer.cs:715`) with `// [SAS] SasHooks.WriteBinderSafely(path, bytes)`. Core then checks that the path is under the mod folder, backs it up, writes to a temp file and renames. | **High** until this is done: DSAS will happily overwrite files in the game dir. |
| TAE round-trip test (load → save → identical bytes) | DSAS saves by **re-generating** TAE from DSAProj (`DSAProj.cs:600-735`), so byte-identical output is not guaranteed. Run the round-trip test in Core with SoulsFormats `TAE` (`SoulsFormats/Formats/TAE`) for reading and analysis. Accept "semantic identical" (same events, times and params) for DSAS-written files. | Medium |
| File watcher | Reuse the pattern of `ParamManager.InitAutoReload` (FileSystemWatcher, flag, poll on the main thread). Implement it in Core `Watch/`. | Low |
| Entity names | `zzz_FmgManagerIns` (FMG loading per document) | Low |
| Live refresh | See §3c. Not needed for P0–P2. | — |

---

## 3. Answers to the open questions in CLAUDE.md §10

### (a) Sekiro TAE event IDs: `DSAS/Res/TAE.Template.SDT.xml`, plus logical names from `DSAS/EmbRes/InternalSimulationTemplate.xml`
Field offsets are measured from the start of the parameter block.

| ID | Template name (SDT) | DSAS sim name | Fields (offset type name) | SDT.xml line |
|---|---|---|---|---|
| **0** | `ChrActionFlag` | `JumpTable` | `0x0 s32 FlagType` (sim: `JumpTableID`), `0x4 f32 ArgA`, `0x8 s32 ArgB`, `0xC u8 ArgC`, `0xD u8 ArgD`, `0xE s16 StateInfo` | 3-36 |
| ↳ value **23** | **`23: End If AI ComboAttack Queued`** (the AI combo window) | | other relevant values: 4 End If RH Attack Queued, 7 Disable Turning (the sim disables tracking on 7, `TaeActionSimulationEnvironment.cs:477`), 11 LS Move, 16 LH Attack, 22 Guard, 26 Dodge, 12 Death | 26 |
| 300 | `ActivateJumpTableEarly` | `JumpTableEx` | `0x0 s16 JumpTableID_ToActivateEarly`, `0x2 s16 JumpTable2ID_ToJudgeHowEarly` | 945 |
| **16** | `Blend` | — | none (duration only) | 196 |
| **67** | `AddSpEffect` | `AddSpEffect` | `0x0 s32 SpEffectID` | 283 |
| 66 | `AddSpEffect_Multiplayer` | same | `0x0 s32 SpEffectID` | 277 |
| 302 | `AddSpEffect_DragonForm` | same | `0x0 s32 SpEffectID` | 966 |
| 331 | `AddSpEffect_WeaponArts` | — | `SpEffectID`, `SpEffectID_LowFP` | 1064 |
| 401 | `AddSpEffect_Multiplayer_401` | — | `0x0 s32 SpEffectID` | 1082 |
| 797 | `AddSpEffect_CultRitualCompletion` | — | `0x0 s32 SpEffectId` (note the lowercase d) | 1666 |
| 940 | `BehaviorParam_AddSpEffect` | — | `0x0 s32 BehaviorJudgeId`, an *indirect* SpEffect through BehaviorParam (RefType 2) | 1825 |
| **1** | `AttackBehavior` | `InvokeAttackBehavior` | `0x0 s32 AttackType`, `0x4 s32 (Index)`, `0x8 s32 BehaviorJudgeID`, `0xC u8 DirectionType`, `0xD u8 Source`, `0xE s16 StateInfo` | 137 |
| **2** | `BulletBehavior` | `InvokeBulletBehavior` | `0x0 s32 DummyPolyID`, `0x4 s32 Index`, `0x8 s32 BehaviorJudgeID`, `0xC u8 AttachmentType`, `0xD b Enable`, `0xE s16 StateInfo` | 155 |
| 4 | `BulletBehavior_Midair` (SDT only) | `InvokeBulletBehavior_Type4` | same layout as 2 | 168 |
| 5 | `CommonBehavior` | `InvokeCommonBehavior` | `0x0 s32 AttackIndex`, `0x4 s32 BehaviorJudgeID` | 181 |
| 304 | `ThrowAttackBehavior` | `InvokeThrowDamageBehavior` | `0x2 s16 Index`, `0x4 s32 BehaviorJudgeID` | 972 |
| 224 / 706 | `SetTurnSpeed` / `ChrTurnSpeed_ForLock` | — | turn speed | 781 / 1260 |
| 782 | `AiReplanningCtrlReset` | — | | 1597 |
| 785 | `SpawnChrFinderBullet` | — | `BulletID` | 1603 |

DSAS treats SpEffect events as types **66, 67, 302 and 401**, plus **331 and 797** for SDT (`TaeActionSimulationEnvironment.cs:1268-1279`).

Recommendation for `GameProfile.Sekiro`:
- **SpEffect value-editable = {67}**. Make the others opt-in.
- **SpEffect sources for the combo index = {66, 67, 302, 331, 401, 797}**, plus 940 resolved through BehaviorParam.
- **JumpTable combo = type 0 where `s32@0x0 == 23`**.
- Address fields by **offset and type**, not by name. Names are inconsistent across templates (`SpEffectID`/`SpEffectId`, `BehaviorJudgeID`/`BehaviorJudgeId`).

§10 Q5 (SpEffect fields in AtkParam, Bullet and BehaviorParam) is **not answerable from DSAS**:
- AtkParam: DSAS's comments show `spEffectId0..4` (5 × s32), right after the SDT-only 2×f32 (`ParamData.AtkParam.cs:478-494`).
- BehaviorParam: `refType` 2 means SpEffect via `refId`.
- Bullet: DSAS has no reader.

Take the authoritative names from the Paramdex `SDT` paramdefs. For Bullet, look for `HitSpEffectId` and `spEffectId0..4`, and verify them against the paramdef.

### (b) ImGui.NET version, text editor widget
- `Lib/ImGui.NET.dll` is **v1.87.3** (AssemblyVersion 1.87.3.0). `Lib/ImGui.NET_old.dll` is 1.78 and unused. The native `cimgui.dll` is the docking build: it exports `igDockSpace` and `igDockBuilder*`, but the managed side does not bind `DockBuilder*`.
- **There is no code-editor widget.** `InputTextMultiline` is bound but never used. The only text inputs are single-line fields with `CallbackAlways` (`Dialog.TextInput.cs:52`, `Dialog.TagPicker.cs:138`, `Tools.cs:292`). The Lua editor is a from-scratch widget: either port ImGuiColorTextEdit to C# or write one on `ImDrawList`. It remains the top risk, as CLAUDE.md says.
- A newer cimgui with the C++ ImGuiColorTextEdit is **not** an option without rebuilding `cimgui.dll`. Avoid that because it changes a native binary that upstream also ships.

### (c) Live refresh for Sekiro
- **In-game:** yes, character reload only. `LiveRefresh/RequestFileReload.cs:117-160` attaches to `sekiro`, writes a flag at `BaseAddress+0x3D7A34F` and injects a call to `0x140A4AC60` with the chr name. These are hardcoded addresses for one exe version. It is triggered by `TaeEditorScreen.LiveRefresh()` (`:1854`), which runs on save (`:1336`) or from the File menu (`MenuBar.000.File.cs:216`). It reloads the chr (anibnd/TAE), **not Lua**. That is consistent with CLAUDE.md §12 (deferred). Do not depend on it.
- **In-app:** only the param watcher (`zzz_ParamManagerIns.cs:90-170`). Nothing watches anibnd, chrbnd or Lua.

### (d) Licenses
- The **root `LICENSE` is GPLv3** (DSAS since commit `0aab63c`). `SoulsAssetPipeline/LICENSE` is GPLv3 too, and it includes SoulsFormats. The whole fork is therefore **GPLv3**: new code must be GPLv3-compatible and the source must be published with any binary.
- `DSAS/LICENSE/DS ANIM STUDIO LICENSE.txt` still says **MIT 2018-2022 Meowmaritus**. It is stale and shipped to the output. Keep it as historical credit, but state in README and THIRD_PARTY that the fork is GPLv3.
- Bundled notices in `DSAS/LICENSE/`:
  - AssimpNet, BouncyCastle, Newtonsoft.Json, NAudio, RawInput.Sharp, SharpDX, TeximpNet, Havoc (Skyth) and Dear ImGui are MIT.
  - MonoGame is Ms-PL. Ms-PL is GPL-**incompatible** for combined source distribution, but MonoGame is used as a separate DLL. Keep its license text and do not copy MonoGame source into our files.
  - vgmstream is ISC-like.
  - **FMOD Ex / fsbankcl is proprietary** (`Res/fsbankcl/LICENSE.TXT`; `fmod*.dll` and `fsbank.dll` are in `DSAS/`). It allows non-commercial use only. **Do not sell or commercially distribute** the build. Consider dropping the sound features from SAS builds.
  - Qt5 (inside fsbankcl) is LGPL.
- Planned additions:
  - MoonSharp is MIT and NLua is MIT, both fine.
  - DSLuaDecompiler: check its license. Calling it as an external process avoids linking questions.
  - Paramdex XML (from SoulsFormats/Smithbox) is GPL/MIT depending on source. Record the source commit in `THIRD_PARTY.md`.
  - Oodle `oo2core_6_win64.dll` is **never redistributed**. Load it from the user's game dir, as DSAS already does.

---

## 4. Code quality of the DSAS parts we touch, and conventions for new code

### 4.1 Assessment (pragmatic: we are not fixing DSAS)
- **God classes:** `TaeEditorScreen` (4560 lines), `Main` (2997), `DSAProj` (2022), `zzz_GameRootIns` (1980), `Window.SceneWindow` (1764), `NewGraphInput` (1600). Changes here conflict with upstream, so keep each edit to one line.
- **Ambient singletons:** document-scoped services are reached through the static `CurrentDocument` (612 references), including from **data readers** (`ParamData.*.Read`). Any code that runs during a document switch, or on a loading thread, can read the wrong game type. Avoid DSAS services outside the main thread.
- **Threading:** MonoGame's synchronous loop runs on the UI thread. `LoadingTaskMan` runs tasks on raw `Thread`s (`zzz_LoadingTaskManIns.cs:95`). Shared state is protected by coarse locks (`_lock_CurrentDocument`, which is held for the **whole Draw**, `Main.cs:2539`) and by `WinForm.Invoke` inside undo. A background task that takes `_lock_CurrentDocument` stalls rendering. Ours must never take DSAS locks; marshal results back with `Main.MainThreadLazyDispatch`.
- **Error handling:** much of it is `catch { }` or `MessageBox.Show` deep inside logic (`Main.cs:2883-2890`, `zzz_ParamManagerIns.cs:690`). Do not copy this.
- **UI as code + MonoGame input:** the TAE graph reads raw input, so ImGui focus rules do not protect it. See the focus risk in §2.
- **No nullable annotations and no tests.** The only test is `Tests.cs` (debug code). Treat every DSAS reference as possibly null.
- **Dead code:** there is a lot of commented-out code and `OLD_*` files. Ignore it and do not clean it up (that would create merge noise).

### 4.2 Conventions for SAS code (top 5 first)
1. **Hard layering.**
   - `src/SoulsAiStudio.Core` (net9.0, no MonoGame, ImGui, WinForms or DSAS reference) holds all analysis, the project, VFS, params, TAE read, undo model, Lua and GameProfile.
   - UI and glue go in **`SoulsAIStudio/SoulsAiStudio/**`** with namespace `SoulsAiStudio.App`. The SDK globs it automatically, the folder is new so it causes no merge conflict, and it can see DSAS internals.
   - Do not create a separate App exe yet. That would need DSAS refactored into a library: `Main.Directory`, embedded resources (`Main.GetEmbeddedResourceText` assumes the `DSAnimStudio.` prefix) and content copying all break.
   - DSAS gets exactly one new `ProjectReference` to Core (one `[SAS]` line in the csproj).
2. **A thin adapter in front of DSAS statics.** `SoulsAiStudio.App/DsasBridge/*`:
   - `IDsasSession` with `CurrentGameType`, `OpenAnibnd(path)`, `GotoAnim(id)`, `PlayFrom(t)`, `ReadGameFile(relPath)` and `ParamBytes(name)`.
   - `ITaeEditGateway`, which wraps `GetUndoManForSpecificAnim().NewAction`.
   - `IViewportOverlay`.
   - Core defines the interfaces it needs (ports). App implements them against `zzz_DocumentManager.CurrentDocument` / `Main.TAE_EDITOR`.
   - Only the bridge may touch `zzz_*`, `Main.*` and `OSD.*`. This keeps CurrentDocument access in one folder and lets Core tests use fakes.
3. **DSAS edits go through `SasHooks` only.** `DSAS/SoulsAiStudio/SasHooks.cs` holds static delegates and policies such as `BuildWindows`, `BuildMenus`, `DrawOverlays`, `TaePolicy`, `WriteBinderSafely` and `GetActionTint`. Every touched DSAS line is `SasHooks.X?.Invoke(...); // [SAS] reason`. To merge upstream: `git grep "\[SAS\]"` lists the full surface. Target fewer than 15 edited lines across fewer than 10 DSAS files for P0–P1.5.
4. **Composition root, constructor injection, no new statics.**
   - One `SasApp` object is built at startup and owns the services: `ProjectService`, `BackupService`, `FileWatchService`, `ParamIndex`, `LuaWorkspace`, `UndoStack`, `GameProfile`.
   - Windows receive their services through the constructor.
   - `SasHooks` is the only static, and it just forwards to `SasApp`.
   - No DI container is needed. Plain constructors are enough.
5. **Commands for every edit, with one SAS-owned undo stack.**
   - `IEditCommand { Do(); Undo(); string Label; }`, with `CompositeCommand` for "Lua + TAE" (combo builder).
   - TAE commands call the DSAS per-anim undo manager internally, so DSAS state stays consistent, but the user only sees **our** Edit→Undo.
   - While the SAS layout is active, the DSAS Undo menu items are hidden through `SasHooks`.
   - Lua edits are commands on the buffer, with coalesced typing.

Other conventions:
- **Nullable enabled, file-scoped namespaces, `sealed` by default, `record`s for the immutable models** (`AnimStep`, edges, resolved params).
- Async: load in Core with `async Task<T>` and a `CancellationToken`, never touching DSAS objects. Apply on the UI thread with `Main.MainThreadLazyDispatch`. Show progress with `doc.LoadingTaskMan.DoLoadingTask` only for DSAS-side loads.
- Debounce analysis (AST, combo graph, diagnostics) on a background task with an immutable snapshot of the Lua text. The UI reads the latest finished result (`volatile` reference swap), so no locks are needed.
- `GameProfile` is data, not code: Sekiro event IDs, the SpEffect type set, JumpTable flag 23, param names and field names, and the goal grouping rules, all in JSON or a C# record. Unit-test that the profile matches `TAE.Template.SDT.xml` (it can parse the XML from `DSAS/Res`).
- Naming: `Sas` prefix only on bridge and hook types. No `zzz_`, `New*` or `OLD_*` style. One public type per file.
- Testability seams:
  - an `IFileSystem` abstraction (or `System.IO.Abstractions`) for project, backup and save
  - a `TestData` helper that copies `test-data/original` to a temp dir and checks `SHA256SUMS.txt` before and after each test class
  - `[Trait("Category","GameData")]` on tests that need the real files or Oodle, with a skip when they are missing
- Read-only discipline: Core's `ModFolderWriter` is the only class allowed to write game-adjacent files. It asserts `path.StartsWith(modRoot)`, and a unit test proves it refuses the game dir.

---

## 5. Skeleton and runtime review

### 5.1 `src/SoulsAiStudio.Core/SoulsAiStudio.Core.csproj`
- Good: `net9.0` (no `-windows`), `Nullable`, `ImplicitUsings`, a reference to **SoulsFormats only**. Keep it that way:
  - `SoulsAssetPipeline` is `net9.0-windows` (`SoulsAssetPipeline.csproj:4`) and pulls in Havoc, AssimpNet and TeximpNet natives. Avoid it in Core.
  - The DSAS TAE template format (`<action_template>`) is parsed only by SAP's `TAE.Template`. SoulsFormats' `TAE.Template` expects `<event_template>`. For Core, either read the needed events from `GameProfile` (offsets above) or write a ~60-line reader for `action_template`.
- Add `<PlatformTarget>x64</PlatformTarget>` (or put it in a `src/Directory.Build.props`). The bench already notes that the SoulsFormats Release build targets x64, and an AnyCPU consumer produces MSB3270 warnings or mismatches.
- Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (or at least `WarningsAsErrors=nullable`) **scoped to src/ and tests/** through `Directory.Build.props` in those folders, not at the repo root. Our code then stays at zero warnings while the ~4000 DSAS warnings are left untouched.
- `public static class AssemblyMarker;` compiles on C# 12+ and is fine.

### 5.2 `tests/SoulsAiStudio.Core.Tests`
- xunit 2.9.2 and `RollForward=LatestMajor` are fine. Also add `PlatformTarget x64`. Remove the empty `<PropertyGroup>`.
- Add `InternalsVisibleTo` from Core when needed.

### 5.3 Solution and repo
- The `.sln` changes add the "SoulsAiStudio" solution folder. Check the configuration mapping: DSAS uses `Debug|x64`, so the new projects must build under the x64 solution platform. Confirm in the `.sln` `ProjectConfigurationPlatforms` section.
- `tools/SoulsAiStudio.Bench` is outside the sln, which is acceptable. It references SoulsFormats only (good).
- `.gitignore` adds `test-data/` (good). Also ignore `**/_DSAS_*` and `*.dsaproj` in case anyone works inside the repo.

### 5.4 .NET 9 vs 10 runtime
The machine has SDK 10.0.401 and runtimes 6.0.36, 7.0.20 and 10.0.12. The 7.0 runtime lets DSLuaDecompiler run as an external process. **There is no 9.0 runtime**, so the DSAS exe (`net9.0-windows7.0`, no RollForward) **will not start** as it is.

| Option | Upstream merge cost | Verdict |
|---|---|---|
| A. Install the .NET 9 Desktop Runtime (x64) | none | Works, but .NET 9 is STS. Its support ended around **2026-11-10**, after the STS extension to 24 months (verify the date). It is acceptable for local dev only, so do not make it a user requirement. |
| B. RollForward without editing DSAS files: add `SoulsAIStudio/runtimeconfig.template.json` with `{"rollForward":"LatestMajor"}` (a new file, so no conflict), or a root `Directory.Build.props` with `<RollForward>LatestMajor</RollForward>`. Prefer the template file: it scopes to the exe and does not touch MonoGame or SAP. | ~0 | **Recommended for shipping until upstream moves.** MonoGame WindowsDX, SharpDX and WinForms run on .NET 10. Smoke-test: open a Sekiro anibnd, save, and check hitboxes. |
| C. Retarget to net10.0 | High: DSAS csproj, the **SoulsAssetPipeline submodule** (SAP `net9.0-windows`, SoulsFormats `net9.0`, Havoc `net6.0-windows`) and MonoGame | **Not now.** Do it only when upstream DSAS retargets. Then follow upstream. |

Keep `src/` and `tests/` on **net9.0** so they match DSAS. Core is referenced by the DSAS exe, so its TFM must be ≤ the exe's TFM. `RollForward` on the test and bench hosts is right.

---

## 6. Top risks (ranked)
1. **The Lua code editor widget.** No ImGui text editor exists, and the DSAS input and focus model fights with typing (shortcut bleed-through: Ctrl+Z, Delete, Space). Prototype it first, and wire `anyFieldFocused` from day one.
2. **Writes to game files.** DSAS saves the anibnd in place with a non-atomic `File.WriteAllBytes`, and writes `_DSAS_*` caches and `.dsaproj` next to game files. Hook the save and redirect it before any user-facing TAE editing.
3. **Undo model mismatch.** DSAS keeps per-anim snapshot stacks; CLAUDE.md wants a single cross-artifact undo. This needs an explicit design (an SAS-owned command stack wrapping DSAS) before the P2 combo builder.
4. **TAE whitelist leakage.** 35 mutation sites across 5 files. Use a central policy, plus a debug assertion in `TaeUndoMan.NewAction`.
5. **Param data gap.** The DSAS readers do not expose SpEffect fields, Bullet or NpcThinkParam. The OnHit and SpEffect index need a paramdef-based loader in Core (Paramdex SDT). Do not grow `ParamData`.
6. **Upstream churn** in `OSD.cs`, `MenuBar.cs`, `TaeEditorScreen.cs` and `NewGraphInput.cs`. Keep `[SAS]` edits to one line each and merge upstream often.
7. **FMOD licensing:** non-commercial only.
