# TITAN ENGINE — Tổng Hợp Toàn Bộ Ứng Dụng

> File tổng hợp logic + tính năng của app, viết để mở lên xem cho dễ hiểu.
> Version hiện tại: **v106.0-CAS-UPSCALE**

---

## 1. TỔNG QUAN

**Titan Engine** là một **video editor / render engine** (WPF trên Windows) dùng **FFmpeg CLI** làm nền tảng render. App nhận video hoặc ảnh, chạy qua chuỗi xử lý (AI upscale → scale → CAS → FX → overlay → color → watermark) và xuất ra file MP4.

**Nguồn đầu vào hỗ trợ:**
- Video: `.mp4 .mov .avi .mkv .webm .m4v .wmv .mpg .mpeg .ts .m2ts .mts .3gp .flv .f4v .vob .ogv`
- Ảnh: `.png .jpg .jpeg .jpe .jfif .bmp .webp .tif .tiff .gif`
- (Có thể kéo-thả file vào cửa sổ app - `AllowDrop`)

**App hoạt động ở 2 chế độ:**
1. **UI Mode** — render bằng tay qua giao diện
2. **Host Mode (API)** — local HTTP API trên cổng `5555` để BAS/automation gọi

---

## 2. TECH STACK

| Thành phần | Giá trị |
|---|---|
| Ngôn ngữ | C# (.NET 8.0-windows, Nullable + ImplicitUsings bật) |
| UI | WPF (WinExe) |
| Render engine | FFmpeg + FFprobe (yêu cầu `ffmpeg.exe`/`ffprobe.exe` cạnh exe) |
| AI upscale | FSRCNNX (shader GLSL qua libplacebo), Upscayl, NVIDIA DLSS (DLVSR/NGX VFX SDK) |
| Whisper AI | `whisper-cli.exe` (whisper.cpp) cho Caption Sync |
| API | HttpListener, `http://127.0.0.1:5555/` |

---

## 3. CẤU TRÚC PROJECT (FILE MAP)

### Engine chính
| File | Vai trò |
|---|---|
| `MainWindow.xaml.cs` (16.6k dòng) | **Trung tâm của app**: `EngineCore` (static) + `MainWindow` (UI, queue, API, FFmpeg command builder, duration sync, audio mix, cleanup) |
| `MainWindow.xaml` | Toàn bộ layout UI |
| `MainWindow.Models.cs` | Data models: `RenderJob`, `LocalhostRenderRequest`, `LocalhostRenderResponse`, `LocalhostQueueJobInfo`, `ApiRenderJobState` |
| `App.xaml(.cs)` | Startup shell, rất tối giản |

### Effect / Overlay / Preset
| File | Vai trò |
|---|---|
| `EffectRecipeLibrary.cs` | Registry chính của tất cả effect/preset (canonical source of truth) |
| `EffectRecipeModels.cs` | Models: enums (`OverlayAssetType`, `OverlayBlendMode`, `ParticleType`, ...), `EffectRecipe`, `OverlayEffectRecipe`, `ParticleEffectRecipe`, `PostProcessEffectRecipe`, `TransitionRecipe`, `TemplateEffectStackRecipe` |
| `EffectRecipeRenderSupport.cs` | FFmpeg graph builders: `EffectRecipeBridge`, `PostProcessEffectGraphBuilder`, `OverlayEffectGraphBuilder`, `TransitionRecipeGraphBuilder` |
| `OverlayAssetBackgroundAnalysis.cs` | Probe asset thật, phân loại nền: `Alpha` / `GreenScreen` / `BlackBackground` / `Unknown` (cache theo path + size + mtime) |
| `OverlayContentLayout.cs` | Helper bố cục canvas cho overlay/border |
| `VideoAssetProbeInfo.cs` | Model kết quả ffprobe cho overlay asset |

### Overlay families (họ overlay)
| File | Loại |
|---|---|
| `SnowfallOverlayPreset.cs` | **Tuyết** — có asset sẵn (`snow_1`, `snow_2`) + fallback hạt vẽ bằng code |
| `RainOverlayPreset.cs` | **Mưa** — asset-only, tự phát hiện nền asset (black/alpha/green) |
| `LightLeakOverlayPreset.cs` | **Light leak / film burn** — asset-based, preset chuẩn `FilmBurnWarm` |
| `LightLeakBurnPreset.cs` | **Light Leak Burn transition** (ghép 2 clip) — asset-only, cấm procedural fallback |
| `DreamyDotOverlayPreset.cs` | **Dreamy dots** — hạt vẽ bằng WPF (chaos variant) |
| `DreamyDotOverlay2Preset.cs` | **Dreamy dot overlay 2** — asset-based (`dreamy_dot_overlay_02.mp4`) |
| `ParticleOverlayGenerator.cs` | Generator hạt tổng quát (snow/rain/sparkle/confetti), cache PNG sequence |
| `ScratchVideoPreset.cs` | Overlay scratch/dust (bụi phim) |
| `PolaroidScrapbook2Preset.cs` | Viền ảnh scrapbook |
| `RgbPolaroidLedPreset.cs` | Viền RGB LED chuyển động |
| `DashedPolaroidFramePreset.cs` | Khung viền dashed |

### Transition
| File | Vai trò |
|---|---|
| `TitanTransitionLibrary.cs` | Map loại transition → tên xfade FFmpeg |
| `TitanTransitionGraphBuilder.cs` | Build filter graph xfade giữa các clip |
| `TitanTransitionModels.cs` | Model `TitanTransitionSettings` |

### Audio
| File | Vai trò |
|---|---|
| `AudioEditorWindow.xaml(.cs)` | Cửa sổ editor audio riêng: preview timeline, play/stop/jump, Single Trim + Segment Cuts, normalize/remove/clear, save/cancel |
| `AudioEditModels.cs` | Model `AudioEditSegment` + `AudioEditHelpers` (parse thời gian linh hoạt, format, normalize segments, detect full-track) |

### Khác
| File | Vai trò |
|---|---|
| `PortInputWindow.xaml(.cs)` | Cửa sổ nhập port cho API host (default 5555) |
| `BAS_LOCALHOST_API_GUIDE.txt` | Guide API cho BAS (được copy vào release) |
| `TITAN_ENGINE_AGENT_HANDOFF.md` | Handoff doc cũ (tiếng Anh) cho agent |
| `shaders/` | GLSL shader: `FSRCNNX.glsl`, `FSR.glsl`, `Anime4K_*.glsl` (dùng libplacebo) |
| `Assets/Fonts/` | `greatvibes.ttf`, `utm_thuphap.ttf` (text overlay) |
| `Assets/Whisper/` | whisper.cpp binaries + `ggml-small.bin` model (CUDA bản 11/12) |

---

## 4. KIẾN TRÚC RUNTIME

### Khởi động (`EngineCore.Initialize()`)
- Resolve từ thư mục app: `ffmpeg.exe`, `ffprobe.exe`, `upscayl/Upscayl.exe`, `dlss/DLVSR.exe`
- Nếu thiếu ffmpeg → hỏi người dùng chọn thủ công
- Kiểm tra GPU encoder: `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`, `av1_amf` (chạy `ffmpeg -encoders`)
- Kiểm tra DLSS/NGX backend: tìm `DLVSR.exe` hoặc `UpscalePipelineApp.exe` (từ NVIDIA VFX SDK Samples)
- **Tự động setup DLSS**: tải bundle source từ GitHub NVIDIA-Maxine/VFX-SDK-Samples, tạo `DLVSR_SETUP_README.txt` + `NVIDIA_VFX_AUTO_SETUP.ps1`, tự cài CMake qua winget nếu thiếu, auto-build backend nếu có VFX SDK + feature `nvvfxupscale`

### Luồng render chính (`EngineCore.ExecuteRenderAsync`)
1. Merge audio paths (nếu nhiều track) → audio tạm
2. Mix Audio 1 + Audio 2 (nếu cả 2) → audio tạm
3. Áp audio edit segments (nếu có)
4. **Caption Sync** (nếu bật): Whisper AI transcribe audio 1 → ASS caption
5. Template / image timeline: build source video tạm (`BuildTemplateTimelineSourceAsync`)
6. Merge videos (nếu bật): concat đơn giản hoặc transition (Light Leak Burn / xfade)
7. **AI upscale pre-pass** (nếu chọn mode upscale): DLSS NGX → fallback Upscayl → fallback nội bộ
8. Build FFmpeg command: trim → input → encoder → bitrate → filter chain → output
9. Render vào `Titan_Output/` (UI mode) hoặc output chỉ định (API mode)

### Filter chain thứ tự bắt buộc
```
[Scale] → [CAS unsharp] → [FX] → [Color] → [Watermark]
```

### Encoder selection
- **Auto/CPU**: `libx264` preset `veryslow`, CRF 16
- **NVIDIA**: `h264_nvenc` (medium) / `hevc_nvenc` (nếu 8K+) / `av1_nvenc` (p6) — nếu không có nvenc → fallback `libx264 slow`
- **AMD**: `h264_amf` (quality) / `av1_amf`
- **Intel**: `h264_qsv`
- Pix format output: `yuv420p`

### Bitrate strategy
- `Match Source`: tự nâng bitrate lên mức tối thiểu theo độ phân giải (`GetMinRecommendedBitrate`)
- `Boost`: ×1.5
- `Custom`: người dùng nhập (kbps)
- NVENC dùng VBR quality level 16

### Duration master logic
- Có **Audio 1** + source là ẢNH → Audio 1 là "vua" thời lượng (cắt image timeline theo audio)
- Có Audio 1 + source là VIDEO → `min(video, audio1)`
- Không có Audio 1 → `min(video, audio đã mix)`

---

## 5. UI MAP (CÁC KHU VỰC TRONG APP)

```
┌─────────────────────────────────────────────┐
│ SOURCE & TRIM      (chọn nguồn, trim video) │
│ FX STUDIO          (FX effect, intensity,   │
│                      timeline %, fade)      │
│ SLOW MOTION        (tốc độ 0.10x–8x, audio) │
│ AI UPSCALE ENGINE  (FSRCNNX 2x/4x, sharp)  │
│ MOTION EDITOR      (template zoom/pan/spin) │
│ TEXT OVERLAY & VIBE (text, glow, style,     │
│                      audio spectrum)        │
│ CAPTION SYNC (AI Whisper) (ngôn ngữ, vị trí,│
│                      font, model size)      │
│ AUDIO MIXER        (vol video/audio1/audio2,│
│                      tốc độ, audio editor)  │
│ WATERMARK          (scale/rotate/opacity,   │
│                      kéo-thả debug)         │
│ COLOR GRADING      (color filter, intensity,│
│                      brightness)            │
│ EXPORT             (hardware, resolution,   │
│                      bitrate, fps, output)  │
│ QUEUE + LOGS       (job queue, status, log) │
└─────────────────────────────────────────────┘
```

---

## 6. DANH SÁCH TÍNH NĂNG CHI TIẾT

### 6.1 Source & Trim
- Chọn video/ảnh đơn hoặc **merge nhiều video** (`MergeVideos`)
- Trim video theo `-ss` / `-t`
- Image timeline: ảnh thành video theo `ImageTimelineDurationSeconds`

### 6.2 FX Studio (`cmbFxType`)
Các effect hiển thị trong UI:
- `None`
- `Flip Horizontal (Anti-Copyright)`
- `Rò phim (Leak 1 / Light Leak)`
- `Light Leak Burn`
- `Cinematic Glow`, `Halo Glow`, `Prism Light`, `Editorial Bloom`
- `Crop Video (Custom Frame)`

Kèm: `FxIntensity` (0-100), `FxStartPercent`/`FxEndPercent` (timeline), `EnableFade` (fade in/out).

### 6.3 Motion Editor / Template (`cmbTemplateStyle`)
- `Smooth Zoom In/Out`, `Pan Left/Right`, `Velocity Punch`, `Cinematic Fade Zoom`, `Glitch Distort`, `3D Spin Lite`, `Trend Zoom Flash`, `Trend Blur Pulse`, `Trend Spin Glitch`
- Template stack: `Digicam Memory`, `Beat Photo Dump`, `Film Strip`, `Magazine Cover`, `Before/After`

### 6.4 Border (`cmbBorderType`)
- `None`
- `Polaroid Scrapbook` / `Polaroid Scrapbook 2`
- `RGB Polaroid Border` (LED animation)
- `Dashed Polaroid Frame`

(Các border này là overlay vẽ bằng WPF → render PNG sequence → loop trong FFmpeg)

### 6.5 Overlay toggles
- `Overlay 1` / `Overlay 2` → asset tuyết `snow_1.mp4` / `snow_2.mp4`, mỗi cái có opacity riêng
- `Snowfall Preset`: Snow Cinematic (fallback asset)
- `Rain Preset`: Heavy Rain / Cinematic Rain / Window Drops + Intensity (subtle/medium/strong)
- `Overlay Intro`

### 6.6 AI Upscale Engine
- Mode: Off / Off (Original Size) / 2x / 4x
- Upscale pipeline:
  1. **NVIDIA DLSS** (NGX DLVSR / UpscalePipelineApp) nếu hardware NVIDIA
  2. **Upscayl** (external)
  3. **FSRCNNX** shader qua libplacebo (nội bộ) + CAS (Contrast Adaptive Sharpening) unsharp
- `SharpnessIntensity` slider
- Không làm mất bitrate: hardcode ~25Mbps khi upscale

### 6.7 Text Overlay
- Text, vị trí, align, font size, màu, style (mặc định `Aesthetic Lyric (Georgia Italic)`)
- `EnableTextGlow` + màu glow
- `EnableAudioSpectrum` (hiệu ứng theo nhạc), `AddQuotes`, `DisableTextScroll`
- Fonts đi kèm: `greatvibes.ttf`, `utm_thuphap.ttf`

### 6.8 Caption Sync (AI Whisper)
- Chạy `whisper-cli.exe` lên audio 1 (pre-pass trước render)
- Bước: extract audio → WAV 16kHz mono (kèm EQ filter vocal) → transcribe → SRT → ASS
- Model: Small (mặc định) / Medium / Large-v3-Turbo — **tự tải từ HuggingFace** nếu thiếu
- Ngôn ngữ: auto / vi / en / ja / ko / zh
- Tùy chỉnh: vị trí, X/Y %, font size, màu, animation (None / Spotify Scroll), font family

### 6.9 Audio
- **Audio 1** (nhạc nền chính) + **Audio 2** (overlay thêm)
- Nếu có cả 2 → mix trước khi vào pipeline (`amix`, `aresample`, `aformat`, `alimiter`)
- Nếu chỉ có Audio 2 → dùng làm external audio
- **MergeAudio**: nối nhiều track audio (concat) — khác với stacking Audio 2
- Volume riêng: VideoVolume / AudioVolume / SecondaryAudioVolume
- Tốc độ: SourceAudioSpeed + ExternalAudioSpeed (0.10–8x)
- Trim audio riêng (start/duration)
- **Audio Editor Window**: cắt segment, single trim, seek ±5s, normalize segments

### 6.10 Watermark
- Scale, rotation, opacity, X/Y percent, aspect ratio
- **Debug mode**: kéo-thả watermark trên preview để chỉnh vị trí

### 6.11 Brand Logo
- Logo `Content/Branding/video-logo.png`, vị trí (Top Right, ...)

### 6.12 Color Grading
- Color filter (None, ...) + `ColorIntensity` + `VideoBrightness`

### 6.13 Crop
- Crop zoom %, aspect ratio (Free, ...), X/Y/W/H percent — có preview kéo-thả

### 6.14 Export
- Hardware profile: Auto / NVIDIA (H264/HEVC/AV1) / AMD (AMF) / Intel (QSV)
- Resolution: Original / tùy chỉnh `WxH` / `Np` (như 1080p) / TargetWidth/Height
- Framerate: Original / 60fps
- Bitrate: Match Source / Boost / Custom
- Output name + folder (`Titan_Output`)

### 6.15 Queue
- `ObservableCollection<RenderJob>` hiển thị list job, mỗi job có StatusDisplay + StatusColor + Progress
- Batch render + cancel token
- Chống chồng render giữa batch mode và API mode

---

## 7. API LOCAL (BAS / Automation)

**Base:** `http://localhost:5555/` (đổi port qua PortInputWindow, dùng nút HOST trong UI)

### Endpoints
| Endpoint | Mô tả |
|---|---|
| `POST /api/queue/add` | Thêm job vào hàng đợi → trả `queueId` + `jobId` |
| `POST /api/queue/start` | Bắt đầu chạy queue |
| `GET /api/render/status?jobId=...` | Poll trạng thái (progress, result, metadata) |
| `POST /api/render` | Render đồng bộ 1 job |
| `POST /api/render/async` | Render async |
| `POST /api/render/wait` | Render + chờ tới khi xong |
| `OPTIONS` | CORS preflight support |

### Request fields chính (JSON camelCase)
`inputFile`, `inputFiles` (merge), `mergeVideos`, `audioFiles`, `mergeAudio`, `audioFile` (=Audio 1), `audioFile2` (=Audio 2), `audioVolume`, `audio2Volume`/`secondaryAudioVolume`, `watermarkFile`, `templateName`, `fxEffect`, `overlay`/`overlayEffect`, `border`/`borderEffect`, `snowfallPreset/Mode/Opacity`, `snowOverlay1/2/3Opacity`, `rainOverlayPreset/Intensity`, `lightLeakBurn*`, `imageTimelineDurationSeconds`, `crop*`, `hardwareProfile`, `bitrateStrategy`, `resolution`, `framerate`, `videoTrim*`, `audioTrim*`, `videoVolume`, `sourceAudioSpeed`, `externalAudioSpeed`, `upscaleMode`, `sharpnessIntensity`, `slowMotionSpeed`, `slowMotionAudio`, `captionSync*`, `features`, + `JsonExtensionData` cho field mở rộng

### Behavior
- Nếu host mode OFF → API trả `503 Host mode is OFF`
- Status trả về đầy đủ metadata: source/output size, duration, fps, overlay selection, encoding time, webhookUrl
- Job state lưu trong `Dictionary<string, ApiRenderJobState>` + semaphore lock

---

## 8. HỆ THỐNG EFFECT RECIPE

`EffectRecipeLibrary.cs` là registry canonical. Mọi thứ đi qua `EffectRecipeBridge` để normalize tên cũ → recipe mới.

### Overlay recipes
- `FilmBurnWarm` (asset light leak, black background, screen blend, burst timing)
- `DustScratchRetro` (generated scratch overlay)

### Particle recipes
- `SnowSoft`, `SnowBokeh`, `RainCinematic`, `SparkleSoft`, `ConfettiBurst`

### Post-process recipes
- `VHSRetro`, `GlitchRGB`, `CRTScanline`, `LightSweepLuxury` + grain/aberration/vignette/blur

### Transition recipes
- `LightLeakTransition`, `GlitchTransition`, `ZoomPunch`, `ShakeHit`, `FlashPop`

### Quan trọng
- Có danh sách **RetiredRecipeNames** (20 tên cũ) — nếu API/UI gọi tên cũ sẽ bị chặn không tự ý bật lại preset cũ
- **KHÔNG suy luận kiểu asset từ tên file** — phải probe asset thật mỗi lần render (`OverlayAssetBackgroundAnalysis`), cache theo path+size+mtime

---

## 9. ASSET FOLDERS

| Folder | Nội dung |
|---|---|
| `Assets/Overlays/Snow/` | `snow_1.mp4`, `snow_2.mp4` (2 toggle UI) |
| `Assets/Overlays/Rain/` | `rain_heavy_black_bg_01.mp4` — **asset-only, fail fast nếu thiếu** |
| `Assets/Overlays/LightLeaks/` | `light_leak_warm_01.mp4`, `pexels_light_leak_30042778.mp4` |
| `Assets/Overlays/DreamyDots/` | `dreamy_dot_overlay_02.mp4`, `dreamy_dot_chaos_540x960_18_000.mp4` |
| `Content/Overlays/Snow/` | `snow_cinematic_01.mp4` (bundled fallback) |
| `Content/Overlays/LightLeaks/` | `warm_film_burn_01.mp4`, `soft_bokeh_leak_01.mp4`, `mixkit_light_leaks_overlay_48011.mp4` |
| `Content/Branding/` | `video-logo.png` |
| `Assets/Fonts/` | 2 font text overlay |
| `Assets/Whisper/` | whisper.cpp + model + CUDA libs |
| `shaders/` | FSRCNNX / FSR / Anime4K GLSL |

> Lưu ý: `TitanEngine.csproj` copy tất cả các folder trên vào output — khi thêm asset mới phải thêm rule copy tương ứng.

---

## 10. CÁC INVARIANT QUAN TRỌNG (ĐỪNG VI PHẠM)

1. **Không suy luận kiểu overlay từ tên file** — phải probe asset thật
2. **Không reintroduce procedural fallback** cho rain / light leak (asset-only, phải fail nhanh)
3. Không blur base video trừ khi là fit-blur background
4. Không mất source audio trừ khi bị tắt tường minh
5. `audioFile2` (stacking) khác `audioFiles`+`mergeAudio` (concat)
6. Image timeline sync logic phải chạy trước effect stack
7. Temp overlay pre-render phải tách khỏi output cuối
8. Giữ `BAS_LOCALHOST_API_GUIDE.txt` đồng bộ với request fields thật
9. Giữ csproj copy rules khớp với runtime assets
10. Light Leak Burn transition: cấm procedural, thiếu asset phải throw lỗi cứng

---

## 11. THAM KHẢO NHANH CÁC HÀM QUAN TRỌNG (MainWindow.xaml.cs)

| Hàm | Dòng | Vai trò |
|---|---|---|
| `EngineCore.Initialize()` | ~122 | Resolve ffmpeg/ffprobe/upscayl/dlss |
| `EngineCore.ExecuteRenderAsync` | ~1414 | Pipeline render chính |
| `EngineCore.AnalyzeMediaDetailsAsync` | ~1111 | Probe video bằng ffprobe |
| `EngineCore.ProbeVideoAssetAsync` | ~1211 | Probe asset overlay |
| `UpscaleWithNgxDlssAsync` | ~6480 | Upscale qua NVIDIA NGX |
| `UpscaleWithUpscaylAsync` | ~6726 | Upscale qua Upscayl |
| `BuildVideoCodecArgs` | ~8149 | Args codec theo encoder/preset |
| `GetMinRecommendedBitrate` | ~8185 | Bitrate tối thiểu theo resolution |
| `GenerateCaptionSyncAssAsync` | ~8750 | Whisper caption pipeline |
| `MergeAudiosToTempTrackAsync` | ~10166 | Concat nhiều audio |
| `MixAudiosToTempTrackAsync` | ~10234 | Mix Audio 1 + 2 |
| `HandleLocalApiRequestAsync` | ~13186 | Xử lý mọi request API |
| `BuildTemplateTimelineSourceAsync` | (trong file) | Image timeline / template source |
| `ResolveEffectiveImageTimelineTotalDurationAsync` | (trong file) | Duration sync ảnh + audio |

---

## 12. BẢO TRÌ & PHÁT TRIỂN

- **Thêm/đổi preset effect** → sửa `EffectRecipeLibrary.cs` trước
- **Đổi filter graph FFmpeg** → sửa `EffectRecipeRenderSupport.cs`
- **Đổi pipeline asset** → sửa `OverlayAssetBackgroundAnalysis.cs`
- **Đổi field API** → sửa `MainWindow.Models.cs` + `BAS_LOCALHOST_API_GUIDE.txt`
- **Thêm asset runtime** → thêm rule copy trong `TitanEngine.csproj`
- Output release: `bin\Release\net8.0-windows\TitanEngine.exe`
