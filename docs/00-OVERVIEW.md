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
| Ngôn ngữ | C# / .NET 8.0-windows, Nullable + ImplicitUsings bật |
| UI | WPF (`WinExe`), không dùng NuGet — zero dependency |
| Render | FFmpeg + FFprobe CLI, đặt cạnh `.exe` |
| AI upscale | FSRCNNX (libplacebo), Upscayl (external), NVIDIA DLSS NGX (DLVSR) |
| Caption | `whisper-cli.exe` (whisper.cpp), model tải từ HuggingFace |
| API | `HttpListener`, JSON camelCase, port mặc định `5555` |
| Shader | GLSL trong `shaders/` (FSRCNNX, FSR, Anime4K) |

## 3. Bản đồ file

### Lõi

| File | Vai trò |
|---|---|
| `MainWindow.xaml.cs` | **Trung tâm, ~18 200 dòng.** `EngineCore` (static) + `MainWindow`: render pipeline, FFmpeg command builder, duration sync, audio mix, queue, HTTP API, overlay wiring |
| `MainWindow.xaml` | Toàn bộ layout UI |
| `MainWindow.Models.cs` | `RenderJob`, `LocalhostRenderRequest/Response`, `LocalhostQueueJobInfo`, `ApiRenderJobState` |
| `App.xaml(.cs)` | Entry point, rất mỏng |

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
# từ repo root
dotnet build TitanEngine\TitanEngine.csproj -c Release
TitanEngine\bin\Release\net8.0-windows\TitanEngine.exe
```

- **Đóng app trước khi build** — nếu không sẽ gặp `MSB3026/MSB3027` (file exe bị khoá).
- Output UI mode mặc định: `Titan_Output/`.
- Không có unit test; kiểm chứng bằng build + render thật + decode lại output.

## 10. Trạng thái phiên bản

- Title bar: **“Titan Engine V193 UI+Soundbar Update”**.
- Header comment trong `MainWindow.xaml.cs` vẫn ghi **V106** — đã lệch, chỉ là comment lịch sử,
  đừng dùng làm nguồn sự thật.
- Soundbar renderer: **`soundbar-r38`**.
- Nhánh `master`; tại thời điểm viết tài liệu này, `r36`–`r38` đã commit nhưng **chưa push**.
