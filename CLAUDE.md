# CLAUDE.md — TitanEngine

> Handbook cho các phiên Claude Code làm việc với repo này. Viết ngày 2026-11-05 từ
> kiểm chứng trực tiếp mã nguồn (mọi claim quan trọng có `file:line`), không phải từ suy đoán.
> Đọc `AGENTS.md` trước — luật cứng ở đó vẫn có hiệu lực và được lồng vào đây.
> Để ý chi tiết theo từng subsystem thì đọc đúng file trong `docs/` (xem §2), đừng đọc hết.

---

## 1. Project Identity

- **TitanEngine** (`github.com/Huynek0110/Titan-engine`): **WPF video editor / render engine** —
  nhận video hoặc ảnh, xử lý bằng **FFmpeg CLI**, xuất **MP4**.
- **Chỉ chạy trên Windows.** `net8.0-windows` + WPF (`WinExe`), `Nullable` + `ImplicitUsings` bật,
  **zero NuGet package** (xem `TitanEngine/TitanEngine.csproj` — không có `PackageReference` nào).
- Render bằng `ffmpeg.exe` / `ffprobe.exe` đặt **cạnh file .exe** (không dùng PATH) — resolve ở
  `EngineCore.Initialize()` (`TitanEngine/MainWindow.xaml.cs:134`).
- Hai chế độ hoạt động, **cùng một pipeline**:
  1. **UI mode** — chọn file, chỉnh thông số, đưa job vào queue, render hàng loạt.
  2. **Host mode (API)** — HTTP server nội bộ `http://127.0.0.1:5555/`, bật bằng nút **HOST**;
     tắt → mọi request trả `503 Host mode is OFF`.
- Khả năng chính: merge/timeline nhiều video (concat hoặc transition), image timeline, template
  stack, AI upscale 2x/4x, motion (60fps/RSMB), slow motion 0.10x–8x, FX + post-process recipe,
  color grading, text/caption (Whisper), overlay (tuyết/mưa/light leak/dreamy dots/scratch/particle/
  polaroid/soundbar), watermark/brand logo, audio mix 2 track + concat nhiều track, audio editor,
  queue + log FFmpeg.
- Dấu mốc phiên bản (đã verify): title bar `Titan Engine V193 UI+Soundbar Update`
  (`TitanEngine/MainWindow.xaml:7`); soundbar renderer `soundbar-r38`
  (`SoundBarVisualizerPreset.cs:270`); **header comment trong `MainWindow.xaml.cs` vẫn ghi V106 —
  đã lệch, là comment lịch sử, đừng dùng làm nguồn sự thật.**

## 2. Repository Architecture

```
repo root
├── AGENTS.md                        # luật cứng + workflow (giữ nguyên, không sửa)
├── CLAUDE.md                        # file này
├── docs/00-OVERVIEW.md              # định hướng: app là gì, file nào làm gì
├── docs/10-RENDER-PIPELINE.md       # pipeline render, filter chain, audio, encoder, upscale
├── docs/20-SOUNDBAR.md              # soundbar / audio analysis / vocal / timeline gate
├── docs/30-API.md                   # bản đồ HTTP API (hướng dẫn, không phải hợp đồng đầy đủ)
├── BAS_LOCALHOST_API_GUIDE.txt      # ⚠ BẢN CŨ, lệch — bản canonical nằm trong TitanEngine/ (xem §7)
├── TitanEngine/BAS_LOCALHOST_API_GUIDE.txt  # ✅ CANONICAL — được csproj đóng gói
├── Modelfile, Modelfile2            # Ollama (qwen3.5:9b, qwen2.5-coder:7b) — KHÔNG được code tham chiếu
├── setup_titan.ps1                  # tải FFmpeg — ⚠ đường dẫn trong script đã cũ (xem §13)
├── TitanEngine.slnx                 # 1 project duy nhất
├── PROJECT_SUMMARY.md               # untracked, bản tóm tắt của phiên agent trước — tham khảo, không authoritative
└── TitanEngine/                     # toàn bộ source (111 file)
    ├── App.xaml(.cs)                # entry: StartupUri=MainWindow.xaml; App.xaml.cs rỗng (14 dòng)
    ├── MainWindow.xaml              # 1.585 dòng — toàn bộ layout UI
    ├── MainWindow.xaml.cs           # 20.566 dòng — TRUNG TÂM: class EngineCore (static) + MainWindow
    ├── MainWindow.Models.cs         # RenderJob:34, LocalhostRenderRequest:360, LocalhostRenderResponse:536,
    │                                #   LocalhostQueueJobInfo:628, ApiRenderJobState:694
    ├── AudioEditorWindow.xaml(.cs)  # audio editor: trim / segment cuts / normalize / preview
    ├── AudioEditModels.cs, AudioAnalysisModels.cs
    ├── AudioSpectrumAnalyzer.cs     # 2.429 dòng — PCM → FFT → bands/waveform/Voice/onset
    ├── SoundBarStyleRenderer.cs     # 4.462 dòng — hình học 13 style soundbar
    ├── SoundBarVisualizerPreset.cs  # sinh PNG loop, cache SHA-256 — RendererVersion ở đây (:270)
    ├── SoundBarPalette.cs           # 30 palette NCS
    ├── EffectRecipeLibrary.cs       # registry effect/preset + RetiredRecipeNames (:9)
    ├── EffectRecipeModels.cs, EffectRecipeRenderSupport.cs   # model + bridge recipe → filter graph
    ├── OverlayAssetBackgroundAnalysis.cs  # probe asset thật, cache theo path+size+mtime
    ├── OverlayContentLayout.cs, VideoAssetProbeInfo.cs
    ├── TitanTransitionLibrary.cs, TitanTransitionGraphBuilder.cs  # xfade / transition
    ├── *OverlayPreset.cs, ParticleOverlayGenerator.cs           # các họ overlay
    ├── PortInputWindow.xaml(.cs)    # chọn IP/port cho API (mặc định localhost:5555)
    ├── TitanEngine.csproj           # net8.0-windows, WPF, zero NuGet + các rule copy Content
    ├── Assets/  Overlays/{Snow,Rain,LightLeaks,DreamyDots}, Effects/{church,echo}_ir.wav, Fonts/, Whisper/ (gitignored)
    ├── Content/ Overlays/{Snow,LightLeaks}, Branding/video-logo.png
    └── shaders/                     # chỉ FSRCNNX.glsl được code dùng (qua libplacebo); FSR.glsl + Anime4K_* là asset thừa, không .cs nào tham chiếu
```

**Entry point & luồng chính:**

- `App.xaml` (`StartupUri="MainWindow.xaml"`) → `MainWindow` → `EngineCore.Initialize()`
  (`MainWindow.xaml.cs:134`): resolve `ffmpeg.exe`/`ffprobe.exe` cạnh exe, `upscayl\Upscayl.exe`,
  `dlss\DLVSR.exe` (+ `RefreshNvidiaUpscaleBackendPaths()` tìm `UpscalePipelineApp.exe`).
  Thiếu ffmpeg → hỏi chọn tay hoặc `throw FileNotFoundException`.
- **Pipeline duy nhất:** `EngineCore.ExecuteRenderAsync(RenderJob, …)` tại `MainWindow.xaml.cs:1426`.
  UI batch gọi ở `:16712`, API gọi ở `:19627` — mọi fix ở đây áp cho cả hai mode.
- Queue: `JobQueue` (`ObservableCollection<RenderJob>`, `:12293`); UI batch duyệt các job
  `WAITING`/`FAILED` tuần tự dưới `_localApiRenderLock` (bắt đầu ~`:16659`), API báo `409`/`429`
  nếu đang bận.

## 3. How the Application Works (data flow)

```
input (video/ảnh, audio1, audio2, audioFiles[]…)
  → pre-pass (chạy tuần tự trong ExecuteRenderAsync):
      1. concat audio   MergeAudiosToTempTrackAsync        :1463  (nếu mergeAudio)
      2. mix Audio1+2   MixAudiosToTempTrackAsync + amix   :1477  (stack)
      3. audio edit     segments/trim/IR reverb           :1507-1518
      4. caption        whisper-cli → .ass                 :9456+
      5. template/merge video (image timeline, transitions) :1626-1651
      6. AI upscale pre-pass (DLSS → Upscayl → FSRCNNX)    :1678
  → build FFmpeg command: filter_complex + encoder      :1786+ (postInputInsertPoint)
  → launch Process (:6294), đọc stderr `time=` làm progress (cap 99.5% :6281), cancel → Kill (:6300-6304),
    HW encoder fail → đúng 1 lần retry libx264/slow (:6327-6346), kiểm bitstream (:6352-6381)
  → finalize: thumbnail (:6392), onProgress(100) (:6439), dọn temp trong finally (:6466-6474; cache soundbar giữ lại)
  → output: UI → `<base>\Titan_Output\Titan_<source>.mp4` (:1445, :1448-1452, :19454); API → đúng đường dẫn client yêu cầu;
    artifact tạm ở %TEMP%\TitanEngineTemp
```

UI mode và host API chỉ khác nhau ở **cửa vào** (nút bấm / HTTP request → `RenderJob`), cùng đi
qua một `ExecuteRenderAsync`.

## 4. Render Pipeline (verified)

Thứ tự stage trong `filterChain` (mọi `filterChain.Append` trong `ExecuteRenderAsync`, theo line):

```
[1] CROP            BuildCropFilter                :2628
[2] SCALE/UPSCALE   upscale filter + scale         :2674, :2684, :2719   (external pre-pass đã chạy ở :1678)
[3] MOTION          minterpolate 60fps :2776 / tmix (RSMB) :2785
[4] FX              hflip :2795, glow :2814, halo :2820, prism :2826, bloom :2832,
                    polaroid base :2840, polaroid :2851
[5] SLOW MOTION     setpts                         :2859
[6] TRANSITION / POST-PROCESS recipe             :2875, :2894
[7] COLOR           color filter + intensity       :2927
[8] TEXT / CAPTION  typewriter :3003, subtitle/ass :3018
[9] BRIGHTNESS      eq=brightness                  :3026
[10] FADE           fade in/out                    :6130, :6162
[11] OVERLAY        graphParts + cmd.Insert(postInputInsertPoint, …) :3063-3547
[12] WATERMARK      BuildWatermarkFilterComplex    :3553, :3717, :3912  (LUÔN CUỐI)
[13] FORMAT         yuv420p → encoder
```

**Thứ tự này là bất biến** (AGENTS.md rule 5). Overlay là các input phụ được `Insert` vào vị trí
`postInputInsertPoint` (:1786) — offset **số**, nếu `Replace` đổi độ dài trước đó phải cộng delta
(:2759-2766). Watermark luôn cuối để không bị effect làm mờ; soundbar là overlay cuối trước watermark.
- **Ngoại lệ đã verify:** ở các nhánh có overlay/watermark/soundbar, video fade (`postVideoFilters`) được
  áp **sau watermark**, là filter cuối cùng trước `[vout]` (:8293-8296); fade chỉ nằm trong `filterChain`
  ở nhánh không overlay (:6127, :6162, :6170). Progress đọc từ `time=` của ffmpeg stderr, cap 99.5% khi
  encode, 100% sau finalize (:6281, :6439).

- **Encoder** (chọn ở ~:1856, log `[ENCODER]`/`[ENCODER-WARN]`/`[ENCODER-FALLBACK]`): kiểm tra
  `ffmpeg -encoders` — `h264_nvenc` / `hevc_nvenc` (target > 4096px) / `av1_nvenc` / `h264_amf` /
  `av1_amf` / `h264_qsv`; fallback CPU `libx264`. Luôn `-pix_fmt yuv420p`.
  `h264_nvenc` + scale động (template zoom) segfault trên build gyan 2026-01-22 → **giữ CPU retry**.
- **Thread cap** 3/4 số core cho `-threads`/`-filter_threads`/`-filter_complex_threads`.
- **Upscale 3 tầng** (pre-pass, trước filter chain; cập nhật SourceW/H):
  `dlss\DLVSR.exe`/`UpscalePipelineApp.exe` (DLSS NGX; 4x = 2 pass 2x) → `upscayl\Upscayl.exe`
  (`-s {2|4} -m realesrgan`) → nội bộ `libplacebo` + `shaders/FSRCNNX.glsl` (:12104-12176;
  lightweight 2x: libplacebo→unsharp→eq; heavy 4x: deblock→libplacebo→unsharp→eq→smartblur).
  External fail → fallback tiếp; internal fail (thiếu shader/libplacebo) = lỗi cấu hình, render fail.
- **Temp files:** mọi artifact tạm trong `GetTitanTempDir()` = `%TEMP%\TitanEngineTemp`
  (`:11635`) và được track trong `tempTemplateArtifacts`, xoá trong `finally` (bỏ qua lỗi xoá).
  **Không bao giờ ghi scratch/thumbnail cạnh output.**

## 5. Audio Architecture

- **Hai kiểu "nhiều audio" — đừng lẫn:**
  - **Stack** (chồng): `audioFile` (Audio 1) + `audioFile2` (Audio 2) → `MixAudiosToTempTrackAsync`
    → `amix` (:1477, graph ở :3570/:3627).
  - **Concat** (nối): `audioFiles[]` + `mergeAudio` → `MergeAudiosToTempTrackAsync` (:1463), concat demuxer.
- **Mix bắt buộc có `normalize=0`:** `amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95`
  (:3570, :3627). Mặc định `normalize=1` của FFmpeg chia đôi mỗi input — volume 200% thành 100%.
  Mix với silence (`anullsrc`) cũng cần `normalize=0` nhưng **không** limiter.
- Volume 0–200% → clamp 0.0–2.0 (`volume=0.0` có thể bị FFmpeg từ chối ở một số chain → clamp).
- Speed 0.10–8x qua chuỗi `atempo` (mỗi filter 0.5–2.0). Trim/segments qua `AudioEditSegment` + audio editor.
- **Duration master:** có Audio 1 + source là ảnh → audio quyết định; source là video →
  `min(video, audio1)`; không có Audio 1 → `min(video, audio đã mix)`.
  Ước tính độ dài: `GetEstimatedOutputDurationSeconds` (:2001), slow motion được tính vào.
- IR reverb pre-pass đọc `Assets\Effects\church_ir.wav` / `echo_ir.wav` cạnh exe (:11989-11996);
  thiếu file → log `[AUDIO1-FX-WARN]` và bỏ qua (không fail render).

## 6. Effects and Overlays

- Registry effect/preset: `EffectRecipeLibrary.cs` (có `RetiredRecipeNames` :9 và
  `TryResolveCanonicalEffectName` :383 — tên cũ được map về tên canonical); bridge sang filter graph
  ở `EffectRecipeRenderSupport.cs`.
- **Không suy luận loại overlay từ tên file.** Probe asset thật qua `OverlayAssetBackgroundAnalysis`:
  cache key = path + size + `LastWriteTimeUtc` (:65) trong `ConcurrentDictionary` (:53); phân loại
  `Alpha` (pix_fmt có alpha, :90-95) / `GreenScreen` (:110) / `BlackBackground` (:115) / `Unknown` (:120).
  Blend mode và keying phụ thuộc kết quả probe.
- **Asset-only, fail-fast:** Rain / Light Leak / Light Leak Burn thiếu asset thì throw lỗi cứng —
  ví dụ `LightLeakBurnPreset.cs` ném `"Light leak asset missing. Asset-only effect cannot render."`
  (:94, :177, :194, :331, :692). **Cấm thêm procedural fallback.**
- Các họ overlay: Snowfall (preset + asset), Rain, LightLeak, LightLeakBurn, DreamyDot(2),
  Scratch, Particle, Polaroid Scrapbook 2, RGB LED, Dashed frame + `OverlayContentLayout`.
- Thêm asset runtime mới phải thêm rule copy tương ứng trong `TitanEngine.csproj`.

## 7. Local HTTP API

- `HttpListener` tại prefix `http://127.0.0.1:5555/` (const :12352); đổi qua `PortInputWindow`
  (:16792; IP mặc định `localhost`, port mặc định `5555`). Start ~:16830 (task `RunLocalApiLoopAsync`);
  lỗi thường do thiếu urlacl → log gợi ý `netsh http add urlacl …`. Host OFF → `503`.
- **Endpoints** (const :12353-12359, route ở `HandleLocalApiRequestAsync` :16944):

  | Method | Path | Ý nghĩa |
  |---|---|---|
  | POST | `/api/render` | **mặc định trả ngay (async)** — blocking chỉ khi body/query có `waitForCompletion`/`wait`/`sync`/`blocking` (:17312-17330) |
  | POST | `/api/render/async` | bắt buộc async, trả ngay `jobId` |
  | POST | `/api/render/wait` | bắt buộc blocking, giữ connection tới khi xong |
  | GET  | `/api/render/status?jobId=…` | poll status/progress/metadata |
  | POST | `/api/queue/add` | thêm vào queue → `queueId`+`jobId` |
  | POST | `/api/queue/start` | chạy queue tuần tự (`RunApiQueueAsync` :19472-19573) |
  | GET  | `/api/soundbar/presets` | danh sách style/palette soundbar |

- ⚠ docs/30-API.md nói `/api/render` là blocking — **đã lệch**, code mặc định async (xem trên).
- **Không có endpoint `/api/cancel`** — huỷ chỉ qua nút STOP của UI (:16643) hoặc tắt host.
- Mọi render được serialize qua một `SemaphoreSlim(1,1)` `_localApiRenderLock` (:12348) — một ffmpeg một lúc.
- Status codes: 200 / 400 / 404 / 405 / 409 (UI batch đang chạy, :17276) / 429 (host bận) / 499 (cancel) / 503 (host OFF).
- Status string trong code: `queued` / `processing` / `completed` / `failed` / `idle`.
- JSON camelCase, `PropertyNameCaseInsensitive=true`; CORS `*`; `OPTIONS` preflight riêng.
- Webhook: nếu request có `webhookUrl`, khi job `done` POST status JSON 1 lần (:19171-19199); lỗi chỉ log `[API-WEBHOOK-ERROR]`, không fail job.
- **Hợp đồng đầy đủ: `TitanEngine/BAS_LOCALHOST_API_GUIDE.txt` là bản CANONICAL** (được csproj
  `CopyToOutputDirectory`). Bản ở repo root là **bản trùng một phần đã lệch** (tự khai báo ở banner
  đầu file; diff ~835 dòng so với bản canonical). Đổi field API phải sửa cả `MainWindow.Models.cs`
  và bản guide canonical.

## 8. Development Environment

**Windows (máy dev chuẩn):**

- Cần **.NET 8 SDK**; không cần NuGet (zero dependency).
- Build: `dotnet build TitanEngine\TitanEngine.csproj -c Release` (từ repo root).
- Chạy: `TitanEngine\bin\Release\net8.0-windows\TitanEngine.exe` — cần `ffmpeg.exe`/`ffprobe.exe`
  cạnh exe (`setup_titan.ps1` tải bản gyan nhưng script đang trỏ sai thư mục `TitanEngineV60\…` —
  chép tay vào `bin\…\net8.0-windows\` nếu cần).
- **Đóng app trước khi build Release** — ngược lại lỗi `MSB3026/MSB3027` (exe bị khoá), không phải lỗi code.
- `dotnet run` có thể treo → kill dotnet → build tay → `dotnet run --no-build`.
- Build phải **0 warning / 0 error** cả Debug lẫn Release trước khi commit.

**Linux (đã kiểm chứng trên host CachyOS này, 2026-11-05):**

- **Build được** nếu cài .NET 8 SDK (đã cài bản 8.0.427 vào `~/.dotnet` qua `dotnet-install.sh`):
  `dotnet build TitanEngine/TitanEngine.csproj -c Release -p:EnableWindowsTargeting=true`
  → **build succeeded, 0 warning / 0 error**, ra `bin/Release/net8.0-windows/TitanEngine.dll` (PE32)
  + apphost và copy đủ Content (shaders, Assets/Overlays, API guide…). `bin/`/`obj/` đã được gitignore.
- **Nhưng không chạy được trên Linux**: `dotnet TitanEngine.dll` báo thiếu framework
  `Microsoft.WindowsDesktop.App 8.0.0` — **framework này không có bản linux-x64** ("No frameworks
  were found"). WPF cần Win32/DirectX của Windows; **Wine cũng không hỗ trợ WPF**. Host có `/dev/kvm`
  nhưng không có QEMU/Windows image (và không có bản quyền) → không mô phỏng được ở đây.
  ⇒ Kết luận: trên Linux chỉ **build-check** được, **runtime test chỉ thực hiện được trên Windows**
  (máy thật hoặc VM Windows có bản quyền).
- FFmpeg/FFprobe n9.0.2 trên host này chạy tốt các filter mà app dùng — xem kết quả đo ở §9.

## 9. Build and Verification

Đã chạy thật trên host Linux này (2026-11-05):

- ✅ **FFmpeg smoke test**: `ffmpeg -f lavfi -i testsrc2=… -f lavfi -i sine=… -c:v libx264 -c:a aac smoke.mp4`
  → exit 0; `ffprobe` đọc được h264 320x240 + aac 44100 mono, duration 2.0s; decode
  `ffmpeg -v error -i smoke.mp4 -f null -` → exit 0, **0 dòng lỗi**. (Test ở thư mục tạm ngoài repo, đã dọn sạch.)
  Đây là kiểm chứng FFmpeg nói chung, **không phải** bằng chứng pipeline của TitanEngine chạy được.
- ❌ **Chạy app trên Linux**: không thể (xem §8 — WPF cần Windows, không có WindowsDesktop runtime cho Linux, Wine không hỗ trợ WPF).
- ✅ **Cross-build trên Linux**: `dotnet build … -p:EnableWindowsTargeting=true` → 0 warning / 0 error (đo ngày 2026-11-05, SDK 8.0.427).
- ✅ **Các filter mà pipeline dùng, chạy thật trên ffmpeg 9.0.2 của host này** (test ở thư mục tạm trong repo, đã dọn sạch):
  - `libplacebo=w=…:h=…:custom_shader_path='TitanEngine/shaders/FSRCNNX.glsl'` (upscale nội bộ, code ở `MainWindow.xaml.cs:12114`) → exit 0, 10 frame OK.
  - `amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95` vs `normalize=1` mặc định: đo bằng `volumedetect` → mean −17.6 dB / max −11.8 dB so với −24.1 dB / −15.4 dB (**chênh ~6.5 dB** — đúng tiên đề `normalize=1` chia đôi mỗi input).
  - `silencedetect=n=-48dB:d=1.0` (timeline gate r38) trên đoạn im lặng 2 s: `silence_start: 1`, `silence_end: 3.000023` → đúng hằng số.
  - `overlay=…:enable='between(t,1,3)'` (gate theo khoảng nghe được) → exit 0.
  - Mini end-to-end theo đúng thứ tự docs/10 (scale → overlay bằng asset thật `Assets/Overlays/Snow/snow_1.mp4` → drawtext → `libx264 -pix_fmt yuv420p`) → exit 0; ffprobe: h264 640x360 yuv420p, duration 3.0 s; decode `-f null -` 0 dòng lỗi.
  - Lưu ý: `snow_1.mp4` probe được `yuv420p` (không alpha) — đúng tinh thần "đừng suy luận loại overlay từ tên file", phải probe mới biết.
  - Các test trên chứng minh **recipe FFmpeg** chạy được trên Linux, **không phải** app WPF chạy được — app vẫn phải test runtime trên Windows.
- Không có unit test trong repo — kiểm chứng bằng build + render thật + decode lại output.

Chuẩn verify khi đổi code (theo AGENTS.md, đã khớp với code):

| Việc | Cách kiểm |
|---|---|
| Build | 0 warning, 0 error cả Debug + Release |
| Render | job thật, `exit code = 0` |
| Output | `ffprobe` đọc được; duration/frame khớp |
| Decode sạch | `ffmpeg -v error -i out.mp4 -f null -` → 0 dòng lỗi |
| Soundbar | đổi pixel ⇒ bump `RendererVersion` (`SoundBarVisualizerPreset.cs:270`) → cache bust |
| API | gọi thật, đọc `status`, `progress`, `finalOutput` |

**Đo, đừng nhìn suông** (ví dụ: loop reset bằng dBFS ở t=0 vs t=4.5s; vocal bằng `corr(Voice, stem)`;
gate bằng PSNR 2 frame + dBFS).

## 10. Engineering Rules (bắt buộc)

1. Inspect trước khi edit; trace call site của method trung tâm (`MainWindow.xaml.cs` 20.566 dòng —
   đừng đọc nguyên file, grep rồi đọc đoạn).
2. Không suy luận loại overlay từ tên file — probe asset thật (`OverlayAssetBackgroundAnalysis`).
3. Rain / Light Leak / Light Leak Burn là asset-only — thiếu asset thì fail nhanh, cấm fallback procedural.
4. Đổi pixel soundbar ⇒ bump `RendererVersion` (bẫy số 1, đã cắn 6+ lần: quên bump là cache PNG cũ).
5. Thứ tự filter chain bất biến (§4). Đặc biệt: color sau FX, watermark cuối, soundbar trước watermark.
6. `amix` luôn có `normalize=0`; mix thật thêm `alimiter=limit=0.95`.
7. `audioFile2` (stack) ≠ `audioFiles[]`+`mergeAudio` (concat).
8. Image-timeline duration sync chạy **trước** effect stack.
9. Không blur base video trừ fit-blur background; không làm mất source audio trừ khi tắt tường minh.
10. Artifact tạm chỉ ở `GetTitanTempDir()` (%TEMP%\TitanEngineTemp); không ghi cạnh output.
11. Build Release khi app đã đóng. Đổi field API ⇒ sửa `MainWindow.Models.cs` + bản guide canonical.
12. Thêm asset runtime ⇒ thêm rule copy trong `.csproj`. Tránh thêm dependency không cần thiết.
13. Thay đổi nhỏ nhất đúng; giữ behavior không liên quan; verify rồi mới nói xong; không bịa kết quả test.

## 11. Debugging Playbook

1. Đọc log thật trước. Tag thật trong code: `[FFMPEG-CMD]` (:6228), `[FFMPEG-FILTER-COMPLEX]` (:3581+),
   `[FFMPEG-ERROR]` (:6249), `[ERROR-CODE]` (:6311), `[FFMPEG-STDERR-DUMP]` (:6312), `[ENCODER-*]`,
   `[UPSCALE-*]`, `[DLSS-NGX]`, `[UPSCAYL]`, `[CAPTION-SYNC]`, `[SOUNDBAR]`, `[AUDIO-MERGE]/[AUDIO-OVERLAY]/[AUDIO-EDIT]`,
   `[TEMPLATE]/[TRANSITION]/[MERGE]`. ⚠ tag `[FFMPEG-ERROR-DETAILS]` trong AGENTS.md **không tồn tại trong code**.
2. Xác định stage: probe / timeline / upscale / filter / overlay / audio / encoder.
3. Cô lập: copy `[FFMPEG-CMD]` + `[FFMPEG-FILTER-COMPLEX]` chạy tay bằng ffmpeg — tách nhanh lỗi app
   khỏi lỗi filter graph; hoặc tắt từng nhánh overlay.
4. Exit code FFmpeg hay gặp (`RunFfmpegCaptureAsync` trả `(ExitCode, Stderr)`): 0 OK; -1 không start
   được process; -2 input không đọc được; -22 invalid argument (encoder/preset/filter sai — đọc stderr);
   126 permission; 127 command not found.
5. `enable` phải là parameter (`:enable=`), không nối bằng `,`. `-pix_fmt` sai → mất alpha hoặc chậm ~5 lần
   (`rgba`/`yuva420p` vs `yuv420p` cho overlay).
6. Sửa root cause, không che symptom; verify lại bằng đúng phép đo đã phát hiện bug.
   Hai vòng không tiến bộ → dừng đoán, đọc đủ code path hoặc đổi giả thuyết.

## 12. Git Workflow

- Repo hiện **1 commit duy nhất** (lịch sử đã squash): `ac8bd33` "Consolidate 48 stale docs into five
  current documents", branch `master`, tree sạch (trừ `PROJECT_SUMMARY.md` untracked — file tóm tắt
  của phiên agent trước, đừng commit trừ khi được yêu cầu).
- Trước khi sửa: `git status`, đọc diff. Commit nhỏ, tách theo vùng; message nêu **hành vi**,
  không nêu tên file. Không commit file tạm/build/log.
- **Report cho user trước khi commit. Chỉ push khi user bảo rõ ràng.** Không bao giờ
  `git reset --hard` / `git clean` / huỷ thay đổi của user.

## 13. Known Limitations & Technical Debt (có bằng chứng)

- Header comment `MainWindow.xaml.cs` ghi **V106** trong khi title bar là **V193** — comment lịch sử, đã lệch.
- `setup_titan.ps1` copy FFmpeg vào `TitanEngineV60\bin\Debug\net8.0-windows` — tên thư mục đã cũ
  (project thật là `TitanEngine\`); đường dẫn trong script không còn đúng.
- Bản `BAS_LOCALHOST_API_GUIDE.txt` ở **repo root là bản cũ, đã lệch** so với bản canonical trong
  `TitanEngine/` (tự ghi nhận ở banner; ~835 dòng diff).
- `AGENTS.md` nói `MainWindow.xaml.cs` "~18 200 dòng" — thực tế **20 566 dòng** (đã lớn hơn).
- `Assets/Effects/*.wav` (IR reverb) **không có rule copy trong `.csproj`** — ở runtime thiếu file thì
  code tự bỏ qua IR pre-pass với `[AUDIO1-FX-WARN]` (không crash), nhưng tính năng reverb sẽ im lặng
  nếu không chép tay vào output.
- `Modelfile`/`Modelfile2` (Ollama) **không được code nào tham chiếu** — sót lại, không phải dependency của app.
- Các file test (`test1.mp4`, `test2.mp4`, `test3.mp4`, `test_newline.mp4`, `test_thumb.mp4`,
  `thumb.jpg`, `temp_lightleak_contact.jpg`, `temp_models.txt`) nằm ngay trong `TitanEngine/` và
  **không được code nào tham chiếu** — rác đã commit.
- Không có test project nào. App **run chỉ được trên Windows**; trên Linux chỉ cross-build được (xem §8), các recipe FFmpeg thì chạy được trên cả ffmpeg của Linux (§9).
- `docs/30-API.md` nói `POST /api/render` là blocking — code mặc định **async** (:17312-17330); blocking qua `/api/render/wait` hoặc `waitForCompletion=true`.
- `docs/10-RENDER-PIPELINE.md` bẫy 3 nói amix normalize=0 "đã sửa **34** chỗ" — code hiện có **35** dòng `normalize=0` (34 site render graph kèm `alimiter=limit=0.95`, thêm 1 pre-mix ở :11508 dùng `duration=longest:dropout_transition=2` không limiter).
- Comment trong `AudioAnalysisModels.cs:7` và `SoundBarVisualizerPreset.cs:290` vẫn ghi "12 styles" nhưng enum + `BuildStyles` có **13** style (0-12) — tin code.
- `GET /api/render/status` không kiểm tra HTTP method ở code (:16979) — mọi verb đều trả status, guide nói GET.
- Bản guide ở root 997 dòng, bản canonical trong `TitanEngine/` 1196 dòng — chênh không chỉ là banner.
- `docs/00-OVERVIEW.md` liệt kê `Assets/Whisper/` (whisper.cpp + model + CUDA libs) là nội dung repo, nhưng thư mục này đang bị `.gitignore` (`**/Assets/Whisper/`) — chỉ có khi tự tải về.
- `AGENTS.md` rule 5 và docs/10 §2 không liệt kê stage SLOW MOTION và TRANSITION/POST-PROCESS trong chuỗi — code có đủ hai stage này (:2859, :2875, :2894).
- Boot gọi `StartLocalApiServer()` (:12556) nhưng log ghi "[API] Host mode OFF" — host có thực sự ON lúc boot phụ thuộc urlacl/quyền lúc runtime (chưa verify được ở runtime); nút HOST là cần gạt chính, và không gạt được khi batch đang chạy (:16776-16780).
- Chưa verify bằng code: chi tiết bên trong một số nhánh overlay/soundbar (xem log `[SOUNDBAR]`/từng preset khi debug).

## 14. Tool Usage Instructions (cho các phiên Claude Code sau)

- Dùng tool thật (Read/Glob/Grep/Bash/Edit/Write), đừng trả lời bằng suy đoán. Muốn khẳng định một
  điều trong code phải có tool call chứng minh; nếu tool lỗi thì báo FAIL thật, đừng nói đã làm.
- `MainWindow.xaml.cs` quá lớn để đọc một lần: **grep tìm symbol trước, rồi Read theo khoảng dòng**
  (~150-400 dòng), rồi grep trace caller. Ghi `file:line` vào mọi claim.
- Đọc đúng doc theo việc sắp làm (bảng ở đầu `AGENTS.md`): overview → `docs/00`, pipeline/audio/upscale
  → `docs/10`, soundbar → `docs/20`, API → `docs/30` + bản guide canonical trong `TitanEngine/`.
- Coi tài liệu là ngữ cảnh, không phải chân lý tuyệt đối: đối chiếu với code trước khi tin
  (ví dụ line count, version, đường dẫn trong §13). Nếu code và doc mâu thuẫn → tin code, và ghi rõ
  điều chưa verify thay vì bịa.
- Mọi lệnh build/run trong §8 là cho **Windows**; trên Linux chỉ đọc/soạn code được, đừng claim đã build.
