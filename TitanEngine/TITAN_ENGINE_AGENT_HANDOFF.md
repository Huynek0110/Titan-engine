# Titan Engine Agent Handoff

This repo is a WPF video editor/render engine built around FFmpeg CLI.
The app is not a small UI shell. `MainWindow.xaml.cs` is the center of gravity and contains:

- the editor UI event handlers
- the render queue
- the local BAS localhost API
- the FFmpeg command builders
- duration sync logic
- audio mixing and audio editing hooks
- overlay/effect/template normalization
- release/runtime cleanup helpers

If you need to understand the app quickly, start here:

1. `MainWindow.xaml.cs`
2. `EffectRecipeLibrary.cs`
3. `EffectRecipeRenderSupport.cs`
4. `SnowfallOverlayPreset.cs`
5. `RainOverlayPreset.cs`
6. `LightLeakOverlayPreset.cs`
7. `LightLeakBurnPreset.cs`
8. `DreamyDotOverlayPreset.cs`
9. `DreamyDotOverlay2Preset.cs`
10. `ParticleOverlayGenerator.cs`
11. `AudioEditorWindow.xaml(.cs)`

## What This App Does

Titan Engine renders video or image sources into final MP4 outputs using FFmpeg.
It supports:

- single source render
- multiple source merge render
- image timeline render
- audio 1 + audio 2 stacking
- audio concat/merge mode
- templates and FX studio presets
- overlays, borders, watermarks, brand logo
- local HTTP API for BAS and automation
- optional GPU / upscale integration

The app currently supports both UI usage and API usage.
The BAS guide is already copied into release output and should stay in sync with API changes.

## Build And Release

- Project file: `TitanEngine.csproj`
- Target framework: `net8.0-windows`
- App type: WPF `WinExe`
- Nullable enabled
- Implicit usings enabled

Release output is under:

- `bin\Release\net8.0-windows\TitanEngine.exe`

The release folder also copies important content:

- `BAS_LOCALHOST_API_GUIDE.txt`
- `Content\Branding\video-logo.png`
- `Content\Overlays\LightLeaks\**\*`
- `Assets\Overlays\LightLeaks\**\*`
- `Content\Overlays\Snow\**\*`
- `Assets\Overlays\Snow\**\*`
- `Assets\Overlays\Rain\**\*`
- `Assets\Overlays\DreamyDots\**\*`

Release guide path after build:

- `C:\Users\Huy\Desktop\TitanEngine\TitanEngine\TitanEngine\bin\Release\net8.0-windows\BAS_LOCALHOST_API_GUIDE.txt`

## Repository Map

- `MainWindow.xaml.cs` - the main render engine, queue system, API host, and UI code-behind
- `MainWindow.xaml` - the full WPF UI layout
- `App.xaml` / `App.xaml.cs` - WPF app startup shell
- `AssemblyInfo.cs` - WPF theme info
- `TitanEngine.csproj` - build config and content copy rules
- `BAS_LOCALHOST_API_GUIDE.txt` - API usage guide for BAS and localhost automation
- `AudioEditModels.cs` - trim/segment data models and helpers
- `AudioEditorWindow.xaml(.cs)` - separate audio editor window
- `VideoAssetProbeInfo.cs` - ffprobe result model for overlay assets
- `OverlayContentLayout.cs` - canvas/content layout helper used by overlays and borders
- `OverlayAssetBackgroundAnalysis.cs` - asset probing and background classification
- `EffectRecipeModels.cs` - shared recipe models and enums
- `EffectRecipeLibrary.cs` - canonical effect/preset registry
- `EffectRecipeRenderSupport.cs` - FFmpeg graph builders for recipes
- `ParticleOverlayGenerator.cs` - generated particle overlay sequence builder and cache
- `SnowfallOverlayPreset.cs` - snow generated or pre-rendered overlay system
- `RainOverlayPreset.cs` - rain asset-based overlay system with background detection
- `LightLeakOverlayPreset.cs` - asset-based light leak / film burn overlay preset
- `LightLeakBurnPreset.cs` - asset-only light leak burn transition logic
- `DreamyDotOverlayPreset.cs` - generated dreamy dots / chaos overlay
- `DreamyDotOverlay2Preset.cs` - asset-based dreamy dot overlay
- `ScratchVideoPreset.cs` - generated scratch/dust overlay
- `RgbPolaroidLedPreset.cs` - animated RGB border preset
- `PolaroidScrapbook2Preset.cs` - scrapbook border preset
- `DashedPolaroidFramePreset.cs` - dashed frame preset
- `Content\Branding\video-logo.png` - bundled logo asset
- `Content\Overlays\Snow\README.txt` - bundled snow asset notes
- `Content\Overlays\LightLeaks\README.txt` - bundled light leak notes
- `Assets\Overlays\Snow\README.txt` - UI snow asset notes
- `Assets\Overlays\Rain\README.txt` - rain asset notes

Workspace artifacts that are not core source:

- `bin\` and `obj\` - build artifacts
- `TestRenders_*` - smoke test and validation outputs
- `temp_lightleak_contact.jpg` - scratch/test artifact in repo root

## Runtime Architecture

### App Startup

`App.xaml.cs` is minimal. The app starts in `MainWindow.xaml`.

`EngineCore.Initialize()` resolves runtime dependencies from the app base folder:

- `ffmpeg.exe`
- `ffprobe.exe`
- `upscayl\Upscayl.exe`
- `dlss\DLVSR.exe`

If FFmpeg is missing, the app prompts for manual selection.

### MainWindow Role

`MainWindow.xaml.cs` contains:

- `RenderJob`
- API request/response DTOs
- API job state storage
- the render queue
- batch render orchestration
- host mode toggling
- UI event handlers
- duration sync
- source probing
- filter graph construction
- output path resolution
- cleanup helpers

### Host Mode

The local API uses:

- `http://localhost:5555/`

Endpoints currently supported:

- `POST /api/queue/add`
- `POST /api/queue/start`
- `GET /api/render/status?jobId=...`
- `POST /api/render`
- `POST /api/render/async`
- `POST /api/render/wait`

The app logs an admin urlacl hint if localhost binding fails.

## UI Map

`MainWindow.xaml` currently exposes these major sections:

- source media and trim
- audio 1 and audio 2
- merge videos
- merge audio
- FX Studio
- overlay toggles
- rain overlay settings
- border selector
- snow opacity controls
- brand logo toggle and logo position
- intensity slider
- magazine cover title/subtitle
- crop zoom
- audio mixer
- watermark editor
- color grading
- export settings
- queue and logs

Important visible labels in the current UI:

- `Audio 1 (Opt)`
- `Audio 2 (Overlay)`
- `Overlay 1`
- `Overlay 2`
- `Rain Preset`
- `Rain Intensity`
- `Border`
- `Polaroid Scrapbook`
- `Polaroid Scrapbook 2`
- `RGB Polaroid Border`
- `Dashed Polaroid Frame`
- `Audio 1 Volume`
- `Audio 2 Volume`

## Render Pipeline

The render flow in `MainWindow.xaml.cs` is roughly:

1. Resolve source files.
2. Resolve output name and output folder.
3. Resolve video merge mode.
4. Resolve audio 1 / audio 2 / merge audio mode.
5. Apply audio edit segments if present.
6. Sync image timeline duration and external audio duration.
7. Resolve template / FX / overlay / border / watermark selections.
8. Build FFmpeg input graph.
9. Render with NVENC, CPU, or AMF depending on hardware/profile.
10. Save result to output path and update queue/API state.

### Duration Sync

This app has explicit duration logic for image jobs and audio jobs.

Key behavior:

- image source jobs can use `ImageTimelineDurationSeconds`
- if that value is empty, the engine uses a default per-image duration
- when the source is all images, external audio can cap the total image timeline duration
- if audio is longer than the image timeline, the source duration remains the driver

The main helper is:

- `ResolveEffectiveImageTimelineTotalDurationAsync(...)`

This matters because many effects and overlays use the final duration as their timing base.

### Source Merge Behavior

`BuildInputFilesForJob(...)` returns:

- merged input paths when `MergeVideos` is enabled
- otherwise the single source path

`BuildAudioFilesForJob(...)` returns:

- merged audio paths when `MergeAudio` is enabled
- otherwise primary audio path
- then appends `SecondaryAudioPath` if present

### Audio 1 / Audio 2

The current engine explicitly supports two audio layers:

- `AudioPath` = audio 1
- `SecondaryAudioPath` = audio 2
- `AudioVolume` = audio 1 volume
- `SecondaryAudioVolume` = audio 2 volume

If both audio tracks exist, they are mixed before the normal external-audio pipeline.
If only audio 2 exists, it becomes the external audio track.
`audioFiles` is still the concat/merge path for multiple files, and should not be confused with audio 2 stacking.

### Image Timeline

If the source is one or more images:

- the engine behaves as a video timeline render
- image duration is controlled by `ImageTimelineDurationSeconds`
- the app defaults to a portrait-friendly timing if needed
- template motion effects such as zoom in/out are time-based, so their speed depends on the resolved duration

## Canonical FFmpeg Shapes

These are the key graph shapes the project uses.

### Base Cover Crop

```text
[src]scale=canvasWidth:canvasHeight:force_original_aspect_ratio=increase,crop=canvasWidth:canvasHeight,setsar=1[base]
```

### Fit Blur Background Only

```text
[src]scale=canvasWidth:canvasHeight:force_original_aspect_ratio=increase,crop=canvasWidth:canvasHeight,boxblur=30:1[bg];
[src]scale=canvasWidth:canvasHeight:force_original_aspect_ratio=decrease[fg];
[bg][fg]overlay=(W-w)/2:(H-h)/2[base]
```

### Alpha Overlay Asset

```text
[base][ov]overlay=0:0:shortest=1[outv]
```

### Black Background Overlay Asset

```text
[base][ov]blend=all_mode=screen:all_opacity=0.18[outv]
```

### Green Screen Overlay Asset

```text
[ovsrc]chromakey=0x00FF00:0.18:0.08[ovkey];
[base][ovkey]overlay=0:0:shortest=1[outv]
```

### Asset-Only Light Leak / Film Burn

```text
-i input.mp4
-stream_loop -1 -i Assets/Overlays/LightLeaks/light_leak_warm_01.mp4
...
[0:v]... [base];
[1:v]... [leak];
[base][leak]blend=all_mode=screen:all_opacity=0.14[outv]
```

## Effect And Overlay System

The new system is recipe-driven. The core model file is `EffectRecipeModels.cs`.

### Core Enums And Models

`EffectRecipeModels.cs` defines:

- `OverlayAssetType`
- `OverlayBlendMode`
- `OverlayTimingMode`
- `OverlayPlacement`
- `OverlayScaleMode`
- `ParticleType`
- `ParticleShape`
- `ParticleOutputMode`
- `PostProcessEffectType`
- `TransitionEffectType`
- `EffectRange`
- `EffectRecipe`
- `OverlayEffectRecipe`
- `ParticleLayerRecipe`
- `ParticleEffectRecipe`
- `PostProcessEffectRecipe`
- `TransitionRecipe`
- `TemplateEffectStackRecipe`

### Recipe Registry

`EffectRecipeLibrary.cs` is the canonical registry.

Current overlay recipes:

- `FilmBurnWarm`
- `DustScratchRetro`

Current particle recipes:

- `SnowSoft`
- `SnowBokeh`
- `RainCinematic`
- `SparkleSoft`
- `ConfettiBurst`

Current post-process recipes:

- `VHSRetro`
- `GlitchRGB`
- `CRTScanline`
- `LightSweepLuxury`

Current transition recipes:

- `LightLeakTransition`
- `GlitchTransition`
- `ZoomPunch`
- `ShakeHit`
- `FlashPop`

Current template stacks:

- `Digicam Memory`
- `Polaroid Scrapbook`
- `Beat Photo Dump`
- `Film Strip`
- `Magazine Cover`
- `Before/After`

### Retired Names

The library intentionally keeps a retired-name set so old UI/API names do not accidentally re-enable broken presets.

Retired names include:

- `FilmGrain35mm`
- `VHSRetro`
- `GlitchRGB`
- `CRTScanline`
- `ChromaticAberrationSubtle`
- `LensVignette`
- `LightSweepLuxury`
- `LightLeakTransition`
- `GlitchTransition`
- `ZoomPunch`
- `ShakeHit`
- `FlashPop`
- `LightLeakSoft`
- `FilmBurnWarm`
- `SnowSoft`
- `SnowBokeh`
- `RainCinematic`
- `SparkleSoft`
- `ConfettiBurst`
- `BokehDream`
- `DustScratchRetro`

The engine still has compatibility/normalization logic, but the recipe registry is the canonical source of truth.

### Overlay Graph Builder

`EffectRecipeRenderSupport.cs` contains:

- `EffectRecipeBridge`
- `PostProcessEffectGraphBuilder`
- `OverlayEffectGraphBuilder`
- `TransitionRecipeGraphBuilder`

`OverlayEffectGraphBuilder` supports:

- looped sequence overlays
- asset overlays
- alpha assets
- black-background assets
- placement geometry
- timing windows
- fade in/out

### Post-Process Filters

`PostProcessEffectGraphBuilder` currently builds FFmpeg chains for:

- grain
- VHS
- glitch
- RGB split
- chromatic aberration
- vignette
- lens distortion
- CRT
- light sweep
- blur

### Transition Filters

`TransitionRecipeGraphBuilder` currently builds:

- zoom punch / blur zoom
- shake
- flash pop
- glitch
- light leak

### Legacy Light Leak Mapping

There is a bridge from old light-leak overlay naming to the current asset-only film burn preset.
That bridge is important because some older UI/API names still point here.

## Asset Probing And Background Detection

This is one of the most important pieces of the project.

### VideoAssetProbeInfo

`VideoAssetProbeInfo.cs` stores:

- file path
- exists
- probe success
- video stream presence
- file size
- last modified UTC
- width
- height
- duration
- fps
- codec name
- pixel format
- failure reason

### Overlay Background Analyzer

`OverlayAssetBackgroundAnalysis.cs` probes the real asset file and classifies it as:

- `Alpha`
- `GreenScreen`
- `BlackBackground`
- `Unknown`

Cache key includes:

- file path
- file size
- last modified UTC

That means if the user swaps the file contents in place, the engine will re-probe when size or timestamp changes.

Classification logic:

- alpha pixel formats go straight to `Alpha`
- sample frames are extracted at multiple times
- frames are scaled down for analysis
- green coverage is measured on full frame and border
- dark coverage is measured on full frame and border
- green-heavy assets become `GreenScreen`
- dark-heavy assets become `BlackBackground`
- unclear assets become `Unknown`

Pipeline selection:

- `Alpha` -> direct overlay
- `GreenScreen` -> chromakey/colorkey + overlay
- `BlackBackground` -> screen/lighten blend
- `Unknown` -> low-opacity screen blend with warning

### Why This Matters

Do not infer overlay type from file name.
The engine is designed to inspect the actual asset every render and choose the pipeline from real content.

This is especially important for:

- rain clips
- light leaks
- bokeh clips
- dreamy dot assets

## Overlay Families

### Snow

Snow is currently the most flexible overlay family.

Files and notes:

- `Assets\Overlays\Snow\snow_1.mp4`
- `Assets\Overlays\Snow\snow_2.mp4`
- `Assets\Overlays\Snow\README.txt`
- `Content\Overlays\Snow\snow_cinematic_01.mp4`
- `Content\Overlays\Snow\README.txt`

UI behavior:

- `Overlay 1` maps to `snow_1`
- `Overlay 2` maps to `snow_2`
- each toggle has its own opacity slider
- there is also a shared snow opacity slider for the non-2-toggle mode

Engine behavior:

- if an asset is present, snow can run as pre-rendered overlay
- if no asset is present, snow can fall back to generated particles
- the engine checks whether both toggles accidentally point to identical media and logs a warning

### Rain

Rain is asset-first.

Files and notes:

- `Assets\Overlays\Rain\rain_heavy_black_bg_01.mp4`
- `Assets\Overlays\Rain\README.txt`

The rain readme documents the preferred source clips and license notes.

Supported preset names:

- `Heavy Rain`
- `Cinematic Rain`
- `Window Drops`

Intensity:

- `subtle`
- `medium`
- `strong`

Rain pipeline behavior:

- the engine probes the real asset
- it classifies background type
- black background rain uses blend mode
- alpha rain uses overlay
- green-screen rain uses chromakey
- unknown assets default to low-opacity screen blending

Rain should fail fast if the asset is missing.
Do not replace it with procedural lines or particles.

### Light Leak Overlay

There are two related light leak systems and they must not be confused.

#### `LightLeakOverlayPreset`

This is the asset-based film burn / light leak overlay preset.

Current canonical asset:

- `Assets\Overlays\LightLeaks\light_leak_warm_01.mp4`

Current canonical preset:

- `FilmBurnWarm`

Behavior:

- asset-only
- burst timing
- black-background blend mode by default
- short fade in/out
- no procedural fallback

#### `LightLeakBurnPreset`

This is the light leak burn transition path.

Important current behavior:

- procedural burn path is disabled
- procedural single-clip graph is disabled
- generated layers are disabled
- the transition must use an asset-backed path

If asset rendering is missing, the code throws a hard error rather than silently faking the effect.

### Dreamy Dot

There are two dreamy-dot paths.

#### `DreamyDotOverlayPreset`

This is the generated dreamy dot / chaos overlay.

Behavior:

- WPF-generated particle frames
- generated PNG sequence in temp
- cached pre-render sequence
- chaos variant increases direction jitter and swirl

#### `DreamyDotOverlay2Preset`

This is the asset-based dreamy dot overlay.

Current asset:

- `Assets\Overlays\DreamyDots\dreamy_dot_overlay_02.mp4`

Behavior:

- asset-based only
- full-timeline subtle overlay
- blackBackgroundOrDarkBokeh handling
- screen/lighten blend path depending on detected background

UI / normalization note:

- generic dreamy-dot text is currently normalized toward the chaos overlay
- the asset-based `Dreamy Dot Overlay 2` is separate and should remain separate

### Borders / Frames

Generated border/frame presets:

- `PolaroidScrapbook2Preset.cs`
- `DashedPolaroidFramePreset.cs`
- `RgbPolaroidLedPreset.cs`
- `ScratchVideoPreset.cs`

These are WPF-drawn overlays that render to PNG sequences and then loop in FFmpeg.

Typical behavior:

- build overlay frames on STA thread
- save preview frames into temp
- render animated borders or paper-frame styling

### Particle Generator

`ParticleOverlayGenerator.cs` is the general cache-based particle pre-renderer.

Supported particle families from the recipe library:

- snow
- rain
- sparkle
- confetti

The generator:

- computes a cache key from recipe + size + fps + duration
- renders PNG frames
- writes a manifest
- reuses cached sequences when possible

This is the general mechanism behind generated particle overlays.

## UI Presets And Templates

### FX Studio Effect Dropdown

Current visible effect names include:

- `None`
- `Flip Horizontal (Anti-Copyright)`
- `Rò phim (Leak 1 / Light Leak)`
- `Light Leak Burn`
- `Cinematic Glow`
- `Halo Glow`
- `Prism Light`
- `Editorial Bloom`
- `Crop Video (Custom Frame)`

### Template Dropdown

Current visible template names include:

- `Smooth Zoom In`
- `Smooth Zoom Out`
- `Pan Left`
- `Pan Right`
- `Velocity Punch`
- `Cinematic Fade Zoom`
- `Glitch Distort`
- `3D Spin Lite`
- `Trend Zoom Flash`
- `Trend Blur Pulse`
- `Trend Spin Glitch`
- `Digicam Memory`
- `Beat Photo Dump`
- `Film Strip`
- `Magazine Cover`

### Border Dropdown

Current visible border names include:

- `None`
- `Polaroid Scrapbook`
- `Polaroid Scrapbook 2`
- `RGB Polaroid Border`
- `Dashed Polaroid Frame`

### Overlay Toggle Area

Current visible toggles:

- `Overlay 1`
- `Overlay 2`

These are tied to the snow asset pipeline and opacity controls.

## Audio System

The app has a real audio subsystem, not just a single audio file picker.

### Audio Editor Window

`AudioEditorWindow.xaml(.cs)` is a separate WPF editor with:

- preview timeline canvas
- play / stop / jump controls
- single trim mode
- segment cuts mode
- normalize / remove / clear operations
- save / cancel

The segment model is in `AudioEditModels.cs`.

### Audio Editing Logic

`AudioEditHelpers` provides:

- flexible time parsing
- time formatting
- segment normalization
- full-track detection

Segments are sorted and merged so overlapping cuts become a clean render list.

### Audio Mixing In Render

Important render paths:

- audio volume is applied through FFmpeg
- source audio speed is supported
- external audio speed is supported
- audio 1 and audio 2 can be mixed before the normal render pipeline
- audio concat/merge mode is separate and should not be confused with audio stacking

The mix helper uses:

- `amix`
- `aresample`
- `aformat`
- `alimiter`

## Local BAS API

### Core Request Model

`LocalhostRenderRequest` and related DTOs include:

- `inputFile`
- `inputFiles`
- `mergeVideos`
- `audioFiles`
- `mergeAudio`
- `audioFile`
- `audioFile2`
- `audioVolume`
- `audio2Volume`
- `secondaryAudioVolume`
- `watermarkFile`
- `templateName`
- `fxEffect`
- `overlay`
- `overlayEffect`
- `border`
- `borderEffect`
- `snowfallPreset`
- `snowfallMode`
- `snowfallOpacity`
- `snowOverlay1Opacity`
- `snowOverlay2Opacity`
- `rainOverlayPreset`
- `rainOverlayIntensity`
- `lightLeakBurn*`
- `imageTimelineDurationSeconds`
- `crop*`
- `hardwareProfile`
- `bitrateStrategy`
- `resolution`
- `framerate`
- `videoTrim*`
- `audioTrim*`
- `videoVolume`
- `sourceAudioSpeed`
- `externalAudioSpeed`
- `upscaleMode`
- `sharpnessIntensity`
- `slowMotionSpeed`
- `slowMotionAudio`
- `features`
- `extra fields` via `JsonExtensionData`

### Audio 1 / Audio 2 API

The current BAS guide and engine agree on:

- `audioFile` = Audio 1
- `audioFile2` = Audio 2
- `audioVolume` = Audio 1 volume
- `audio2Volume` or `secondaryAudioVolume` = Audio 2 volume

Behavior:

- if only `audioFile` exists, it is used as the external audio track
- if only `audioFile2` exists, it is used as the external audio track
- if both exist, the engine mixes them together first
- `audioFiles` + `mergeAudio` is concat/merge mode, not audio stacking

### BAS Guide

The guide is already updated to explain the current audio 1 / audio 2 behavior.

Key file:

- `BAS_LOCALHOST_API_GUIDE.txt`

The guide is also copied into release output.

### API Job State

The engine tracks both queue state and render state using:

- `ApiRenderJobState`
- `LocalhostQueueJobInfo`
- `LocalhostRenderResponse`

This lets BAS poll status and receive job metadata like source size, duration, overlay selection, and output paths.

## Build And Runtime Dependencies

### Required At Runtime

- `ffmpeg.exe`
- `ffprobe.exe`

Optional / detected when present:

- `upscayl\Upscayl.exe`
- `dlss\DLVSR.exe`
- NVIDIA NVENC support
- HEVC NVENC support

### Output Paths

The app uses two common output behaviors:

- manual UI render writes into `Titan_Output` under the chosen base folder
- API jobs can write to explicit output paths

The default base directory is resolved from the app base folder when the custom output folder is not available.

## Asset Folders

### Runtime Asset Folders

- `Assets\Overlays\Snow`
- `Assets\Overlays\Rain`
- `Assets\Overlays\LightLeaks`
- `Assets\Overlays\DreamyDots`

### Bundled Content Folders

- `Content\Overlays\Snow`
- `Content\Overlays\LightLeaks`
- `Content\Branding`

### Notable Bundled Media

- `Content\Branding\video-logo.png`
- `Content\Overlays\Snow\snow_cinematic_01.mp4`
- `Content\Overlays\LightLeaks\warm_film_burn_01.mp4`
- `Content\Overlays\LightLeaks\soft_bokeh_leak_01.mp4`
- `Content\Overlays\LightLeaks\mixkit_light_leaks_overlay_48011.mp4`
- `Assets\Overlays\Snow\snow_1.mp4`
- `Assets\Overlays\Snow\snow_2.mp4`
- `Assets\Overlays\Rain\rain_heavy_black_bg_01.mp4`
- `Assets\Overlays\LightLeaks\light_leak_warm_01.mp4`
- `Assets\Overlays\LightLeaks\pexels_light_leak_30042778.mp4`
- `Assets\Overlays\DreamyDots\dreamy_dot_overlay_02.mp4`
- `Assets\Overlays\DreamyDots\dreamy_dot_chaos_540x960_18_000.mp4`

### Asset README Notes

- `Assets\Overlays\Rain\README.txt` says rain is asset-only and must fail fast if missing
- `Assets\Overlays\Snow\README.txt` explains the 2-toggle snow asset mapping
- `Content\Overlays\Snow\README.txt` documents the bundled fallback snow clip
- `Content\Overlays\LightLeaks\README.txt` documents bundled light leak samples and sample source links

## Important Invariants

Keep these rules in mind when changing the engine:

- do not infer overlay background type from filename alone
- do not reintroduce procedural fallback for asset-only rain/light leak paths
- do not blur the base video unless it is background-only fit blur
- do not lose source audio unless explicitly disabled
- keep `audioFile2` separate from `audioFiles` concat mode
- keep image timeline sync logic before the effect stack
- keep overlay pre-render temp output isolated from final render output
- keep release guide and API docs in sync with real request fields
- keep `TitanEngine.csproj` copy rules aligned with actual runtime assets

## Debug And Test Artifacts

The repo contains a lot of generated validation output.

Useful to know, but not part of the app source:

- `TestRenders_EffectOverlay`
- `TestRenders_Visible`
- `temp_lightleak_contact.jpg`

These are helpful for smoke testing and visual comparison, but they should not be treated as runtime dependencies.

## Known Current Behaviors

- `Dreamy Dot Chaos Overlay` is generated and cacheable
- `Dreamy Dot Overlay 2` is asset-based and points at the Pexels bokeh clip
- `Rain Overlay` auto-detects black/alpha/green background and chooses a pipeline
- `FilmBurnWarm` light leak overlay is asset-based
- `Light Leak Burn` transition is asset-backed and procedural paths are disabled
- `Polaroid Scrapbook 2`, `Dashed Polaroid Frame`, `RGB Polaroid Border`, and `Scratch Video` are generated border/frame overlays
- the host API uses `HttpListener` on port `5555`
- batch mode and API mode are guarded so they do not render on top of each other at the same time

## Next-Agent Checklist

If you pick this project up next:

- start with `MainWindow.xaml.cs`
- check `EffectRecipeLibrary.cs` before adding or renaming any effect preset
- check `EffectRecipeRenderSupport.cs` before changing any FFmpeg filter graphs
- check `OverlayAssetBackgroundAnalysis.cs` before changing any asset pipeline
- check `BAS_LOCALHOST_API_GUIDE.txt` whenever API fields change
- update `TitanEngine.csproj` when adding any new runtime asset folder
- keep asset-only effects failing fast if the asset is missing
- keep the release copy rules and runtime asset paths aligned

This file is intentionally broad so the next agent can understand the app without re-reading the entire codebase first.
