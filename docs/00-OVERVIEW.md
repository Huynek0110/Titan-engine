# 00 — Tổng quan TitanEngine

> Tài liệu định hướng: app là gì, code nằm ở đâu, sửa gì ở đâu, build/run thế nào.
> Các phần sâu hơn nằm ở [`10-RENDER-PIPELINE.md`](10-RENDER-PIPELINE.md),
> [`20-SOUNDBAR.md`](20-SOUNDBAR.md), [`30-API.md`](30-API.md).

## 1. App làm gì

TitanEngine nhận **video hoặc ảnh**, chạy qua chuỗi xử lý bằng FFmpeg, và xuất **MP4**:

```
source → (merge / template / timeline) → AI upscale → scale/crop → FX → color
       → text/caption → overlay (tuyết, mưa, light leak, polaroid, soundbar…)
       → watermark/logo → encode (NVENC / AMF / QSV / libx264)
```

Hai chế độ hoạt động:

1. **UI mode** — chọn file, chỉnh thông số, đưa job vào queue, render hàng loạt.
2. **Host mode (API)** — HTTP server nội bộ tại `http://127.0.0.1:5555/` để BAS/automation điều khiển.
   Bật bằng nút **HOST**; khi tắt, API trả `503 Host mode is OFF`.

## 2. Tech stack

| Thành phần | Giá trị |
|---|---|
| Ngôn ngữ | C# / .NET 8, Nullable + ImplicitUsings bật |
| UI Windows | WPF (`net8.0-windows`) — bản gốc, giữ nguyên |
| UI cross-platform | Avalonia 11.3.2 (`TitanEngine.Avalonia`, pin version, compiled bindings, CommunityToolkit.Mvvm 8.4.0, SkiaSharp 2.88.9) |
| CLI headless | `TitanEngine.Cli` (net8.0): `doctor` / `render` / `host` / `test-escape` |
| Render | FFmpeg + FFprobe CLI (Windows: cạnh `.exe`; Linux: cạnh app → PATH) |
| AI upscale | FSRCNNX (libplacebo), Upscayl (external), NVIDIA DLSS NGX (DLVSR — Windows-only) |
| Caption | `whisper-cli` (whisper.cpp), model tải từ HuggingFace |
| API | `HttpListener` trong `TitanEngine.Core/LocalHostServer.cs` (dùng chung WPF/CLI/Avalonia), JSON camelCase, port mặc định `5555` |
| Shader | GLSL trong `shaders/` (FSRCNNX, FSR, Anime4K) |

## 3. Bản đồ file

### Phân tầng (từ Giai đoạn 1 port/linux)

**`TitanEngine.Core/` (net8.0 thuần — KHÔNG tham chiếu WPF/UI):** engine, render pipeline, FFmpeg command
builder, audio mix, HTTP API. Gồm: `EngineCore.{State,Initializer,FfmpegCommandBuilder,AudioMixer,RenderPipeline,
HostApiServer,Helpers}.cs` (partial class `EngineCore`), `MainWindow.Models.cs`, `EffectRecipe*`, `TitanTransition*`,
`Audio*`, `OverlayAssetBackgroundAnalysis.cs`, `OverlayContentLayout.cs`, `VideoAssetProbeInfo.cs`, và nửa Core của
các preset overlay/soundbar (model + settings + BuildSingleClipGraph/BuildAssetClipGraph… — logic filter graph).
`EngineServices.cs` định nghĩa các interface `IUserPrompt / IUiDispatcher / IEngineLogSink / ITextMetrics /
IImageServices` + service locator `EngineServices` — **Core gọi UI/ảnh vẽ CHỈ qua đây**, không gọi ngược project WPF.
`TitanEngineService.cs` = facade `ITitanEngineService` (phase 2): host không phải WPF (CLI/Avalonia) dùng để
thêm/xoá/chạy/huỷ job, đọc trạng thái engine, nhận progress (`IProgress<RenderProgress>`) và log stream —
bọc nguyên API tĩnh `EngineCore`, không đổi logic engine.
`PixelRect.cs` = struct `PixelRect`/`Vector` (double, semantics giống WPF; `Vector.Normalize` chia 1/max(|x|,|y|)
trước rồi chia Length — số học phải khớp bản Windows vì seed/particle/cache SHA-256 phụ thuộc).

**`TitanEngine/` (WPF, net8.0-windows):** UI + phần vẽ pixel (WPF-stay). `MainWindow.xaml(.cs)` (UI, queue),
`WpfOverlayArt.cs` (9 class `*Art` — member vẽ chuyển nguyên văn từ Core: Snowfall/DreamyDot/Particle/Polaroid2/
RgbPolaroid/Scratch/LightLeakBurn/SoundBar + DashedPolaroid), `SoundBarStyleRenderer.cs` (nguyên file, từ Core),
`SoundBarPaletteArt.cs` (TryParseHex + 4 hàm màu), `WpfImageCodecArt.cs` (decode ảnh/sanitize logo),
`WpfEngineServices.cs` (implement các interface trên bằng MessageBox/OpenFileDialog/FormattedText/RenderTargetBitmap;
`Install()` chạy đầu `MainWindow()`). Core sinh overlay pixel qua `EngineServices.Images.*` → `*Art` classes.

**`TitanEngine.Cli/` (net8.0):** host headless — `doctor` (tool/encoder/font/libplacebo thật), `render job.json`
(JSON theo `LocalhostRenderRequest`), `host --port` (bọc `LocalHostServer`), `test-escape` (24 case ffmpeg thật).
`CliServices.Install()` cài `IUserPrompt` headless + heuristic `ITextMetrics`; không cài `IImageServices`
→ job xin overlay sinh pixel sẽ fail với thông báo rõ (không NRE).

**`TitanEngine.Avalonia/` (net8.0, Avalonia 11.3):** UI cross-platform thay WPF, cùng `TitanEngine.Core`.
13 khu `UserControl` + ViewModel (mirror 12 khu WPF + QueueLogs), `JobBuilder.cs` (dựng `RenderJob` từ VM —
mirror `BtnAddJob_Click`), `UiLogic.cs` (helper), `Services/`: `AvaloniaImageServices` (decode/probe thật bằng
SkiaSharp; 8 capability vẽ WPF-stay throw `NotSupportedException` với thông điệp rõ), `AvaloniaTextMetrics`
(đo chữ bằng TextLayout), `FfplayAudioPreview` (ffplay subprocess thay MediaPlayer), `UserPrompt` (StorageProvider),
`LocalApiHost` (HOST: OFF/ON bọc `LocalHostServer`). Verify harness: `--screenshot <dir>` (chụp 13 khu PNG)
và `--e2e <dir>` (thêm job → render → ffprobe → host API — chạy đúng command của UI).

### Lõi (WPF project)

| File | Vai trò |
|---|---|
| `MainWindow.xaml.cs` | UI + queue + wiring; `WpfEngineServices.Install()` chạy đầu constructor |
| `MainWindow.xaml` | Toàn bộ layout UI |
| `App.xaml(.cs)` | Entry point, rất mỏng |

(`MainWindow.Models.cs`, `EngineCore` và các model/preset không dính vẽ đã chuyển sang `TitanEngine.Core/`.)

### Effect / overlay / template

| File | Vai trò |
|---|---|
| `EffectRecipeLibrary.cs` | Registry canonical của effect/preset; có `RetiredRecipeNames` |
| `EffectRecipeModels.cs` | Enum + model recipe (overlay, particle, post-process, transition, template stack) |
| `EffectRecipeRenderSupport.cs` | Bridge recipe → filter graph FFmpeg |
| `OverlayAssetBackgroundAnalysis.cs` | Probe asset thật, phân loại `Alpha` / `GreenScreen` / `BlackBackground` / `Unknown` |
| `OverlayContentLayout.cs` | Bố cục canvas cho overlay/border |
| `VideoAssetProbeInfo.cs` | Kết quả ffprobe cho overlay asset |
| `TitanTransitionLibrary.cs` / `TitanTransitionGraphBuilder.cs` | Map + dựng xfade/transition |

### Overlay families

`SnowfallOverlayPreset`, `RainOverlayPreset`, `LightLeakOverlayPreset`, `LightLeakBurnPreset`,
`DreamyDotOverlayPreset`, `DreamyDotOverlay2Preset`, `ParticleOverlayGenerator`, `ScratchVideoPreset`,
`PolaroidScrapbook2Preset`, `RgbPolaroidLedPreset`, `DashedPolaroidFramePreset`.

> Rain và Light Leak/Burn là **asset-only**. Riêng Light Leak Burn cấm procedural fallback — thiếu asset
> phải throw lỗi cứng.

### Soundbar

| File | Vai trò |
|---|---|
| `AudioSpectrumAnalyzer.cs` | PCM → FFT → bands + waveform + `Voice` + onset + timeline audibility |
| `AudioAnalysisModels.cs` | `SoundBarStyleId` (13 style), `SoundBarAudioSource`, `AudioFrameData` |
| `SoundBarStyleRenderer.cs` | Hình học 13 style + helper dùng chung (peak cap, beat envelope, bloom, reflection…) |
| `SoundBarVisualizerPreset.cs` | Sinh PNG loop, cache SHA-256, dựng graph composite. **`RendererVersion` ở đây** |
| `SoundBarPalette.cs` | 30 palette NCS |

### Audio

`AudioEditorWindow.xaml(.cs)` — preview timeline, play/stop/jump, Single Trim + Segment Cuts,
normalize/remove, save/cancel. `AudioEditModels.cs` — `AudioEditSegment` + parse/format thời gian.

### Asset runtime

| Folder | Nội dung |
|---|---|
| `Assets/Overlays/{Snow,Rain,LightLeaks,DreamyDots}` | asset overlay |
| `Content/Overlays/{Snow,LightLeaks}` | fallback bundled |
| `Content/Branding/` | `video-logo.png` |
| `Assets/Fonts/` | `greatvibes.ttf`, `utm_thuphap.ttf` |
| `Assets/Whisper/` | whisper.cpp + model + CUDA libs |
| `shaders/` | FSRCNNX / FSR / Anime4K GLSL |

`TitanEngine.csproj` copy các folder trên vào output. **Thêm asset mới phải thêm rule copy tương ứng.**

## 4. Khởi động (`EngineCore.Initialize`)

- Resolve `ffmpeg.exe`, `ffprobe.exe`, `upscayl/Upscayl.exe`, `dlss/DLVSR.exe` từ thư mục app.
- Thiếu FFmpeg → hỏi user chọn tay.
- Dò GPU encoder bằng `ffmpeg -encoders`: `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`, `av1_amf`, `h264_amf`, `h264_qsv`.
- Dò DLSS backend: `DLVSR.exe` hoặc `UpscalePipelineApp.exe`.
- Có thể auto-download source VFX SDK Samples, auto-cài CMake qua `winget`, auto-build backend
  (chi tiết ở [`10-RENDER-PIPELINE.md`](10-RENDER-PIPELINE.md) §7).

## 5. UI map

```
SOURCE & TRIM        chọn nguồn, merge nhiều video, trim
FX STUDIO            effect + intensity + timeline % + fade
SLOW MOTION          0.10x–8x, xử lý audio
AI UPSCALE ENGINE    Off / 2x / 4x, sharpness
MOTION EDITOR        template zoom/pan/spin
TEXT & VIBE          text, glow, style, soundbar
CAPTION SYNC         Whisper: ngôn ngữ, font, vị trí, model
AUDIO MIXER          volume video/audio1/audio2, tốc độ, audio editor
WATERMARK            scale/rotate/opacity, kéo-thả trên preview
COLOR GRADING        filter + intensity + brightness
EXPORT               hardware, resolution, bitrate, fps, output
QUEUE + LOGS         job queue, tiến trình, log FFmpeg
```

## 6. Tính năng chính

- **Source**: video (mp4/mov/avi/mkv/webm/m4v/wmv/mpg/mpeg/ts/m2ts/mts/3gp/flv/f4v/vob/ogv),
  ảnh (png/jpg/jpeg/jpe/jfif/bmp/webp/tif/tiff/gif); kéo-thả được.
- **Timeline**: merge video (concat hoặc transition), image timeline, template stack.
- **FX**: flip, light leak, light leak burn, cinematic/halo/prism/editorial bloom, crop,
  post-process recipe (VHS, glitch, CRT, grain…), transition recipe.
- **Border**: Polaroid scrapbook 1/2, RGB LED, dashed frame.
- **Overlay**: tuyết (2 toggle + preset), mưa (3 preset + intensity), dreamy dots, scratch, particle, intro.
- **Upscale**: DLSS NGX → Upscayl → FSRCNNX nội bộ; 2x lightweight, 4x heavy.
- **Text**: font, màu, glow, quotes, scroll, soundbar.
- **Caption**: Whisper tiny→large-v3-turbo, auto-download, ASS burn-in.
- **Audio**: Audio 1 + Audio 2 (stack) khác `audioFiles` + `mergeAudio` (concat); volume 0–200%;
  speed 0.1–8x; audio editor; loudnorm.
- **Watermark / brand logo**: scale/rotation/opacity/vị trí, debug kéo-thả.
- **Color / crop / export / queue** như UI map trên.

## 7. Bất biến (đừng vi phạm)

1. Không suy luận loại overlay từ tên file — phải probe asset thật.
2. Rain / Light Leak / Light Leak Burn: asset-only, fail nhanh.
3. Không blur base video trừ khi đó là fit-blur background.
4. Không làm mất source audio trừ khi bị tắt tường minh.
5. `audioFile2` (stack) ≠ `audioFiles` + `mergeAudio` (concat).
6. Image-timeline duration sync phải chạy **trước** effect stack.
7. Temp overlay pre-render phải tách khỏi output cuối.
8. `BAS_LOCALHOST_API_GUIDE.txt` phải đồng bộ với request fields thật.
9. Copy rules trong `.csproj` phải khớp asset runtime.
10. `amix` phải có `normalize=0`; mix thật thêm `alimiter=limit=0.95`.

## 8. Sửa gì ở đâu

| Muốn đổi | Sửa |
|---|---|
| Thêm/đổi preset effect | `EffectRecipeLibrary.cs` trước |
| Filter graph của effect | `EffectRecipeRenderSupport.cs` |
| Logic probe asset overlay | `OverlayAssetBackgroundAnalysis.cs` |
| Field API | `MainWindow.Models.cs` + `BAS_LOCALHOST_API_GUIDE.txt` |
| Thêm asset runtime | rule copy trong `TitanEngine.csproj` |
| Hình học soundbar | `SoundBarStyleRenderer.cs` |
| Sinh PNG / cache / composite soundbar | `SoundBarVisualizerPreset.cs` |
| FFT / bands / voice / gate | `AudioSpectrumAnalyzer.cs` |
| Pipeline render / encoder / audio mix | `MainWindow.xaml.cs` (`EngineCore`) |
| UI | `MainWindow.xaml` + handler trong `MainWindow.xaml.cs` |

## 9. Build & chạy

```powershell
# Windows / từ repo root
dotnet build TitanEngine\TitanEngine.csproj -c Release
TitanEngine\bin\Release\net8.0-windows\TitanEngine.exe
```

```bash
# Linux / CachyOS (dotnet SDK ở ~/.dotnet)
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build TitanEngine.Core/TitanEngine.Core.csproj -c Release
dotnet build TitanEngine.Cli/TitanEngine.Cli.csproj -c Release
dotnet build TitanEngine.Avalonia/TitanEngine.Avalonia.csproj -c Release   # UI chạy được trên Linux
dotnet build TitanEngine/TitanEngine.csproj -c Release -p:EnableWindowsTargeting=true  # WPF: chỉ build, không chạy

dotnet publish TitanEngine.Avalonia/TitanEngine.Avalonia.csproj -c Release -r linux-x64 --self-contained true
# WPF phải build được cả win-x64 publish (chỉ verify được trên Windows thật)
```

- **Đóng app trước khi build** — nếu không sẽ gặp `MSB3026/MSB3027` (file exe bị khoá).
- Build Avalonia "0/0 giả": build incremental từng bỏ qua XAML compile — nghi ngờ thì xoá `obj`/`bin`
  rồi `--no-incremental`; "xong" chỉ khi ảnh chụp harness chạy được (không còn "No precompiled XAML").
- ffmpeg/ffplay: Windows cạnh exe; Linux pacman (`ffmpeg` gói ffplay kèm) — `setup_titan.sh` cài đủ.
- Output UI mode mặc định: `Titan_Output/`.
- Không có unit test; kiểm chứng bằng build + render thật + decode lại output
  (`ffmpeg -v error -i out.mp4 -f null -` → 0 dòng lỗi).

## 10. Trạng thái phiên bản

- Title bar: **“Titan Engine V193 UI+Soundbar Update”** (WPF và Avalonia cùng tiêu đề).
- Header comment trong `MainWindow.xaml.cs` vẫn ghi **V106** — đã lệch, chỉ là comment lịch sử,
  đừng dùng làm nguồn sự thật.
- Soundbar renderer: **`soundbar-r38`**.
- Nhánh: `master` (WPF gốc) và `port/linux` (port cross-platform). Port đang chạy Giai đoạn 5
  (Avalonia); tiến độ chi tiết trong `PORT-PROGRESS.md` (không commit).
