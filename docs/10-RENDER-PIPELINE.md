# 10 — Render Pipeline

> Đọc kèm [`00-OVERVIEW.md`](00-OVERVIEW.md) để biết file nào làm gì.
> Mọi đường dẫn hàm dưới đây nằm trong `TitanEngine/MainWindow.xaml.cs` trừ khi ghi khác.

## 1. Điểm vào và thứ tự stage

`EngineCore.ExecuteRenderAsync(RenderJob job, …)` là pipeline duy nhất — **UI và API dùng chung**,
nên mọi fix trong này tự động áp cho cả hai.

```
1. Merge audio paths        (nếu MergeAudio)        → audio tạm (concat)
2. Mix Audio 1 + Audio 2    (nếu có cả hai)         → audio tạm (amix)
3. Apply audio edit         (nếu có segments)       → audio tạm (trim/volume/speed)
4. Caption Sync             (nếu bật)               → .ass
5. Template / image timeline                        → source video tạm
6. Merge video              (nếu bật)               → concat hoặc transition
7. AI upscale pre-pass      (nếu UpscaleMode ≠ Off) → video đã upscale
8. Build FFmpeg command     → filter_complex + encode
9. Output                   → Titan_Output/ (UI) hoặc đường dẫn API
```

Thứ tự này là lý do **image-timeline duration sync phải chạy trước effect stack**: mọi thứ sau đó
giả định timeline đã có độ dài cuối cùng.

## 2. Filter chain — thứ tự bắt buộc

```
[1] CROP                (nếu bật)
[2] SCALE / UPSCALE     external AI → internal FSRCNNX/libplacebo → scale lanczos → no-op
[3] MOTION              60fps (minterpolate) / RSMB (tmix)
[4] FX                  flip, glow, bloom, prism, polaroid base, transition/post-process recipe
[5] SLOW MOTION         setpts / atempo
[6] COLOR               color filter + intensity
[7] TEXT / CAPTION      drawtext hoặc ass=
[8] BRIGHTNESS          eq=brightness
[9] FADE                fade in/out
[10] OVERLAY            light leak burn, scratch, dreamy dot, particle, polaroid, snow, rain,
                        soundbar, watermark + brand logo (watermark luôn cuối)
[11] FORMAT             yuv420p → encoder
```

**Không đổi thứ tự nếu không có lý do đo được.** Đặc biệt: color sau FX (để LUT không ăn lên mask
overlay), watermark cuối cùng (để không bị effect làm mờ), soundbar trước watermark.

## 3. Duration master logic

| Trường hợp | Độ dài output |
|---|---|
| Có Audio 1 + source là **ảnh** | Audio 1 quyết định; image timeline cắt theo audio |
| Có Audio 1 + source là **video** | `min(video, audio1)` — không kéo dài video |
| Không có Audio 1 | `min(video, audio đã mix)` |

Slow motion được tính vào độ dài ước tính (`GetEstimatedOutputDurationSeconds`), và audio dùng
`atempo` chain để khớp.

## 4. Audio pipeline

### 4.1 Hai kiểu "nhiều audio" — đừng lẫn

| Cách | Field | Hành vi |
|---|---|---|
| **Stack** (phát chồng) | `audioFile` (Audio 1) + `audioFile2` (Audio 2) | `MixAudiosToTempTrackAsync`, `amix` |
| **Concat** (nối tiếp) | `audioFiles` + `mergeAudio` | `MergeAudiosToTempTrackAsync`, concat demuxer |

`audioFile2` **không** phải track thứ hai của concat. Nhầm hai cái này là bug im lặng.

### 4.2 Mix

```text
[0:a] volume=VideoVolume               [src_audio]
[1:a] atrim/atempo/volume=AudioVolume  [ext_audio]
[src_audio][ext_audio] amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95 [mixed]
```

- `normalize=0` là **bắt buộc**: mặc định `normalize=1` chia mỗi input cho số input, làm volume 200%
  thành 100% hiệu dụng.
- `alimiter=limit=0.95` chặn clip sau khi bỏ normalize.
- `RenderTemplateSegmentAsync` mix audio thật với `anullsrc` (silence) cũng phải có `normalize=0`,
  nhưng **không** thêm limiter (mix với silence không thể clip).

### 4.3 Volume / speed / edit

- Volume UI: 0–200% → clamp 0.0–2.0.
- Speed: `SourceAudioSpeed` / `ExternalAudioSpeed`, 0.10–8x, dùng chuỗi `atempo` (mỗi filter
  0.5–2.0, nối nhiều filter).
- Audio editor: `AudioEditSegment` → cắt segment, single trim, normalize. Volume **không** bake vào
  file edit; nó áp ở chain sau.

### 4.4 Caption Sync (Whisper)

1. Extract audio 1 → WAV 16 kHz mono, kèm EQ vocal.
2. `whisper-cli.exe` transcribe.
3. SRT → ASS.
4. Burn bằng `ass=file.ass:fontsdir=Fonts`.

Model: Small (mặc định) / Medium / Large-v3-Turbo, tự tải từ HuggingFace nếu thiếu.
Ngôn ngữ: auto / vi / en / ja / ko / zh.

## 5. Encoder selection

| Profile | Điều kiện | Encoder | Preset | Quality flag |
|---|---|---|---|---|
| Auto / khác | — | `libx264` | `veryslow` | `-crf 16` |
| NVIDIA | có `h264_nvenc`, target ≤ 4096px | `h264_nvenc` | `medium` | `-cq 16` |
| NVIDIA + AV1 | có `av1_nvenc` | `av1_nvenc` | `p6` | `-cq 20` |
| NVIDIA, target > 4096px | có `hevc_nvenc` | `hevc_nvenc` | `medium` | `-cq 18` |
| NVIDIA thiếu encoder | — | `libx264` | `slow` | `-crf 16` |
| AMD | — | `h264_amf` | `quality` | — |
| AMD + AV1 | có `av1_amf` | `av1_amf` | `quality` | — |
| Intel | — | `h264_qsv` | `veryslow` | — |

- Output pix fmt luôn `yuv420p`.
- libx264 nhận `-threads {FfmpegThreadCapCount()}` (3/4 số core) để tránh tranh CPU với filter.
- Filter graph nhận `-filter_threads` / `-filter_complex_threads` cùng mức cap.
- NVENC + `scale` biểu thức động (`eval=frame`, template zoom) **segfault 8/8** trên build ffmpeg
  gyan 2026-01-22. `RenderTemplateSegmentAsync` retry một lần bằng `libx264`.

## 6. Bitrate

| Strategy | Công thức |
|---|---|
| Match Source | `target` = bitrate nguồn; nếu thấp hơn mức tối thiểu theo resolution thì nâng lên. Args: `-minrate 0.7× -maxrate 1.5× -bufsize 4×` |
| Boost | `target × 1.5`; `-b:v target -minrate 0.8× -maxrate 1.3× -bufsize 2×` |
| Custom | `kbps × 1000`; `-b:v target -minrate 0.8× -maxrate 1.2× -bufsize 2×` |

Mức tối thiểu (`GetMinRecommendedBitrate`):

| Resolution | Bitrate |
|---|---|
| ≥ 4K (3840×2160) | 25 Mbps |
| ≥ 1440p | 12 Mbps |
| ≥ 1080p | 6 Mbps |
| ≥ 720p | 3.5 Mbps |
| SD và nhỏ hơn | 2 Mbps |

## 7. Upscale

Ba tầng: **NVIDIA DLSS (NGX DLVSR)** → **Upscayl** → **nội bộ (FSRCNNX/libplacebo)**.

```
UpscaleMode == Off / Off (Original Size)
   └── không chạy pre-pass

UpscaleMode != Off
   ├── 1. NVIDIA DLSS NGX (DLVSR.exe / UpscalePipelineApp.exe)
   │        fail → log, sang 2
   ├── 2. Upscayl (upscayl/Upscayl.exe, model realesrgan)
   │        fail → log, sang 3
   └── 3. Internal filter (FSRCNNX GLSL qua libplacebo)
```

- Pre-pass chạy **trước** filter chain và cập nhật `SourceW/H`.
- External fail **không** làm chết render — luôn còn internal fallback.
- Internal fail (thiếu shader / ffmpeg không có libplacebo) → render fail; đây là lỗi cấu hình.

### 7.1 Internal — chọn theo mode

| Mode | Nhánh | Filter chain |
|---|---|---|
| `Off` / `Off (Original Size)` | scale thường | `scale=W:H:flags=lanczos` |
| chứa `2x` | **Lightweight** | `libplacebo` FSRCNNX → `unsharp` nhẹ → `eq` nhẹ |
| chứa `4x` | **Heavy** | `deblock` → `libplacebo` FSRCNNX → `unsharp` mạnh → `eq` mạnh → `smartblur` |
| khác | Lightweight | như 2x |

Lightweight (2x):

```
libplacebo=w=W:h=H:custom_shader_path='shaders/FSRCNNX.glsl'
unsharp=lx=3:ly=3:la=(0.5 + sharpness×0.5)                              // 0.5–1.0
eq=contrast=(1.05 + sharpness×0.10):saturation=(1.0 + sharpness×0.10)   // 1.05–1.15
```

Heavy (4x):

```
deblock=filter=weak
libplacebo=w=W:h=H:custom_shader_path='shaders/FSRCNNX.glsl'
unsharp=lx=5:ly=5:la=(0.8 + sharpness×1.0)                              // 0.8–1.8
eq=contrast=(1.15 + sharpness×0.25):saturation=(1.10 + sharpness×0.20):brightness=0.02
smartblur=luma_radius=1.0:luma_strength=0.2
```

`sharpnessIntensity` mặc định `1.0`. Heavy chậm hơn đáng kể — dùng khi cần chất lượng, không cho batch lớn.

> **Lưu ý lịch sử:** header comment trong `MainWindow.xaml.cs` ghi “CAS algorithm” và
> “Hardcode Bitrate 25Mbps”, nhưng code hiện tại dùng `unsharp` và bitrate lấy theo
> `GetMinRecommendedBitrate` (25 Mbps chỉ khi ≥ 4K). Tin code, đừng tin header.

### 7.2 Auto resolution

Khi chọn 2x/4x, **multiplier áp lên target resolution, kể cả “Original”**:

```
2x + Original  → sourceW×2 × sourceH×2
4x + Original  → sourceW×4 × sourceH×4
2x + 1920x1080 → 3840x2160
```

- Ảnh + “Original” mặc định về **1080×1920 portrait** trước khi nhân.
- `AutoOrientResolution` giữ orientation gốc; không tự xoay ngang video dọc.
- Log: `[UPSCALE-MULTIPLIER]`, `[UPSCALE-APPLY]`, `[RESOLUTION-PARSE]`.

### 7.3 DLSS NGX (NVIDIA)

Backend hợp lệ:

```
[AppDir]\dlss\DLVSR.exe
[AppDir]\dlss\UpscalePipelineApp.exe
[AppDir]\dlss\UpscalePipelineApp\UpscalePipelineApp.exe
[AppDir]\dlss\...\build*\apps\UpscalePipelineApp\Release\UpscalePipelineApp.exe
```

Chọn hardware `NVIDIA NVENC`, chọn 2x/4x, render. Log:

```
[DLSS-NGX] DLVSR bridge: READY
[PREPROCESSING] Trying NVIDIA DLSS (NGX DLVSR)...
[DLSS-NGX] Success...
```

Auto download / build khi backend thiếu:

1. Tải public sample source từ `NVIDIA-Maxine/VFX-SDK-Samples` vào `dlss\NVIDIA_VFX_SDK_Samples.zip`.
2. Tạo `DLVSR_SETUP_README.txt` + `NVIDIA_VFX_AUTO_SETUP.ps1`.
3. Auto-cài CMake qua `winget` nếu thiếu.
4. Auto-build `UpscalePipelineApp.exe` **nếu** máy đã có VFX SDK Core + feature `nvvfxupscale`.

Giới hạn thực tế:

- Sample zip **không** chứa `DLVSR.exe` build sẵn.
- VFX SDK Core và `nvvfxupscale` đến từ **NVIDIA NGC**, không nằm trong zip GitHub.
- Cần CMake + Visual Studio C++ tools.
- Thiếu SDK → build dừng; README/script trong `dlss\` hướng dẫn phần còn lại.

Link chuẩn:

- Install guide: <https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/InstalltheVFXSDK.html>
- NGC collection: `maxine_windows_vfx_sdk_collection_ga`
- Sample source: <https://github.com/NVIDIA-Maxine/VFX-SDK-Samples>

`2x` → một pass 2x của backend. `4x` → **hai pass 2x** liên tiếp (không phải một pass 4x).
Output 4K source + 2x = 7680×4320 → encoder tự chuyển `hevc_nvenc`; nếu GPU encoder fail, retry
`libx264` (`[ENCODER-RETRY]`).

### 7.4 Upscayl

- Đường dẫn: `[AppDir]\upscayl\Upscayl.exe`.
- Gọi CLI: `-i input -o output -s {2|4} -m realesrgan`.
- Log: `[UPSCAYL]`, `[UPSCAYL-CMD]`, `[UPSCAYL-LOG]`, `[UPSCAYL-ERROR]`.
- Không có → log hướng dẫn đặt folder, fallback internal.

## 8. Overlay composition

Filter graph phức tạp, thứ tự cố định; mỗi nhánh có base riêng rồi mới stack overlay:

```
Overlay base:
  Light Leak Burn  → base burn → overlay stack → watermark
  RGB Polaroid     → base LED  → overlay stack → watermark
  Polaroid 2       → base P2   → overlay stack → watermark
  Dashed + Scratch → scratch → dashed → dreamy dot? → watermark
  Dashed only      → dashed base → overlay stack → watermark
  Standard         → overlay stack → watermark

Overlay stack (tuỳ bật):
  particle recipe → light leak asset → rain → dreamy dot → scratch
  → snow (multi-layer) → soundbar → watermark + brand logo
```

Quy tắc:

- **Asset-only**: Rain, Light Leak, Light Leak Burn. Thiếu asset = lỗi cứng, không fallback procedural.
- **Probe trước khi blend**: `OverlayAssetBackgroundAnalysis` quyết định `Alpha` / `GreenScreen` /
  `BlackBackground`; blend mode và keying phụ thuộc kết quả probe, không phụ thuộc tên file.
- **Soundbar** là overlay cuối trước watermark; xem [`20-SOUNDBAR.md`](20-SOUNDBAR.md).

## 9. Temp files & cleanup

Mọi artifact trung gian nằm trong `GetTitanTempDir()` và được track trong `tempTemplateArtifacts`:

```
concat_list.txt, merged_source.mp4, audio_concat.txt, merged_audio.*, audio_overlay_mix.*,
audio_edit.*, template_*.mp4/png, upscaled.*, overlay_*/frame_%04d.png, captions.ass,
cover-art .thumb.jpg
```

- Cleanup trong `finally`; luôn xoá kể cả khi job fail.
- **Không ghi scratch cạnh output.** `.thumb.jpg` từng sót cạnh MP4 và bị user báo là bug.
- Antivirus có thể khoá file lúc cleanup — deletion phải an toàn (bỏ qua lỗi), không làm fail job.

## 10. Error handling

`RunFfmpegCaptureAsync` trả `(ExitCode, Stderr)`; `AnalyzeFfmpegError` map lỗi thành gợi ý.

| Exit code | Nghĩa | Hướng xử lý |
|---|---|---|
| `0` | OK | Kiểm tra output tồn tại + decode được |
| `-1` | Process không start | `ffmpeg.exe` thiếu hoặc không execute được |
| `-2` | Input không đọc được | Sai đường dẫn / file bị xoá |
| `-22` | Invalid argument | Encoder/preset/filter syntax sai — đọc stderr |
| `126` | Permission denied | Quyền execute |
| `127` | Command not found | FFmpeg không có trong PATH |

Các pattern `-22` thường gặp:

| stderr | Nguyên nhân |
|---|---|
| `Unknown encoder 'h264_nvenc'` | FFmpeg build không có NVENC → dùng libx264 |
| `Option … not found` | Tham số filter sai |
| `Invalid data found` | File input hỏng |
| `split/overlay/amix` | Syntax filter_complex |
| `volume: value` | Volume ngoài range |
| `Unrecognized option 'enable=…,'` | `enable` phải là parameter (`:`), không phải filter nối bằng `,` |

**Fail-open cho tính năng phụ:** soundbar timeline gate, caption sync, overlay không quan trọng —
lỗi thì log WARN và render tiếp. **Fail-fast cho asset bắt buộc:** rain, light leak burn.

## 11. Log prefix

| Prefix | Stage |
|---|---|
| `[OUTPUT]` | Đường dẫn output |
| `[AUDIO-MERGE]` / `[AUDIO-OVERLAY]` / `[AUDIO-EDIT]` | Audio prep |
| `[TEMPLATE]` / `[TRANSITION]` / `[MERGE]` | Timeline |
| `[PREPROCESSING]` | Upscale ngoài |
| `[ENCODER]` / `[ENCODER-WARN]` / `[ENCODER-RETRY]` | Chọn/retry encoder |
| `[FILTER-*]` | Từng filter được thêm |
| `[FFMPEG-FILTER-COMPLEX]` / `[FFMPEG-COMBINED-FILTER-COMPLEX]` | Graph cuối |
| `[FFMPEG-MAP]` | Stream mapping |
| `[BITRATE-AUTO]` / `[UPSCALE-AUTO]` | Tự nâng bitrate / resolution |
| `[DLSS-NGX]` / `[DLSS-BUILD]` / `[UPSCAYL]` | Upscale backend |
| `[CAPTION-SYNC]` | Whisper |
| `[SOUNDBAR]` | Soundbar analysis / gate / cache |
| `[LIGHT-LEAK-BURN]` / `[SNOWFALL]` / `[RAIN]` / `[RGB-POLAROID]` | Overlay generation |

Khi debug: copy `[FFMPEG-CMD]` + `[FFMPEG-FILTER-COMPLEX]` ra chạy tay. Đó là cách nhanh nhất để
tách lỗi app khỏi lỗi graph.

## 12. Hiệu năng — những gì đã đo

| Thay đổi | Kết quả |
|---|---|
| Overlay blend `rgba` → `yuv420p` + `yuva420p` | job 156s: 60s → **16.4s**; CPU 180s → **34s** |
| `RenderTargetBitmap` mỗi khung → tái dùng | gen-2 GC 33/50 khung → **1/50** |
| audio-ring gom bucket | 1 485 object/khung → **~28** |
| Single-image fast path (`4019825`) | job 12s 1080×1920: 45s → **4.6s** |
| Thread cap 3/4 core | giảm tranh CPU, output không đổi |
| PNG cache theo SHA-256 | render lại cùng tham số gần như miễn phí |

Single-image fast path: một ảnh + nhạc ngoài, không template/merge/upscale/trim → bỏ hẳn bước
pre-render timeline (mất một generation nén), ảnh được scale trước một lần thay vì decode lại mỗi frame.

## 13. Bẫy

1. **`cmd.Insert(postInputInsertPoint, …)` dùng offset số.** Nếu `Replace` làm đổi độ dài command
   trước khi `Insert` chạy, phải cộng delta — bug exit `-22` thời r30.
2. **NVENC + dynamic scale = segfault.** Đã có CPU retry; đừng bỏ retry khi refactor.
3. **`amix` thiếu `normalize=0`** → volume giảm một nửa. Đã sửa 34 chỗ; thêm chỗ mới phải theo.
4. **Thumbnail/scratch cạnh output** → user thấy file rác. Luôn dùng temp dir.
5. **Duration sync đặt sau effect** → ảnh bị cắt sai, audio lệch.
6. **`-pix_fmt` sai** → overlay mất alpha hoặc encode chậm gấp 5 lần.
7. **Upscale mode dùng `Contains("2x"/"4x")`** — thêm mode mới phải khớp cả `BuildAdvancedScaleFilter`
   và chỗ tính `upscaleMultiplier`.
8. **4x ≠ một pass 4x cho NGX** — là hai pass 2x.
9. **Auto resolution áp cho mọi preset**, không chỉ “Original”.
10. **`sharpnessIntensity` ảnh hưởng cả unsharp lẫn eq** — đừng chỉ sửa một chỗ.
11. **External upscale là pre-pass**, không nằm trong filter chain; đổi thứ tự sẽ mất 1 generation.
