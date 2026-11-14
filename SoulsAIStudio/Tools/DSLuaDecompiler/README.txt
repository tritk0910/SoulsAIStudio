DSLuaDecompiler - HavokScript (Lua 5.0) bytecode decompiler for FromSoftware games.

Bundled with Souls AI Studio, unmodified binaries:
- Original author: katalash - https://github.com/katalash/DSLuaDecompiler
- This build: ElaDiDu fork - https://github.com/ElaDiDu/DSLuaDecompiler
  (its Resources/VariableNamePatterns.json gives readable argument / variable names)
- License: MIT, see LICENSE in this folder.
- System.CommandLine.dll and its resources: (c) .NET Foundation and Contributors, MIT License.

Souls AI Studio runs DSLuaDecompiler.exe as a separate process when a compiled (bytecode) script is opened for the
first time. It is started with DOTNET_ROLL_FORWARD=Major, so it uses .NET 7 if installed, otherwise the newer .NET
runtime that Souls AI Studio itself needs (9 or 10).
