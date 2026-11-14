# CLAUDE.md — Souls AI Studio (tên tạm)

> Trạng thái: **PLANNING**. Chưa có code. Game: Sekiro · UI: ImGui · Codebase: fork DSAS. Các mục `[TBD]`/`[xác minh]` chốt sau khi đọc code DSAS.

## 1. Mục tiêu dự án

Một desktop app để **đọc, hiểu và chỉnh sửa Lua AI** của enemy/NPC trong game FromSoftware.
Lấy cảm hứng từ DS Anim Studio (DSAS) về phần load model + animation (Havok) và viewport 3D,
nhưng **trọng tâm là AI script, không phải chỉnh animation**.

Viewport và dữ liệu param chỉ là *công cụ hỗ trợ* để người mod thấy được AI đang ra lệnh gì:
attack nào, hitbox ở đâu, SpEffect gì, combo nối ra sao, chỗ nào combo bị interrupt.

### Không làm (non-goals)
- Không chỉnh sửa animation keyframe/HKX. Chỉ sửa TAE **có giới hạn** (xem mục 5, "P1.5 — TAE editor giới hạn"); những thứ ngoài giới hạn đó thì dùng DSAS.
- Không tạo event TAE mới từ đầu, không đổi event type, không sửa value tùy ý của mọi event.
- Không chỉnh sửa model/texture/map.
- Không làm param editor đầy đủ (chỉ đọc + sửa nhanh các field liên quan AI). Param editor đầy đủ thì dùng Smithbox/DSMapStudio.

## 2. Game hỗ trợ

- Game mục tiêu đầu tiên: **Sekiro: Shadows Die Twice** (đã chốt).
- Chỉ Sekiro được test và hỗ trợ cho đến khi P0–P2 xong. Code vẫn đi qua `IGameProfile` (đường dẫn file, tên param, paramdef, TAE event ID, Lua API) để sau này thêm game khác, nhưng **không viết code cho game khác** trước khi được yêu cầu.

### Ghi chú riêng cho Sekiro
- File game cần được unpack (UXM). Mod chạy qua **Sekiro Mod Engine**. Mod của người dùng: `C:\Program Files (x86)\Steam\steamapps\common\Sekiro\mods\Severance-dev\` (đã xác minh). App chỉ ghi vào mod folder.
- Params: `param/gameparam/gameparam.parambnd.dcx` (không phải `regulation.bin`) [xác minh].
- Lua AI (**đã xác minh** trên mod Severance-dev):
  - Luabnd **theo map**, không theo enemy: `script/m10_00_00_00.luabnd.dcx`, `script/m11_02_00_00.luabnd.dcx`, … + `script/aicommon.luabnd.dcx`.
  - Được unpack bằng **WitchyBND** thành `script/<map>-luabnd-dcx/` gồm `_witchy-bnd4.xml` (metadata BND4: ID, flags, root, DCX_DFLT), `<map>.luagnl`, `<map>.luainfo`, `out/bin/<npcId>_battle.lua` / `_logic.lua`.
  - **Game chạy được Lua plain text** trong luabnd → save luôn ghi plain text, không cần compile lại.
  - **Một luabnd chứa lẫn cả hai loại**: chỉ những file người dùng đã sửa là plain text, còn lại là bytecode (header `1B 4C 75 61 50`). Vd. m10: 2 plain / 54 bytecode; m11_02: 7 / 49; m25: 1 / 66; aicommon: 2 / 103. → App phải đọc được cả hai trong cùng một bnd; mở file bytecode lần đầu thì decompile bằng DSLuaDecompiler, khi save file đó thành plain text. Các file không sửa giữ nguyên bytecode.
  - Plain text hiện có: ASCII, LF, không BOM, có newline cuối → writer giữ nguyên các thuộc tính này.
  - Ngoài `_battle.lua` / `_logic.lua` còn có loại khác, vd. `000120_platoon.lua`.
  - **Compression không cố định**: m10, m11_02, aicommon là `DCX_DFLT`; **m25 là `DCX_KRAK` (Oodle)**. Writer phải giữ đúng compression ghi trong `_witchy-bnd4.xml`. Oodle cần `oo2core_6_win64.dll` (có trong thư mục cài Sekiro) — app tìm dll từ game folder của project.
  - BND4 Sekiro: version `07D7R6`, format `IDs, Names1, Names2, Compression`, unicode, extended=4, flag `Flag1`; tên entry = `<root>` trong xml + `\` + `<path>`; `.luagnl` / `.luainfo` là 2 entry cuối.
  - Repack DCX_DFLT chỉ **giống nội dung**, không giống từng byte so với WitchyBND (zlib khác) → tiêu chí test là nội dung entry giống nhau.
  - Repack: dùng SoulsFormats in-process, **tôn trọng `_witchy-bnd4.xml`** (ID, flags, root, compression) để kết quả tương thích WitchyBND; hoặc gọi WitchyBND CLI nếu người dùng cấu hình. Cạnh file `.luabnd.dcx` thường có `.bak` của WitchyBND — không đụng vào.
- Cơ chế riêng Sekiro cần hiển thị trong inspector: **posture damage**, **perilous attack (危)** (qua SpEffect), thrust/sweep/grab (Mikiri / jump-able), deflect window, deathblow.
- TAE Sekiro chạy 30 fps; snap timeline theo đó.

## 3. Tech stack (đã chốt: fork DSAS)

Dự án là **fork của DS Anim Studio**. Không viết lại những gì DSAS đã có (load HKX/FLVER/TAE, renderer, ImGui shell). Việc chính: **thêm** phần AI và **cắt/khóa** bớt tính năng TAE thừa.

| Phần | Lựa chọn | Ghi chú |
|---|---|---|
| Ngôn ngữ / build | Theo DSAS (C#, target framework hiện có của DSAS) | Không nâng .NET version trừ khi cần |
| Đọc file game | SoulsFormats (bản đi kèm DSAS) | |
| Havok (HKX) | Code HKX / SoulsAssetPipeline có sẵn trong DSAS | Read-only |
| Viewport 3D | Renderer MonoGame của DSAS | Thêm overlay: hitbox, range ring |
| UI shell | **ImGui.NET như DSAS** (đã chốt) | |
| Lua editor | **Copy source ImGuiColorTextEditNet (MIT)** vào code của mình, chỉnh cho ImGui.NET **1.87.3** của DSAS (native 1.88 docking). Không dùng NuGet (cần ImGui.NET ≥ 1.91.6.1, nâng ImGui cho cả DSAS quá rủi ro). Lexer Lua của mình cho highlight. | Có sẵn undo/redo, selection, clipboard, UTF-8, gutter, error marker, chỉ vẽ dòng nhìn thấy. Phải tự thêm: find/replace, goto line, autocomplete, squiggle, Tab, folding. **Rủi ro lớn nhất** — prototype gate trong `docs/ux/ui-spec.md` §3.4 (file Owl 2731 dòng + file 20k dòng, gõ tiếng Việt Unikey/Telex, phím tắt không lọt sang timeline). |
| Lua parse / phân tích tĩnh | Parser Lua viết riêng hoặc port (cần AST đầy đủ + vị trí dòng/cột) | Để dựng graph goal/combo và refactor an toàn |
| Lua chạy thử (simulation) | MoonSharp hoặc NLua + stub Game API | Chạy AI "khô" để xem goal nào được chọn |
| Decompile Lua bytecode | **DSLuaDecompiler** bản build sẵn tại `C:\Users\erizo\Downloads\modding\DSLuaDecompiler` | Xem mục 3.1 |

### 3.1 DSLuaDecompiler
- Bản có sẵn: `DSLuaDecompiler.exe` + `LuaDecompilerCore.dll`, target **.NET 7** (cần .NET 7 runtime).
- CLI: `DSLuaDecompiler.exe <files...> [-o <output>] [-c]` (`-c` = in ra console).
- Cách tích hợp mặc định: **gọi exe như process ngoài** (an toàn, không phụ thuộc target framework của DSAS).
- **Đã đóng gói kèm app** (2026-11-13): bản build ở trên (fork ElaDiDu, có `Resources/VariableNamePatterns.json` cho tên arg dễ đọc) nằm trong `SoulsAIStudio/Tools/DSLuaDecompiler/` (MIT, kèm LICENSE; xem `THIRD_PARTY.md`), copy cạnh exe khi build/publish. Thứ tự tìm: `DecompilerPath` của project → bản đi kèm → `DecompilerPath` trong settings. Chạy với `DOTNET_ROLL_FORWARD=Major` nên máy chỉ có .NET 9/10 vẫn chạy được (đã so: 272 file bytecode cho output giống hệt nhau trên .NET 7/9/10).
- Chỉ khi DSAS chạy .NET ≥ 7 mới cân nhắc reference thẳng `LuaDecompilerCore.dll`.
- Decompile chỉ chạy **một lần** khi mở một file bytecode lần đầu; kết quả plain text được cache trong project (mục P0 Project). Thực tế phần lớn file trong luabnd là bytecode (xem mục 2), nên decompile là đường dùng thường xuyên, không phải ngoại lệ.

## 4. Dữ liệu game cần đọc

### 4.1 Lua AI
- Vị trí: xem "Ghi chú riêng cho Sekiro" (luabnd theo map, unpack WitchyBND, `out/bin/<npcId>_battle.lua`).
- Mỗi enemy: `<npcId>_battle.lua` và thường có `<npcId>_logic.lua`.
- Hai dạng: **bytecode** (game gốc) và **plain text** (mod). App đọc cả hai; save luôn ghi plain text.
- Đọc thêm từ `aicommon`: định nghĩa `GOAL_COMMON_*`, hằng số `INTERUPT_*`, `TARGET_*`, `AI_DIR_*`, để autocomplete + tooltip.

#### Cấu trúc battle script Sekiro (đã xác minh trên `506300_battle.lua`, Owl)
- Đầu file: `RegisterTableGoal(GOAL_X, "GOAL_X")`, `REGISTER_GOAL_NO_UPDATE(GOAL_X, true)`.
- `ai:AddObserveSpecialEffectAttribute(TARGET_ENE_0 | TARGET_SELF, <id>)` được gọi **bên trong `Goal.Activate`** (đã xác minh trên cả 3 file mẫu: Owl 71 lệnh, Isshin 145, True Monk 12). Analysis vẫn chấp nhận mọi vị trí (Activate, Initialize, hàm khác) — tìm theo lệnh gọi, không theo vị trí. Combo builder chèn observe mới vào `Goal.Activate`, cạnh các observe sẵn có.
- `Goal.Initialize`, `Goal.Activate` (chọn act bằng bảng weight), `Goal.Act01..ActNN`, `Goal.Interrupt`, `Goal.Parry`, `Goal.ShootReaction`, `Goal.Kengeki_Activate`, `Goal.Kengeki01..`, `Goal.Update`, `Goal.Terminate`.
- SpEffect interrupt API (**đã xác minh**):
  ```lua
  local interruptSpeffect = arg1:GetSpecialEffectActivateInterruptType(0)
  if arg1:IsInterupt(INTERUPT_ActivateSpecialEffect) then
      if interruptSpeffect == 3506000 then
          arg2:ClearSubGoal()
          ...
  ```
- `AddSubGoal(...)` có thể được **gọi chuỗi method**: `arg2:AddSubGoal(GOAL_COMMON_ComboRepeat, 1, 3012, TARGET_ENE_0, 9999, 0, 0):TimingSetNumber(13, 0, AI_TIMING_SET__ACTIVATE)` → parser/analysis phải xử lý.
- Điều kiện nhánh không chỉ random: `arg1:GetNinsatsuNum()`, `HasSpecialEffectId(TARGET_SELF, id)`, distance, HP…; có cả nested if trong nhánh random.
- Tên biến là tên do decompiler sinh (`f2_local6`, `f59_arg1`) hoặc do người mod đặt lại (`interruptRand`, `getDist`) → analysis dựa vào data-flow, **không dựa vào tên biến**.

### 4.2 Params (`regulation.bin` hoặc `gameparam.parambnd.dcx`)
Chuỗi tham chiếu chính cần dựng:

```
NpcParam ──thinkId──▶ NpcThinkParam ──logicId/battleGoalId──▶ Lua goal
   │
   └─ behaviorVariationId ─▶ BehaviorParam (NPC) ──refId──▶ AtkParam_Npc  (hitbox: dmyPoly, radius, damage, guardBreak...)
                                               ├─▶ Bullet ─▶ AtkParam / SpEffect
                                               └─▶ SpEffectParam
TAE event (InvokeAttackBehavior / Bullet / SpEffect) ──behaviorJudgeId──┘
```

- Cần paramdef tương ứng game (lấy từ Paramdex / Smithbox, để trong `Resources/Paramdex/<Game>/`).
- Encrypt `regulation.bin` (ER/AC6) xử lý qua SoulsFormats.

### 4.3 Animation (HKX chỉ đọc, TAE sửa giới hạn)
- `chr/c####.anibnd.dcx`: HKX skeleton + animation, TAE (events).
- `chr/c####.chrbnd.dcx`: FLVER model + dummy polys (dmyPoly → vị trí hitbox).
- TAE event Sekiro (từ `Res/TAE.Template.SDT.xml` của DSAS, đã xác minh). Để trong `GameProfile.Sekiro`:

  | Event | Type | Field |
  |---|---|---|
  | JumpTable (`ChrActionFlag`) | **0** | `s32@0x0 FlagType`; **FlagType 23 = "End If AI ComboAttack Queued"** |
  | ActivateJumpTableEarly | 300 | |
  | Blend | **16** | (không field) |
  | AddSpEffect | **67** | `s32@0x0 SpEffectID` |
  | SpEffect khác DSAS cũng coi là SpEffect | 66, 302, 401, 331, 797; 940 = BehaviorParam_AddSpEffect | |
  | AttackBehavior | **1** | `BehaviorJudgeID s32@0x8` |
  | Bullet | **2** (4 = midair) | `DummyPolyID@0x0`, `BehaviorJudgeID@0x8` |
  | CommonBehavior | 5 | `BehaviorJudgeID@0x4` |
  | Throw | 304 | |

  Tên field không thống nhất giữa các event (`SpEffectID` vs `SpEffectId`) → **đọc field theo offset**, không theo tên.

## 5. Tính năng (theo thứ tự ưu tiên)

### P0 — Nền tảng
1. **Project system (New / Open / Recent Project)** — để không phải nhập lại input mỗi lần mở app.
   - File project: `<tên>.sasproj` (JSON, người đọc được, path tương đối khi có thể). Lưu:
     - đường dẫn game (unpacked), mod folder (Mod Engine `mods/`), đường dẫn decompiler,
     - danh sách **entity** trong project: `c####`, file Lua battle/logic đã chọn, anibnd, chrbnd, NpcParam ID liên quan,
     - trạng thái làm việc: tab đang mở, anim đang chọn, vị trí cursor, layout panel, bookmark.
   - **New Project wizard** (1 màn hình, không lồng dialog): chọn game folder → mod folder → tick các Lua AI / entity muốn làm việc (có search) → app tự tìm anibnd/chrbnd/param tương ứng → decompile Lua → tạo project.
   - **Open Project / Recent Projects** trên màn hình khởi động; mở lại đúng trạng thái lần trước.
   - Thêm/bớt entity vào project bất cứ lúc nào.
   - Cấu trúc thư mục project:
     ```
     MyProject/
       MyProject.sasproj
       lua/            # Lua plain text đang làm việc (nguồn sự thật khi sửa)
       backups/        # xem mục Backup
       cache/          # decompile gốc, index param, ... (xóa được, tự tạo lại)
     ```
   - Luôn đọc mod folder trước, fallback về game. Không bao giờ ghi vào thư mục game gốc.
2. **Entity browser**: list `c####` kèm tên (từ NpcParam/FMG), lọc theo NpcThinkParam để biết dùng Lua goal nào.
3. **Lua editor**: syntax highlight, autocomplete từ aicommon + game API, **Goal navigator** (breadcrumb + outline, xem mục 5b), go-to-definition (goal, hàm), find usages, diagnostics cơ bản (gọi hàm không tồn tại, goal ID sai, anim ID không có trong TAE).
4. **Save an toàn**: ghi lại vào luabnd / anibnd (repack BND4 + DCX) trong mod folder. Không bao giờ ghi đè file game gốc. Save = ghi file tạm rồi rename (atomic), lỗi giữa chừng không làm hỏng file cũ.
5. **Backup**
   - **Snapshot gốc**: lần đầu một file được đưa vào project, lưu bản gốc vào `backups/original/` — không bao giờ tự xóa.
   - **Backup mỗi lần save**: `backups/<yyyyMMdd-HHmmss>/<đường dẫn tương đối>` cho mọi file bị ghi (luabnd, anibnd, Lua plain text). Giữ N bản gần nhất (mặc định 50, chỉnh trong settings).
   - **Autosave buffer chưa lưu** (mỗi ~60s) vào `cache/autosave/` để khôi phục khi crash; mở project sẽ hỏi khôi phục nếu có.
   - **Backup browser**: list theo thời gian, xem diff với bản hiện tại (Lua dạng text; TAE dạng danh sách event thay đổi), Restore 1 file hoặc cả snapshot.
6. **Tự reload trong app (file watcher)**
   - Theo dõi file Lua trong project, luabnd/anibnd/chrbnd trong mod folder và gameparam. File đổi từ ngoài (VSCode, Smithbox, DSAS, …) → tự reload; nếu buffer đang có thay đổi chưa save thì hiện banner chọn *Giữ của tôi / Lấy bản mới / Xem diff*, không tự ghi đè.
   - Sau mỗi save: phân tích lại AST, combo graph, diagnostics, param resolve ngay (không cần mở lại project).
   - In-game hot reload: **tạm bỏ** (xem mục 12).

### P1 — Hiểu AI bằng hình ảnh
7. **Viewport 3D** (kế thừa ý tưởng DSAS): load model + skeleton + animation, play/scrub timeline.
8. **Attack inspector**: click vào một lệnh `AddSubGoal(GOAL_COMMON_Attack..., animId, ...)` trong editor → viewport play anim đó, overlay:
   - hitbox (dmyPoly + radius từ AtkParam) theo từng frame active,
   - SpEffect / Bullet được bắn,
   - bảng damage/poise/guard break/range từ param.
9. **Act table view**: parse hàm chọn act (bảng `act01..actN` với weight theo khoảng cách) → hiển thị bảng: act, điều kiện (distance, góc, HP...), weight, animation, có thể chỉnh weight trực tiếp → sinh lại code.
10. **Distance/range overlay**: vẽ vòng tròn khoảng cách trên mặt đất trong viewport, đối chiếu với điều kiện distance trong Lua và range thật của hitbox.

### P1.5 — TAE editor giới hạn
Giữ lại phần cần thiết của DSAS, không hơn. Timeline hiển thị **tất cả** event (read-only), nhưng chỉ cho sửa:

| Thao tác | Áp dụng cho | Ghi chú |
|---|---|---|
| Kéo/sửa **StartTime / EndTime** | Mọi event có duration, ưu tiên: SpEffect, Blend, **End If AI ComboAttack Queued** (JumpTable), cửa sổ attack behavior | Kéo trên timeline + ô nhập số; snap theo frame (30/60 fps tùy game) |
| Sửa **value** | **Chỉ event SpEffect** (SpEffect ID) | Ô nhập có lookup tên/mô tả từ SpEffectParam, cảnh báo nếu ID không tồn tại |
| **Copy / Paste** event | Mọi event | Copy 1 hoặc nhiều event từ anim A → paste sang anim B (giữ thời gian gốc hoặc dán tại vị trí playhead) |
| **Delete** event | Mọi event | Có confirm |
| Undo / Redo | Tất cả thao tác trên | Bắt buộc |

- Danh sách "event được phép sửa value" và "event ưu tiên hiển thị" đặt trong `GameProfile` (whitelist), không hardcode trong UI.
- Đọc/ghi TAE bằng SoulsFormats + TAE template (lấy từ DSAS) để biết tên event và field. Round-trip test: load → save không sửa gì → file giống hệt.
- Ghi vào anibnd trong mod folder, backup tự động như Lua.
- Liên kết với AI: chọn một attack trong Lua → timeline nhảy tới anim đó; event JumpTable "ComboAttack Queued" được tô nổi để thấy cửa sổ combo nối.

### P2 — Combo (bao gồm combo rẽ nhánh theo SpEffect)

**Đây là điểm khác biệt chính so với DSAS.** DSAS chỉ hiểu combo thẳng
(`ComboAttack → ComboRepeat → ComboFinal`). Combo thật trong Sekiro **rẽ nhánh giữa animation qua SpEffect**:

1. Một goal add chuỗi combo, có thể có nhánh random:
   ```lua
   Goal.Kengeki03 = function(ai, goal, _)
       local r = ai:GetRandam_Int(1, 100)
       goal:ClearSubGoal()
       goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3002, TARGET_ENE_0, 9999, 0, 0)
       if r <= 50 then
           goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3016, TARGET_ENE_0, 9999, 0, 0)
       else
           goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3048, TARGET_ENE_0, 9999, 0, 0)
       end
   end
   ```
2. Trong TAE của anim **3002** có event **SpEffect 5060040** tại ~2.0s.
3. Trong interrupt handler của Lua, SpEffect đó **cắt combo hiện tại** và mở chuỗi mới (lại có random):
   ```lua
   elseif interruptSpeffect == 5060040 then
       goal:ClearSubGoal()
       if interruptRand <= 25 then     goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3037, ...)
       elseif interruptRand <= 50 then goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3018, ...)
       elseif interruptRand <= 75 then goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3047, ...)
       else                            goal:AddSubGoal(GOAL_COMMON_ComboRepeat, 10, 3044, ...)
       end
   ```
4. Anim 3037/3018/… lại có thể chứa SpEffect khác → rẽ tiếp. Combo là **đồ thị (có thể có vòng)**, không phải chuỗi thẳng.

#### Mô hình dữ liệu: `ComboGraph`
- **Node `AnimStep`**: một `AddSubGoal` cụ thể — goal type (`ComboAttack*`, `ComboRepeat`, `ComboFinal`, `AttackImmediateAction`, `Attack`, …), anim ID, target, các tham số, vị trí trong source (file/dòng) để nhảy tới.
- **Edge `Sequence`**: anim kế tiếp trong cùng subgoal queue; nối tại cửa sổ "End If AI ComboAttack Queued" (JumpTable) trong TAE của anim trước.
- **Edge `RandomBranch` / `ConditionBranch`**: từ chuỗi `if/elseif/else` → xác suất tính từ ngưỡng `rand <= N` (25/25/25/25, 50/50…). Điều kiện không phải random (distance, HP, góc, `HasSpecialEffectId`…) hiển thị nguyên biểu thức, không gán xác suất.
- **Edge `SpEffectInterrupt`**: từ node anim X → tới các chuỗi mới trong nhánh `== S` của handler. Có cờ `ClearsQueue` (có `ClearSubGoal()` hay không — không clear thì chuỗi mới được nối thêm chứ không thay thế). Mỗi edge mang **nguồn gốc (`SpEffectSource`)** của S, vì S **không chỉ đến từ TAE**:

  | Nguồn | Cách S được gán | Ai nhận S | Thời điểm rẽ | Tính chất |
  |---|---|---|---|---|
  | `TaeEvent` | Event SpEffect trong TAE của anim X | Bản thân enemy | Start time của event | Chắc chắn xảy ra (nếu anim chạy tới đó) |
  | `OnHit` | TAE AttackBehavior/Bullet của X → BehaviorParam → AtkParam / Bullet → field SpEffect khi trúng | **Mục tiêu bị trúng** (thường là player) hoặc bản thân, tùy field | Cửa sổ hitbox active của X | **Có điều kiện: chỉ khi đòn trúng** |
  | `BehaviorParam` / `Bullet` khác | SpEffect gán trực tiếp khi behavior/bullet kích hoạt | Theo field | Thời điểm event behavior/bullet | Chắc chắn hoặc có điều kiện tùy field |
  | `SpEffectChain` | SpEffect khác sinh ra S (field chain/cycle trong SpEffectParam) | Theo SpEffect cha | Theo SpEffect cha | Gián tiếp |
  | `Unknown` | Không truy được nguồn nào trong dữ liệu đã load | — | — | Không kết luận được |

  Ví dụ **Owl (Father)**: đòn đạp tới → hitbox trúng player → AtkParam gán SpEffect S lên **player** → Lua thấy S (observe trên `TARGET_ENE_0`) → `ClearSubGoal` + nhảy bật ra sau + phóng shuriken. S không có trong TAE, chỉ nằm trong param. Graph phải hiển thị edge này là `OnHit` từ anim đạp, nhãn "khi trúng".
- Mỗi edge giữ ngược lại vị trí source để click là nhảy tới dòng code.

#### Phân tích (Core/Lua/Analysis/Combo)
- Tìm các goal function có `AddSubGoal` tạo combo; theo dõi biến local đơn giản (data-flow trong một function): `local r = ai:GetRandam_Int(1,100)` → biết `r` là random 1..100; `local s = ai:GetSpecialEffectActivateInterruptType(0)` → biết `s` là SpEffect interrupt ID (API đã xác minh).
- Tìm `Goal.Interrupt`, nhánh `IsInterupt(INTERUPT_ActivateSpecialEffect)` và tách bảng `SpEffectID → các nhánh hành động`. Nhánh có thể là `Replanning()` (không phải combo) → ghi nhận là edge loại "Replan".
- **Index nguồn SpEffect** cho từng anim X của entity (dựng một lần khi load, cập nhật khi TAE/param đổi):
  1. Event SpEffect trực tiếp trong TAE của X → `TaeEvent`.
  2. Event AttackBehavior / Bullet / CommonBehavior trong TAE của X → resolve BehaviorParam → AtkParam / Bullet → mọi field SpEffect (khi trúng, gán cho bản thân/mục tiêu…) → `OnHit` / `BehaviorParam` / `Bullet`. Danh sách field SpEffect cụ thể của từng param để trong `GameProfile.Sekiro` **[xác minh tên field theo paramdef Sekiro]**.
  3. Mở rộng theo chain trong SpEffectParam → `SpEffectChain` (giới hạn độ sâu, chống vòng).
- Với mỗi S trong bảng handler, tra index → tạo edge `SpEffectInterrupt` từ mọi anim X có nguồn S, kèm source + thời điểm + điều kiện ("khi trúng").
- **Ai nhận S phải khớp với target được observe**: S gán lên player phải được observe trên `TARGET_ENE_0`, S gán lên bản thân phải observe trên `TARGET_SELF`. Không khớp → cảnh báo.
- Đối chiếu đăng ký observe: SpEffect chỉ gây interrupt nếu đã được đăng ký `AddObserveSpecialEffectAttribute(<target>, S)` ở bất kỳ đâu trong file (thực tế: trong `Goal.Activate`).
- Đệ quy qua anim mới, phát hiện vòng (đánh dấu edge quay lui thay vì lặp vô hạn).
- Tính xác suất từng đường đi (tích các nhánh random), đánh dấu đường không xác định khi có nhánh điều kiện.

#### Diagnostics combo
Nguyên tắc: diagnostics combo là **gợi ý**, không phải lỗi chắc chắn — luôn ghi rõ app đã tìm ở đâu. Không được kết luận "nhánh chết" chỉ vì TAE không có S.

- Handler có `== S` nhưng **không tìm thấy nguồn nào** (TAE, AtkParam/Bullet/BehaviorParam, SpEffect chain) trong entity → mức **Info**: "Không tìm thấy nguồn của S trong dữ liệu đã load — có thể đến từ entity khác, map event (EMEVD), player, hoặc nguồn chưa được index." Không gọi là lỗi.
- Handler có S và nguồn chỉ là `OnHit` → hiển thị nhánh là **có điều kiện (khi trúng)**, không tính vào xác suất cố định của đường đi.
- Anim có SpEffect S nằm trong combo nhưng handler không xử lý S → Info (có thể là SpEffect cho mục đích khác).
- Handler có S nhưng thiếu đăng ký observe S, hoặc observe sai target (bản thân vs mục tiêu) so với nơi S được gán → Warning.
- SpEffect (TAE) hoặc cửa sổ hitbox (OnHit) nằm **sau** cửa sổ JumpTable / sau khi anim đã chuyển → interrupt có thể không bao giờ kịp xảy ra (Warning).
- Ngưỡng random không phủ hết 1..100 hoặc chồng lấn.
- Anim ID trong `AddSubGoal` không tồn tại trong TAE.

#### UI combo
11. **Combo graph view**: đồ thị node-anim; edge Sequence (liền), Random (kèm %), Condition (kèm biểu thức), SpEffect interrupt (màu SpEffect, kèm ID + tên SpEffect + thời điểm + **nguồn**: TAE / OnHit "khi trúng" (nét đứt) / Param / Chain / Unknown). Click vào edge SpEffect → inspector hiện chuỗi nguồn đầy đủ (vd. `anim 3010 → TAE AttackBehavior → BehaviorParam 123 → AtkParam_Npc 456 → SpEffect S lên mục tiêu`). Click node → viewport play anim, timeline nhảy tới; click edge → editor nhảy tới dòng code.
12. **Combo timeline (chuỗi dài)**: trải một **đường đi** cụ thể của graph thành một timeline liên tục: anim nối anim tại JumpTable, và tại điểm SpEffect interrupt thì cắt anim hiện tại và rẽ. Người dùng chọn nhánh ở mỗi điểm rẽ (hoặc "Random" để app tung theo xác suất); với nhánh `OnHit` có công tắc **Trúng / Trượt** để xem cả hai kịch bản. Viewport play liên tục cả đường đi.
13. **Combo builder**: dựng/sửa combo bằng UI, đồng bộ **3 nơi cùng lúc** (một thao tác = một undo):
    - Lua goal (chuỗi `AddSubGoal`, nhánh random với ngưỡng tự tính từ % người dùng nhập),
    - Lua interrupt handler (nhánh `== S` + `ClearSubGoal` + chuỗi mới) và đăng ký observe,
    - TAE (thêm/di chuyển event SpEffect S trong anim nguồn bằng cách copy một event SpEffect có sẵn — đúng giới hạn P1.5).
   - Đổi start time của SpEffect trên timeline = đổi điểm rẽ của combo.
14. **Interrupt khác** (không phải SpEffect): hiển thị các nhánh `INTERUPT_*` khác (bị đánh, player hồi máu, player dùng item, …) cũng dạng edge trong graph, cùng cơ chế phân tích.

### P3 — Nâng cao
15. **AI simulation (dry run)**: chạy Lua với stub API (distance, HP, góc tùy chỉnh bằng slider) → xem act nào được chọn, xác suất mỗi act.
16. **Diff view** so với script gốc của game.
17. Hỗ trợ thêm game qua `GameProfile`.

## 5b. Nguyên tắc UI
- **Dễ nhìn, dễ dùng, đủ dùng** — không nhồi tính năng như DSAS.
- Layout cố định mặc định (có thể kéo resize), không bắt người dùng tự sắp panel:
  ```
  ┌──────────────┬──────────────────────────┬──────────────────┐
  │ Entity list  │  Lua editor (chính)      │ Viewport 3D      │
  │ + file Lua   │                          ├──────────────────┤
  │              │                          │ Inspector        │
  │              │                          │ (param của attack│
  │              │                          │  / event đang chọn)│
  ├──────────────┴──────────────────────────┴──────────────────┤
  │ TAE timeline (event của anim đang chọn) + play/scrub       │
  └────────────────────────────────────────────────────────────┘
  ```
- Màu event nhất quán theo loại (SpEffect, Blend, Attack, JumpTable/Combo…); event không sửa được hiển thị mờ hơn.
- Thông tin param hiện dạng tên dễ đọc (tên SpEffect, damage, range), ID chỉ để phụ.
- Thao tác nào cũng có undo; ô số có validate; không có dialog lồng nhau.

### Goal navigator (shorthand nhảy tới goal trong Lua editor)
Giống breadcrumb/outline của VSCode, nhưng hiểu cấu trúc AI Sekiro:
- **Breadcrumb trên đầu editor**: `entity › file.lua › Goal.Interrupt` — luôn hiện function đang chứa cursor. Click vào phần tử cuối → dropdown liệt kê **toàn bộ** function trong file, function hiện tại được highlight, chọn → nhảy tới dòng định nghĩa.
- Mỗi dòng trong dropdown: tên (`Goal.Act45`, `Goal.Interrupt`, `Goal.Kengeki03`, …) + chữ ký mờ (`function (arg0, arg1, arg2)`), có ô **lọc gõ nhanh** (gõ `keng` → chỉ còn `Kengeki*`).
- **Gom nhóm tự động** theo tên/vai trò (có thể thu gọn): `Lifecycle` (Activate / Update / Terminate / Initialize), `Interrupt` (Interrupt, Parry, ShootReaction, …), `Act` (Act01…ActNN), `Kengeki` (Kengeki_Activate, Kengeki01…), `Khác`. Quy tắc gom nhóm đặt trong `GameProfile`, không hardcode trong UI.
- Mỗi mục có **badge thông tin nhanh**: số `AddSubGoal`, anim ID chính (vd. `3041`), có xử lý SpEffect nào (`5060040`), có diagnostics không (chấm vàng/đỏ). Hover → tooltip tóm tắt chuỗi anim của goal.
- **Mở rộng** một goal (mũi tên `>`) → hiện các `AddSubGoal` bên trong (goal type + anim ID), click → nhảy tới đúng dòng đó. Với `Goal.Interrupt`, mở rộng → hiện từng nhánh `== SpEffectID` / `INTERUPT_*`.
- Phím tắt: `Ctrl+Shift+O` mở navigator, `Ctrl+Click` / `F12` trên tên goal hoặc trên chuỗi `Goal.XXX` trong code → nhảy tới định nghĩa; `Alt+←` / `Alt+→` quay lại / tới vị trí trước (lịch sử điều hướng).
- Đồng bộ hai chiều: click một node trong Combo graph / act table → editor nhảy tới goal tương ứng; di chuyển cursor trong editor → breadcrumb và graph highlight goal đó.
- Panel trái (Entity list) có thể hiện cùng outline này dưới dạng cây thường trực cho file đang mở.
- Dữ liệu lấy từ AST (`Core/Lua/Analysis` → `SymbolIndex`), cập nhật lại sau mỗi lần sửa (debounce), không parse lại bằng regex.

## 6. Kiến trúc dự kiến (trên nền fork DSAS)

Kiến trúc DSAS cần biết (chi tiết: `docs/review/dsas-architecture.md`):
- Mọi thứ chạy trên UI thread của MonoGame `Main`; `OSD.Build` (`ImguiOSD/OSD.cs`) vẽ dockspace, menu, rồi `Update` từng window. Panel mới = subclass `Window` (public abstract) + 1 dòng đăng ký `// [SAS]` trong `OSD.Build`.
- Truy cập document qua static `zzz_DocumentManager.CurrentDocument` (rất nhiều chỗ dùng).
- **TAE timeline không phải ImGui**: tự vẽ, đọc input MonoGame trực tiếp; phím tắt TAE chỉ dừng khi `OSD.AnyFieldFocused`. → Lua editor phải set cờ này, và cần router phím theo panel có focus. ImGui hiện chỉ nhận một phần phím (F12, Ctrl+F/G/S/O, Alt+mũi tên, Tab chưa tới được) → cần patch `[SAS]` chuyển tiếp đủ phím.
- TAE edit: undo snapshot theo từng animation + một stack chung; có **35 chỗ** sửa dữ liệu TAE → khóa bằng **một policy trung tâm** + debug assert trong `TaeUndoMan.NewAction`, không vá từng chỗ.
- **DSAS save không an toàn với mình**: `TaeFileContainer.cs:715` ghi đè anibnd tại chỗ (kể cả trong game folder), và DSAS rải `.dsaproj`, `_DSAS_WORKSPACE.json`, `_DSAS_CACHE`, `_DSAS_TEMP_UNPACK` cạnh file game. → **Phải hook save trước khi cho người dùng sửa TAE**: chỉ ghi vào mod folder, atomic, có backup.
- Project của DSAS (`.dsaproj`) là model TAE nhị phân, **không dùng làm `.sasproj`**; chỉ tái dùng workspace setting (game dir, ModEngine dir) qua `GameRoot.SetInterrootPickerPathsManually`.
- Param: DSAS load `GameParam.parambnd.dcx` của Sekiro (mod folder trước) nhưng bằng reader offset viết tay, thiếu `spEffectId0..4` của AtkParam, không đọc SpEffectParam cho Sekiro, không có Bullet/NpcThinkParam → **Core tự viết loader dựa trên paramdef** (Paramdex). Port 2 công thức ID từ `zzz_ParamManagerIns.cs`.
- Hitbox: `TaeActionSimulationEnvironment.cs` (event 1, 5, 304, 307) → `NewDummyPolyManager`. Range ring: hook `DBG.DrawPrimitives`.
- Live refresh của DSAS cho Sekiro chỉ reload chr qua memory injection (địa chỉ cứng theo 1 bản exe), không reload Lua.
- **Nhập liệu cho Lua editor** (`SoulsAIStudio/SoulsAiStudio/DsasKeyInput.cs`): ký tự lấy từ `WM_CHAR` qua `NativeWindow` (KeyPress của WinForms bắn 2 lần khi có IME vì cả `WM_IME_CHAR` lẫn `WM_CHAR`; `io.InputQueueCharacters` của ImGui.NET 1.87 đọc ra rỗng). IME của Windows bị tắt khi editor có focus (composition không vẽ được → chữ hiện trễ), bật lại bằng menu Souls AI → "Allow Windows IME in Lua Editor". Unikey vẫn gõ được. `SAS_DEBUG=1` hiện trạng thái input của editor.
- Font: DSAS lấy glyph range từ font debug DS3 → tiếng Việt hiện `?`. Font trong `Content/Fonts` có đủ glyph → thêm 4 glyph range là xong.

Quy ước code mới (đã chốt):
1. `SoulsAiStudio.Core` giữ `net9.0`, chỉ tham chiếu SoulsFormats, không UI. Code UI đặt trong `SoulsAIStudio/SoulsAiStudio/` (namespace `SoulsAiStudio.App`), chưa tách app project riêng.
2. Chỉ một thư mục adapter **`DsasBridge`** được đụng tới `zzz_*`, `Main.*`, `OSD.*`; nó implement interface do Core định nghĩa → Core test bằng fake.
3. Mỗi chỗ sửa trong file DSAS là **một dòng gọi vào class `SasHooks`**, đánh dấu `// [SAS]`. Mục tiêu < 15 dòng sửa DSAS cho P0–P1.5.
4. Một object khởi động sở hữu mọi service, truyền qua constructor; **không thêm static mới**.
5. Mọi thao tác sửa là command trên **một undo stack do mình quản lý**; command TAE gọi xuống undo theo-animation của DSAS; một thao tác sửa cả Lua + TAE = một undo.

Quy tắc khi sửa code DSAS:
- **Không refactor lớn code DSAS cũ.** Code mới đặt trong thư mục/namespace riêng (`SoulsAiStudio.*`), chỉ móc vào DSAS ở những điểm cần (menu, panel, viewport overlay, TAE edit).
- Mỗi chỗ sửa trực tiếp trong file DSAS gốc đánh dấu `// [SAS]` + lý do ngắn, để dễ merge khi DSAS upstream cập nhật.
- Tính năng TAE thừa của DSAS: **ẩn/khóa qua whitelist**, không xóa code (giữ khả năng merge upstream).
- Giữ remote `upstream` trỏ về repo DSAS gốc.
- Giữ nguyên license DSAS, ghi credit trong README và `THIRD_PARTY.md`.

Cấu trúc dưới đây là phần **code mới thêm vào**; vị trí cụ thể trong solution DSAS sẽ chốt sau khi đọc code.

```
src/
  SoulsAiStudio.Core/          # không phụ thuộc UI
    Project/                   # .sasproj model, New/Open/Recent, wizard logic
    Backup/                    # snapshot gốc, backup mỗi lần save, autosave, restore, retention
    Watch/                     # file watcher + debounce, xung đột buffer vs file ngoài
    Game/                      # IGameProfile, GameProfile.Sekiro, VirtualFileSystem (game + mod override)
    Formats/                   # wrapper SoulsFormats, luabnd read/write, param+paramdef loader
    Params/                    # model đã resolve: NpcInfo, AttackInfo, HitboxInfo, SpEffectInfo
    Lua/
      Parsing/                 # lexer, parser, AST (giữ vị trí + comment để round-trip)
      Analysis/                # symbol table, act table extractor, diagnostics
        Combo/                 # ComboGraph: AnimStep, Sequence/Random/Condition/SpEffectInterrupt edges, path probability
                               # SpEffectSourceIndex: anim → SpEffect (TAE, OnHit qua BehaviorParam/AtkParam/Bullet, chain)
      Decompile/               # gọi DSLuaDecompiler.exe (process), đường dẫn từ settings
      Simulation/              # sandbox chạy Lua + stub API
      CodeGen/                 # sinh/sửa code từ UI (act weight, combo builder) — sửa AST, không string-replace bừa
    Anim/                      # HKX skeleton/anim (read-only, port/reuse từ DSAS)
    Tae/                       # TAE read/write, template, edit commands (move start/end, set SpEffect, copy/paste, delete) + undo stack
  SoulsAiStudio.Render/        # viewport, FLVER mesh, skeleton, hitbox/dmyPoly/range overlay
  SoulsAiStudio.App/           # UI shell, panels, editor, docking
tests/
  SoulsAiStudio.Core.Tests/    # parser round-trip, param resolve, luabnd read/write
Resources/
  Paramdex/<Game>/             # paramdef XML
  LuaApi/<Game>.json           # mô tả API (tên hàm, tham số, doc) cho autocomplete
```

Nguyên tắc:
- `Core` không tham chiếu `Render`/`App`. Mọi logic phân tích AI test được không cần mở UI.
- UI ↔ code luôn đồng bộ hai chiều qua AST: chỉnh trên UI (weight, combo) = một edit trên AST rồi in lại, **giữ nguyên format và comment** phần không đụng tới.
- Mọi I/O file game đi qua `VirtualFileSystem` (ưu tiên mod folder).

## 7. Quy ước code
- C# nullable enable, file-scoped namespace, `async` cho load file nặng (không block UI thread).
- Không hardcode param field name / event ID trong logic — đặt trong `GameProfile`.
- Code lấy/port từ DSAS hoặc thư viện khác: ghi nguồn + license ở đầu file và trong `THIRD_PARTY.md`.
- Test: mỗi tính năng Core có test; parser Lua phải có test round-trip (parse → print → giống hệt) trên toàn bộ script thật của game.

### 7.1 Dữ liệu test & quy tắc an toàn file
**Tuyệt đối không sửa file gốc của người dùng.** Mọi test và mọi thao tác dev chạy trên **bản sao**.

- Bản backup gốc (read-only, đã có checksum): `test-data/original/Severance-dev/script/`
  - `m10_00_00_00-luabnd-dcx/` + `m10_00_00_00.luabnd.dcx` — **Inner Owl (Father)**, `out/bin/506300_battle.lua`
  - `m11_02_00_00-luabnd-dcx/` + `m11_02_00_00.luabnd.dcx` — **Inner Isshin**, `out/bin/540300_battle.lua`
  - `m25_00_00_00-luabnd-dcx/` + `m25_00_00_00.luabnd.dcx` — **True Monk**, `out/bin/500000_battle.lua`
  - `aicommon.luabnd.dcx`
  - Checksum: `test-data/original/SHA256SUMS.txt` (sha256 của từng file, path dạng `./script/...` tương đối từ `test-data/original/Severance-dev/`). Test `OriginalChecksumGuard` kiểm tra tự động.
  - Hạ tầng test có sẵn: `tests/SoulsAiStudio.Core.Tests/TestSupport/` (`TestPaths`, `TempWorkDir`, `LuaCorpus` TheoryData cho parser test, `WitchyBnd4Xml` để đối chiếu repack, `OodleSupport`). Test plan: `docs/testing/test-plan.md`.
- Nguồn thật (KHÔNG ghi vào trong lúc dev/test): `C:\Program Files (x86)\Steam\steamapps\common\Sekiro\mods\Severance-dev\script\...`
- Quy tắc:
  - `test-data/original/` là **read-only**: không bao giờ sửa, xóa, ghi đè. Test cần ghi → copy sang thư mục tạm (`test-data/work/` hoặc temp của test) rồi thao tác trên đó.
  - Mỗi test chạy xong có thể kiểm tra checksum `original/` không đổi.
  - App chỉ ghi vào mod folder thật khi người dùng chủ động Save trong project; luôn backup trước (mục P0 Backup).
  - `test-data/` không đưa vào repo public (là file game/mod của người dùng) → thêm vào `.gitignore`.
- Ca test bắt buộc:
  - Parser round-trip trên cả 3 file battle + toàn bộ `out/bin/*.lua` trong 3 thư mục.
  - Goal navigator liệt kê đúng `Goal.*` (vd. Owl: Act01..Act50, Interrupt, Parry, ShootReaction, Kengeki_Activate, Kengeki01..).
  - ComboGraph: Owl `Goal.Interrupt` nhánh `interruptSpeffect == 3506000` (ClearSubGoal + random 33% + `GetNinsatsuNum` + `:TimingSetNumber` chain); Kengeki03 + SpEffect 5060040 (nguồn TAE); đòn đạp Owl → SpEffect qua AtkParam (nguồn OnHit).
  - Luabnd: unpack → repack theo `_witchy-bnd4.xml` → so sánh nội dung file con giống bản gốc.

## 8. Repo, build / run
- Repo này là clone của `https://github.com/Meowmaritus/DSAnimStudio` (remote `upstream`), làm việc trên branch **`sas/main`**. Submodule `SoulsAssetPipeline` (SoulsFormats, Havoc) đã init. Submodule `MeowDSIO` không dùng.
- App/project đã đổi tên: thư mục `SoulsAIStudio/` (trước là `DSAnimStudioNETCore/`), `SoulsAIStudio/SoulsAIStudio.csproj`, `SoulsAIStudio.sln`, exe `Souls AI Studio.exe`, icon `SoulsAIStudio/SoulsAIStudio_Icon.ico`. **Namespace C# của code DSAS vẫn là `DSAnimStudio`** (đổi namespace = sửa hàng nghìn file, phá merge upstream).
- **License: GPLv3** (theo DSAS) → toàn bộ fork là GPLv3.
- DSAS app: `SoulsAIStudio/` (net9.0-windows7.0). Code mới: `src/SoulsAiStudio.Core` (net9.0), test: `tests/SoulsAiStudio.Core.Tests` (xUnit). Cả hai nằm trong solution folder `SoulsAiStudio` của `SoulsAIStudio.sln`.
- Máy dev đã cài cả .NET 9 và .NET 10 runtime. Vẫn giữ `RollForward=LatestMajor` (test project + `SoulsAIStudio/runtimeconfig.template.json`) để chạy được trên máy chỉ có .NET 10. Không retarget sang net10 (phải sửa submodule SoulsAssetPipeline).
```
dotnet build SoulsAIStudio/SoulsAIStudio.csproj   # DSAS (≈4000 warning có sẵn từ upstream, 0 error)
dotnet test tests/SoulsAiStudio.Core.Tests
```
- Tài liệu nhóm: `docs/review/` (code review), `docs/testing/` (test plan), `docs/perf/` (benchmark + budget), `docs/ux/` (UI spec). Benchmark: `tools/SoulsAiStudio.Bench/`.

## 9. Quyết định đã chốt
- Game: **Sekiro**.
- UI: **ImGui.NET** như DSAS.
- Codebase: **fork DSAS**.
- Lua decompiler: **DSLuaDecompiler** tại `C:\Users\erizo\Downloads\modding\DSLuaDecompiler` (gọi qua exe).
- Combo phải xử lý **rẽ nhánh theo SpEffect trong TAE + interrupt handler Lua**, không chỉ combo thẳng.
- Có **project system**, **backup**, **file watcher** (tự reload trong app). In-game hot reload **tạm bỏ**.
- Lua trong luabnd Sekiro: **plain text chạy được**, luabnd theo map, unpack bằng WitchyBND (đã xác minh).
- SpEffect interrupt API: `GetSpecialEffectActivateInterruptType(0)` + `IsInterupt(INTERUPT_ActivateSpecialEffect)`; observe bằng `ai:AddObserveSpecialEffectAttribute` trong `Goal.Activate` (đã xác minh).

## 10. Câu hỏi còn mở
1. Tên field SpEffect trong AtkParam/Bullet/BehaviorParam theo paramdef Sekiro (cho nguồn `OnHit`) — cần lấy paramdef Sekiro từ Paramdex.

### Hiệu năng (chi tiết: `docs/perf/baseline.md`, bench: `tools/SoulsAiStudio.Bench`)
Đo trên i5-13500H, NVMe. Chr ID thật: **Owl = c5060, Isshin = c5400, True Monk = c5000** (c5063/c5403 chỉ là stub).
- **Không dùng `ApplyParamdef` của SoulsFormats khi mở project**: 6 param AI mất 1.3 s và ~197 MB. Core dùng **reader theo offset chỉ đọc field cần** (29 ms, đã đối chiếu khớp 100% với ApplyParamdef — prototype `tools/SoulsAiStudio.Bench/LightParamReader.cs`). Offset lấy từ paramdef lúc khởi động.
- Paramdef Sekiro: chưa có trong repo; bench dùng bản đi kèm DSMapStudio 1.11 (`C:\Users\erizo\Downloads\modding`). Copy 6 def cần thiết vào `Resources/Paramdex/SDT/` kèm credit/license [xác minh license].
- Gameparam của mod cũng nén **DCX_KRAK** → Oodle phải load trước khi đọc param.
- Ngân sách (1920×1080, 60 FPS):

  | Việc | Ngân sách |
  |---|---|
  | Mở project → Lua editor tương tác được | < 300 ms (phần còn lại load nền, < 3 s) |
  | Load param AI | < 100 ms, < 60 MB |
  | Parse + analyze lại file 3000 dòng | < 50 ms p95, **ngoài UI thread**, debounce 250 ms, hủy được |
  | Goal navigator | build < 5 ms; apply trên UI thread < 0.2 ms |
  | Lua editor mỗi frame | < 1 ms CPU, 0 allocation; chỉ vẽ dòng nhìn thấy, chỉ đọc snapshot do thread phân tích tạo |
  | Save | < 100 ms (DFLT); < 600 ms (KRAK, chạy async) |
  | Bộ nhớ | < 800 MB với 3 boss mở cùng lúc |
- DSAS load anim **đồng bộ trên UI thread** (parse mọi HKX lúc load anibnd, `LoadingTaskMan` spin-wait, `GetFilesInDir(null)` quét 19k file, `FileExists` đọc nguyên file) → nhảy Lua → anim **không được đi qua các đường này**; cần đường load riêng/lazy.
- DSLuaDecompiler: ~315 ms/file (chủ yếu khởi động process) → decompile theo lô trong một process, chạy nền, cache kết quả.
- File read-only: `AtomicFile` từ chối ghi đè file read-only (`ReadOnlyFileException`), UI hỏi người dùng thay vì tự gỡ cờ.

### Quyết định UX (2026-11-11)
- Màn hình mục tiêu: **1920×1080** (layout mặc định thiết kế cho kích thước này).
- **Không** làm command palette (Ctrl+P).
- Mặc định (người dùng chưa phản đối, có thể đổi): UI tiếng Anh; word wrap tắt; di cursor **không** tự play anim (bấm ▶); timeline nhóm hàng theo loại event.

### License đã biết
- Fork là GPLv3 (root LICENSE + SoulsAssetPipeline). File `DS ANIM STUDIO LICENSE.txt` trong output ghi MIT là lỗi thời.
- MonoGame: Ms-PL (ok, DLL riêng). FMOD Ex/fsbankcl đi kèm: proprietary, chỉ phi thương mại.
- **Oodle (`oo2core_6_win64.dll`) không bao giờ được phân phối kèm app** — luôn load từ thư mục game của người dùng.

## 11. Thứ tự bắt tay vào làm
1. ~~Fork DSAS, build~~ (xong: clone + branch `sas/main`, build xanh). Còn: chạy thử app với Sekiro.
2. ~~Đọc code DSAS, chốt `[TBD]`~~ (xong phần lớn — xem `docs/review/`, `docs/ux/`).
3. ~~Project system (New/Open/Recent) + đọc/ghi luabnd + backup~~ (xong: `src/SoulsAiStudio.Core/Project`, UI trong `SoulsAIStudio/SoulsAiStudio/`: start screen, wizard, Scripts panel + goal outline, Lua editor tạm bằng InputTextMultiline). Còn: file watcher.
   - Mở thẳng project: `"Souls AI Studio.exe" --sas-project <file.sasproj>` (exe: `SoulsAIStudio/bin/Release/net9.0-windows7.0/Souls AI Studio.exe`). Biến môi trường `SOULSAISTUDIO_USERDATA` đổi chỗ lưu settings/recent (UI test phải dùng, không ghi vào %AppData% thật).
4. Prototype **Lua editor trong ImGui** (rủi ro lớn nhất) + đọc/ghi luabnd Sekiro.
5. Entity browser + resolve param chain (NpcParam → NpcThinkParam → Lua; BehaviorParam → AtkParam/SpEffect).
6. Khóa TAE editor theo whitelist (P1.5).
7. Liên kết Lua ↔ timeline ↔ viewport, overlay hitbox (P1).
8. ComboGraph phân tích (Core, có test trên script Sekiro thật: Kengeki03 + SpEffect 5060040 cho nguồn TAE; Owl (Father) đòn đạp → SpEffect qua AtkParam cho nguồn OnHit) → graph view → combo timeline → combo builder (P2).

## 12. Tạm hoãn (không làm cho tới khi được yêu cầu)
- **In-game Lua hot reload.** Ghi lại để sau này dùng: HavokScript của Sekiro có `loadfile`; cộng đồng (credit f_wang) đã reload `Goal.*` khi đang đánh bằng cách đặt `loadfile(<.lua tuyệt đối>)()` đầu `Goal.Interrupt`, kích hoạt khi player crouch (`HasSpecialEffectId(TARGET_ENE_0, 109200)`). Ý tưởng nếu làm lại: stub có marker do app chèn/gỡ, kích hoạt bằng version file app tăng mỗi lần save, chỉ có ở bản Dev, gỡ sạch khi Export.
