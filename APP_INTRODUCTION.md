# ?? TITAN ENGINE v87.0 - FX-STUDIO-MASTER

**Enterprise Video Processing Pipeline with Advanced Effects & Color Grading**

---

## ?? Overview

**Titan Engine** is a professional WPF-based video processing application built with **.NET 8** and **C# 12.0**. It provides batch video rendering with real-time effects, color grading, watermarking, and advanced audio/video manipulation.

**Target**: Video editors, content creators, YouTubers, streaming professionals  
**Language**: Vietnamese UI with English backend  
**Framework**: WPF (.NET 8)  
**Video Engine**: FFmpeg (must be present in bin directory)

---

## ? Key Features (v87.0)

### ?? Video Processing
- **Batch Rendering**: Process multiple videos in sequence
- **Video Trim**: Precise start time + duration trimming
- **Resolution Control**: Scale to any resolution (Original, 720p, 1080p, 2160p, etc.)
- **Framerate Control**: Original or custom FPS (24, 30, 60, etc.)
- **Hardware Acceleration**: NVIDIA NVENC, AMD AMF, Intel QSV, or CPU fallback
- **Bitrate Strategies**: Match Source, Boost (+50%), or Custom

### ?? Advanced Effects (FX-STUDIO)
1. **RGB Glitch** - Digital glitch with configurable noise (20%-100%)
2. **Cinematic Blur** - Gaussian blur with luma/chroma control
3. **Old TV Noise** - Vintage scanlines + noise overlay
4. **Ghost Trails** - Motion blur trail effect (tblend)
5. **Color Pulse** - Dynamic saturation oscillation
6. **Mirror Horizontal** - Video flip effect
7. **Zoom In** - 1.1x scale effect

**?? Disabled Effects** (logic preserved, not available in UI):
- ~~Negative X-Ray~~ - Inverted colors + high contrast
- ~~Vignette~~ - Dark edges fade effect
- ~~Smart Sharpen~~ - Unsharp mask with intensity control
- ~~Edge Neon~~ - Edge detection with colorization
- ~~Camera Shake~~ - Noise tremor effect

**Effect Timeline**: Each effect supports start% ? end% timeline control (e.g., 0% - 6% = apply only first 6% of video)

### ?? Color Grading (6 Presets)
1. **Spring (Green)** - Vibrant greens/reds, high saturation
2. **Autumn (Orange)** - Warm tones, vintage feel
3. **Winter (Cyan)** - Cool/desaturated, very dark
4. **Cinematic (Teal)** - High contrast, teal+orange shift
5. **Cold (Blue)** - Blue/cyan heavy desaturation
6. **Warm (Red)** - Saturated reds/yellows, bright

**Intensity Control**: 0% - 100% opacity blending (using colorchannelmixer overlay method)

### ?? Audio Processing
- **Dual Audio Mixing**: Video audio + External audio (MP3/WAV/AAC)
- **Audio Trim**: Separate start/duration for audio
- **Volume Control**: Video (0%-200%) + Audio (0%-200%)
- **Format**: AAC codec, 256kbps, automatic mixing

### ?? Watermark Engine
- **Multi-format Support**: PNG, JPG, JPEG (with alpha channel)
- **Visual Editor**: Real-time drag-to-position + scale/rotate/opacity sliders
- **Position Control**: X/Y as percentage of video (0-100%)
- **Transform Options**: 
  - Scale: 0.1x - 3.0x
  - Rotation: 0° - 360°
  - Opacity: 0% - 100%

### ?? Output Management
- **Custom Output Directory**: Save to any folder (default: bin/Titan_Output)
- **Auto-Naming**: Default uses Job # (Job1, Job2, Job3, etc.) to prevent overwrites
- **Custom Output Names**: User can override with custom names
- **Batch Processing**: All jobs render sequentially

### ??? UI Features
- **Job Queue**: Visual list with real-time progress bars
- **Drag-and-Drop**: Drop videos/audio/watermarks directly
- **Status Colors**: 
  - ?? WAITING (Cyan)
  - ?? PROCESSING (Yellow) - with % progress
  - ?? DONE (Lime Green)
  - ?? FAILED (Red)
- **Taskbar Integration**: Shows overall batch progress
- **Live Logging**: Full FFmpeg debug output, warnings, errors
- **Stop Button**: Cancel batch at any time

---

## ??? Architecture

### Core Classes

**RenderJob** (Data Model)
```
- Stores all render parameters (video path, effects, colors, audio, watermark, etc.)
- Implements INotifyPropertyChanged for UI binding
- Properties: Id, Guid, SourcePath, AudioPath, OutputName, ConfigSummary, Progress, Status
```

**EngineCore** (Processing Engine)
```
Static class handling all FFmpeg operations:
- Initialize() - Locate FFmpeg/FFprobe executables
- AnalyzeMediaAsync() - Get duration + bitrate via ffprobe
- ExecuteRenderAsync() - Main render pipeline with filter_complex
- BuildFxFilterChain() - Generate effect filters dynamically
- AnalyzeFfmpegError() - Detailed error analysis with suggestions
```

**MainWindow** (UI Controller)
```
WPF Window with event handlers for:
- File browsing (video, audio, watermark)
- Effect timeline slider management
- Watermark visual editor with drag/drop
- Job queue management (add, delete, clear)
- Batch rendering with cancellation token
- Real-time logging with thread-safe lock
```

### FFmpeg Filter Chain Architecture

```
Input: [0:v] ? Video Filters ? [v_proc]
              ? Color Grading ? [v_proc]
              ? Watermark Overlay ? [v_out]

Input: [0:a] + [1:a] ? Volume Control ? [a1] [a2]
                    ? Audio Mix (amix) ? [aout]

Output: -map [v_out] -map [aout] ? MP4 with H.264 video + AAC audio
```

---

## ?? Technical Specifications

### Supported Formats
- **Video**: MP4, MOV, AVI, MKV (via FFmpeg auto-detection)
- **Audio**: MP3, WAV, AAC
- **Image**: PNG, JPG, JPEG (watermarks)
- **Output**: MP4 (H.264 + AAC)

### Encoding Options
- **Video Codec**: libx264 (CPU) / h264_nvenc (NVIDIA) / h264_amf (AMD) / h264_qsv (Intel)
- **Audio Codec**: AAC (256kbps)
- **Color Space**: YUV420p
- **Preset**: veryslow (CPU) / p7 (NVIDIA) / quality (AMD) / veryslow (Intel)

### Filter Limitations & Workarounds
- ? `crop` filter doesn't support `enable='between(t,start,end)'` ? removed from Old TV Noise
- ? `ih` not valid in overlay context ? use `main_h` instead
- ? Multiple input pads can reuse same source (e.g., `[v1]crop...` twice)
- ? All enable clauses use `between(t,startSec,endSec)` format

---

## ?? Configuration Summary Format

Each job displays config in one line:
```
[NVIDIA NVENC] Original @ Original | Watermark enabled
```

Conditionally shows:
- `VTrim:duration` - if video trim enabled
- `ATrim:duration` - if audio trim enabled
- `Wmk` - if watermark attached

---

## ?? Usage Workflow

### Basic Workflow:
1. **Select Video** ? Browse or drag .mp4/.mov/.avi/.mkv
2. **Select Audio** (Optional) ? Browse or drag .mp3/.wav/.aac
3. **Select Watermark** (Optional) ? Browse or drag .png/.jpg
4. **Configure Effects**:
   - FX Effect dropdown (RGB Glitch, Cinematic Blur, etc.)
   - FX Intensity slider (0%-100%)
   - FX Timeline sliders (start% - end%)
5. **Configure Color Grading**:
   - Color Filter dropdown (Spring, Autumn, Winter, etc.)
   - Color Intensity slider (0%-100%)
6. **Set Output Settings**:
   - Hardware Profile (Auto/NVIDIA/AMD/Intel)
   - Resolution (Original/720p/1080p/2160p)
   - Bitrate Strategy (Match Source/Boost/Custom)
7. **Add to Queue** ? "Add Job" button
8. **Start Render** ? "Start Batch" button
9. **Monitor Progress** ? Watch job status + live logs
10. **Open Output** ? Auto-prompt to open folder when done

---

## ?? Known Issues & Fixes

| Issue | Root Cause | Fix Applied |
|-------|-----------|------------|
| `boxblur=lr:lb` error | Invalid parameter names | Changed to `luma_radius:chroma_radius` |
| `[v1]split=2[a][b]; [v1]...` | Can't reuse after split | Crop `[v1]` twice instead |
| `overlay=y=ih*0.88` | `ih` undefined in overlay | Use `main_h` instead |
| Color filter not applying | ComboBox value doesn't match switch case | Added `or "Name (Suffix)"` pattern matching |
| Volume=0.0 FFmpeg error | Invalid volume range | Clamp to [0.1, 2.0] with warning |
| Color intensity ignored | Missing intensity suffix in names | Using colorchannelmixer for blend |

---

## ?? Default Behaviors

### Auto Output Naming
- **Default**: `Job#{job.Id}` (e.g., "Job1", "Job2", "Job3")
- **Custom**: User can override in "Output Name" textbox
- **Batch**: Multiple videos get suffixes `_01`, `_02`, etc.

### Default Settings
- Hardware: Auto (CPU fallback)
- Resolution: Original
- Framerate: Original
- Bitrate: Match Source (±50%)
- Video Volume: 100%
- Audio Volume: 100%
- FX Effect: None
- Color Filter: None
- Watermark: None (if not selected)

### Logging Levels
- `[BOOT]` - Startup
- `[OK]` - Success operations
- `[INPUT]` - File selection
- `[CONFIG]` - Job configuration
- `[FX-STUDIO]` - Effect details
- `[COLOR-GRADING]` - Color details
- `[WARN]` - Warnings (volume clamp, etc.)
- `[DEBUG-FILTER]` - Full FFmpeg filter chain
- `[FFMPEG-CMD]` - Full command line
- `[FFMPEG-ERROR-*]` - Error details
- `[SUCCESS]` - Job completed
- `[FATAL]` - Critical failure

---

## ?? Future Enhancement Ideas

- [ ] Video preview with effects preview
- [ ] Effect keyframe automation
- [ ] Multiple watermark support
- [ ] Custom filter builder UI
- [ ] A/B comparison view
- [ ] Subtitle/Caption support
- [ ] Format conversion (WebM, AV1, etc.)
- [ ] Cloud rendering integration
- [ ] Mobile app companion
- [ ] Plugin system for custom effects

---

## ?? Support

**GitHub**: (To be added)  
**Issues**: Report via GitHub Issues  
**Discord**: (Community server - TBD)

---

**Version**: v87.0-FX-STUDIO-MASTER  
**Last Updated**: 2026-01-22  
**Author**: HUYNEK0110  
**License**: Proprietary © 2026

