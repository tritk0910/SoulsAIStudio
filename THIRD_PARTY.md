# Third-party components in Souls AI Studio

Souls AI Studio is a fork of [DS Anim Studio](https://github.com/Meowmaritus/DSAnimStudio) and is licensed GPLv3 (see `LICENSE`).
Components DS Anim Studio itself uses are credited in `README.md` ("Libraries Utilized").
Components added by Souls AI Studio:

| Component | Where | License | Notes |
|---|---|---|---|
| [DSLuaDecompiler](https://github.com/katalash/DSLuaDecompiler), (c) 2019 katalash; this build from the [ElaDiDu fork](https://github.com/ElaDiDu/DSLuaDecompiler) | `SoulsAIStudio/Tools/DSLuaDecompiler/` (unmodified binaries, copied next to the exe) | MIT (`Tools/DSLuaDecompiler/LICENSE`) | Run as a separate process to decompile bytecode AI scripts. Its `Resources/VariableNamePatterns.json` gives readable argument names. |
| System.CommandLine (used by DSLuaDecompiler) | same folder | MIT, (c) .NET Foundation and Contributors | |
| [ImGuiColorTextEditNet](https://github.com/csinkers/ImGuiColorTextEditNet) | `src/SoulsAiStudio.Editor/` | MIT | See `src/SoulsAiStudio.Editor/THIRD_PARTY_ImGuiColorTextEditNet.md`. |

Not distributed: Oodle (`oo2core_6_win64.dll`) is proprietary and is always loaded from the user's own Sekiro install.
