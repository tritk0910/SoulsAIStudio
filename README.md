<p align="center"><img src="Resources/Branding/jizo_statue_256.ico" width="96" alt="Souls AI Studio icon"></p>

# Souls AI Studio

A desktop tool for **reading, understanding and editing enemy AI (Lua) in Sekiro: Shadows Die Twice**, built on top of
[DS Anim Studio](https://github.com/Meowmaritus/DSAnimStudio) by Meowmaritus.

DS Anim Studio is great at animations (TAE events, model viewer), but enemy behaviour lives in Lua AI scripts, and combos
in Sekiro branch in ways that are hard to follow from the code alone. A goal queues anims; a SpEffect fired from an anim's
TAE triggers `Goal.Interrupt`, which clears the queue and starts a new random chain. Souls AI Studio puts the Lua editor,
the TAE timeline and the 3D viewport side by side so you can see exactly what the AI does.

> Status: early pre-release (v0.1.x). **Sekiro only.** Back up your mod before trying it, even though the app takes
> backups itself (see [Safety](#safety)).

## Features

### Projects
- **New Project wizard**: pick the Sekiro folder, your Mod Engine mod folder, then the AI scripts you work on (search by
  NPC id or map). Everything is remembered in a `.sasproj` file; reopen it from the start screen's Recent list.
- **Animations only** projects: no Lua needed, just pick characters (`c5060`, `c5400`, …) to edit their animations quickly.
- Layouts: **AI** (Lua Editor next to the Graph) or **Animation** (Lua Editor hidden), switchable any time.

### Lua AI editor
- Opens scripts straight from the map's `.luabnd.dcx`, plain text or compiled. Compiled (bytecode) scripts are decompiled
  on first open with the bundled DSLuaDecompiler, which gives readable argument names.
- Syntax highlighting, find (Ctrl+F), go to line (Ctrl+G), undo/redo, and a goal outline (Act, Kengeki, Interrupt…) with
  the number of `AddSubGoal` calls and the anim ids of each goal.
- Click an anim id in the code to play it in the viewport.
- **Ctrl+S** saves into your mod: the repacked `.luabnd.dcx` (same compression and ids as before) and, if you keep an
  unpacked WitchyBND/Yabber folder, the `.lua` in it too.

### Combo player
- **From Lua**: pick a goal and it plays the whole combo in the viewport, cutting each anim at its
  `End If AI ComboAttack Queued` window the way the game does.
  - Random branches (`GetRandam_Int` thresholds) are shown with their percentages; choose one or let **Random path** roll them.
  - **SpEffect interrupts**: when an anim's TAE fires a SpEffect handled in `Goal.Interrupt`, the combo branches into the
    handler's chain (with `ClearSubGoal` replacing the queue). Distance-based handlers are never auto-picked; you choose
    the trigger frame.
- **Manual**: type a combo in the DS Anim Studio 4.9.9 combo-script syntax, plus `SpEffect <id>` cuts. Includes examples
  and a cut-point picker. Replaces DS Anim Studio's Combo Viewer (Tools → Combo / F8).

### Animation (TAE) editing
- Everything DS Anim Studio does for Sekiro TAE, plus:
  - an animation's unsaved `*` mark disappears when Undo brings it back to the saved state;
  - StateInfo ON/OFF toggle next to StateInfo fields;
  - root-motion blending and time-cursor scrubbing like DS Anim Studio 4.9.9.
- Characters that only exist in the game folder can be copied into your mod with one click (**Copy to mod**), since the
  game folder is never written to.

### Luabnd unpack / repack
- **Souls AI → Luabnd (Unpack / Repack)**: unpack a map's `.luabnd.dcx` into a WitchyBND-format folder, or repack a
  WitchyBND (`_witchy-bnd4.xml`) or Yabber (`_yabber-bnd4.xml`) folder into the mod's `.luabnd.dcx`, without leaving the
  app. Shows which maps have folder edits that still need repacking.
- Built in: no WitchyBND/Yabber install needed.

### Viewport
- DS Anim Studio's model/animation viewer, with an optional DS Anim Studio 4.9.9-style lighting preset (side light, tonemap
  2 / 0.5, cubemap skybox). Toolbox lighting values persist between launches.

## Safety

- **Writes only into your mod folder**, never into the game folder. Saves are atomic (temp file, then rename).
- Before every save the overwritten file is backed up in the project's `backups/` folder:
  - `backups/original/` keeps the first version ever seen;
  - `backups/<timestamp>/` keeps one snapshot per save.
- Saving over a read-only file is refused instead of silently clearing the flag.

## Requirements

- Windows 10/11, 64-bit, with a DirectX 11 capable GPU.
- [.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) or newer.
- Sekiro unpacked with [UXM](https://github.com/JKAnderson/UXM), and a [Mod Engine](https://www.nexusmods.com/sekiro/mods/6)
  mod folder for your changes.
- Oodle (`oo2core_6_win64.dll`) is needed for some archives (`DCX_KRAK`). It is **not** shipped; the app loads it from your
  Sekiro install.

## Getting started

1. Download the latest release zip, extract it anywhere, run `Souls AI Studio.exe`.
2. Start screen → **New Project…**
   - **AI scripts + animations**: set the Sekiro folder and your mod folder, tick the scripts (e.g. filter `506300` for
     Inner Owl), name the project, **Create Project**.
   - **Animations only (no Lua AI)**: same folders, tick characters instead.
3. The script opens in the Lua Editor and its character (model + animations) loads in the viewport.
4. Open **Window → Combo Player** (or Souls AI → Combo Player), pick a goal and press Play.
5. Edit, **Ctrl+S**, and test in game through Mod Engine.

Settings and the recent-project list live in `%AppData%\SoulsAiStudio\`.

## Building from source

```
git clone --recursive https://github.com/tritk0910/SoulsAIStudio.git
dotnet build SoulsAIStudio/SoulsAIStudio.csproj -c Release
dotnet test tests/SoulsAiStudio.Core.Tests
```

- Needs the .NET 9 SDK. The app is `SoulsAIStudio/bin/Release/net9.0-windows7.0/Souls AI Studio.exe`.
- To ship it, `dotnet publish SoulsAIStudio/SoulsAIStudio.csproj -c Release -o <folder>` and zip the folder.
- `src/SoulsAiStudio.Core` holds the UI-free logic: project, luabnd I/O, Lua parsing and combo analysis.
- New UI lives in `SoulsAIStudio/SoulsAiStudio/`. Changes to DS Anim Studio's own files are one-line hooks marked `// [SAS]`.
- Many tests need local game data (`test-data/`, not in the repo) and are skipped without it.

## Credits

Souls AI Studio would not exist without **[DS Anim Studio](https://github.com/Meowmaritus/DSAnimStudio) by Meowmaritus**.
The whole viewport, TAE editor, Havok/FLVER loading and UI shell are DS Anim Studio's work.

DS Anim Studio's own thanks:
- [TKGP](https://github.com/JKAnderson): TAE format discoveries and SoulsFormats.
- [Pav](https://github.com/JohrnaJohrna): tons of TAE events mapped across all the games.
- [Katalash](https://github.com/katalash): much help with animation file understanding.
- [PredatorCZ](https://github.com/PredatorCZ): reverse engineered Havok spline-compressed animation.
- [Horkrux](https://github.com/horkrux): PS3/PS4 texture header and swizzling.
- StaydMcButtermuffin: shaders, and reversing DS3/Sekiro/Elden Ring shaders.

Libraries and tools:
- [SoulsAssetPipeline](https://github.com/Meowmaritus/SoulsAssetPipeline) and [SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT)
- [DSLuaDecompiler](https://github.com/katalash/DSLuaDecompiler) by katalash, build from the
  [ElaDiDu fork](https://github.com/ElaDiDu/DSLuaDecompiler) (MIT), bundled in `Tools/DSLuaDecompiler`.
- [ImGuiColorTextEditNet](https://github.com/csinkers/ImGuiColorTextEditNet) (MIT), adapted for the Lua editor.
- [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET), [Newtonsoft Json.NET](https://www.newtonsoft.com/json).
- An edited MonoGame Framework (from DS Anim Studio).
- Parts of [HavokLib](https://github.com/PredatorCZ/HavokLib) and [DeS-BNDBuild](https://github.com/horkrux/DeS-BNDBuild), adapted by Meowmaritus.

See [THIRD_PARTY.md](THIRD_PARTY.md) for the components Souls AI Studio adds.

## License

GPLv3, like DS Anim Studio (see [LICENSE](LICENSE)). Bundled third-party components keep their own licenses
(see [THIRD_PARTY.md](THIRD_PARTY.md)). Not affiliated with FromSoftware.
