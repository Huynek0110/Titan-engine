# TitanEngine — Code Review Findings (single shared report)

> Working document for the review/fix campaign.
> **Phase 1**: read every source file, record findings here. **Phase 2**: read this file back and apply fixes, thinking deeply before touching heavy/complex structures.
>
> Rules honoured by every entry:
> - No implementation change without a traced, reproducible reason.
> - Line numbers are from the working tree at review time (working tree also carries the fixes marked `FIXED`).
> - Invariants from `AGENTS.md` are never "fixed": `amix…:normalize=0,alimiter=limit=0.95` (~35 sites), asset-only fail-fast for Light Leak / Rain, overlay type from probe (path+size+mtime), SoundBar pixel change ⇒ bump `RendererVersion` ("soundbar-r38").
> - Status legend: `FIXED` (already applied, build-verified) · `OPEN` (confirmed, fix not yet applied) · `NEEDS-DECISION` (behaviour change; needs owner's call) · `REFUTED` (investigated, not a bug — kept so it is not re-investigated).

---

## 0. Campaign status

| Area | Files | State |
|---|---|---|
| Small support/model files | OverlayContentLayout, TitanTransitionLibrary, TitanTransitionModels, TitanTransitionGraphBuilder, AudioAnalysisModels, VideoAssetProbeInfo, App.xaml(.cs), AssemblyInfo | reviewed |
| Recipe system | EffectRecipeLibrary, EffectRecipeModels, EffectRecipeRenderSupport | reviewed |
| Overlay probing | OverlayAssetBackgroundAnalysis | reviewed |
| Particle / palette / models | ParticleOverlayGenerator, SoundBarPalette, MainWindow.Models, AudioEditModels, VideoAssetProbeInfo, TitanTransitionModels | reviewed |
| UI windows | AudioEditorWindow.xaml.cs, PortInputWindow.xaml.cs, App.xaml.cs, AssemblyInfo.cs | reviewed |
| Renderer / presets | SoundBarStyleRenderer, AudioSpectrumAnalyzer, SoundBarVisualizerPreset, SnowfallOverlayPreset, LightLeakBurnPreset, DreamyDot(2)OverlayPreset, PolaroidScrapbook2Preset, RgbPolaroidLedPreset, DashedPolaroidFramePreset, ScratchVideoPreset, LightLeakOverlayPreset, RainOverlayPreset | pending |
| MainWindow.xaml.cs (20,566 lines) | chunked pass | in progress |

Every fix batch is verified with `dotnet build TitanEngine/TitanEngine.csproj -c Release -p:EnableWindowsTargeting=true` (baseline: 0 warnings / 0 errors).

---

## 1. Findings

### F-01 · FIXED · OverlayAssetBackgroundAnalysis.cs — one bad frame killed the whole probe
`SampleCoverageAsync` called `AnalyzeFrame(...)` unguarded inside the sampling loop. A single corrupt/undecodable frame (`BitmapDecoder.Create` / `CopyPixels` throws) aborted the entire analysis task, so a probe whose contract is "fall back to Unknown + low-opacity screen blend" instead propagated the exception into the render.
**Fix applied**: wrapped the call in `try/catch` filtered on `is not OperationCanceledException`; partial samples are kept, and if no frame survives `Samples == 0` keeps the caller's Unknown default.

### F-02 · FIXED · EffectRecipeRenderSupport.cs — provably dead branch in `BuildMissingAssetFallbackFilter`
The `if (recipe.Name.Equals("FilmBurnWarm", …))` branch was unreachable: the same method already throws for every FilmBurnWarm / light-leak-keyed recipe above it, and `EffectRecipe.Name` is non-nullable (`EffectRecipeModels.cs:118`, `= string.Empty`), so no NPE path existed either.
**Fix applied**: branch removed, replaced with a comment explaining why light leak must never take a procedural fallback.

### F-03 · FIXED · stale "12 styles" comments (AudioAnalysisModels.cs, SoundBarVisualizerPreset.cs)
`SoundBarStyleId` has 13 members (`AudioRing = 12`, r22+). Two XML docs still said "The 12 …". Comment-only fix.

### F-10 · REFUTED · TitanTransitionGraphBuilder.cs:171 — "NaN duration reaches the graph builder"
Hypothesis: a non-numeric probe result yields `double.NaN`, which would poison the xfade offset. Traced the producer: `AnalyzeMediaDetailsAsync` (MainWindow.xaml.cs:1123) parses with `double.TryParse`, so a failed parse yields `0.0`, never `NaN`. Not a bug; no change.

### F-20 · NEEDS-DECISION · EffectRecipeLibrary.cs — the recipe lookups are never populated
The five lookup dictionaries (`:374-378`) are initialised **empty**, and `BuildLookup` (`:438`) is never called — there is no static constructor and no other caller in the file (verified by grep). Consequences:
- `TryGetOverlayEffectRecipe` / `TryGetParticleEffectRecipe` / `TryGetPostProcessEffectRecipe` / `TryGetTransitionRecipe` / `TryResolveCanonicalEffectName` can only ever return `false`, so every `use…Recipe…` flag derived from them (MainWindow.xaml.cs:2091-2130) is permanently `false`.
- `ResolveTemplateStack` and the whole `TemplateEffectStackRecipe` dataset are dead: no caller anywhere in the repo.
- Because every canonical recipe name is also in `RetiredRecipeNames`, and `ResolveFxEffectName` / `ResolveOverlayEffectName` (MainWindow.xaml.cs:9166 / :9205) test `IsRetiredRecipeName` **first**, exact retired names are already rejected before the lookups are consulted — so for exact names, empty-vs-populated lookups are behaviourally identical.
- The one place they *differ*: **aliases**. `"warm film burn"` normalises to `warmfilmburn`, which is *not* in the retired set, so with empty lookups it falls through to the fuzzy matcher and becomes the live `"Light Leak Burn"` effect (MainWindow.xaml.cs:9180); with populated lookups it would resolve to the retired canonical `FilmBurnWarm` and be dropped.
**Why not fixed here**: both readings are defensible (the file was added in the repo's single squashed commit `ac8bd33`, so history cannot arbitrate). Restoring population is a real behaviour change for alias inputs and would silently re-arm the recipe subsystem; deleting the dead data would remove the reference table the retirement list is derived from. Owner's call.

### F-21 · NEEDS-DECISION · audio volume at slider 0 emits `volume=0.0`
MainWindow.xaml.cs:7693 / :7733 emit a `volume` filter whenever `Math.Abs(v - 1.0) > 0.0001`, which includes `v == 0`. `volume=0` is a valid hard mute, but AGENTS.md trap 15 warns about volume handling; the surrounding code elsewhere relies on the audio graph always producing a stream. Flagged for confirmation, not changed.

### F-22 · NEEDS-DECISION · two temp-root conventions in the same codebase
`Path.Combine(Path.GetTempPath(), "TitanEngine", …)` (e.g. OverlayAssetBackgroundAnalysis.cs:136) coexists with `"TitanEngineTemp"` roots in other files. Both work; unifying them would invalidate existing caches and touch many call sites. Owner's call.

### F-23 · NEEDS-DECISION · EffectRecipeRenderSupport.cs:375 — placed-mode sequence clip hardcodes a 1920×1920 canvas
`BuildLoopedSequenceClipGraph` resolves placement geometry against a fixed 1920×1920 canvas instead of the render canvas. Untouched: it sits inside the blend/timing block (`:444-446`) whose behaviour depends on that geometry, so any "fix" needs a render comparison, not a code read.

### F-30 · DOC (informational) · code-vs-documentation discrepancies already recorded in CLAUDE.md
`/api/render` defaults to async (:17312-17330); fade is applied **after** watermark in the overlay branches (:8293-8296); the `normalize=0` invariant actually appears 35 times (not 34); the V106 header comment and the `TitanEngineV60` setup path are stale; the root `BAS_LOCALHOST_API_GUIDE.txt` is a stale partial duplicate (997 lines) of the canonical `TitanEngine/BAS_LOCALHOST_API_GUIDE.txt` (1,196 lines, 835-line diff); the `[FFMPEG-ERROR-DETAILS]` tag does not exist in code. Documentation targets, deliberately not "fixed" in source.

### F-04 · FIXED · PortInputWindow.xaml.cs:52 — API port is stored as raw text, not the parsed number
`BtnOk_Click` validates with `int.TryParse(txtPort.Text, out int port)` and then stores `SelectedPort = txtPort.Text;`. `int.TryParse` accepts leading/trailing whitespace and a sign, so `" 5555 "` and `"+5555"` pass validation and are stored verbatim. The consumer at `MainWindow.xaml.cs:16792` builds
`LocalApiPrefix = $"http://{portWindow.LocalIp}:{portWindow.SelectedPort}/"`, which reaches `HttpListener.Prefixes.Add(LocalApiPrefix)` at `:16841` — an invalid prefix throws there, so HOST mode fails to start and the catch logs the misleading `netsh http add urlacl` hint (`:16861`).
**Fix applied**: `SelectedPort = port.ToString(CultureInfo.InvariantCulture);` (+ `using System.Globalization;`) — zero behaviour change for well-formed input; `" 5555 "` / `"+5555"` giờ được chuẩn hoá thành `"5555"` thay vì ném vào `HttpListener.Prefixes` nguyên văn.

### F-05 · FIXED · AudioEditorWindow.xaml.cs:446 — `GetRangeEndFallback()` is dead
Repo-wide grep finds only the definition; it is private and cannot be a XAML event handler (no handler signature). Dead code, no behaviour attached. **Fix applied**: method deleted (re-verified: zero references repo-wide).

### F-06 · FIXED · AudioEditorWindow.xaml.cs:26 / :158 / :348 — `_isDraggingTimeline` can never be true
The field was only ever assigned `false`, so the guard at `:158` was always taken; no drag path sets it (`AudioEditorWindow.xaml` wires only `MouseLeftButtonDown`). **Fix applied (behaviour-identical)**: field deleted, guard unwrapped to the plain slider update, stray `= false` at the click handler removed.

### F-07 · FIXED (trivial) · AudioEditorWindow.xaml.cs:287-291 — redundant `UpdateMetaText()`
`Segments.Clear()` raises `CollectionChanged`, and `Segments_CollectionChanged` already calls `RedrawTimeline()` + `UpdateMetaText()` unconditionally. **Fix applied**: duplicate `UpdateMetaText()` call removed.

### F-08 · REVIEWED-CLEAN (no defect) · 11 files
`AudioEditModels.cs`, `MainWindow.Models.cs`, `PortInputWindow.xaml.cs` (bar F-04), `SoundBarPalette.cs`, `VideoAssetProbeInfo.cs`, `TitanTransitionModels.cs`, `ParticleOverlayGenerator.cs`, `OverlayContentLayout.cs`, `TitanTransitionLibrary.cs`, `EffectRecipeModels.cs`, `App.xaml.cs`/`AssemblyInfo.cs`.
Positive verifications worth keeping (so they are not re-investigated):
- The "aliased-but-unread API field" bug class does **not** currently exist: `Audio2Volume` **and** `SecondaryAudioVolume` are both consumed at `MainWindow.xaml.cs:18003-18004` (null-coalescing chain), `Features` at `:19827+`, `AudioSegments` at `:18050`, `AudioFile2` handled as its own mix path. The warning comment in `MainWindow.Models.cs:391-396` is already honoured.
- `ParticleOverlayGenerator` is well-hardened: deterministic seeding from `SHA256(recipeId|cacheKey)` (not `string.GetHashCode`), `InvariantCulture` for every numeric parse, brushes/geometries frozen, cache re-validated by manifest **and** first-frame existence, STA render thread with cancellation checks, `EnsureEven` guard on dimensions.
- `SoundBarPalette`: hex parsing never throws, gradient stops clamped, `Names` returns a clone, all lookups culture-invariant.

---

## 2. Fix log (phase 2)

| # | File | Change | Build |
|---|---|---|---|
| 1 | OverlayAssetBackgroundAnalysis.cs | fail-open per-frame try/catch (F-01) | 0/0 Release |
| 2 | EffectRecipeRenderSupport.cs | dead FilmBurnWarm branch removed (F-02) | 0/0 Release |
| 3 | AudioAnalysisModels.cs | "12 styles" → "13 styles" (F-03) | 0/0 Release |
| 4 | SoundBarVisualizerPreset.cs | "All 12 styles" → "All 13 styles" (F-03) | 0/0 Release |
| 5 | 6 files (see §3) | 3 soundbar styles mới port từ Noctalia 5.4.1: `echo-wave`, `sunburst`, `media-card` + UI panel + API fields | 0/0 Release + Debug (full rebuild) |
| 6 | PortInputWindow.xaml.cs | F-04: lưu port đã parse (`port.ToString(InvariantCulture)`) thay vì raw text | 0/0 Release + Debug |
| 7 | AudioEditorWindow.xaml.cs | F-05 xoá `GetRangeEndFallback` · F-06 gỡ `_isDraggingTimeline` (guard luôn true) · F-07 bỏ `UpdateMetaText()` trùng | 0/0 Release + Debug |
| 8 | SoundBarStyleRenderer.cs | đối chiếu lại với 4 ảnh reference + source gốc Noctalia: sunburst thêm annulus đặc ở mép lỗ (shader tô wedge từ innerRadius, `:198-253`), ripple alpha bỏ thừa 0.5 (`:121-122`), media-card: title/artist màu accent, text căn giữa phần trên, spectrum strip ở hàng controls dưới đáy (`:424-442`), progress = full-card fill (media_widget.cpp `:196-203`) | 0/0 Release + Debug |

### Test với media thật (host Linux, 2026-11-06, file ở `tmp_sbtest/` — gitignored)
- Tải `sample_video.mp4` (BBB 640×360@30 10s, không có audio stream) + `sample_music.mp3` (44.1k stereo 19s).
- Analyzer path: decode nhạc `-ac 1 -ar 22050 -f s16le` ra đúng số mẫu (422784 ≈ 19.17s×22050); `silencedetect -48dB/1s` trên nhạc không có đoạn im → gate mở suốt; `volumedetect` mean −21.8 dB / max −5.9 dB.
- Composite path đúng recipe của app: 150 frame PNG RGBA (yuva420p) + `overlay=0:H-h:enable='between(t,2,7)'` + nhạc làm audio → `libx264/yuv420p + aac` → ffprobe OK, decode `-f null -` **0 lỗi**; YAVG dải dưới khác rõ theo gate: t=1s **129.1** (OFF) → t=3s/5s **81.0** (ON) → t=9s **118.7** (OFF).
- Unit-test toán học các công thức port (console app scratch `tmp_sbtest/mathtest`, 9/9 PASS): echo-wave pooling/blur/1-6-sampler/edge-fade/seam; sunburst mirroredPos, r1 ∈ [r0, 0.95·radius], độ rộng wedge = 2·barW·radius; media-card layout nằm trong thẻ.
- **Vẫn chưa test được runtime WPF** (chỉ chạy được trên Windows): cần mở app, chọn 3 style xem preview, render 1 job thật rồi decode kiểm output.

---

## 3. Feature — 3 soundbar style mới port từ Noctalia 5.4.1 (2026-11-06)

Nguồn: `/home/hina/noctalia-src-v5.4.1` (tag v5.4.1), báo cáo `/home/hina/noctalia-media-widgets-report.md`.

| Style mới | id | Port từ | Ý chính |
|---|---|---|---|
| Echo Wave | `echo-wave` (enum 13) | `audio_spectrum_program.cpp` `show_wave=true` | 32 điểm điều khiển pool từ band, gamma 0.4 + baseline 0.10, blur 3-tap 0.25/0.5/0.25, đường cong đúng công thức 1/6 trong source, fade mép smoothstep 18%, thêm lớp echo lệch `0.3*pointCount` scale 0.55 |
| Sunburst | `sunburst` (enum 14) | `fancy_audio_visualizer_program.cpp` mode bars+rings | 64 tia đối xứng từ vòng trong (inner 0.7), ramp 3 màu primary*0.6→primary→secondary, sáng theo bass, treble glint, wobble, 6 vòng trang trí (ripple/energy 8 cung/particle 16 chấm/grid/accent/outer), bloom; **xoay đúng 1 vòng/loop** (`ctx.Time*2π`) để seam; plot vuông qua `UsesSquarePlot` |
| Media Card | `media-card` (enum 15) | `desktop_media_player_widget` | thẻ bo góc: ảnh bìa (cover-fit, cache `ArtworkCache` theo full path, DecodePixelWidth 256, placeholder nốt nhạc nếu thiếu) + tên bài bold + nghệ sĩ + spectrum 16 dải alpha 0.5 + thanh tiến trình ở đáy thẻ quét theo `ctx.Time` trong PNG loop |

Điểm nối (đều đã có, không đụng style khác):
- `AudioAnalysisModels.cs`: enum 13/14/15 + `SoundBarSettings.MediaArtworkPath/MediaTitle/MediaArtist`.
- `SoundBarVisualizerPreset.cs`: 3 entry trong `BuildStyles()`; `AppendGeometryCacheMaterial` chỉ thêm `art=`(kèm size:mtime)/`title=`/`artist=` **khi style == MediaCard** → cache key của 13 style cũ byte-identical, **không bump RendererVersion (vẫn r38)** — quyết định có chủ đích vì không có pixel nào của style cũ đổi.
- `SoundBarStyleRenderer.cs`: 3 `case` trong dispatch + `UsesSquarePlot` += Sunburst + 3 cụm method mới.
- `MainWindow.Models.cs`: `RenderJob.SoundBarArtworkPath/MediaTitle/MediaArtist` + 3 field nullable ở `LocalhostRenderRequest`.
- `MainWindow.xaml` + `.xaml.cs`: panel `pnlMediaCard` chỉ hiện khi chọn media-card (`UpdateMediaCardPanel` trong `CmbSoundBar_Changed`); chọn ảnh (`BtnPickMediaCardArt_Click`), 2 ô text, và **thanh tiến trình bám theo VIDEO preview** `medCropPreview` qua `DispatcherTimer` 250 ms (`UpdateMediaCardVideoProgress`) — trong PNG loop không có đồng hồ video nên thanh trong thẻ quét theo loop, còn thanh ở UI bám playhead video như yêu cầu.
- Render path + preview + UI job builder + API mapper (`soundBarArtworkPath`/`soundBarMediaTitle`/`soundBarMediaArtist` + alias qua `Features` bag).
- Doc: `TitanEngine/BAS_LOCALHOST_API_GUIDE.txt` (canonical: 16 ids, field mới, alias, note 7 style vẽ hình vuông), `docs/20-SOUNDBAR.md`, `docs/00-OVERVIEW.md`. Bản guide ở repo root cố tình để lệch (đã tự khai báo stale, không phải canonical).

Tự kiểm tra (trên Linux, đúng giới hạn của host này):
- Build cross-compile `Release` + `Debug` `--no-incremental`: **0 warning / 0 error cả hai** (SDK 8.0.427, `-p:EnableWindowsTargeting=true`).
- `git diff --check` clean.
- Đồng bộ đếm bằng script: enum 16 = `MakeStyle` 16 = `case` dispatch 16; kebab id mới có trong catalogue (ParseStyleId resolve tự động); tên XAML (`pnlMediaCard`, `imgMediaCardArt`, `txtMediaCardArtPath`, `btnPickMediaCardArt`, `txtMediaCardTitle`, `txtMediaCardArtist`, `txtMediaCardProgress`, `prgMediaCardVideo`) khớp handler code-behind (`BtnPickMediaCardArt_Click`, `TxtMediaCardField_Changed`).
- Port kiểm lại với source C++: công thức đường cong (hệ 1/6), 3-tap blur, edge fade, echo offset/scale của echo-wave khớp verbatim `audio_spectrum_program.cpp:195-250`.
- **Chưa test được runtime** (WPF chỉ chạy được trên Windows — xem CLAUDE.md §8): cần mở app trên Windows, chọn từng style xem preview, rồi render 1 job thật có `soundBarStyle:"media-card"` + 3 field media để kiểm decode output (`ffmpeg -v error -f null -`).
