# 30 — Local HTTP API

> Server nội bộ cho BAS / automation. Hợp đồng đầy đủ (mọi field, mọi ví dụ) nằm ở
> **`TitanEngine/BAS_LOCALHOST_API_GUIDE.txt`** — file này là bản chuẩn và được csproj đóng gói.
> Tài liệu dưới đây là bản đồ để định hướng, không thay thế guide đó.

## 1. Khởi động

- `HttpListener` tại `http://127.0.0.1:5555/` (đổi port qua `PortInputWindow`, mặc định 5555).
- Bật/tắt bằng nút **HOST** trong UI. Không bật → mọi request trả `503 Host mode is OFF`.
- JSON **camelCase**; request/response dùng `PropertyNameCaseInsensitive = true`.
- CORS: `Access-Control-Allow-Origin: *`; `OPTIONS` được trả riêng cho preflight.
- Không chạy song song với batch UI: đang render UI mà gọi API → `409 UI batch is running`.
- Đang render request khác → `429 Host is busy rendering another request`.

## 2. Endpoints

| Method | Path | Việc |
|---|---|---|
| `POST` | `/api/render` | Render **blocking** (giữ connection tới khi xong) |
| `POST` | `/api/render/async` | Trả ngay `jobId`, render nền |
| `POST` | `/api/render/wait` | Render rồi chờ tới khi xong |
| `GET` | `/api/render/status?jobId=…` | Poll trạng thái / progress / metadata |
| `POST` | `/api/queue/add` | Thêm job vào queue, trả `queueId` + `jobId` |
| `POST` | `/api/queue/start` | Bắt đầu chạy queue |
| `GET` | `/api/soundbar/presets` | Danh sách style/palette soundbar để client chọn |
| `OPTIONS` | bất kỳ | CORS preflight |

Status code thường gặp:

| Code | Nghĩa |
|---|---|
| `200` | OK |
| `400` | Request sai (thiếu `inputFile`, field không hợp lệ…) |
| `404` | Endpoint / `jobId` không tồn tại |
| `405` | Sai method |
| `409` | Queue đang chạy / UI batch đang chạy |
| `429` | Host đang bận render request khác |
| `499` | Request bị cancel |
| `503` | Host mode OFF |

## 3. Request — các nhóm field chính

Tất cả field đều nullable; chỉ `inputFile` (hoặc `inputFiles` khi merge) là bắt buộc.

### Nguồn & timeline

```
inputFile, inputFiles[], mergeVideos
imageTimelineDurationSeconds
videoTrimStart, videoTrimDuration, videoTrimEnd
templateName, enableCrossTransitions, crossTransitionType, crossTransitionDuration
```

### Audio

| Field | Nghĩa |
|---|---|
| `audioFile` | **Audio 1** (track chính) |
| `audioFile2` | **Audio 2** (stack chồng lên Audio 1) |
| `audioFiles[]` + `mergeAudio` | **Concat** nhiều track — khác hoàn toàn `audioFile2` |
| `videoVolume`, `audioVolume`, `audio2Volume` / `secondaryAudioVolume` | 0.0–2.0 |
| `sourceAudioSpeed`, `externalAudioSpeed` | 0.1–8.0 |
| `audioTrimStart/End/Duration`, `audioSegments[]` | trim / audio editor |
| `enableLoudnorm` | loudnorm sau mix |

Alias được chấp nhận: `audioVol`, `musicVolume`, `audio2Vol`.

### Hình ảnh / effect

```
fxEffect, overlayEffect, borderEffect, fxIntensity, fxStartPercent, fxEndPercent, enableFade
cropEnabled, cropZoomPercent, cropAspectRatio, cropXPercent, cropYPercent, cropWPercent, cropHPercent
colorFilter, colorIntensity, videoBrightness
upscaleMode, sharpnessIntensity
enable60fps, rsmbIntensity, slowMotionSpeed, slowMotionAudio
watermarkFile + watermarkScale/Rotation/Opacity/XPercent/YPercent/AspectRatio
```

### Overlay

```
snowfallPreset, snowfallMode, snowfallOpacity
snowOverlay1Opacity, snowOverlay2Opacity, snowOverlay3Opacity
rainOverlayPreset, rainOverlayIntensity
lightLeakBurn*, dreamyDot*, scratch*
enableOverlayIntro, enablePolaroidScrapbook, enableRgbPolaroidScrapbook,
enableDashedPolaroidScrapbook, enableBrandLogo, brandLogoPosition
```

### Text / caption

```
textOverlay, textOverlayPosition, textAlign, textOverlayFontSize, textOverlayColor,
textOverlayStyle, addQuotes, disableTextScroll, enableTextGlow, textGlowColor
enableCaptionSync, captionSyncLanguage, captionSyncModelSize, captionSyncAnimation, …
```

### Soundbar

| Field | Default | Ghi chú |
|---|---|---|
| `enableSoundBar` | `false` | `enableAudioSpectrum` là alias cũ; **`enableSoundBar` thắng** nếu cả hai có |
| `soundBarStyle` | `neon-bars` | stable id, xem `/api/soundbar/presets` |
| `soundBarPalette` | `ncs-cyan` | `audio-ring` mặc định `ncs-house-yellow` |
| `soundBarXPercent` / `soundBarYPercent` | `0.50` / `0.82` | toạ độ tâm |
| `soundBarWidthPercent` / `soundBarHeightPercent` | `0.62` / `0.26` | kích thước |
| `soundBarOpacity` | `1.0` | |
| `soundBarSensitivity` / `soundBarSmoothing` | `1.0` / `1.0` | |
| `soundBarBarCount` / `soundBarRingCount` | `64` / `2` | |
| `soundBarRoundness` / `soundBarGlowIntensity` | `1.0` / `1.0` | |
| `soundBarLoopSeconds` | `5.0` | |
| `soundBarAudioSource` | `auto` | `auto` / `external` / `video` / `audio1` / `audio2` |

Token nguồn audio còn nhận: `voice`, `vocal`, `a1`, `a2`.

### Output / API

```
outputVideo (đường dẫn đích), outputFolder, outputName
hardwareProfile, resolution, framerate, bitrateStrategy, customBitrate
isApiJob, webhookUrl
```

## 4. Response

`LocalhostRenderResponse`:

```jsonc
{
  "success": true,
  "queueId": 1,
  "totalJobs": 1,
  "startedJobs": 1,
  "message": "Render completed",
  "jobId": "ABCDEF12",
  "status": "completed",      // queued | processing | completed | failed
  "done": true,
  "jobSuccess": true,
  "progress": 100,
  "statusUrl": "http://localhost:5555/api/render/status?jobId=ABCDEF12",
  "requestedOutput": "C:\\out\\final.mp4",
  "engineOutput": "…\\Titan_Output\\input_Titan.mp4",
  "finalOutput": "C:\\out\\final.mp4",

  "videoId": "ABCDEF12",
  "sourceFileName": "input.mp4",
  "outputFileName": "final.mp4",
  "sourceDuration": 120.5,
  "sourceWidth": 1920,
  "sourceHeight": 1080,
  "sourceBitrate": 5000000,
  "sourceFramerate": 30.0,
  "outputWidth": 1920,
  "outputHeight": 1080,
  "outputFramerate": "30",
  "outputFileSize": 150000000,
  "encodingTime": 45.3
}
```

Quy ước:

- `videoId` **=** `jobId` (8 ký tự hex).
- `outputWidth/Height = -1` khi resolution là “Original”.
- `outputFileSize` / `encodingTime` chỉ có khi `done = true` và `finalOutput` tồn tại.
- Path luôn là absolute Windows path.
- `engineOutput` là file engine tạo; `finalOutput` là file đã copy tới chỗ client yêu cầu
  (nếu có `outputVideo`).

### Webhook

Nếu request có `webhookUrl`, khi job `done` server POST status JSON tới URL đó một lần.
Lỗi webhook chỉ log `[API-WEBHOOK-ERROR]`, **không** làm fail job.

## 5. Ví dụ

### Blocking

```bash
curl -X POST http://localhost:5555/api/render \
  -H "Content-Type: application/json" \
  -d "{\"inputFile\":\"C:\\\\in\\\\a.mp4\",\"outputVideo\":\"C:\\\\out\\\\a.mp4\",\"enableSoundBar\":true}"
```

### Async + poll

```jsonc
// POST /api/render/async
{ "inputFile": "C:\\in\\a.mp4", "hardwareProfile": "NVIDIA NVENC" }
// → { "jobId": "ABCDEF12", "status": "queued", "statusUrl": "…" }

// GET /api/render/status?jobId=ABCDEF12
// → { "status": "processing", "progress": 45.5, "done": false }
```

### Queue

```jsonc
// POST /api/queue/add  → { "queueId": 1, "jobId": "…", "status": "queued" }
// POST /api/queue/start
// GET  /api/render/status?jobId=…   (poll từng job)
```

## 6. `/api/soundbar/presets`

`GET` trả danh sách style (id, tên, mô tả, default bar count, cờ `usesFft/usesWaveform/usesOnset/usesRms`)
và palette. Client nên lấy từ đây thay vì hardcode — danh sách style là nguồn sự thật duy nhất.

## 7. Bẫy

1. **`audioFile2` không phải track thứ hai của concat.** Dùng `audioFiles[]` + `mergeAudio` để nối.
2. **`enableAudioSpectrum` là alias cũ.** Trước đây được parse rồi bỏ, nên client gửi `true` nhận
   render không có gì. Nay đã nối; nếu gửi cả hai, `enableSoundBar` thắng.
3. **Host mode phải bật.** `503` không phải lỗi payload.
4. **Đừng chạy API song song với batch UI** — `409`.
5. **Đổi field API phải sửa cả `MainWindow.Models.cs` và `BAS_LOCALHOST_API_GUIDE.txt`.**
6. **`BAS_LOCALHOST_API_GUIDE.txt` ở repo root là bản cũ, đã lệch.** Bản được đóng gói là
   `TitanEngine/BAS_LOCALHOST_API_GUIDE.txt`.
