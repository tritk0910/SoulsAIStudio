# Souls AI Studio — UI/UX Spec

> Trạng thái: **DRAFT v1** (2026-11-11). Phạm vi: P0 → P2 theo `CLAUDE.md` §5, §5b.
> Tài liệu viết bằng tiếng Việt; **chữ trên UI của app viết bằng tiếng Anh** (lý do ở §0.2).
> Mockup dùng tên goal / anim / SpEffect thật của Owl (Father) `506300_battle.lua`. Các param ID, damage, tên SpEffect trong mockup là **ví dụ minh họa**, không phải dữ liệu đã xác minh.

---

## 0. Tóm tắt

### 0.1 Quyết định chính
| # | Quyết định | Lý do ngắn |
|---|---|---|
| D1 | **Layout cố định do app tự tính** (custom splitter), không cho người dùng kéo/dock panel tự do. Panel chỉ resize và thu gọn. | §5b "không bắt người dùng tự sắp panel". Docking của DSAS cho phép kéo lung tung, dễ vỡ layout. |
| D2 | Giữ renderer timeline + viewport của DSAS (vẽ bằng MonoGame bên dưới window ImGui trong suốt), chỉ thêm **gating whitelist** và **màu theo loại event**. | Không viết lại; đúng nguyên tắc fork (§6). |
| D3 | Lua editor = **vendor source ImGuiColorTextEditNet (MIT) vào repo**, backport về ImGui.NET 1.87, thay highlighter bằng lexer Lua của mình, rồi mở rộng (find, autocomplete, folding, squiggle). | §3 của doc này. |
| D4 | Mọi panel có undo; **Ctrl+Z tác động lên panel đang focus**; thao tác chéo nhiều nơi (combo builder) = 1 lệnh trong "project command stack". | §5b + P2.13. |
| D5 | Không có dialog lồng nhau. Hỏi xác nhận bằng **banner/inline confirm** khi có thể; modal chỉ dùng cho xóa và thoát khi chưa save. | §5b. |
| D6 | Diagnostics 3 mức: **Error** (chỉ: parse lỗi, save/IO lỗi), **Warning**, **Info**. Nguồn SpEffect không tìm thấy luôn là **Info**. | P2 "Diagnostics combo". |

### 0.2 Ngôn ngữ UI: tiếng Anh
- Cộng đồng mod Souls (Discord, wiki, Paramdex, tên field, TAE template, tên `GOAL_COMMON_*`) đều tiếng Anh; người dùng tra cứu bằng tiếng Anh → nhãn UI trùng thuật ngữ gốc giảm "dịch ngược trong đầu".
- Font hiện tại của DSAS **chưa hiển thị được tiếng Việt** (xem §1.4) nên UI tiếng Việt sẽ cần sửa font trước.
- Đề xuất: mọi string UI đi qua 1 bảng `Strings` (key → text) ngay từ đầu, để sau này thêm `vi` mà không phải sửa code. **Không làm bản dịch ở P0–P2.**
- Thuật ngữ cố định (dùng thống nhất ở mọi nơi):

  | Khái niệm (CLAUDE.md) | Nhãn UI |
  |---|---|
  | khi trúng / OnHit | `On hit` |
  | cửa sổ JumpTable "End If AI ComboAttack Queued" | `Combo window` (tooltip ghi tên event gốc) |
  | SpEffect interrupt | `SpEffect branch` |
  | nhánh random | `Random 25%` |
  | không tìm thấy nguồn | `Source not found` (Info) |
  | Giữ của tôi / Lấy bản mới / Xem diff | `Keep mine` / `Take theirs` / `Compare` |

---

## 1. Audit UI hiện tại của DSAS

Đường dẫn tương đối từ `SoulsAIStudio/`.

### 1.1 Kiến trúc UI
- **ImGui.NET 1.87.3** (managed, `../Lib/ImGui.NET.dll`), native `cimgui.dll` là **bản docking branch, chuỗi version 1.88**. Target framework DSAS: `net9.0-windows7.0` (`SoulsAIStudio.csproj`).
- Native `cimgui.dll` **có export `igDockBuilder*`** nhưng wrapper managed 1.87 **không bọc** → muốn dùng DockBuilder phải tự `[DllImport]`.
- **Docking bật** (`ImguiOSD/OSD.cs:316`, `ImGuiRenderer.cs:96`): một window full-screen không viền `MainDockSpace` chứa `ImGui.DockSpace` (`OSD.cs:350-378`). Layout mặc định nạp từ ini nhúng `EmbRes/imgui_default_V5.ini` / `imgui_default_Legacy.ini` (`OSD.cs:20-41`), lưu ở `imgui.ini` cạnh exe.
- `io.ConfigWindowsMoveFromTitleBarOnly = true`, resize từ cạnh (`OSD.cs:318-319`).
- **Viewport 3D và TAE graph không vẽ bằng ImGui**: `Window.Viewport` và `Window.Graph` là window ImGui trong suốt `NoInputs` chỉ để lấy rect; MonoGame vẽ bên dưới (`ImguiOSD/Window.Viewport.cs`, `Window.Graph.cs`; graph vẽ bằng SpriteBatch trong `TaeEditor/NewGraphDrawer.cs`). Hệ quả: popup ImGui luôn nằm trên viewport/timeline (tốt cho dropdown), nhưng tooltip/overlay trong timeline phải vẽ bằng SpriteBatch hoặc bằng ImGui foreground draw list.
- Khi một WinForms form đang mở thì **toàn bộ GUI ImGui bị disable** (`OSD.cs:321`). DSAS còn nhiều form WinForms (`TaeEditor/*Form*.cs`, `TaeLoadFromArchivesWizard`, `TaeGameDirPicker`, `FormAskWhatGame`). Ta chỉ nên dùng WinForms cho **OpenFileDialog/FolderBrowser** hệ thống.
- Dialog: `ImguiOSD/DialogManager.cs` + `Dialog.*.cs` (modal ImGui: YesNo, TextInput, MultiChoice, Welcome…).
- Tooltip: `TooltipManager.cs`, `Window.Tooltip.cs` (tooltip có delay, nhiều instance theo vùng).

### 1.2 Bàn phím → ImGui (vấn đề quan trọng cho Lua editor)
- `ImGuiRenderer.UpdateInput` chỉ forward **một tập phím giới hạn** sang ImGui: modifier, Enter, Tab, mũi tên, PageUp/Down, Home/End, Delete, Backspace, Escape, **A C V X Y Z** (`ImGuiRenderer.cs:~287-312`). **Không có F1–F12, F, G, O, S, B, J, K, Insert, Alt+mũi tên…**
- Ký tự gõ đi qua `Window.TextInput` → `io.AddInputCharacter` (`ImGuiRenderer.cs:222-227`), **lọc bỏ `\t`**.
- Phím tắt của TAE editor đọc **trực tiếp từ XNA** (`Main.Input`), chỉ chặn bằng cờ `OSD.AnyFieldFocused` (`TaeEditor/TaeEditorScreen.cs:3794`). Nhiều phím trùng với phím editor:
  `Ctrl+F` (Find value), `Ctrl+G` (Go to anim ID), `Ctrl+H`, `Ctrl+I`, `Ctrl+J/K`, `Ctrl+S`, `Ctrl+R` (hard reset), `Ctrl+A/C/X/V` (event), `Alt+←/→` (anim history), `F1`–`F8`, `Delete`, `1`–`4` (mute/solo), `C`, `T`, `Ctrl+RightClick` (**thêm event mới** — vi phạm non-goal).
- ⇒ **Bắt buộc** (patch `[SAS]` nhỏ): (1) forward **đủ** phím sang ImGui; (2) một **shortcut router** theo panel đang focus; (3) khi Lua editor focus thì set `AnyFieldFocused` để chặn phím TAE.

### 1.3 Theme / màu
- Style ImGui mặc định dark + chỉnh: `WindowBg = (0.15,0.15,0.15)` (mờ 0.65 khi không focus), rounding 0, border 2px, `FramePadding (4,0)` (`OSD.cs:328-398, 425`).
- Màu cấu hình trong `ColorConfig.cs` (grid, hitbox root/middle/tip, dummy poly, timeline: `GuiColorActionBox_*`, `GuiColorActionGraph*`, playback cursor…). Event box **cùng một màu** (`GuiColorActionBox_Normal_Fill`), người dùng tự tô màu riêng từng event (`Dialog.EditActionColors.cs`, phím `C`). **Không có màu theo loại event.**

### 1.4 Font & glyph (CJK / tiếng Việt)
- Font UI: `Content/Fonts/NotoSansMonoCJKjp-Bold.otf`, 19 px × DPI (`Main.cs:585-590`, `DoActualImguiFontBuild` `Main.cs:1842-1880`).
- Glyph range được dựng **từ danh sách ký tự của font debug DS3** `Content/Fonts/dbgfont14h_ds3.ccm` (15 165 ký tự, có ~10k CJK, có `危`).
- **Đã kiểm tra: range này THIẾU ơ ư đ ă và toàn bộ Latin Extended Additional (U+1EA0–U+1EF9: ạ ế ờ ệ ỹ…)** → comment tiếng Việt trong Lua sẽ hiện `?`. Trong khi chính file font **có đủ** glyph tiếng Việt (đã kiểm tra cmap của cả `NotoSansMonoCJKjp-Bold.otf` và `iosevka-term-ss03-regular.ttf`).
- Fix (patch nhỏ): thêm range `U+0100–U+017F`, `U+01A0–U+01B0`, `U+0300–U+0323` (dấu tổ hợp), `U+1EA0–U+1EF9` vào builder.
- Font editor đề xuất: `iosevka-term-ss03-regular.ttf` (đã có sẵn, monospace hẹp → nhiều cột hơn, có tiếng Việt, có `›▸●→`) **merge** Noto CJK cho ký tự CJK (`危`, comment tiếng Nhật trong script gốc). Iosevka **không có** `危`.

### 1.5 Mô hình tương tác TAE timeline (DSAS)
Nguồn: `TaeEditor/NewGraphInput.cs`, `NewGraph.cs`, `TaeEditorScreen.cs`, `TaeUndoMan.cs`.
- Drag type: `Move`, `ResizeLeft`, `ResizeRight`, `SelectionRect`, `TimelineScrub`, `TimelineScrub_Relative` (`NewGraphInput.cs:17-31, 221-396`). Move có thể đổi **row/track** (`MoveResizeDragRowDelta`).
- Snap theo `ActionSnapType` (FPS30/FPS60) (`NewGraphInput.cs:178-196`); Sekiro = 30 fps.
- Kéo trên **toàn bộ selection** cùng lúc; clamp không cho start < 0, không cho end < start + 1 frame (`:102-170`).
- Chọn: click, Shift/Ctrl+click, kéo khung chọn, `Ctrl+A`, `Shift+D` bỏ chọn.
- Clipboard: `Ctrl+C/X/V` (dán tương đối tại playhead), `Ctrl+Shift+V` (dán giữ thời gian gốc) (`TaeEditorScreen.cs:3964-4060`) — **khớp đúng yêu cầu P1.5**.
- Undo: per-anim (`Ctrl+Z/Y`) + global (`Ctrl+Shift+Z/Y` có cảnh báo) (`MenuBar.010.Edit.cs:28-60`, `TaeUndoMan.cs`).
- "Ghost graph" = chế độ read-only khi anim tham chiếu anim khác (`NewGraph.cs:96-97`, `isReadOnly` ở `UpdateCurrentDrag`) → **có sẵn cơ chế read-only, nhưng theo cả graph, không theo từng event**. Ta cần read-only **theo event** (whitelist).
- Inspector event: `Window.Parameters.cs` ("Action Inspector") sửa mọi field → phải khóa.

### 1.6 Bảng phân loại KEEP / HIDE / ADAPT

| Thành phần DSAS | File | Phân loại | Ghi chú cho app mình |
|---|---|---|---|
| Main menu bar | `MenuBar.cs` | ADAPT | Chỉ còn File / Edit / View / Go / Help (§2.9). |
| File menu (Open, Open from archives, Recent Files, Reload GameParam, Save, config…) | `MenuBar.000.File.cs` | ADAPT | Thay bằng project: New/Open/Recent Project, Save, Save All, Backups…. Bỏ "Register Protocol Handler", "Force Ingame Entity Reload". |
| Edit menu (undo, find, go to anim, rename anim, duplicate anim, sort tracks, mute/solo, tags, custom color) | `MenuBar.010.Edit.cs` | ADAPT | Giữ Undo/Redo, Find, Go to anim. HIDE: change event type (F1), rename/duplicate/delete anim, category props, sort/regen tracks, mute/solo, tags, custom color. |
| Action Graph menu (snap, scroll, scrub options… ~30 tùy chọn) | `MenuBar.020.Graph.cs` | HIDE | Giữ giá trị mặc định hợp lý; chỉ lộ "Snap to 30 fps" trong context menu timeline. |
| Simulation menu (~25 checkbox) | `MenuBar.030.Simulation.cs` | HIDE (giữ 4) | Lộ trong View: Hitboxes, SpEffect application, Bullet spawns, Blending. Còn lại mặc định. |
| Animation menu (root motion, camera follow, playback speed) | `MenuBar.040.Animation.cs` | ADAPT | Chuyển vào menu ⚙ nhỏ trên viewport (playback speed, root motion on/off, camera follow). |
| Tools menu (export skeleton/anims, export names…) | `MenuBar.050.Tools.cs` | HIDE | Ngoài phạm vi. |
| Window menu | `MenuBar.060.Window.cs` | ADAPT | Thành View: toggle panel + Reset layout. |
| Help / Support menus | `MenuBar.070.Help.cs`, `MenuBar.080.SupportMeowmaritus.cs` | ADAPT / KEEP credit | Help → Shortcuts, About (credit DSAS/Meowmaritus giữ trong About). |
| Transport (play/pause/frame step trong menu bar) | `Window.NewTransport.cs`, `TaeEditor/TaeTransport.cs` | KEEP (dời chỗ) | Đưa xuống header của panel Timeline. |
| Documents toolbar (multi-document) | `Window.DocManToolbar.cs` | HIDE | Một project = một document. |
| Animations list | `Window.Animations.cs` | ADAPT | Không thành panel riêng; thành dropdown "Anim" trên header timeline + mục "Animations" trong cây entity (lọc: chỉ anim được Lua tham chiếu / tất cả). |
| Graph (TAE timeline) + Tracks + Actions | `Window.Graph*.cs`, `TaeEditor/NewGraph*.cs` | ADAPT | Giữ renderer, thêm whitelist + màu theo loại + highlight Combo window (§2.5). |
| Parameters / Action Inspector | `Window.Parameters.cs` | ADAPT | Thay bằng Inspector của mình (§2.6); field event read-only trừ start/end và SpEffect ID. |
| Viewport | `Window.Viewport.cs` | KEEP | + overlay hitbox/range ring (P1). |
| Entity (NPC param selection, behavior/hitbox source, equipment) | `Window.Entity.cs` | ADAPT | Giữ chọn NpcParam ID; còn lại ẩn vào "Advanced". |
| Equipment | `Window.Equipment.cs` | HIDE | Player-only. |
| Scene (light, shader, render channels) | `Window.SceneWindow.cs` | HIDE | Chỉ giữ 3 toggle: grid, skeleton, dummy poly (vào ⚙ viewport). |
| Toolbox (camera, bokeh, light…) | `Window.Toolbox.cs` | HIDE | Camera speed vào ⚙ viewport. |
| Sound | `Window.Sound.cs` | HIDE | Mặc định tắt âm thanh (có toggle mute trên viewport). |
| Find (tìm value trong TAE) | `Window.Find.cs` | HIDE | Thay bằng Find trong editor + Go to anim. |
| Project (tag manager của DSAProj) | `Window.Project.cs` | HIDE | Khác khái niệm project của mình. |
| Combo Viewer (bảng combo thẳng, F8) | `Window.NewComboViewer.cs` | HIDE (thay thế) | Thay bằng Combo graph + Combo path (§2.7). |
| Log / Notifications | `Window.Notifications.cs` | KEEP | Thành tab "Log" ở panel dưới + toast. |
| ERRORS | `Window.ERRORS.cs` | ADAPT | Gộp vào tab "Problems". |
| Help window | `Window.Help.cs` | ADAPT | Thành Shortcuts cheat sheet. |
| Debug | `Window.Debug.cs` | HIDE | Chỉ hiện ở build Debug. |
| Welcome dialog | `Dialog.Welcome.cs` | HIDE (thay) | Thay bằng Start screen (§2.1). |
| Setup Workspace dialog | `Dialog.SetupWorkspace.cs` | HIDE (thay) | Thay bằng New Project wizard (§2.2). |
| Dialog anim/category/root TAE properties, duplicate category, tag picker, edit colors, change type | `Dialog.*.cs`, `TaeInspectorFormChangeEventType.cs` | HIDE | Ngoài giới hạn P1.5. |
| Dialog YesNo / TextInput / MultiChoice | `Dialog.cs`, `Dialog.TextInput.cs`, `Dialog.MultiChoice.cs` | KEEP | Dùng cho confirm xóa / thoát. |
| Tooltip manager | `TooltipManager.cs`, `Window.Tooltip.cs` | KEEP | Dùng chung cho mọi tooltip. |
| ImGuiEx / Tools helpers | `ImGuiEx.cs`, `Tools.cs`, `ImGuiWidgets.cs` | KEEP | |
| Layout ini mặc định | `EmbRes/imgui_default_*.ini` | HIDE | Layout do LayoutManager mình tính (§2.3). |
| Phím tắt TAE (xem §1.2) | `TaeEditorScreen.cs:3794-4320` | ADAPT | Giữ: Ctrl+C/X/V, Ctrl+Shift+V, Delete (có confirm), Ctrl+Z/Y, Space, ←/→ frame, Home/End, Ctrl +/−/0 zoom. Tắt: Insert, F1–F7, F8, 1–4, C, T, Ctrl+RightClick, Ctrl+H/I/J/K/R, Shift+Delete. |

---

## 2. Thiết kế màn hình

### 2.1 Start screen
Hiện khi mở app không có project, hoặc File › Close Project. Một window full-screen, không panel nào khác.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Souls AI Studio                                              v0.1 · Sekiro  │
│                                                                              │
│   ┌───────────────────────────┐   Recent projects            [filter...   ] │
│   │  ＋  New Project…   Ctrl+N │   ┌──────────────────────────────────────┐ │
│   ├───────────────────────────┤   │ ▸ OwlRework          3 entities       │ │
│   │  📂  Open Project… Ctrl+O │   │   D:\mods\OwlRework\OwlRework.sasproj │ │
│   └───────────────────────────┘   │   Edited 2 h ago · Owl (Father), …    │ │
│                                   ├──────────────────────────────────────┤ │
│   Getting started                 │ ▸ IsshinTest         1 entity         │ │
│   · What is a project?            │   ⚠ Folder not found — Locate… Remove │ │
│   · Shortcuts                     ├──────────────────────────────────────┤ │
│                                   │ ▸ Severance-dev AI   12 entities      │ │
│   Settings                        │   …                                    │ │
│   · Decompiler: DSLuaDecompiler ✓ └──────────────────────────────────────┘ │
│   · Theme: Dark                                                              │
│                                                                              │
│  ⓘ Recovered unsaved changes found for "OwlRework". [Open & Restore] [Discard]│
└──────────────────────────────────────────────────────────────────────────────┘
```
- Click 1 lần vào recent = mở (không cần double-click). Hover hiện đường dẫn đầy đủ + thời gian mở.
- Recent bị mất folder: không xóa tự động, hiện cảnh báo inline `Locate…` / `Remove`.
- Kéo thả file `.sasproj` vào cửa sổ = Open.
- Banner autosave recovery (P0.5) nằm dưới cùng, không popup.
- Decompiler chưa cấu hình → dòng Settings hiện `⚠ Not set — Browse…` (chỉ cần khi import Lua bytecode).

### 2.2 New Project wizard (1 màn hình, không dialog lồng)
Một màn hình chia 4 bước dọc; bước sau mở khóa khi bước trước hợp lệ. Mọi bước luôn nhìn thấy được (không "Next/Back").

```
┌─ New Project ────────────────────────────────────────────────────────────────┐
│ 1  Game folder (unpacked)                                                    │
│    [C:\Program Files (x86)\Steam\steamapps\common\Sekiro          ] [Browse] │
│    ✓ Sekiro detected · UXM unpacked · gameparam.parambnd.dcx found          │
│                                                                              │
│ 2  Mod folder (Mod Engine)                                                   │
│    [C:\…\Sekiro\mods\Severance-dev                                ] [Browse] │
│    ✓ 3 luabnd · 41 anibnd · gameparam (mod overrides game)                   │
│                                                                              │
│ 3  Pick Lua AI / entities                       [search: owl          ] [×]  │
│    Map  ▾ All maps     Show ▾ Battle scripts    ☐ Only files in mod folder   │
│   ┌──────────────────────────────────────────────────────────────────────┐  │
│   │ ☑ 506300  Owl (Father)       m10_00_00_00  battle ✓ logic ✓  c5060  │  │
│   │ ☐ 506000  Owl                m11_…          battle ✓ logic ✓  c5060  │  │
│   │ ☐ 506310  Owl (Father) ph2?  m10_00_00_00  battle ✓          c5060  │  │
│   └──────────────────────────────────────────────────────────────────────┘  │
│    1 selected                                                                │
│                                                                              │
│ 4  Detected files (auto)                                     [Re-scan]       │
│   ┌──────────────────────────────────────────────────────────────────────┐  │
│   │ 506300 Owl (Father)                                                  │  │
│   │   Lua   script/m10_00_00_00.luabnd.dcx › 506300_battle.lua  plain ✓   │  │
│   │         506300_logic.lua                                     plain ✓   │  │
│   │   Anim  chr/c5060.anibnd.dcx                         mod ✓            │  │
│   │   Model chr/c5060.chrbnd.dcx                         game (fallback)  │  │
│   │   NPC   NpcParam 50600000 · NpcThinkParam 506300 [change ▾]           │  │
│   │   ⓘ Lua is bytecode in game files → will decompile once on create.   │  │
│   └──────────────────────────────────────────────────────────────────────┘  │
│                                                                              │
│ Project name [OwlRework            ]  Location [D:\mods\AIProjects\ ] [Browse]│
│ → D:\mods\AIProjects\OwlRework\OwlRework.sasproj                             │
│                                                       [Cancel] [Create ▶]   │
└──────────────────────────────────────────────────────────────────────────────┘
```
- Validate inline từng ô (✓ xanh / ⚠ vàng có giải thích + cách sửa). Nút `Create` chỉ sáng khi bước 1–3 hợp lệ.
- Bước 3: list ảo hóa (clipper), search theo ID, tên NPC (FMG), tên map. Tên entity lấy từ NpcParam/FMG; khi không có tên → hiện ID xám.
- Bước 4: mỗi dòng có `[change ▾]` khi có nhiều ứng viên (ví dụ nhiều NpcParam dùng cùng think ID) — dropdown, không dialog.
- `Create` → progress inline thay cho bước 4 (Decompiling 1/2…, Indexing params…); lỗi từng file không chặn cả project (file lỗi đánh dấu `⚠ Failed — Retry`).
- Folder picker: dùng dialog hệ thống (WinForms FolderBrowser) — đây là ngoại lệ duy nhất, không lồng thêm gì.

### 2.3 Main layout

#### Bố cục chuẩn (≥ 1600×900)
```
┌ File Edit View Go Help ─────────────── OwlRework ● ──────────────── ⟳ watching ┐
├──────────────┬─────────────────────────────────────────┬───────────────────────┤
│ EXPLORER   ⟨ │ 506300_battle.lua ● │ Combo graph │ Acts │  VIEWPORT        ⚙ ⟩ │
│ [filter   ]  │ Owl (Father) › 506300_battle.lua ›      │ ┌───────────────────┐ │
│ ▾ Owl(Father)│   Goal.Kengeki03 ▾          ⚠2 ⓘ5      │ │                   │ │
│   ▾ Lua      │  2524│Goal.Kengeki03 = function(f59_…  │ │     (3D model)    │ │
│     battle ● │  2525│  local f45_local7 = f59_arg0:Ge… │ │                   │ │
│     logic    │  2526│  f59_arg1:ClearSubGoal()         │ │  3002 · 62/94 f   │ │
│   ▸ Anims(41)│  2527│  f59_arg1:AddSubGoal(GOAL_COMM… │ └───────────────────┘ │
│   ▸ Params   │   …                                     ├───────────────────────┤
│ ──────────── │                                         │ INSPECTOR             │
│ OUTLINE      │                                         │ Attack · anim 3002    │
│ ▸ Lifecycle 4│                                         │ …                     │
│ ▸ Interrupt 3│                                         │                       │
│ ▸ Act      50│                                         │                       │
│ ▾ Kengeki  31│                                         │                       │
│   Kengeki03 ◂│                                         │                       │
├──────────────┴─────────────────────────────────────────┴───────────────────────┤
│ TIMELINE │ COMBO PATH │ PROBLEMS 7 │ LOG          anim [3002 ▾] ◀◀ ▶ ▶▶ 1.0x ⌄ │
│ (TAE timeline, §2.5)                                                           │
├────────────────────────────────────────────────────────────────────────────────┤
│ Ln 2527, Col 9 · Lua · UTF-8 · ⚠2 ⓘ5 · Saved 12:04 · backup 20261111-120412    │
└────────────────────────────────────────────────────────────────────────────────┘
```

| Vùng | Mặc định | Min | Thu gọn |
|---|---|---|---|
| Menu bar | 22 px | — | — |
| Left (Explorer + Outline) | 260 px | 180 px | Rail 32 px (icon) — `Ctrl+B` |
| Center (tab: Lua files, Combo graph, Acts) | phần còn lại | 480 px | không |
| Right (Viewport trên, Inspector dưới) | 420 px; viewport 55 % chiều cao | 300 px | Rail 32 px — `Ctrl+Alt+B` |
| Bottom (Timeline / Combo path / Problems / Log) | 220 px | 120 px | Strip 26 px (chỉ header + transport) — `Ctrl+J` |
| Status bar | 22 px | — | — |

- Splitter 4 px, hover đổi màu accent; **double-click splitter = thu gọn / khôi phục**. Kéo dưới min → snap về thu gọn.
- Kích thước lưu theo **tỉ lệ** trong `.sasproj` (§P0 "layout panel"). View › Reset Layout trả về bảng trên.
- Explorer và Outline cùng cột trái, chia dọc 45/55, mỗi phần có header thu gọn được.
- Center là tab bar kiểu VSCode: file Lua, `Combo graph`, `Acts` (act table P1.9). Middle-click đóng tab; `Ctrl+Tab` chuyển tab.
- Viewport: header nhỏ (anim ID, frame, ⚙). Inspector: nội dung theo lựa chọn hiện tại (§2.6).

#### Hành vi khi cửa sổ nhỏ
| Kích thước cửa sổ | Hành vi |
|---|---|
| ≥ 1600 × 900 | Như trên. |
| 1280–1599 rộng | Left 220, Right 340. |
| < 1280 rộng | Left tự thu thành **rail**; click icon → panel trượt ra **đè lên** editor (overlay), click ra ngoài để đóng. |
| < 1024 rộng | Right cũng thành rail; Viewport + Inspector mở dạng overlay; hoặc ghim Inspector thành tab trong Center. |
| < 720 cao | Bottom tự thu thành strip; mở bằng click header / `Ctrl+J` (mở đè lên 40 % chiều cao). |
| Tối thiểu hỗ trợ | 1024 × 640. Nhỏ hơn: hiện thông báo trong status bar "Window too small — some panels hidden". |
- Panel tự thu gọn do cửa sổ nhỏ **không** ghi đè trạng thái người dùng đã chọn; phóng to lại → về như cũ.
- DPI: mọi kích thước × `Main.DPI` (DSAS đã có `UpdateDpiStuff`).

#### Cách hiện thực (gợi ý cho dev)
- `LayoutManager` (code mới) tính rect mỗi frame, gọi `SetNextWindowPos/Size(…, ImGuiCond.Always)` cho các window host (gồm cả `Window.Viewport`, `Window.Graph` của DSAS) với `NoMove | NoDocking | NoTitleBar | NoCollapse`. Không dùng `imgui.ini` cho các panel chính.
- Lý do không dùng DockBuilder: wrapper 1.87 không bọc, ini/dock state dễ hỏng khi đổi tên window, và rule "thu gọn theo kích thước" dễ viết bằng splitter tự tính hơn.

### 2.4 Lua editor + Goal navigator

#### Breadcrumb + thanh trên editor
```
 Owl (Father) › 506300_battle.lua › Goal.Interrupt ▾   ‹ › │ ⚠2 ⓘ5 │ 🔍 │ ⋯
```
- `‹ ›` = Back/Forward (Alt+←/→). Mỗi phần breadcrumb click được: entity → mở dropdown entity khác; file → dropdown file trong entity; goal → **Goal navigator**.
- Breadcrumb cập nhật theo cursor (debounce ~100 ms). Ngoài mọi function → hiện `(top level)`.

#### Goal navigator (dropdown, `Ctrl+Shift+O`)
```
 Owl (Father) › 506300_battle.lua › Goal.Interrupt ▾
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ [keng|                                                     ] 31 / 87    │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ ▾ Kengeki                                                         31     │
 │     Goal.Kengeki_Activate  function (f56_arg0, …)           ×24  L2364   │
 │   > Goal.Kengeki01         function (f57_arg0, …)   3010     ×1   L2512   │
 │   ▾ Goal.Kengeki03         function (f59_arg0, …)   3002     ×3   L2524 ◂│
 │        ComboRepeat 3002                                           L2527  │
 │        ├ Random 50%  ComboRepeat 3016                             L2529  │
 │        └ Random 50%  ComboRepeat 3048                             L2531  │
 │   > Goal.Kengeki04         function (f60_arg0, …)   30xx     ×2   L2535  │
 │   …                                                                      │
 └──────────────────────────────────────────────────────────────────────────┘
   ↑↓ move · → expand · Enter jump · Esc close
```
Khi ô lọc rỗng:
```
 │ ▸ Lifecycle    Initialize · Activate · Update · Terminate            4   │
 │ ▾ Interrupt                                                          3   │
 │   ▾ Goal.Interrupt         function (arg0, arg1, arg2)  ⚡52 ⚠1 ●  L1590 │
 │        ⚡ 3506080  Replanning                                      L1614  │
 │        ⚡ 3506000  ClearSubGoal · Random 33/33/34 · 3012 3045 …   L1617  │
 │        ⚡ 5060040  ClearSubGoal · Random 25×4 · 3037 3018 3047 3044 L1873 │
 │        ⚡ 5060089  if rand2 ≤ 45 · …                               L1690  │
 │        ⚑ INTERUPT_ParryTiming   → Goal.Parry                     L1603  │
 │        ⚑ INTERUPT_ShootImpact   → Goal.ShootReaction             L1606  │
 │        …                                                                 │
 │     Goal.Parry             function (f54_arg0, …)                 L2281  │
 │     Goal.ShootReaction     function (f55_arg0, f55_arg1)          L2352  │
 │ ▾ Act                                                               50   │
 │     Goal.Act01             function (f3_arg0, …)    30xx     ×n   L345   │
 │     …                                                                    │
 │     Goal.Act45             function (f47_arg0, …)   3039 3092 ×2  L1425  │
 │ ▸ Kengeki                                                           31   │
 │ ▸ Other        NoAction · ActAfter_AdjustSpace                       2   │
```
- Cột: tên (đậm) · chữ ký mờ · **badge** anim ID chính (tối đa 2, thừa → `+n`) · `×N` số AddSubGoal · `⚡N` số nhánh SpEffect · chấm diagnostics (vàng Warning / xanh Info; đỏ chỉ khi parse lỗi) · số dòng (mờ, căn phải).
- Lọc: fuzzy theo tên (`keng`, `k03`, `act4`), **và** theo số: gõ `3002` → các goal có AddSubGoal anim 3002; gõ `5060040` → nhánh handler + goal liên quan. Kết quả khớp tô sáng ký tự.
- Nhóm: Lifecycle / Interrupt / Act / Kengeki / Other (quy tắc trong `GameProfile`). Đang lọc → bỏ nhóm, list phẳng theo điểm khớp.
- Function hiện tại có `◂` và nền highlight; khi mở dropdown, cuộn tới nó.
- Mở rộng: `>` / `→`. Goal thường → danh sách AddSubGoal (goal type rút gọn bỏ `GOAL_COMMON_`, anim ID, nhãn nhánh `Random 50%` / `if …`). `Goal.Interrupt` → từng nhánh `== SpEffectID` (kèm tên SpEffect khi tra được) và `INTERUPT_*`.
- Hover một goal (300 ms) → tooltip tóm tắt chuỗi: `3002 → (50%) 3016 | (50%) 3048` + diagnostics.
- Phím: `↑↓` chọn, `→/←` mở/đóng, `Enter` nhảy (đóng dropdown), `Ctrl+Enter` nhảy nhưng giữ dropdown, `Esc` đóng. Gõ ký tự bất kỳ → vào ô lọc.
- Cây Outline ở panel trái **dùng chung data + render** với dropdown (thường trực, không có ô lọc riêng — có ô filter nhỏ ở header).

#### Điều hướng
| Phím | Hành vi |
|---|---|
| `Ctrl+Shift+O` | Mở Goal navigator, focus ô lọc. |
| `F12` / `Ctrl+Click` | Trên `Goal.Act45`, `arg0.Parry`, tên function local, `GOAL_COMMON_*` (→ tooltip doc aicommon), anim ID trong `AddSubGoal` (→ timeline + viewport nhảy tới anim), SpEffect ID (→ Inspector SpEffect). |
| `Shift+F12` | Find usages (list ở tab Problems › References). |
| `Alt+←` / `Alt+→` | Lịch sử điều hướng (nhảy > 10 dòng hoặc qua F12/navigator mới ghi lịch sử). Chuột nút Back/Forward cũng dùng được. |
| `Ctrl+G` | Go to line (ô nhỏ trên đầu editor, không dialog). |
| `Ctrl+F` / `Ctrl+H` | Find / Replace bar (trên đầu editor). |
| `F8` / `Shift+F8` | Diagnostics kế / trước. |
| `Ctrl+Space` | Autocomplete. |
- Lưu ý: các phím này **trùng phím TAE của DSAS** → router theo focus (§1.2). Alt+←/→ khi focus timeline = lịch sử anim (giữ hành vi DSAS), khi focus editor = lịch sử vị trí code.

#### Liên kết editor ↔ phần còn lại
- Cursor nằm trên một dòng `AddSubGoal(…, 3002, …)` → gutter hiện icon ▶ ; click ▶ (hoặc `Ctrl+Enter`) = **Preview**: timeline chuyển anim 3002, viewport play, Inspector hiện Attack. Không tự preview khi chỉ di chuyển cursor (tránh giật viewport khi đang gõ) — có tùy chọn "Follow cursor" (mặc định tắt).
- Gutter còn có: chấm diagnostics, ⚡ ở dòng `== SpEffectID`, dấu fold `▾/▸`.
- Hover số anim/SpEffect trong code → tooltip: tên + tóm tắt (anim: độ dài, có Combo window ở frame nào; SpEffect: tên, nguồn đã index).

### 2.5 TAE timeline (giới hạn)

```
 TIMELINE  anim [3002 Kengeki slash ▾]  ◀◀ ◀ ▶ ▶ ▶▶  1.0x  62 / 94 f   Show [AI-relevant ▾] 🔒 ⋯
 ┌────────────┬──────┬──────┬──────┬──────┬──────┬──────┬──────┬──────┬──────┬───┐
 │ frames     0     10     20     30     40     50     60 │   70     80     90  │
 ├────────────┼──────────────────────────────────────────────┼─────────────────────┤
 │ Attack     │              ███AttackBehavior 1 [ref 300200]█│                     │
 │ Bullet     │                                           ▒▒▒│Bullet (locked)      │
 │ SpEffect   │                     ◆5060040 Kengeki branch ✎ │                     │
 │ Combo      │                                    ╔═════════╪Combo window═══════╗ │
 │ Blend      │ ███Blend███                                   │                     │
 │ Turn       │ ░░░░░░░░░ SetTurnSpeed (locked) ░░░░░          │                     │
 │ Other (12) │ ░░ ░░░ ░ ░░  ░░░ (sound/FFX, locked)          │                     │
 └────────────┴──────────────────────────────────────────────┴─────────────────────┘
                                                        ▲ playhead
```
- **Hàng theo loại** (thay vì track tự do của DSAS): Attack · Bullet · SpEffect · Combo · Blend · Turn · Other. "Other" thu gọn mặc định (một hàng gộp, mở ra để xem từng event). `Show ▾`: `AI-relevant` (mặc định) / `All events` / `Original tracks` (track gốc DSAS, cho người quen DSAS).
- **Editable vs read-only**:

  | Trạng thái | Hiển thị |
  |---|---|
  | Sửa được start/end | Màu đậm 100 %, viền 1 px sáng, tay nắm resize hiện khi hover 2 đầu, cursor ↔ |
  | Sửa được value (SpEffect) | như trên + icon `✎` nhỏ cuối nhãn |
  | Read-only | Fill 35 % + **sọc chéo nhẹ**, chữ mờ, không viền; hover → cursor mặc định + tooltip `Locked — edit in DS Anim Studio` |
  | Đang chọn | Viền accent 2 px (bỏ hiệu ứng pulse của DSAS — đỡ rối) |
  | Có diagnostics | Tam giác nhỏ vàng/xanh góc trên trái |
- Read-only vẫn **chọn / copy được** (copy là "mọi event" theo P1.5); chỉ không kéo, không sửa field.
- Kéo: start/end snap 30 fps; giữ `Shift` khi kéo = bỏ snap (theo DSAS là speed lock — đổi lại ý nghĩa; ghi rõ trong Shortcuts). Kéo **không đổi hàng**. Tooltip khi kéo: `Start 41 → 44 f (1.367 s)`.
- Sửa số chính xác: chọn event → Inspector có ô Start/End (frame và giây, nhập cái nào cũng được), validate ngay (end > start, ≥ 0, ≤ độ dài anim + cảnh báo nếu vượt).
- **SpEffect value**: Inspector (không popup trong timeline):
  ```
  SpEffect ID  [5060040        ] ✓ "Kengeki branch A" (example name)
               ▾ suggestions: 5060041 …, 5060042 …  (search by ID or name)
  ```
  ID không tồn tại → viền vàng + `⚠ Not found in SpEffectParam` (vẫn cho lưu — Warning, không chặn). ID đổi → hiện "Used by Lua: Goal.Interrupt L1873" nếu handler có nhánh tương ứng, hoặc `ⓘ No Lua handler for this ID`.
- **Copy/Paste giữa anim**: `Ctrl+C` trên event → status bar "2 events copied from 3002". Chuyển anim (dropdown hoặc Alt+←) → `Ctrl+V` dán tại playhead (giữ khoảng cách tương đối), `Ctrl+Shift+V` dán ở thời gian gốc. Event dán xong được chọn + flash 1 lần. Dán event loại không có hàng hiện tại → vẫn dán, hàng tự hiện.
- **Delete**: phím Delete → inline confirm ngay trên header timeline: `Delete 2 events? [Delete] [Cancel]` (không modal). Undo được.
- **Combo window** (ChrActionFlag 0, flag 23 "End If AI ComboAttack Queued" — Sekiro template `Res/TAE.Template.SDT.xml:26`): hàng riêng, viền đôi màu teal + **tô nền dọc mờ toàn bộ chiều cao timeline** trong khoảng đó, để thấy SpEffect/hitbox nằm trước hay sau cửa sổ nối combo. SpEffect/hitbox nằm sau Combo window → tam giác vàng (Warning P2).
- Header: dropdown anim có search, liệt kê anim có trong anibnd; anim đang được Lua tham chiếu có chấm xanh; anim chưa có trong TAE (Lua gọi nhưng TAE không có) không thể chọn, hiện ở Problems.
- Ctrl+scroll = zoom; scroll = cuộn ngang; ← → = frame; Space = play/pause; Home/End.
- Context menu (chuột phải): Copy, Paste here, Paste at original time, Delete, Snap to 30 fps ☑, Go to Lua usage. **Không** có "Add event", "Change type", "Mute/Solo", "Color", "Tags".

### 2.6 Inspector

Nội dung theo lựa chọn gần nhất: dòng `AddSubGoal` / node graph → **Attack**; event timeline → **Event**; edge SpEffect → **SpEffect branch**; không chọn gì → tóm tắt entity.

#### Attack (ví dụ số liệu minh họa)
```
 INSPECTOR                                                         📌 ⋯
 Attack · anim 3002 "Kengeki slash"          ◀ from Goal.Kengeki03 L2527
 ComboRepeat · target ENE_0 · length 94 f (3.13 s)
 ────────────────────────────────────────────────────────────────────────
 Hit 1   frames 41–47                          [Show hitbox ☑] [Solo]
   Damage        Physical 220 · Posture 85
   Type          ⚠ Perilous — Thrust (Mikiri)          危
   Range         2.4 m (dmyPoly 100→101, r 0.6)
   Guard         Deflect window ok · Guard break no
   SpEffect on hit → target   5060xxx "Example: knockback flag"   ⚡ used
   Source        AttackBehavior 1 → BehaviorParam_Npc 506300200
                  → AtkParam_Npc 5063002000                 [Copy chain]
 Hit 2   frames 52–55  …
 ────────────────────────────────────────────────────────────────────────
 SpEffects in this anim
   f 60  5060040 "Kengeki branch A"  self   ⚡ Goal.Interrupt L1873
 Combo window  f 70–94
```
- Tên trước, ID mờ sau (`§5b`). Mọi ID click được (copy / mở trong Smithbox nếu cấu hình — P3).
- Thuộc tính Sekiro (perilous 危, thrust/sweep/grab, deathblow) là **chip màu** dễ quét mắt.
- Field chưa xác minh tên trong paramdef → ghi `?` + tooltip "field name not verified".
- `📌` ghim Inspector (không đổi khi chọn thứ khác).

#### SpEffect branch (edge)
```
 SpEffect branch · 5060xxx "Example: kicked"                       📌 ⋯
 From  anim 3010 (Kick)   →   Goal.Interrupt L17xx  (ClearSubGoal ✓)
 Source  On hit  ─ conditional: only when the attack hits
 ┌──────────────────────────────────────────────────────────────────────┐
 │ anim 3010                                                            │
 │  └ TAE AttackBehavior (f 22–26)                                      │
 │     └ BehaviorParam_Npc 506301000                                    │
 │        └ AtkParam_Npc 5063010000                                     │
 │           └ field spEffectId0 = 5060xxx  → applied to TARGET (player)│
 │ Lua observes 5060xxx on TARGET_ENE_0 (Goal.Activate L2x) ✓ match     │
 └──────────────────────────────────────────────────────────────────────┘
 Branches  Random 50%  → 3015 Backstep → 3060 Shuriken     [Jump to code]
           Random 50%  → 3020 …
 Timing    hit window f 22–26 · Combo window f 40–58 · ✓ branch before window
 ⓘ Also found: none
```
- Chuỗi nguồn dạng cây nhỏ, mỗi node click → nhảy (TAE event → timeline; param → Inspector param; Lua → editor).
- Nhiều nguồn → liệt kê hết, nguồn chính đứng đầu (TaeEvent > OnHit > Param > Chain).
- `Unknown`:
  ```
  Source  Not found (Info)
  ⓘ Searched: TAE of 41 anims, AtkParam/Bullet/BehaviorParam of this entity,
    SpEffect chains (depth 3). It may come from another entity, map events
    (EMEVD), the player, or a source not indexed yet.
  ```

### 2.7 Combo graph + Combo path

#### Combo graph (tab ở Center)
```
 Combo graph  scope [Goal.Kengeki03 ▾]  ☑ Sequence ☑ Random ☑ SpEffect ☑ On hit ☐ Other interrupts  Fit ⤢
 ┌─────────────────────────────────────────────────────────────────────────────────┐
 │                                                                                 │
 │  ┌Kengeki03┐     ┌──────────┐  50%  ┌──────────┐                                │
 │  │ goal    │────▶│ 3002     │──────▶│ 3016     │                                │
 │  └─────────┘     │ ComboRep │──┐    └──────────┘                                │
 │                  └────┬─────┘  │50% ┌──────────┐                                │
 │                       │        └───▶│ 3048     │                                │
 │                       │             └──────────┘                                │
 │          ⚡5060040 @2.0s TAE                                                     │
 │                       │  ┌─25%─▶ 3037                                           │
 │                       ╰──┼─25%─▶ 3018                                           │
 │                          ├─25%─▶ 3047                                           │
 │                          └─25%─▶ 3044 ─ ─ ⚡on hit 50601xx ─ ─▶ 3015 …           │
 │                                                                                 │
 │  Legend: ── Sequence  ─%─ Random  ⚡── SpEffect (TAE)  ─ ─ On hit  ··· Unknown   │
 └─────────────────────────────────────────────────────────────────────────────────┘
 ⓘ 5060052: Source not found in loaded data (Info) · ⚠ 5060089 observed on wrong target
```
- Bố cục tự động trái→phải (layered). Node = anim (ID lớn, tên/goal type nhỏ), viền màu theo goal type; node có hitbox perilous có chip `危`.
- Edge:
  - `Sequence` nét liền xám.
  - `Random` nét liền + nhãn `%`.
  - `Condition` nét liền + nhãn biểu thức rút gọn (`dist ≥ 3`, `Ninsatsu == 2`), không %.
  - `SpEffect` màu tím (cùng màu event SpEffect), nhãn `⚡ID @time` + nguồn: `TAE` / `On hit` (**nét đứt**) / `Param` / `Chain` (nét chấm-gạch) / `Unknown` (nét chấm, xám).
  - Edge quay lui (vòng) cong phía trên, có `↺`.
  - Edge `ClearsQueue = false` có icon `+` ("appends").
  - `Replan` → node đặc biệt `⟲ Replanning`.
- Tương tác: click node → viewport play + timeline nhảy; click edge → editor nhảy tới dòng code + Inspector hiện edge; double-click node → mở rộng tiếp (graph lớn mặc định chỉ mở 2 cấp); scroll zoom, kéo nền pan; `F` = fit.
- Scope: một goal, cả `Goal.Interrupt`, hoặc "Whole file" (cảnh báo nếu > 200 node, hiện cụm thu gọn).
- Khi cursor editor nằm trong goal → node của goal đó highlight (đồng bộ 2 chiều §5b).

#### Combo path (tab ở panel dưới)
```
 COMBO PATH  from [Goal.Kengeki03 ▾]  [⟲ Random roll]  ▶ Play path   total 6.4 s
 ┌──────────────┬────────────────────────┬───────────────────────────────────────┐
 │ 3002  0–2.0s │ ✂ ⚡5060040 (TAE)       │ 3047  ……………………… │ 3044 ……  │ ?        │
 │ ▒▒▒▒▒▒▒▒▒▒▒▒ │ branch: [3047 25% ▾]   │ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ │▒▒▒▒▒▒▒▒ │          │
 │              │                        │                    ⚡on hit 50601xx    │
 │              │                        │                    [● Hit ○ Miss]     │
 └──────────────┴────────────────────────┴───────────────────────────────────────┘
   Path probability: 25% × (On hit: conditional) — fixed part 25%
```
- Một đường đi trải thành timeline liên tục; tại mỗi điểm rẽ có **branch picker** (dropdown có %); `⟲ Random roll` tung theo xác suất toàn đường.
- Điểm cắt SpEffect hiện biểu tượng ✂ và phần anim bị cắt mờ đi.
- Nhánh `On hit` có công tắc **Hit / Miss**; Miss → đường đi tiếp theo Sequence bình thường.
- Nhánh Condition (không random) → picker hiện biểu thức, người dùng chọn "true/false".
- Play path → viewport chạy liên tục; playhead chạy trên strip này. Click một đoạn → timeline TAE chuyển anim đó.

#### Diagnostics (tab Problems + inline)
```
 PROBLEMS  [All ▾] [This file ▾]  ⚠ 2  ⓘ 5
 ⚠  5060089 is observed on TARGET_SELF but applied to target (On hit)   Goal.Activate L39
 ⚠  5060xxx in anim 3005 starts after Combo window (f 72 > 70)           TAE 3005
 ⓘ  5060052: source not found in loaded data — may come from another     Goal.Interrupt L2032
    entity, map events (EMEVD), the player, or an unindexed source.
 ⓘ  Random thresholds in Goal.Act12 cover 1..90 only (91..100 → no action) L705
 ⓘ  anim 3005 applies 5060xxx but no Lua handler uses it                 TAE 3005
```
- Biểu tượng + màu + chữ (không dựa chỉ vào màu). Mỗi dòng: thông điệp bằng tiếng Anh, **nói rõ app đã tìm ở đâu**; click → nhảy.
- Info có thể ẩn (`[All ▾]` → Warnings only). Có "Ignore this" theo ID (lưu trong `.sasproj`).
- **Không bao giờ dùng chữ "error" / "dead branch"** cho nguồn SpEffect không tìm thấy.

### 2.8 Save / backup / file watcher

- **Dirty markers**: tab file `506300_battle.lua ●`; Explorer cùng chấm; tiêu đề app `OwlRework ●`; anim đã sửa TAE có `●` trong dropdown anim và Explorer › Anims. Dòng code đã sửa từ lần save gần nhất: vạch xanh mảnh ở gutter.
- `Ctrl+S` save file/anibnd đang focus; `Ctrl+Shift+S` = Save All. Save thành công → status bar `Saved 12:04 · backup 20261111-120412` (click → mở Backup browser tại snapshot đó). Lỗi save → **Error** toast đỏ, file cũ không đổi (atomic), nút `Retry` / `Show log`.
- Thoát / đóng project có dirty → modal duy nhất: `Save all` / `Don't save` / `Cancel` + danh sách file.
- **Conflict banner** (file đổi từ ngoài khi buffer đang dirty), nằm ngay trên editor, màu vàng:
  ```
  ⚠ 506300_battle.lua changed on disk (VSCode? 12:07). You have unsaved changes.
     [Keep mine] [Take theirs] [Compare]                                   ✕
  ```
  - Không dirty → tự reload, toast nhỏ `Reloaded 506300_battle.lua (changed on disk)` + cursor giữ vị trí gần nhất.
  - `Compare` mở tab diff (2 cột, trái = của tôi, phải = trên đĩa), có nút áp từng hunk sang trái; đóng tab diff → banner vẫn còn tới khi chọn.
  - Với anibnd/param: banner trên Timeline/Inspector tương tự; param chỉ đọc nên luôn auto-reload.
- **Backup browser** (View › Backups…, window nổi lớn, không lồng):
  ```
  ┌ Backups ─────────────────────────────────────────────────────────────────────┐
  │ [filter file…]        │ 20261111-120412 · 2 files                            │
  │ ★ original (pinned)   │  script/m10_00_00_00.luabnd.dcx                      │
  │ 2026-11-11 12:04  2   │   └ 506300_battle.lua   +12 −3   [Diff] [Restore]    │
  │ 2026-11-11 11:40  1   │  chr/c5060.anibnd.dcx                                │
  │ 2026-11-11 10:02  3   │   └ TAE 3002: SpEffect 5060040 start 60→57 f         │
  │ …                     │                                        [Restore all] │
  │ Keeping last 50 · Settings…                                                  │
  └──────────────────────────────────────────────────────────────────────────────┘
  ```
  Diff Lua dạng text; diff TAE dạng **danh sách thay đổi** dễ đọc. Restore = tạo backup của bản hiện tại trước rồi mới restore (undo được bằng chính backup browser). Confirm inline.
- Status bar luôn có chỉ báo watcher: `⟳ watching` / `⏸ watcher paused` (khi app đang save).

### 2.9 Menu (gọn)
- **File**: New Project…, Open Project…, Recent ▸, Add Entity…, Save `Ctrl+S`, Save All `Ctrl+Shift+S`, Backups…, Close Project, Settings…, Exit.
- **Edit**: Undo, Redo, Cut/Copy/Paste, Find `Ctrl+F`, Replace `Ctrl+H`, Toggle comment `Ctrl+/`.
- **View**: Explorer `Ctrl+B`, Right panel `Ctrl+Alt+B`, Bottom panel `Ctrl+J`, Combo graph, Acts, Problems, Viewport options ▸ (Hitboxes, SpEffects, Bullets, Blending, Grid, Skeleton, DummyPoly, Sound), Reset Layout.
- **Go**: Goal… `Ctrl+Shift+O`, Line… `Ctrl+G`, Definition `F12`, Usages `Shift+F12`, Back `Alt+←`, Forward `Alt+→`, Animation… `Ctrl+Shift+A`, Next/Prev problem `F8`/`Shift+F8`.
- **Help**: Shortcuts, About (credit DSAS, SoulsFormats, ImGui, ImGuiColorTextEditNet…).

---

## 3. Lua code editor widget (rủi ro lớn nhất)

### 3.1 Các lựa chọn
| Lựa chọn | Mô tả | Ưu | Nhược |
|---|---|---|---|
| A. `ImGui.InputTextMultiline` | Widget có sẵn | Không thêm code; IME/clipboard có sẵn | **Không tô màu**, không gutter, không marker, không fold, không autocomplete; buffer cố định phải callback resize; undo nội bộ không điều khiển được; mẹo "vẽ chữ màu đè lên" rất dễ lệch khi cuộn/chọn. **Loại.** |
| B. **ImGuiColorTextEditNet** (csinkers, MIT, NuGet 0.1.9 – 10/2025) | Port C# của ImGuiColorTextEdit (BalazsJako) | Đã có: undo/redo, selection/clipboard, UTF-8, error markers (nền đỏ + hover message), breakpoint gutter, palette, highlighter cắm được (`ISyntaxHighlighter`), keybind tách riêng (`Input/EditorKeybind*`), có **unit test** (insert/delete/undo/selection/clipboard), render theo dòng hiển thị | Chưa có: find/replace, goto line, autocomplete, folding, squiggle theo range; regex highlighter chưa port; README ghi "có thể còn regression". **NuGet yêu cầu ImGui.NET ≥ 1.91.6.1** (0.1.3 cần ≥ 1.89.2) trong khi DSAS dùng **1.87.3** |
| C. BellColorTextBox.ImGuiNet (MIT, 0.1.4 – 02/2024) | Text box mới viết | Có folding, multi-caret, word wrap | Không có undo theo mô tả; find/autocomplete còn "To Do"; ít người dùng; cần ImGui.NET ≥ 1.89.7.1 |
| D. Bọc bản C++ (vd. fork goossens / santaclose có find, Lua) qua native DLL | | Nhiều tính năng | Phải build native khớp **chính xác** ImGui 1.88 docking của `cimgui.dll` và dùng chung context — rất dễ crash; khó debug. **Loại.** |
| E. Tự viết từ đầu | | Kiểm soát hoàn toàn | ~4–6k LOC chỉ để có selection/undo/clipboard đúng như B; tốn tháng đầu. |

### 3.2 Khuyến nghị: **B, vendor source (không dùng NuGet)**
1. Copy source `src/TextEdit/*` (MIT, ~30 file) vào code mới của mình (vd. `SoulsAiStudio.App/Editor/TextEdit/`), ghi credit trong `THIRD_PARTY.md`.
2. **Backport về ImGui.NET 1.87**: chủ yếu đổi tên enum modifier (1.87: `ImGuiKey.ModCtrl`…; ≥1.89: `ImGuiKey.ImGuiMod_Ctrl`…), API `Shortcut` (không có ở 1.87), vài overload `DrawList`. Ước lượng nhỏ–vừa; xác nhận bằng prototype.
   - Không chọn hướng "nâng ImGui.NET lên 1.91 cho cả DSAS": 15k dòng UI DSAS + `cimgui.dll` docking tự build → rủi ro lớn, trái nguyên tắc "không refactor lớn DSAS".
3. Thay highlighter bằng **lexer Lua của mình** (`Core/Lua/Parsing`) qua `ISyntaxHighlighter`: tô màu token + màu ngữ nghĩa (goal, `GOAL_COMMON_*`, hằng `TARGET_*`/`INTERUPT_*`, số là anim ID / SpEffect ID khi analysis biết).
4. Bổ sung (thứ tự): forward phím (§1.2) → Tab/Shift+Tab → find/replace bar → goto line → squiggle + gutter marker → autocomplete popup → folding.

### 3.3 Checklist yêu cầu
| Yêu cầu | B có sẵn? | Việc cần làm | Ước lượng |
|---|---|---|---|
| Syntax highlight Lua | Khung có (`ISyntaxHighlighter`) | Viết highlighter từ lexer mình; xử lý trạng thái nhiều dòng `--[[ ]]`, `[[ ]]` | S |
| Undo/redo | ✓ (`TextEditorUndoStack`, có test) | Gộp "typing run" thành 1 bước; cho phép đẩy edit từ code-gen (combo builder) thành 1 record | S |
| Find/replace | ✗ | Bar trên editor: case, whole word, regex, replace all (1 undo), highlight mọi kết quả | M |
| Goto line | ✗ (có API set cursor) | Ô nhập nhỏ + scroll center | S |
| Autocomplete popup | ✗ | Popup ImGui tại caret: `GOAL_COMMON_*`, API `ai:`/`goal:` từ `LuaApi/Sekiro.json`, hằng aicommon, anim ID trong TAE (có tên), SpEffect ID (có tên). Tab/Enter chèn, signature help khi gõ `(` | M–L |
| Error squiggles / gutter markers | Một phần (error marker = nền cả dòng; breakpoint gutter) | Squiggle theo range (vẽ zigzag DrawList), icon gutter (⚠ ⓘ ⚡ ▶), hover message | M |
| Code folding | ✗ | Map dòng hiển thị ↔ dòng thật trong renderer + movement; fold theo AST (`function…end`, `if…end`, khối generated). Phần **khó nhất** — làm sau cùng, có thể P1 | L |
| Hiệu năng 3000 dòng | Render chỉ dòng thấy được; colorize theo dòng | Lex lại từ dòng sửa tới khi state ổn định; parse AST nền (debounce 200–300 ms, thread phụ); mục tiêu < 2 ms/frame cho editor với Owl (2 731 dòng) | S (đo) |
| IME / Unicode tiếng Việt | UTF-8 ✓ | (1) thêm glyph range tiếng Việt (§1.4); (2) ký tự đi qua `TextInput` (WM_CHAR) — Unikey/EVKey gửi ký tự đã dựng sẵn nên OK; Microsoft Telex IME (TSF) cần kiểm thử, đặt `io.SetPlatformImeDataFn`/vị trí ô composition tại caret | S–M |
| Tab / indent | `\t` bị DSAS lọc | Editor tự xử lý phím Tab (indent 4 spaces như file gốc), Shift+Tab outdent | S |
| Đọc phím tắt | Phím thiếu ở ImGui | Patch `[SAS]` forward đủ phím trong `ImGuiRenderer.UpdateInput` | S |
| Read-only regions / generated block | ✗ | Không cần P0; folding dùng cho khối generated | — |

### 3.4 Prototype gate (làm trước mọi UI khác — `CLAUDE.md` §11.4)
- Mở `506300_battle.lua` (2 731 dòng, 400 `AddSubGoal`) + file stress 20 000 dòng.
- Đo: frame time editor (mục tiêu < 2 ms cho Owl, < 6 ms stress), độ trễ gõ, cuộn mượt.
- Gõ comment tiếng Việt bằng Unikey (Telex) và Microsoft Vietnamese Telex; paste text có dấu; round-trip save giữ nguyên byte phần không sửa (CRLF/LF, tab).
- Phím: Ctrl+Z/Y/F/G/S/Shift+O, F12, Alt+←/→ không kích hoạt hành vi TAE.
- Fail tiêu chí nào → quyết định lại giữa "tự viết lõi render (E) giữ lại undo/selection của B".

---

## 4. Style guide

### 4.1 Palette (dark)
Định nghĩa thành token, set một lần qua `ImGui.GetStyle().Colors` + bảng màu riêng của mình (không rải `PushStyleColor` như DSAS).

| Token | Hex | Dùng cho |
|---|---|---|
| `bg.app` | `#1E1F22` | Nền cửa sổ |
| `bg.panel` | `#25262A` | Panel, editor |
| `bg.raised` | `#2E3035` | Header panel, popup, dropdown |
| `bg.hover` | `#363840` | Hover |
| `border` | `#3A3C42` | Splitter, viền |
| `text` | `#D7DAE0` | Chữ chính |
| `text.dim` | `#8B9099` | Chữ phụ, ID, chữ ký function, số dòng |
| `text.disabled` | `#5C6068` | Disabled / read-only label |
| `accent` | `#4C9AFF` | Focus, selection, link, current goal |
| `ok` | `#5BBE7A` | ✓ hợp lệ, saved |
| `info` | `#5AA9E6` | Info |
| `warn` | `#E5B454` | Warning, conflict banner |
| `error` | `#E5534B` | **Chỉ** parse error / IO error |
| `dirty` | `#E0E0E0` chấm `●` | Chưa lưu |

Contrast chữ chính/nền ≥ 7:1, chữ dim ≥ 4.5:1. Không truyền tải thông tin chỉ bằng màu (luôn kèm icon/chữ).

### 4.2 Màu event / edge theo loại (dùng chung timeline, graph, inspector, gutter)
| Loại | Event Sekiro (template `Res/TAE.Template.SDT.xml`) | Màu | Hex |
|---|---|---|---|
| Attack | 1 AttackBehavior, 5 CommonBehavior, 304 ThrowAttackBehavior | đỏ cam | `#E06C4F` |
| Bullet | 2 BulletBehavior | cam vàng | `#E0A04F` |
| SpEffect | 67 AddSpEffect, 66, 401, 940… (whitelist trong GameProfile) | tím | `#B07CE8` |
| Combo window | 0 ChrActionFlag, flag 23 "End If AI ComboAttack Queued" | teal | `#3FC1B0` |
| Blend | 16 Blend | xanh dương xám | `#6C8EBF` |
| Turn | 224 SetTurnSpeed | xanh lá | `#7DBE5B` |
| i-frame / flags khác | 0 ChrActionFlag (flag khác) | xám xanh | `#7A8794` |
| Other (sound, FFX, …) | còn lại | xám | `#6B6F76` |
| Edge Sequence / Random / Condition | — | xám sáng / trắng / vàng nhạt | `#A0A4AB` / `#D7DAE0` / `#D9C27A` |
| Edge SpEffect | — | = SpEffect | `#B07CE8` (On hit: nét đứt) |
- Read-only: cùng màu, 35 % alpha + sọc chéo. Selected: viền `accent`.
- Danh sách mapping event→loại nằm trong `GameProfile`, UI chỉ đọc "loại".

### 4.3 Kích thước & khoảng cách (× DPI)
- Font UI 16 px (giảm từ 19 px của DSAS để hiện được nhiều hơn; cho chỉnh 14–20 trong Settings). Font editor 15 px Iosevka Term, line height 1.25.
- Lưới 4 px: padding panel 8, khoảng giữa control 4/8, header panel 26 px, dòng list 22 px, dòng timeline 20 px.
- Rounding 3 px cho button/popup (DSAS = 0; làm mềm nhẹ), border 1 px (DSAS = 2).

### 4.4 Icon
- Dùng ký tự Unicode có sẵn trong font (đã kiểm: `▸ ▾ › → ● ✓`); bộ icon chính thức nên là **một icon font** (vd. Font Awesome Free / Codicons, merge mode vào atlas) — chỉ dùng ~20 icon: play, pause, step, folder, file, save, search, filter, pin, settings, warning, info, error, lightning (SpEffect), lock, pencil, link, history, graph, eye.
- Icon luôn đi kèm chữ hoặc tooltip. Không dùng emoji màu.

### 4.5 Quy ước câu chữ (UI tiếng Anh)
- Menu & nút: **Title Case**, động từ đứng đầu (`Save All`, `Take Theirs`). Câu thông báo: sentence case, không chấm than.
- Tên trước, ID sau, ID mờ: `Kengeki branch A · 5060040`. Khi không có tên: `SpEffect 5060040`.
- Thời gian: frame trước, giây trong ngoặc: `f 41 (1.367 s)`.
- Thông điệp diagnostics: *cái gì* + *ở đâu đã tìm* + *khả năng*; không phán xét ("may", "not found in loaded data"). Không dùng "error", "invalid", "dead" cho phân tích tĩnh.
- Nút hủy luôn là `Cancel`; xác nhận phá hủy dùng động từ cụ thể (`Delete 2 Events`), không dùng `OK`/`Yes`.

### 4.6 Trạng thái rỗng / đang tải / lỗi
| Tình huống | Hiển thị |
|---|---|
| Chưa chọn anim | Timeline: "Select an attack in Lua or pick an animation ▾" + nút dropdown |
| Entity không có anibnd | Viewport/Timeline: "No animation file for c5060 — Locate… " |
| Lua đang decompile / parse | Editor hiện text ngay; outline hiện skeleton xám + "Analyzing…" (không chặn gõ) |
| Load model/anim | Progress mảnh ở header viewport + %; vẫn dùng được phần khác |
| Graph quá lớn | "312 nodes — showing 2 levels. Expand ▸" |
| Không có Problem | "No problems found in loaded data." |
| Parse lỗi Lua | Error ở Problems + squiggle đỏ; outline giữ kết quả lần parse thành công gần nhất (đánh dấu "stale") |
| Lỗi IO | Toast đỏ với hành động (`Retry`, `Show Log`), không modal |

---

## 5. Rủi ro UX & câu hỏi mở

### 5.1 Rủi ro
1. **Editor widget** (§3): backport ImGuiColorTextEditNet về 1.87 + folding + autocomplete. Giảm rủi ro bằng prototype gate §3.4.
2. **Xung đột phím** giữa editor và TAE của DSAS (đọc phím XNA toàn cục; ImGui thiếu phím) → cần patch input + shortcut router sớm, nếu không người dùng gõ chữ sẽ kích hoạt mute/solo/xóa event.
3. **Undo nhiều nơi** (Lua buffer, TAE per-anim, combo builder 3 nơi) — người dùng dễ "undo nhầm panel". Đề xuất: Ctrl+Z theo focus + toast "Undid: Move SpEffect 5060040 (Timeline)".
4. **Font tiếng Việt** hiện hiển thị `?` (đã xác minh) — sửa nhỏ nhưng bắt buộc.
5. **Combo graph rối** với file lớn (Owl `Goal.Interrupt` ~690 dòng, 52 nhánh SpEffect) → mặc định scope theo goal, mở 2 cấp, filter loại edge.
6. **Hiệu năng**: MonoGame viewport + editor + parse nền cùng UI thread; parse phải ở thread phụ.
7. Layout tự tính phải hợp tác với code DSAS dựa vào rect window ImGui (Viewport, Graph) — kiểm tra sớm.

### 5.2 Câu hỏi cho người dùng
1. **Observe SpEffect**: `CLAUDE.md` §4.1 ghi `AddObserveSpecialEffectAttribute` ở **top-level**, nhưng trong `506300_battle.lua` (Owl) 71 lệnh observe nằm **trong `Goal.Activate`** (dòng 21–91). App nên chấp nhận cả hai vị trí? (Ảnh hưởng diagnostics "thiếu observe" và chỗ combo builder chèn observe.)
2. UI tiếng Anh hoàn toàn được chứ? Có muốn tooltip / diagnostics song ngữ (Anh + Việt) ở P2 không?
3. Có cần **word wrap** trong editor không (file decompile có ~37 dòng > 120 ký tự trong Owl)? Mặc định đề xuất: tắt.
4. Preview anim khi di chuyển cursor: mặc định **tắt** (click ▶ mới play) — đồng ý?
5. Kích thước màn hình bạn hay dùng (1080p / 1440p / laptop)? Để chốt mặc định cột.
6. Timeline gom hàng theo loại event (mặc định) hay giữ track gốc của DSAS?
7. Có muốn phím tắt giống VSCode hoàn toàn (Ctrl+P mở file nhanh, Ctrl+Shift+P command palette) không, hay chỉ những phím trong doc này?

---

## Phụ lục A — Bảng phím tắt (theo focus)
| Phím | Editor | Timeline | Graph | Global |
|---|---|---|---|---|
| Ctrl+S / Ctrl+Shift+S | | | | Save / Save All |
| Ctrl+Z / Ctrl+Y | Undo/redo text | Undo/redo TAE (anim) | Undo combo builder | |
| Ctrl+F / Ctrl+H | Find / Replace | — | Find node | |
| Ctrl+G | Go to line | — | — | |
| Ctrl+Shift+O | | | | Goal navigator |
| Ctrl+Shift+A | | | | Go to animation |
| F12 / Shift+F12 | Definition / Usages | Go to Lua usage | Go to code | |
| Alt+← / Alt+→ | Lịch sử vị trí code | Lịch sử anim (DSAS) | Lịch sử chọn | |
| Ctrl+C / X / V | Text | Event (V tại playhead) | — | |
| Ctrl+Shift+V | — | Paste ở thời gian gốc | — | |
| Delete | Xóa ký tự | Xóa event (confirm inline) | — | |
| Space | Gõ space | Play/Pause | — | |
| ← / → | Caret | ± 1 frame | Pan | |
| Ctrl+Space | Autocomplete | | | |
| Ctrl+/ | Toggle comment | | | |
| F8 / Shift+F8 | | | | Next / prev problem |
| Ctrl+B / Ctrl+Alt+B / Ctrl+J | | | | Toggle left / right / bottom |
| F | — | — | Fit | |

## Phụ lục B — Nguồn tham khảo
- ImGuiColorTextEditNet: https://github.com/csinkers/ImGuiColorTextEditNet · NuGet https://www.nuget.org/packages/ImGuiColorTextEditNet
- BellColorTextBox.ImGuiNet: https://www.nuget.org/packages/BellColorTextBox.ImGuiNet
- DSAS code đã đọc: `ImguiOSD/OSD.cs`, `ImguiOSD/MenuBar.*.cs`, `ImguiOSD/Window.*.cs`, `ImGuiRenderer.cs`, `Main.cs` (font), `ColorConfig.cs`, `TaeEditor/NewGraphInput.cs`, `TaeEditor/TaeEditorScreen.cs`, `Res/TAE.Template.SDT.xml`.
