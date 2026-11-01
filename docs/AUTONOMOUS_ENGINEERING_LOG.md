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
- **Commit/push:** `e793b9c` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 16 — 2026-11-01 (verification sweep — no code change needed)

- **Path:** REVIEW/INVESTIGATION → TEST (không có CODE vì review không tìm ra mục tiêu an toàn mới — theo §5 của lệnh: không bịa feature để giữ vòng lặp)
- **REVIEW (đã rà, đều ổn):** API validate input đủ (400 thiếu/không tồn tại, outer catch 500 gọn); `AudioEditModels` dùng TryParse; `.gitignore` đã cover `tmp_*`/`bin`/`obj`/`.vs/`; docs 10/20/30 current tới r38; màu status trong `MainWindow.xaml.cs` (Cyan/LimeGreen/red/green) là semantic, không phải theme drift; `Modelfile*` là file của user, không đụng.
- **TEST:** build **Debug 0/0** + **Release 0/0**; `git status` sạch, branch đang sync với origin (`e793b9c`).
- **Kết luận vòng:** phần non-protected an toàn đã được dọn phần lớn (UI palette thống nhất, 2 cửa sổ phụ đồng bộ, crash log, log cap, port fix, setup script, docs). Các mục còn lại nằm trong BACKLOG (cần user quyết định hoặc protected core).
- **Commit/push:** `7ee6434` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 17 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** App chưa có phím tắt nào cả (quét `KeyBinding`/`KeyDown` = 0) — vùng "Keyboard shortcuts / Accessibility" được phép đổi theo lệnh. Các handler của nút chính đã có sẵn: `BtnBrowseVideo_Click`, `BtnAddJob_Click`, `BtnStartBatch_Click`, `BtnDeleteJob_Click`.
- **CODE:** thêm `PreviewKeyDown="Window_PreviewKeyDown"` lên `<Window>` + 1 handler mới trong `MainWindow.xaml.cs` chỉ forward tới các handler đang có: **Ctrl+O** browse video, **Ctrl+B** add job, **Ctrl+Enter** start batch, **Delete** xoá job đang chọn trong `lstQueue`. Guard: khi focus đang ở TextBox/ComboBox thì bỏ qua Delete/Backspace để không cướp phím gõ chữ. Không sửa handler cũ, không đổi layout.
- **Files:** `TitanEngine/MainWindow.xaml` (+1 attribute), `TitanEngine/MainWindow.xaml.cs` (+1 method ~40 dòng).
- **Protected-core impact:** không — chỉ UI input.
- **TEST:** build Release **0/0**; launch exe 10 s → `APP_RUNNING_OK` (XAML parse OK, handler wired).
- **Commit/push:** `02acbbc` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 18 — 2026-11-01

- **Path:** REVIEW → CODE (docs) → TEST
- **REVIEW/CODE:** It17 thêm phím tắt nhưng docs chưa ghi → thêm mục "Phím tắt" vào `docs/00-OVERVIEW.md` §9 (Ctrl+O / Ctrl+B / Ctrl+Enter / Delete + ghi chú không hoạt động khi đang gõ trong ô nhập).
- **Files:** `docs/00-OVERVIEW.md`.
- **Protected-core impact:** không.
- **TEST:** không cần build (markdown); build vẫn 0/0 từ It17.
- **Commit/push:** `6a4a2a2` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 19 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** App chưa lưu gì giữa các lần chạy (quét `AppData/settings/Properties.Settings` = 0) — mỗi lần mở lại phải browse lại từ đầu tới thư mục source/output quen dùng. Vùng "Settings and preferences" được phép đổi.
- **CODE:** file mới `AppSettings.cs` (JSON ở `%APPDATA%\TitanEngine\settings.json`, mọi I/O bọc try/catch best-effort, folder chỉ lưu khi còn tồn tại) lưu `LastSourceFolder` + `LastOutputFolder`. Hook mỏng vào `MainWindow`: constructor `AppSettings.Load()` + khôi phục `_customOutputFolder`/`txtOutputDir` nếu có; `BtnBrowseVideo_Click` và `BtnBrowseOutput_Click` set `InitialDirectory` từ settings và gọi Remember khi chọn xong. Không sửa handler render/queue.
- **Files:** `TitanEngine/AppSettings.cs` (mới), `TitanEngine/MainWindow.xaml.cs` (+~12 dòng ở 3 chỗ).
- **Protected-core impact:** không — chỉ UI preference.
- **TEST:** build Release **0/0**; launch exe 10 s → `APP_RUNNING_OK` (không tạo settings.json cho tới khi user chọn folder — đúng thiết kế).
- **Commit/push:** `833c819` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 20 — 2026-11-01

- **Path:** REVIEW → CODE (docs) → TEST
- **REVIEW/CODE:** It19 thêm persistence nhưng docs chưa ghi → thêm 1 bullet vào `docs/00-OVERVIEW.md` §9 về `%APPDATA%\TitanEngine\settings.json`.
- **Files:** `docs/00-OVERVIEW.md`.
- **Protected-core impact:** không.
- **TEST:** markdown only; build vẫn 0/0 từ It19.
- **Commit/push:** `8469897` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 21 — 2026-11-01 (e2e regression verification — no code change)

- **Path:** REVIEW → TEST
- **REVIEW/TEST:** Sau 20 iteration đổi UI (constructor, browse handlers, LogSystem, PreviewKeyDown, AppSettings), chạy e2e thật qua `%TEMP%\SbSphere` (`dotnet SbSphere.dll e2e` — job 1 ảnh + nhạc + soundbar audio-ring, đúng ca image-fast-path): render **23.1 s, exit OK**, output `Titan_thumb.mp4` exists. Validate: ffprobe = h264 1080×1920 + AAC + mjpeg cover, duration **12.000 s** đúng; `ffmpeg -v error -i out -f null -` → **0 dòng lỗi** (full decode sạch). Kết luận: pipeline không bị ảnh hưởng bởi các thay đổi UI.
- **Files:** không đổi code.
- **Commit/push:** `bcfd729` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 22 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** It19 mới áp `LastSourceFolder` cho picker video; các picker còn lại (Audio 1, Audio 2, Watermark) vẫn mở ở mặc định → cùng một tính năng bị thiếu chỗ.
- **CODE:** thêm `InitialDirectory = AppSettings.LastSourceFolder` + `RememberSourceFolder` vào `BtnBrowseAudio_Click`, `BtnBrowseAudio2_Click`, `BtnBrowseWatermark_Click` — đúng y hệt pattern It19. Không đổi logic chọn file.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (3 handler, +2 dòng mỗi cái).
- **Protected-core impact:** không.
- **TEST:** build Release **0/0** + Debug **0/0**.
- **Commit/push:** `d81c1cc` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 23 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Cùng tính năng It19/22 nhưng còn sót: thả file bằng drag-drop (`OnWindowDrop`) không nhớ thư mục nguồn.
- **CODE:** +1 dòng `AppSettings.RememberSourceFolder(files[0])` trong `OnWindowDrop` (bao cả drop video lẫn audio).
- **Files:** `TitanEngine/MainWindow.xaml.cs` (+1 dòng).
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**.
- **Commit/push:** `a5b60dc` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 24 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** `OnMainWindowClosed` dọn preview loaders + timer + API server nhưng KHÔNG cancel `_cancellationTokenSource` — nếu đang render mà đóng app thì ffmpeg child (không thuộc job object của Windows) có thể sống sót process mồ côi và tiếp tục ghi output. Nút STOP thì có cancel (dòng 16721) nên chỉ thiếu ở chỗ đóng cửa sổ.
- **CODE:** +1 dòng `try { _cancellationTokenSource?.Cancel(); } catch { }` đầu `OnMainWindowClosed` — token đã được `RunFfmpegCaptureAsync` nghe và kill process khi fire.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (+1 dòng + comment).
- **Protected-core impact:** không — chỉ shutdown cleanup; hành vi khi đang render vẫn như nút Stop.
- **TEST:** build Release **0/0** + Debug **0/0**.
- **Commit/push:** `f62555f` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 25 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** It8 cap log ở 3000 dòng bằng cách cắtflix — nhưng người dùng không biết là đã bị cắt (dòng đầu tiên tự dưng là giữa chừng).
- **CODE:** khi trim, prepend một dòng marker `--- N older log lines hidden ---` và tính cả dòng marker vào counter.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (2 dòng trong khối trim của `LogSystem`).
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**.
- **Commit/push:** `b4a0652` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 26 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** It17 thêm phím tắt nhưng không có gì cho người dùng biết (3 nút chính không tooltip) → tính năng "vô hình".
- **CODE:** thêm `ToolTip` vào `btnBrowseVideo` ("Ctrl+O"), `btnAddJob` ("Ctrl+B"), `btnStart` ("Ctrl+Enter", nhắc nhở nhấn lần nữa để stop). Chỉ thêm text tooltip trên control đã có — không đổi layout.
- **Files:** `TitanEngine/MainWindow.xaml` (3 attribute).
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**.
- **Commit/push:** `52b575d` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 27 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Nút ✕ xoá job trong queue có tooltip "Remove this job" nhưng không nhắc phím Delete (It17/26).
- **CODE:** tooltip thành "Remove this job (Del)". 1 attribute.
- **Files:** `TitanEngine/MainWindow.xaml`.
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**.
- **Commit/push:** `5fb7623` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 28 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Kéo watermark trên preview (`ImgWatermark_MouseDown/Move/Up`) chỉ reset `_isWatermarkDragging` ở MouseUp — nếu mất capture mà không có MouseUp (mở dialog giữa chừng, alt-tab) thì cờ vẫn true, chuột đi ngang qua là hình nhảy theo mà không cần giữ nút.
- **CODE:** wire thêm `LostMouseCapture="ImgWatermark_LostMouseCapture"` trên `imgWatermarkPreview` + handler mới reset cờ. Không đổi hành vi drag bình thường.
- **Files:** `TitanEngine/MainWindow.xaml` (+1 attribute), `TitanEngine/MainWindow.xaml.cs` (+1 method nhỏ).
- **Protected-core impact:** không — chỉ UI interaction.
- **TEST:** build Release **0/0**.
- **Commit/push:** `5db9c19` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 29 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Quét hết các chỗ `CaptureMouse`: cùng bug như It28 ở khung crop (`BdrCropFrame_MouseDown/Move/Up`, cờ `_isCropDragging` không reset khi mất capture). Còn `_isDraggingTimeline` trong AudioEditorWindow là dead state (không bao giờ set true — slider dùng `_isUpdatingSlider`), để nguyên vì vô hại.
- **CODE:** wire `LostMouseCapture="BdrCropFrame_LostMouseCapture"` lên `bdrCropFrame` + handler reset cờ, y hệt It28.
- **Files:** `TitanEngine/MainWindow.xaml` (+1 attribute), `TitanEngine/MainWindow.xaml.cs` (+1 method).
- **Protected-core impact:** không.
- **TEST:** build Release **0/0**.
- **Commit/push:** `8e3eb2b` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 30 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Ô "Output Name" (`txtOutputName`) đưa thẳng vào tên file output (:15954) — nếu gõ ký tự cấm của filename (`\ / : * ? " < > |`) thì render fail ở bước encode/copy. `SanitizeRandomNamePart` đã có sẵn nhưng chỉ dùng cho tên random. API path đã an toàn nhờ `Path.GetFullPath` ở :17518 ném exception trước.
- **CODE:** ở chỗ tính `outName` cho job UI, chạy qua `SanitizeRandomNamePart(txtOutputName.Text, defaultName)` — ký tự cấm → `_`, tối đa 40 ký tự, rỗng → fallback tên theo source (đúng như khi để trống).
- **Files:** `TitanEngine/MainWindow.xaml.cs` (1 khối).
- **Protected-core impact:** không — chỉ validation tên file ở UI glue; tên hợp lệ không đổi byte nào.
- **TEST:** build Release **0/0** + Debug **0/0**.
- **Commit/push:** `443646e` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 31 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Cùng mạch với It19/22/23 — port của HOST API (`PortInputWindow`) không được nhớ giữa các lần chạy, mỗi lần bật HOST phải gõ lại 5555.
- **CODE:** `AppSettings` thêm `LastApiPort` (validate 1-65535 lúc load, lưu vào settings.json); `PortInputWindow` khởi tạo ô port từ `LastApiPort` và `RememberApiPort` khi OK; `MainWindow` constructor set `LocalApiPrefix` từ `LastApiPort` nếu có.
- **Files:** `TitanEngine/AppSettings.cs`, `TitanEngine/PortInputWindow.xaml.cs`, `TitanEngine/MainWindow.xaml.cs` (mỗi file vài dòng).
- **Protected-core impact:** không — chỉ UI preference; prefix mặc định vẫn 5555 khi chưa có setting.
- **TEST:** build Release **0/0** + Debug **0/0**.
- **Commit/push:** `4a689e8` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 32 — 2026-11-01

- **Path:** REVIEW → CODE (docs) → TEST
- **REVIEW/CODE:** It31 thêm `LastApiPort` vào settings.json nhưng `docs/00-OVERVIEW.md` mới chỉ nói "thư mục source/output" → cập nhật bullet cho đủ (thêm port của HOST API).
- **Files:** `docs/00-OVERVIEW.md`.
- **Protected-core impact:** không.
- **TEST:** markdown only.
- **Commit/push:** `4c3843b` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 33 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Nút CLEAR xoá queue vô điều kiện (`BtnClear_Click` = `JobQueue.Clear()`) — đang chạy batch mà bấm CLEAR thì job đang render biến mất khỏi UI nhưng vẫn chạy ngầm → dễ nhầm là đã huỷ. (Bản thân không crash vì vòng render iterate trên snapshot `pending`.)
- **CODE:** khi `_isBatchRunning` và queue không rỗng thì hỏi confirm Yes/No, có gợi ý dùng STOP RENDER để huỷ đúng cách. Khi không chạy thì giữ nguyên hành vi cũ (xoá ngay, không hỏi).
- **Files:** `TitanEngine/MainWindow.xaml.cs` (1 handler).
- **Protected-core impact:** không — chỉ UI guard; không đổi logic render/queue.
- **TEST:** build Release **0/0**.
- **Commit/push:** `d405c5d` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 34 — 2026-11-01 (second e2e regression pass — no code change)

- **Path:** REVIEW → TEST
- **TEST:** e2e lần 2 với style khác (`e2e neon-bars` qua harness `%TEMP%\SbSphere` trên Debug DLL mới nhất): render **21.1 s OK**, output exists; `ffmpeg -v error -i out -f null -` → **0 lỗi decode**. Pipeline ổn định trên nhiều style sau 33 iteration đổi UI.
- **Files:** không đổi code.
- **Commit/push:** `9830574` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 35 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Ô Custom Bitrate nhận raw vào `job.CustomBitrate` (UI :16081/:16579, API :18074). Phía encoder chỉ `long.TryParse` rồi giữ nguyên giá trị trước nếu parse fail, và số âm/lớn sẽ thành `-b:v` lỗi → render fail muộn. (Phát hiện thêm: `/api/soundbar/presets` không có cờ `usesVoice` dù r36 mọi style đều ăn Voice — nhưng đó là vùng protected soundbar nên chỉ ghi vào BACKLOG, không sửa.)
- **CODE:** helper mới `SanitizeCustomBitrate` (parse + clamp 100..200000 kbps, sai thì về mặc định "5000"), áp ở cả 2 chỗ UI và 1 chỗ API. Encoder không đụng.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (+helper, 3 call site).
- **Protected-core impact:** không — chỉ validate input ở lớp UI/API; encoder giữ nguyên.
- **TEST:** build Release **0/0** + Debug **0/0**.
- **Commit/push:** `494f6e9` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 36 — 2026-11-01 (docs: 2 phát hiện cần authorization)

- **Path:** REVIEW/INVESTIGATION → CODE (docs)
- **REVIEW:** 2 phát hiện ở vùng cần bạn quyết định, đã ghi vào BACKLOG (mục 9, 10): thiếu cờ `usesVoice` trong `/api/soundbar/presets`; và `OnSystemLoaded` tự bật HOST API lúc boot trong khi docs nói OFF mặc định.
- **Commit/push:** `494f6e9` (chỉ log).

---

## ITERATION 37 — 2026-11-01

- **Path:** REVIEW/INVESTIGATION → CODE → TEST
- **REVIEW:** Custom resolution chỉ check `> 0` — nhập `99999x99999` (UI) hoặc `targetWidth=999999` (API) sẽ fail muộn trong `scale` của ffmpeg. Preset combo an toàn vì giá trị có sẵn.
- **CODE:** clamp 16..7680 ở cả 2 đường: chỗ parse custom của UI (:15983) và một clamp chung cho mọi path API (:18053, bao cả targetWidth/targetHeight gửi thẳng). Encoder/scale không đụng.
- **Files:** `TitanEngine/MainWindow.xaml.cs` (2 chỗ).
- **Protected-core impact:** không — validate input ở job-creation; `-1` (Original) không bị đụng vì clamp chỉ khi > 0.
- **TEST:** build Release **0/0** + Debug **0/0**.
- **Commit/push:** `5a45578` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## ITERATION 38 — 2026-11-01

- **Path:** REVIEW → CODE (docs) → TEST
- **REVIEW/CODE:** It35/It37 thêm clamp cho `customBitrate` (100–200000 kbps) và `targetWidth/Height` (16–7680) nhưng docs API chưa ghi → thêm ghi chú vào `docs/30-API.md` §3 (output/API fields).
- **Files:** `docs/30-API.md`.
- **Protected-core impact:** không.
- **TEST:** build Release + Debug 0/0 (chạy ở checkpoint dừng).
- **Commit/push:** `d87cb83` — pushed + verified on `origin/autonomous/continuous-improvements-20261031-201926`

---

## STOP REPORT — 2026-11-01 (user gõ STOP AUTONOMOUS LOOP lần 2)

- **Branch:** `autonomous/continuous-improvements-20261031-201926`, HEAD `d87cb83`, đã push + verify, tree sạch, sync origin. Backup gốc `backup/pre-autonomous-improvements-20261031-201926` = `ac8bd33` không đụng; `master` không đổi.
- **Tổng kết:** 38 iteration (It1–It38). Build Release + Debug đều **0 warn / 0 err** ở checkpoint cuối; smoke-launch exe OK; 2 lần e2e render thật (audio-ring 23.1 s, neon-bars 21.1 s) với ffprobe đúng và full-decode 0 lỗi.
- **Không còn gì chưa push.** Vấn đề mở cần bạn quyết định nằm ở BACKLOG (mục 1: gộp 2 API guide; mục 2: protected core; mục 3: tách MainWindow.xaml.cs; mục 4: feature mới; mục 9: `usesVoice` trong presets API; mục 10: HOST API tự bật lúc boot có đúng ý không).

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
9. **[protected core — cần authorization]** `/api/soundbar/presets` thiếu cờ `usesVoice` (mọi style đã ăn Voice từ r36, nhưng `SoundBarStyleInfo` và payload endpoint chưa có field này — `MainWindow.xaml.cs:20444` + `AudioAnalysisModels.cs:51`). Chỉ là metadata đọc-only, không đổi hành vi vẽ, nhưng nằm trong module soundbar nên cần bạn cho phép trước.
10. **[cần bạn quyết định — API là critical integration, không tự đổi]** `OnSystemLoaded` (`MainWindow.xaml.cs:12574`) gọi `StartLocalApiServer()` ngay lúc boot → HOST mode ON mặc định, trong khi `docs/00-OVERVIEW.md` + `docs/30-API.md` đều ghi "OFF mặc định, 503 cho tới khi bấm HOST" và log in ra "[API] Host mode OFF". hoặc code hoặc docs đang sai một; nếu BAS của bạn đang dựa vào việc API tự bật thì đừng sửa code.
