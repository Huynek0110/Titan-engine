# TitanEngine - OpenCode Agent Guide

## Project Overview

**TitanEngine** is a professional-grade WPF application (.NET 8, Windows) for AI-powered video processing, rendering, and effects compositing. It uses FFmpeg as the core rendering engine with support for NVIDIA/AMD/Intel hardware acceleration, AI upscaling (DLSS/NGX, Upscayl), and a rich effect system.

### Key Capabilities
- **Video Rendering**: Multi-track timeline, merging, transitions, trimming
- **AI Upscaling**: DLSS/NGX (NVIDIA), Upscayl (external), FSR/CAS (internal shaders)
- **Effects System**: Light leaks, film burns, glitch, VHS, snow/rain overlays, polaroid frames, dreamy dots, particles
- **Audio Processing**: Multi-track mixing, loudnorm, trimming, speed control, Whisper AI caption sync
- **Watermarking/Branding**: Dynamic positioning, rotation, opacity, aspect ratio
- **Localhost API**: HTTP endpoint for programmatic job submission
- **Templates**: Pre-built effect stacks (Magazine Cover, Polaroid, Cinematic, etc.)

---

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MainWindow.xaml.cs` | **Main logic (~4000+ lines)** - EngineCore, RenderJob execution, FFmpeg filter graph construction, all effects pipelines |
| `MainWindow.Models.cs` | Data models - `RenderJob`, `LocalhostRenderRequest/Response`, `ApiRenderJobState`, `AudioEditSegment` |
| `App.xaml.cs` | WPF application entry point |
| `TitanEngine.csproj` | Project config - net8.0-windows, WPF, content assets |

### Effect Presets (in TitanEngine folder)

| Preset | Description |
|--------|-------------|
| `LightLeakBurnPreset.cs` | Full-screen light leak burn transition/effect with asset-backed overlays |
| `LightLeakOverlayPreset.cs` | Light leak overlay with looping asset support |
| `SnowfallOverlayPreset.cs` | Multi-layer snowfall (generated or asset-based) |
| `RainOverlayPreset.cs` | Rain overlay with intensity presets |
| `DreamyDotOverlayPreset.cs` / `DreamyDotOverlay2Preset.cs` | Dreamy bokeh/dot overlays |
| `ScratchVideoPreset.cs` | Animated film dust & scratches |
| `PolaroidScrapbook2Preset.cs` | Rounded polaroid frame with tape/sticky notes |
| `RgbPolaroidLedPreset.cs` | Animated RGB LED border polaroid |
| `DashedPolaroidFramePreset.cs` | Dashed rounded frame animation |
| `ParticleOverlayGenerator.cs` | Generic particle system (snow, rain, sparkles, etc.) |

### Effect Recipe System

| File | Purpose |
|------|---------|
| `EffectRecipeModels.cs` | Core recipe types: `OverlayEffectRecipe`, `ParticleEffectRecipe`, `PostProcessEffectRecipe`, `TransitionRecipe`, enums for blend modes, timing, placement |
| `EffectRecipeLibrary.cs` | Registry of all built-in effect recipes |
| `EffectRecipeRenderSupport.cs` | Bridge between recipes and FFmpeg filter graph builders |
| `TitanTransitionLibrary.cs` / `TitanTransitionGraphBuilder.cs` | Traditional transition effects (dissolve, whip, zoom, etc.) |

### Supporting Models

| File | Purpose |
|------|---------|
| `VideoAssetProbeInfo.cs` | FFprobe result container |
| `AudioEditModels.cs` | `AudioEditSegment` for timeline-based audio editing |
| `OverlayContentLayout.cs` | Canvas/content layout calculations for overlays |
| `OverlayAssetBackgroundAnalysis.cs` | Asset analysis utilities |

---

## Render Pipeline (ExecuteRenderAsync)

### 1. Initialization & Asset Preparation
- Validate FFmpeg/FFprobe paths (EngineCore.Initialize)
- Merge multiple video inputs (concat demuxer)
- Merge multiple audio inputs
- Mix audio tracks (overlay)
- Apply audio edits (trim, volume, speed)

### 2. Template/Timeline Building
- Image timeline → video (with duration from audio)
- Template system: Magazine Cover, Polaroid scrapbook, etc.
- Cross-transitions between clips (Light Leak Burn, Dissolve, etc.)

### 3. External AI Upscaling (Pre-processing)
```
Priority: NVIDIA DLSS (NGX DLVSR) → Upscayl → Internal FSR/CAS
```
- If GPU profile = NVIDIA & DLSS available → use NGX
- Fallback to Upscayl executable
- If both fail → internal shader upscale (FSR/CAS/Anime4K)

### 4. Filter Chain Construction (Strict Order)
```
[Crop] → [Scale/Upscale] → [Motion: 60fps/RSMB] → [FX Effects] → [Color Grading] 
→ [Text/Caption Overlay] → [Brightness] → [Fade] → [Overlay Layers] → [Watermark/Brand Logo]
```

### 5. Overlay Composition (Complex Filter Graph)
Multiple overlay types stacked in sequence:
1. Light Leak Burn (base)
2. Scratch/Dust overlay
3. Dreamy Dot overlay
4. Particle effects (recipe-based)
5. RGB Polaroid LED
6. Polaroid Scrapbook 2
7. Dashed Polaroid
8. Snowfall (multi-layer)
9. Rain
10. Light Leak Overlay (asset)
11. Watermark + Brand Logo (final)

### 6. Audio Pipeline
- Source audio (from video) + External audio (user provided)
- Speed adjustment (atempo chain)
- Trim/volume
- Loudnorm (optional)
- Mix: `amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95`

### 7. Encoding
- **Encoder Selection**: 
  - NVIDIA: `h264_nvenc` / `hevc_nvenc` / `av1_nvenc` (checks actual FFmpeg support)
  - AMD: `h264_amf` / `av1_amf`
  - Intel: `h264_qsv`
  - CPU: `libx264` (veryslow, CRF 16)
- **Bitrate**: Match Source (auto-min), Boost (1.5x), Custom
- **Output**: yuv420p, MP4 container

---

## Key Classes & Data Structures

### RenderJob (MainWindow.Models.cs:34-286)
Central job configuration - **all settings live here**:
- Source/Output paths, merge lists
- Resolution, framerate, bitrate, hardware profile
- All effect toggles (FX, Overlay, Border, Snow, Rain, Polaroid, Brand Logo)
- Watermark transform (scale, rotation, opacity, position, aspect)
- Audio settings (volumes, speeds, segments, loudnorm)
- Upscale mode, sharpness
- Slow motion, fade, crop
- Text overlay, Caption Sync (Whisper AI)
- API job support (webhook, queue ID)

### LocalhostRenderRequest/Response (MainWindow.Models.cs:288-577)
JSON API contracts for the HTTP endpoint.

### EngineCore (MainWindow.xaml.cs:44-1412)
Static utilities:
- FFmpeg/FFprobe/Upscayl/DLSS path management
- NVENC capability detection
- Media analysis (ffprobe JSON parsing)
- DLSS/NGX auto-download & build (CMake, VFX SDK)
- Video resolution probing

---

## Effect Recipe System (Data-Driven)

### Recipe Types
1. **OverlayEffectRecipe** - Asset-based overlays (light leaks, burns, textures)
2. **ParticleEffectRecipe** - Procedural particles (snow, rain, sparkles, confetti)
3. **PostProcessEffectRecipe** - Screen-space effects (VHS, glitch, RGB split, grain, CRT)
4. **TransitionRecipe** - Transition pulses (light leak, glitch, blur zoom, shake)

### Recipe Resolution Flow
```
User selects effect name (string) 
  → ResolveFxEffectName() / ResolveOverlayEffectName() 
  → EffectRecipeLibrary.TryGet*Recipe() 
  → Recipe object with all parameters
  → EffectRecipeRenderSupport / GraphBuilder builds FFmpeg filter
```

### Built-in Recipe Categories
- **Overlay**: Light Leak Overlay, Scratch Video, Dreamy Dot, Film Burn, etc.
- **Particle**: Snow Cinematic, Heavy Rain, Sparkle, Confetti, Firework, Bokeh
- **PostProcess**: VHS, Glitch, RGB Split, Chromatic Aberration, Vignette, CRT, Grain
- **Transition**: Light Leak, Glitch, Blur Zoom, Zoom Punch, Shake, Flash Pop

---

## API Endpoints (Localhost Server)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/render` | POST | Submit render job (returns queue ID) |
| `/api/status/{queueId}` | GET | Check job status/progress |
| `/api/cancel/{queueId}` | POST | Cancel queued/running job |
| `/api/video-info` | POST | Probe video metadata (ffprobe) |
| `/api/queue` | GET | List all jobs |

**Request Body** (LocalhostRenderRequest): All RenderJob fields as nullable JSON properties.
**Response** (LocalhostRenderResponse): QueueId, Status, Progress, output paths, video metadata.

---

## Shader Assets (shaders/ folder)

| Shader | Purpose |
|--------|---------|
| `FSR.glsl` | AMD FidelityFX Super Resolution |
| `FSRCNNX.glsl` | CNN-based super resolution |
| `Anime4K_Upscale_CNN_x2_L.glsl` | Anime4K 2x upscale |
| `Anime4K_Upscale_Denoise_CNN_x2_L.glsl` | Anime4K denoise + upscale |
| `Anime4K_Restore_CNN_L.glsl` | Anime4K restoration |

Used by internal upscale pipeline when external AI upscalers unavailable.

---

## Content Assets (Copied to Output)

```
Content/
├── Branding/video-logo.png
├── Overlays/
│   ├── LightLeaks/**/*.png (light leak assets)
│   └── Snow/**/*.png (snowflake assets)
Assets/
├── Overlays/
│   ├── LightLeaks/**/*
│   ├── Snow/**/*
│   ├── Rain/**/*
│   └── DreamyDots/**/*
├── Fonts/**/* (for caption/text rendering)
└── Whisper/**/* (Whisper.cpp models for AI captions)
```

---

## Build & Run

### Prerequisites
- .NET 8 SDK
- **FFmpeg** with NVENC support (copy `ffmpeg.exe` + `ffprobe.exe` to output dir)
- Optional: NVIDIA VFX SDK + `nvvfxupscale` feature for DLSS
- Optional: Upscayl executable in `upscayl/Upscayl.exe`

### Build
```bash
dotnet build TitanEngine/TitanEngine.csproj -c Release
```

### Run
```bash
dotnet run --project TitanEngine/TitanEngine.csproj -c Release
```

---

## Common Development Tasks

### Adding a New Effect
1. Add recipe to `EffectRecipeLibrary.cs`
2. Create preset class (e.g., `MyNewEffectPreset.cs`) with `GenerateOverlayAsync` / `BuildSingleClipGraph`
3. Register in `EffectRecipeRenderSupport.cs` bridge
4. Add UI binding in `MainWindow.xaml` (ComboBox items)
5. Handle in `ExecuteRenderAsync` filter chain construction

### Adding a New Transition
1. Add to `TitanTransitionType` enum
2. Implement in `TitanTransitionGraphBuilder.cs`
3. Register in `TitanTransitionLibrary.cs`

### Modifying Filter Chain Order
Edit `ExecuteRenderAsync` in `MainWindow.xaml.cs` - search for `filterChain.Append` sequence.

### Debugging FFmpeg Commands
- Check log output: `[FFMPEG-FILTER-COMPLEX]`, `[FFMPEG-COMBINED-FILTER-COMPLEX]`
- Copy the printed filter_complex and test manually with ffmpeg

---

## Important Patterns & Conventions

### Filter Graph Construction
- Use `StringBuilder` for filter chains
- Labels: `[0:v]`, `[1:v]`, `[vout]`, `[audio_out]`, intermediate labels like `[burnstage]`, `[overlaystage]`
- Multiple inputs inserted via `cmd.Insert(postInputInsertPoint, ...)`
- Audio/video filter complexes combined with `;`

### Coordinate Normalization
- Percentages (0-100) → FFmpeg normalized (0.0-1.0): `NormalizeCoordinatePercent()`
- Custom positions use `TextOverlayXPercent` / `TextOverlayYPercent`

### Duration Master Logic (MainWindow.xaml.cs:1904-1978)
- **Image timeline + Audio 1**: Audio 1 dictates total length
- **Video + Audio 1**: `Math.Min(video, audio1)` - never extend video
- **No Audio 1**: `Math.Min(video, combinedAudio)`

### Cancellation & Cleanup
- `CancellationToken` passed through all async methods
- Temp files tracked in `tempTemplateArtifacts` list
- Cleanup in `finally` block (not shown in truncated view)

### Logging
- `Action<string> onLog` callback for UI log window
- Prefixes: `[ENCODER]`, `[FILTER-*]`, `[UPSCALE]`, `[TEMPLATE]`, `[DLSS-NGX]`, `[CAPTION-SYNC]`

---

## Known Limitations / Gotchas

1. **Single-file monolith** - MainWindow.xaml.cs is 4000+ lines; consider splitting
2. **FFmpeg dependency** - Must bundle or auto-download; NVENC requires specific FFmpeg build
3. **DLSS/NGX** - Requires NVIDIA VFX SDK + NGC API key + CMake + VS 2022; auto-build is best-effort
4. **Whisper AI** - Requires whisper.cpp models in `Assets/Whisper/`
5. **Windows-only** - WPF, .NET 8 windows target, FFmpeg Windows builds
6. **No unit tests** - Manual verification via UI

---

## File Search Patterns for Common Tasks

| Task | Search Pattern |
|------|----------------|
| Find effect implementation | `grep -r "class.*Preset" TitanEngine/` |
| Find filter builder | `grep -r "Build.*Filter" TitanEngine/` |
| Find recipe registration | `grep -r "EffectRecipeLibrary" TitanEngine/` |
| Find API endpoint | `grep -r "MapGet\|MapPost" TitanEngine/` (if using Minimal API) or check MainWindow for HttpListener |
| Find NVENC detection | `grep -r "HasNvencSupport" TitanEngine/` |

---

## Quick Reference: RenderJob Key Properties

```csharp
// Input
SourcePath, MergeInputPaths, MergeVideos
AudioPath, SecondaryAudioPath, MergeAudioPaths, MergeAudio

// Output
OutputName, Resolution, Framerate, HardwareProfile, BitrateStrategy, CustomBitrate

// Video Processing
VideoTrimStart, VideoTrimDuration, SplitDurationSeconds
CropEnabled, CropZoomPercent, CropAspectRatio
UpscaleMode, SharpnessIntensity
SlowMotionSpeed, SlowMotionAudio
Enable60fps, RsmbIntensity

// Effects
FxEffect, OverlayEffect, BorderEffect
EnableSnowOverlay1/2/3, EnableOverlayIntro, EnableCrossTransitions
TemplateName, EnablePolaroidScrapbook, EnableRgbPolaroidScrapbook
EnableDashedPolaroidScrapbook, EnableBrandLogo, BrandLogoPosition
LightLeakBurn*, Snowfall*, RainOverlay*, FxIntensity, FxStartPercent, FxEndPercent

// Watermark
WatermarkPath, WatermarkScale, WatermarkRotation, WatermarkOpacity
WatermarkXPercent, WatermarkYPercent, WatermarkAspectRatio

// Color/Grading
ColorFilter, ColorIntensity, VideoBrightness

// Audio
VideoVolume, AudioVolume, SecondaryAudioVolume
SourceAudioSpeed, ExternalAudioSpeed
AudioSegments, EnableLoudnorm

// Text/Caption
TextOverlay, TextOverlayPosition, TextAlign, TextOverlayFontSize
TextOverlayColor, TextOverlayStyle, AddQuotes, DisableTextScroll
EnableTextGlow, TextGlowColor
EnableCaptionSync, CaptionSyncLanguage, CaptionSyncModelSize, CaptionSyncAnimation

// API
IsApiJob, WebhookUrl
```

---

## Version Info

Current: **TITAN ENGINE V106** (per header comment in MainWindow.xaml.cs)
- AI Upscale Rewrite: CAS algorithm + Custom Resolution Fix
- Filter Chain Order: [Scale] → [CAS] → [FX] → [Color] → [Watermark]
- Hardcoded Bitrate: 25Mbps for upscaled output