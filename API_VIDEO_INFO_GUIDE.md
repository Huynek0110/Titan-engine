# TITAN ENGINE - API VIDEO INFORMATION GUIDE

## ✅ Thay đổi Mới (Updated)

API `/api/render` bây giờ **trả về thông tin video chi tiết** khi render hoàn thành, thay vì im lặng.

---

## 📋 RESPONSE FIELDS (Thông tin Video)

### Video Identity
- **videoId** (string) - Unique Job ID (8 ký tự hex, ví dụ: "ABCDEF12")
- **SourceFileName** (string) - Tên file video gốc (ví dụ: "input.mp4")
- **OutputFileName** (string) - Tên file video output (ví dụ: "final.mp4")

### Source Video Info (Video Gốc)
- **SourceDuration** (double) - Độ dài video gốc (giây)
  - Ví dụ: 120.5 (= 120.5 giây)
- **SourceWidth** (int) - Chiều rộng gốc (pixel)
  - Ví dụ: 1920
- **SourceHeight** (int) - Chiều cao gốc (pixel)
  - Ví dụ: 1080
- **SourceBitrate** (long) - Bitrate gốc (bits/second)
  - Ví dụ: 5000000 (= 5 Mbps)
- **SourceFramerate** (double) - Frame rate gốc (fps)
  - Ví dụ: 30.0, 60.0

### Output Video Info (Video Output)
- **OutputWidth** (int) - Chiều rộng output (pixel)
  - Ví dụ: 1920 (nếu không custom = -1)
- **OutputHeight** (int) - Chiều cao output (pixel)
  - Ví dụ: 1080 (nếu không custom = -1)
- **OutputFramerate** (string) - Frame rate output
  - Ví dụ: "Original", "30", "60", "24"
- **OutputFileSize** (long) - Kích thước file output (bytes)
  - Ví dụ: 150000000 (= 150 MB)

### Processing Info
- **EncodingTime** (double) - Thời gian render (giây)
  - Ví dụ: 45.3 (= 45.3 giây)

---

## 📊 EXAMPLE RESPONSE (JSON)

### 1️⃣ Blocking Mode (`POST /api/render`)

**Khi render HOÀN THÀNH:**

```json
{
  "success": true,
  "queueId": 1,
  "message": "Render completed",
  "jobId": "ABCDEF12",
  "status": "completed",
  "done": true,
  "jobSuccess": true,
  "progress": 100,
  "requestedOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",
  "engineOutput": "C:\\Users\\lemin\\Desktop\\TitanEngine\\Titan_Output\\input_Titan.mp4",
  "finalOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",
  "statusUrl": "http://localhost:5555/api/render/status?jobId=ABCDEF12",

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

### 2️⃣ Async Mode (`POST /api/render/async`)

**Phản hồi ngay:**

```json
{
  "success": true,
  "queueId": 1,
  "message": "Accepted. Poll statusUrl for completion",
  "jobId": "ABCDEF12",
  "status": "queued",
  "done": false,
  "jobSuccess": false,
  "progress": 0,
  "requestedOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",
  "statusUrl": "http://localhost:5555/api/render/status?jobId=ABCDEF12",

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
  "outputFramerate": "30"
}
```

### 3️⃣ Status Check (`GET /api/render/status?jobId=ABCDEF12`)

**Khi render ĐANG DIỄN RA:**

```json
{
  "success": true,
  "queueId": 1,
  "message": "Processing",
  "jobId": "ABCDEF12",
  "status": "processing",
  "done": false,
  "jobSuccess": false,
  "progress": 45.5,
  "requestedOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",
  "engineOutput": "C:\\Users\\lemin\\Desktop\\TitanEngine\\Titan_Output\\input_Titan.mp4",

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
  "outputFramerate": "30"
}
```

**Khi render HOÀN THÀNH:**

```json
{
  "success": true,
  "queueId": 1,
  "message": "Render completed",
  "jobId": "ABCDEF12",
  "status": "completed",
  "done": true,
  "jobSuccess": true,
  "progress": 100,
  "requestedOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",
  "engineOutput": "C:\\Users\\lemin\\Desktop\\TitanEngine\\Titan_Output\\input_Titan.mp4",
  "finalOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",

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

---

## 🔍 CÁC TRƯỜNG HỢP

### ❌ LỖI (Render THẤT BẠI)

```json
{
  "success": false,
  "queueId": 1,
  "message": "Error: Input file corrupted",
  "jobId": "ABCDEF12",
  "status": "failed",
  "done": true,
  "jobSuccess": false,
  "progress": 32.5,
  "requestedOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",
  "engineOutput": null,

  "videoId": "ABCDEF12",
  "sourceFileName": "input.mp4"
}
```

### ⏸️ CANCELLED (Render BỊ HỦY)

```json
{
  "success": false,
  "queueId": 1,
  "message": "Request cancelled",
  "jobId": "ABCDEF12",
  "status": "failed",
  "done": true,
  "jobSuccess": false,
  "progress": 45.0,
  "requestedOutput": "C:\\Users\\lemin\\Desktop\\output\\final.mp4",

  "videoId": "ABCDEF12",
  "sourceFileName": "input.mp4"
}
```

---

## 🎯 SỬ DỤNG TRONG BAS

### Trích xuất Video ID

```javascript
// Sau khi POST /api/render thành công:
let response = JSON.parse(responseBody);
let videoId = response.videoId;  // "ABCDEF12"
let duration = response.sourceDuration;  // 120.5
let width = response.sourceWidth;  // 1920
let height = response.sourceHeight;  // 1080
```

### Lưu trữ Metadata

```javascript
// Lưu thông tin render vào database/file
let renderInfo = {
  videoId: response.videoId,
  sourceFile: response.sourceFileName,
  outputFile: response.outputFileName,
  duration: response.sourceDuration,
  resolution: response.sourceWidth + "x" + response.sourceHeight,
  outputResolution: response.outputWidth + "x" + response.outputHeight,
  encoding_time: response.encodingTime,
  output_size_mb: (response.outputFileSize / 1024 / 1024).toFixed(2)
};
```

### Polling Status

```javascript
// Poll status dengan video info
async function checkStatus(jobId) {
  let response = await fetch("http://localhost:5555/api/render/status?jobId=" + jobId);
  let data = await response.json();

  console.log(`Progress: ${data.progress}% (${data.sourceFileName})`);

  if (data.done) {
    console.log(`✓ Done in ${data.encodingTime}s`);
    console.log(`Output: ${data.outputFileSize} bytes`);
  }
}
```

---

## 📝 LƯU Ý

1. **videoId** = **jobId** (cùng 8 ký tự)
2. **outputWidth/Height = -1** nếu dùng "Original" resolution
3. **EncodingTime** chỉ có khi **done=true**
4. **OutputFileSize** chỉ có khi **finalOutput** tồn tại
5. Tất cả paths là **absolute Windows paths** (C:\path\to\file.mp4)

---

## ✨ BỌ CHỨA

✅ Nếu render thành công → Có tất cả video info (duration, resolution, bitrate, fps, file size, encoding time)

✅ Nếu render đang diễn ra → Có video info (duration, resolution, fps) nhưng không có outputFileSize/encodingTime

✅ Nếu render lỗi → Có videoId + sourceFileName nhưng thiếu output info

---

**Bây giờ BAS có thể theo dõi video metadata trong quá trình render! 🎬**
