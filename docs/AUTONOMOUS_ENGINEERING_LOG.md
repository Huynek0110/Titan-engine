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
- **Commit/push:** `56f9727` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 10 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Header comment của `MainWindow.xaml.cs` still said "V106" trong khi app đang ở V193 (docs/00-OVERVIEW.md §10 cũng flag là đã lệch) → dễ gây nhầm lẫn cho agent/human mới đọc.
- **CODE:** header đổi thành "V193 (UI+Soundbar Update)", phần mô tả V106 giữ nguyên nhưng đánh dấu là lịch sử; dòng tương ứng trong `docs/00-OVERVIEW.md` cập nhật lại. Comment/docs only — zero behavior.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (comment block), `docs/00-OVERVIEW.md`.
- **Protected-core impact:** không (chỉ comment).
- **TEST:** build Release **0/0** (comment không ảnh hưởng nhưng build để chắc).
- **Commit/push:** `9b31b62` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 11 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Sau khi retheme XAML của `AudioEditorWindow`, phần vẽ tay trên canvas (code-behind) vẫn dùng palette cũ: nền `#101016`, track `#1C1C26`, segment teal `#00BAD6/#85EBFF`, playhead `#FF6666`, label `White`.
- **CODE:** map sang V193 palette: nền → card #11141A, viền → #1E242E, track → input #161B22, segment → accent #4CC3FF (alpha 230) + stroke #6FD4FF, label → #F0F4F8, playhead → #F43F5E. Chỉ đổi giá trị màu trong `DrawTimeline`, không đổi hình học.
- **Files:** `TitanEngine/AudioEditorWindow.xaml.cs` (5 màu).
- **Protected-core impact:** không — canvas của cửa sổ phụ.
- **TEST:** build Release **0/0**; grep xác nhận không còn màu cũ nào trong file.
- **Commit/push:** `ccbb981` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 12 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Quét lại toàn bộ XAML/C# tìm màu palette cũ: phát hiện `PortInputWindow.xaml` vẫn còn `#1E1E1E/#2E2E2E/#448844/#559955/#88CCFF` — vòng trước regex `#[0-9A-Fa-f]{3}` đứng trước `{6}` nên nuốt mất 3 ký tự đầu của hex 6 chữ số và không map được. `AudioEditorWindow.xaml` kiểm lại sạch (0 màu cũ).
- **CODE:** chạy lại perl với thứ tự đúng (6-digit trước 3-digit) → toàn bộ màu của PortInputWindow đã về palette V193 (xem output: `#0A0C10/#161B22/#4CC3FF/#149E44/#22C55E/#171B23/#262D38/#F0F4F8`). `EffectRecipeLibrary.cs:204` là palette của effect recipe (protected) — cố tình không đụng.
- **Files:** `TitanEngine/PortInputWindow.xaml`.
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**; grep xác nhận không còn hex cũ nào trong cả 2 file.
- **Commit/push:** `94445c4` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 13 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** `setup_titan.ps1` (script auto-tải ffmpeg) vẫn trỏ `TitanEngineV60\bin\Debug\...` — tên project cũ thời V60; chạy thật sẽ tạo thư mục sai chỗ và chép ffmpeg vào nơi app không bao giờ đọc.
- **CODE:** resolve động qua `TitanEngine.csproj` (Recurse từ root), fallback về layout chuẩn `TitanEngine\bin\Debug\net8.0-windows`. Không chạy cả script (có tải 30-50MB), chỉ dry-run phần resolve path.
- **Files:** `setup_titan.ps1`.
- **Protected-core impact:** không — dev-workflow script.
- **TEST:** dry-run logic trong PowerShell → `RESOLVED_BIN=...\TitanEngine\TitanEngine\bin\Debug\net8.0-windows`, `CSPROJ_FOUND=True`. Build cả Release và Debug đều **0/0** (đủ chuẩn AGENTS.md cả 2 config).
- **Commit/push:** `59f4bc9` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 14 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE (docs) → TEST
- **REVIEW:** Theo §8 của loop phải có **prioritized backlog** trong log này — hiện chưa có. Đồng thời rà soát các chỗ còn lại: API đã validate input (400 khi thiếu/không tồn tại inputFile, outer catch trả 500 gọn), gitignore đã đủ (`tmp_*`, `bin/`, `obj/`, `.vs/`), docs 10/20/30 đều current tới r38.
- **CODE:** thêm mục **BACKLOG** vào cuối file này — các việc còn lại + lý do chưa làm (cần user quyết định / protected core / việc lớn).
- **Files:** `docs/AUTONOMOUS_ENGINEERING_LOG.md`.
- **Protected-core impact:** không.
- **TEST:** không cần build (chỉ markdown); build Release+Debug vẫn 0/0 (đã chạy ở It13).
- **Commit/push:** `96af2e9` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 15 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Quét lại body `MainWindow.xaml` phát hiện còn inline `<Style>` trong body dùng `Setter Property="…" Value="#hex"` — các vòng trước chỉ match attribute trực tiếp (`Background="#…"`), nên dạng Setter bị sót. Trong số đó 2 màu có sẵn brush resource (`#232B36`, `#4CC3FF` trong job-card border); phần còn lại (#171B23, #202836, #E8EEF5…) là màu nội bộ của style không có resource — cố tình giữ nguyên để không nở thêm token cho quelques dòng.
- **CODE:** perl thứ tự đúng, chỉ từ sau `</Window.Resources>` (dòng 418), bao cả dạng `Property="X" Value="#hex"` với map palette đã có. Kết quả: đúng 2 dòng đổi (job card border + trigger), các màu không có resource giữ nguyên.
- **Files:** `TitanEngine/MainWindow.xaml` (2 dòng).
- **Protected-core impact:** không; layout không đổi.
- **TEST:** build Release **0/0**; launch exe 10 s → `APP_RUNNING_OK` (mọi StaticResource resolve).
- **Commit/push:** (điền hash sau khi push)

---

## BACKLOG (ưu tiên từ cao xuống thấp — theo evidence, không làm lại cái đã xong)

1. **[cần user quyết định]** Gộp 2 bản `BAS_LOCALHOST_API_GUIDE.txt` (root bản cũ lệch vs bản được đóng gói trong `TitanEngine/`). Mỗi bản có phần riêng — cần user chọn giữ gì. Xem `docs/30-API.md` §mục 6.
2. **[protected core — cần authorization rõ]** Mọi thay đổi render pipeline / soundbar / effect / API core: `MainWindow.xaml.cs` (EngineCore), `SoundBar*`, `AudioSpectrumAnalyzer.cs`, `EffectRecipe*`, overlay presets. Hiện tại không có issue mở ở các vùng này.
3. **[việc lớn, không gấp]** Tách `MainWindow.xaml.cs` ~18.2k dòng thành nhiều file theo vùng (UI / queue / API / EngineCore). Rủi ro cao, cần kế hoạch riêng.
4. **[cần user duyệt vì là feature mới]** Per-style palettes, animated preview trong UI picker, loop window >6 s (hiện cap 180 frame = 6 s).
5. **[đã xong ở It8]** Log console unbounded growth — đã cap 3000 dòng.
6. **[đã xong It3]** Port dialog whitespace → invalid HttpListener prefix — đã normalize.
7. **[đã xong It5]** Không có crash log → đã có `%TEMP%\TitanEngine\crash.log`.
8. Cache PNG `%TEMP%\TitanEngine\soundbar_cache` — vùng protected (soundbar), để nguyên trừ khi user yêu cầu.
