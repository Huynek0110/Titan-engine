# AUTONOMOUS ENGINEERING LOG

> Ghi lại mọi iteration của vòng lặp tự động (theo lệnh "AUTONOMOUS ENGINEERING LOOP V3" của user, 2026-10-31).
> Backup gốc: branch `backup/pre-autonomous-improvements-20261031-201926` (= `ac8bd33`, đã push + verify trên origin).
> Working branch: `autonomous/continuous-improvements-20261031-201926`.
> Protected core (không đụng): render pipeline / filter chain / encoder, soundbar + audio analysis + visualizer, effect algorithms, transitions, compositing, API core, layout UI (chỉ màu sắc được phép đổi).
> Dừng vòng lặp khi user gõ đúng: `STOP AUTONOMOUS LOOP`.

---

## ITERATION 1 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST (không cần research ngoài — vấn đề nội bộ)
- **REVIEW:** Quét `MainWindow.xaml`: phần body (sau `</Window.Resources>` dòng 413) còn ~155 chỗ tô màu cứng trùng với palette của design system (`#4CC3FF`, `#8B95A5`, `#5C6672`, `#161B22`, `#262D38`, `#11141A`, `#1E242E`, `#F0F4F8`, `#1F4258`) thay vì dùng `{StaticResource}`, cộng 3 màu xám lệch palette (`#9AA3B2` ×7, `#7A7A8A`, `#6B7480`). Rủi ro: đổi màu một chỗ ở resources thì body không theo → giao diện trôi.
- **Problem/root cause:** palette bị hardcode lặp lại ở body, không có single source of truth.
- **CODE:** trong body, mọi thuộc tính brush (`Background|Foreground|BorderBrush|Fill|Stroke|CaretBrush|SelectionBrush`) đổi từ hex sang `{StaticResource ...Brush}` của 9 brush đang có; 3 màu xám lệch map về `TextSecondaryBrush`/`TextTertiaryBrush`. `GradientStop Color=` và các màu accent theo section (#8B7CF6, #FF9900, #F43F5E, #7EE787…) giữ nguyên — đó là nhận diện thị giác có chủ đích. Không đụng geometry/layout/handler.
- **Files:** `TitanEngine/MainWindow.xaml` (155 dòng đổi, toàn bộ là thay giá trị màu — đã kiểm bằng diff: không có dòng nào đổi ngoài màu).
- **Protected-core impact:** không. UI layout giữ nguyên 100%; chỉ token màu.
- **TEST:** `dotnet build -c Release` → **0 warning / 0 error**; diff xác nhận không có thay đổi phi-màu; launch `TitanEngine.exe` bản Release → chạy 12 s không crash (`APP_RUNNING_OK`) ⇒ XAML parse OK, mọi StaticResource resolve đủ (thiếu key sẽ throw lúc load).
- **Pixel change:** 9 màu palette = y hệt (cùng giá trị hex); 3 màu xám strays đổi nhẹ về đúng palette (#9AA3B2/#7A7A8A→#8B95A5, #6B7480→#5C6672).
- **Commit/push:** `5444180` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`
- **Remaining:** body vẫn còn một số màu surface phụ (#0D1014, #232B36, #12151C, #2A3340…) chưa có brush resource — ứng viên cho iteration sau nếu muốn token hoá hết.

---

## ITERATION 2 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Hai cửa sổ phụ `AudioEditorWindow.xaml` (207 dòng) và `PortInputWindow.xaml` (27 dòng) vẫn dùng palette cũ riêng (#0E0E12/#2B2B33/#8CCBFF/#FFD166…, và #1E1E1E/#2E2E2E kiểu VS-cũ ở PortInput) → giao diện không đồng nhất với design system V193 của main window (canvas #0A0C10, card #11141A, accent #4CC3FF…).
- **CODE:** remap toàn bộ hex của 2 file sang palette V193 (bg #0A0C10, surfaces #11141A/#161B22/#0D1014, borders #1E242E/#2A3340/#262D38, text #F0F4F8/#8B95A5/#5C6672, accent #4CC3FF/#1F4258, greens #7EE787/#22C55E/#149E44, purple #8B7CF6/#241E38, White→#F0F4F8). Chỉ đổi giá trị màu — không đụng layout/geometry/handler/control. Diff kiểm: mọi dòng bị xoá đều là màu cũ.
- **Files:** `TitanEngine/AudioEditorWindow.xaml`, `TitanEngine/PortInputWindow.xaml` (38 dòng, color-only).
- **Protected-core impact:** không — đây là cửa sổ phụ, không dính render/effect.
- **TEST:** `dotnet build -c Release` → **0 warning / 0 error** (XAML compile ra BAML nên markup sai sẽ fail build).
- **Commit/push:** `8a514e8` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 3 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Đọc `PortInputWindow.xaml.cs` (dialog hỏi port cho host API). `BtnOk_Click` validate bằng `int.TryParse` — nhưng `TryParse` chấp nhận whitespace và dấu (" 5555 " hợp lệ) trong khi `SelectedPort = txtPort.Text` lưu nguyên chuỗi raw. Call site `MainWindow.xaml.cs:16792` ghép thẳng vào `LocalApiPrefix = $"http://{ip}:{SelectedPort}/"` → prefix có dấu cách = `HttpListener` ném lỗi "invalid prefix" lúc bật HOST.
- **CODE:** lưu giá trị đã parse: `SelectedPort = port.ToString(CultureInfo.InvariantCulture)` (+ comment giải thích). Không đổi behavior với input bình thường.
- **Files:** `TitanEngine/PortInputWindow.xaml.cs` (1 dòng + comment).
- **Protected-core impact:** không — dialog phụ, không dính render/API core (chỉ передача chuỗi vào prefix).
- **TEST:** `dotnet build -c Release` → **0 warning / 0 error**.
- **Commit/push:** `3ba1851` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 4 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST (tiếp nối iteration 1)
- **REVIEW:** Sau iteration 1, body của `MainWindow.xaml` vẫn còn các màu surface hardcode lặp lại: `#C9D2DE` ×15, `#0D1014` ×4, `#2A3340` ×3, `#12161D` ×2, `#232B36` ×4, `#12151C` ×2 — chưa có brush tương ứng trong `Window.Resources`.
- **CODE:** thêm 5 brush vào design system (`TextBodyBrush #C9D2DE`, `DeepSurfaceBrush #0D1014`, `BorderSoftBrush #2A3340`, `DropdownBrush #12161D`, `TrackBrush #242B36`) và đổi toàn bộ occurrence ở body sang `{StaticResource}` (chỉ thuộc tính brush; `GradientStop Color=` giữ hex). `#232B36`→`TrackBrush(#242B36)` và `#12151C`→`CardBrush` là 2 chỗ unify nhỏ về đúng palette. Các accent theo section (#8B7CF6, #FF9900, #F43F5E, #7EE787, #241E38, #FFFFFF) cố tình giữ nguyên.
- **Files:** `TitanEngine/MainWindow.xaml` (+5 resources, 27 dòng body đổi màu).
- **Protected-core impact:** không; layout/geometry/handler không đổi.
- **TEST:** build Release **0/0**; launch exe 10 s → `APP_RUNNING_OK` (XAML hợp lệ, mọi StaticResource resolve).
- **Commit/push:** `c6a280b` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 5 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** `App.xaml.cs` rỗng — app không có handler nào cho unhandled exception, nên crash toàn bộ chỉ để lại cửa sổ Windows generic, không có evidence trong máy. (Đây là diagnostics/startup — vùng cho phép; không đụng core.)
- **CODE:** override `OnStartup`, móc `DispatcherUnhandledException` ghi `%TEMP%\TitanEngine\crash.log` (UTF-8 không BOM, rotate khi > 2 MB). `e.Handled` cố tình để false → hành vi crash mặc định giữ nguyên 100%, chỉ thêm log. Toàn bộ phần ghi log bọc try/catch để logger không bao giờ che exception gốc.
- **Files:** `TitanEngine/App.xaml.cs`.
- **Protected-core impact:** không — chỉ diagnostics lúc startup.
- **TEST:** build Release **0/0**; launch exe 10 s → `APP_RUNNING_OK` (app khởi động bình thường với handler mới).
- **Commit/push:** `fb73c44` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 6 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** `AudioEditorWindow.xaml.cs` có 2 `catch` rỗng nuốt exception của MediaElement (`:111` `_player.Close()` lúc đóng cửa sổ, `:369` `_player.Position = …` lúc seek) — lỗi bị im lặng, không có dấu vết cả khi debug.
- **CODE:** cả hai đổi thành `catch (Exception ex)` + `Debug.WriteLine("[AUDIO-EDITOR] …" + ex.Message)` kèm comment giải thích vì sao nuốt là đúng (hành vi không đổi — vẫn nuốt, chỉ thêm dấu vết).
- **Files:** `TitanEngine/AudioEditorWindow.xaml.cs` (2 khối catch).
- **Protected-core impact:** không — cửa sổ phụ, diagnostics thuần.
- **TEST:** build Release **0/0**.
- **Commit/push:** `8a12b57` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 7 — 2026-10-31

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** `docs/00-OVERVIEW.md` §10 còn ghi "r36–r38 đã commit nhưng chưa push" — đã push từ lâu; và thiếu pointer tới engineering log + crash.log vừa có.
- **CODE:** cập nhật §10: toàn bộ r22–r38 đã push, ghi convention nhánh autonomous/backup + link `AUTONOMOUS_ENGINEERING_LOG.md`, và vị trí `crash.log`. Docs-only.
- **Files:** `docs/00-OVERVIEW.md`.
- **Protected-core impact:** không.
- **TEST:** không cần build (chỉ markdown); `git diff` xác nhận chỉ đổi chữ trong §10.
- **Commit/push:** `49f3497` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 8 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** `LogSystem` (`MainWindow.xaml.cs:20535`) là nơi duy nhất ghi `txtLogs` (TextBox của log console) và không có giới hạn — mỗi `AppendText` là O(độ dài hiện tại), render dài + ffmpeg verbose → TextBox phình vô hạn, tốn RAM và làm UI thread giật khi `ScrollToEnd`. Đây là UI layer, không phải render core.
- **CODE:** cap view ở 3000 dòng; khi vượt, cắt một lần 1000 dòng cũ nhất bằng một lần gán `Text` (rẻ hơn nhiều so với để chuỗi lớn mãi). Thêm field `_logViewLines` + 2 const gần `_logLock`. Không đổi nội dung log, không đổi hành vi render.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (1 method + 3 dòng field/const).
- **Protected-core impact:** không — chỉ log view của UI; pipeline/queue/API không đụng.
- **TEST:** build Release **0/0**; launch exe 10 s → `APP_RUNNING_OK`.
- **Commit/push:** `2f4b71c` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 9 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Main window có dark DWM title bar + bo góc + icon (từ round V193), nhưng `AudioEditorWindow` và `PortInputWindow` vẫn để chrome mặc định của Windows → khi mở 2 cửa sổ này chúng sáng/già hơn hẳn so với main.
- **CODE:** copy đúng pattern `OnSourceInitialized` (DWMWA_USE_IMMERSIVE_DARK_MODE=20, DWMWA_WINDOW_CORNER_PREFERENCE=33=ROUND, icon từ `Content/Branding/video-logo.png`, mọi thứ bọc try/catch) vào cả 2 code-behind. Visual-only, additive, không đổi layout/hành vi.
- **Files:** `TitanEngine/AudioEditorWindow.xaml.cs`, `TitanEngine/PortInputWindow.xaml.cs`.
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**.
- **Commit/push:** (điền hash sau khi push)
