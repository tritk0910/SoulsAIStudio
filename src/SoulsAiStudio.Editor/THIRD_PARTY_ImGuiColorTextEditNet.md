# Third-party: ImGuiColorTextEditNet

Parts of `SoulsAiStudio.Editor` are derived from **ImGuiColorTextEditNet**, a C# port of
BalazsJako's ImGuiColorTextEdit.

- Upstream: https://github.com/csinkers/ImGuiColorTextEditNet
- Copied from: tag `0.1.9`, commit `2a6d4feb9f1a0f68acae15012d22be3ca7c74497` (2025-10-06)
- Original C++ project: https://github.com/BalazsJako/ImGuiColorTextEdit

## What was taken and what was changed

| Our file | Upstream origin | Changes |
|---|---|---|
| `Text/Coordinates.cs` | `Coordinates.cs` | Column is a UTF-16 index in the line (no tab expansion). Trimmed API. |
| `Text/Line.cs`, `Text/TextBuffer.cs` | `Line.cs`, `Editor/TextEditorText.cs` | Glyph list split into char + color lists. Insert/delete rewritten around one primitive. Keeps the file's line ending (LF/CRLF). Line insert/remove events kept. |
| `Text/UndoStack.cs` | `Editor/TextEditorUndoStack.cs`, `Operations/UndoRecord.cs` | One record type (replace range). Typing runs merge. Clean/dirty save point. |
| `Text/EditorCore.cs` | `Editor/TextEditorModify.cs`, `TextEditorMovement.cs`, `TextEditorSelection.cs` | Rewritten as anchor/cursor selection. All edits go through `ReplaceRange`. No ImGui dependency, so it can be unit-tested. Clipboard is behind `IClipboard`. |
| `Syntax/Colorizer.cs` | `Editor/TextEditorColor.cs` | Same idea (incremental per-line colorize with carried state). Stops early when the state settles. |
| `Rendering/EditorView.cs`, `LuaCodeEditor.cs` | `Editor/TextEditorRenderer.cs`, `Input/Standard*Input.cs` | Rewritten for ImGui.NET 1.87.3, zero allocations per frame (native UTF-8 draw calls), a fixed gutter, diagnostics squiggles, find/go-to bars, and keys through `IKeyInput`. |

`Syntax/LuaLexer.cs`, `Text/DiagnosticStore.cs`, `Text/FindModel.cs` and `Input/*` are new code.

## License (MIT)

```
MIT License

Copyright (c) 2022 CSinkers (C# port)
Copyright (c) 2017 BalazsJako (original project)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The rest of this repository is a fork of DS Anim Studio and is licensed GPLv3.
MIT code can be included in a GPLv3 work; this notice must be kept.
