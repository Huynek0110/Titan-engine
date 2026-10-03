# ?? TITAN ENGINE - FFmpeg ERROR DEBUGGING GUIDE

## ?? Overview
Khi FFmpeg fail, Titan Engine s? capture ??y ??:
1. **Exit Code** - mã l?i t? FFmpeg
2. **Full Stderr Output** - toàn b? log t? FFmpeg
3. **Error Analysis** - phân tích chi ti?t l?i là gì

## ?? Exit Code Mapping

### **Exit Code: -22 (Invalid Argument)** ??
```
[FFMPEG-ERROR-CODE] Exit Code: -22
[FFMPEG-ERROR-DETAILS] Unknown encoder 'h264_nvenc' / Invalid parameter ...
[FFMPEG-ERROR-ANALYSIS] ERROR -22: Invalid encoder preset. Check -preset value.
```

**Nguyên nhân & Cách s?a:**

| L?i | Nguyên nhân | Cách s?a |
|-----|-----------|---------|
| Unknown encoder | Encoder không ???c support | Ch?n CPU (libx264) thay vì NVIDIA |
| Invalid filter option | Cú pháp filter sai | Ki?m tra filter_complex syntax |
| Unrecognized option | Tham s? FFmpeg không h?p l? | Review FFmpeg command line |
| Invalid data found | File input b? corrupt | Ch?n file video khác |
| Could not find codec | Codec không ???c cài ??t | Cài ??t FFmpeg ??y ?? |
| Invalid scale filter | Tham s? scale sai | Format: `scale=width:height:flags=lanczos` |
| Invalid fps filter | FPS không h?p l? | Format: `fps=30` (s? nguyên) |
| Invalid enable clause | Enable syntax sai | Format: `enable='between(t,start,end)'` (dùng `:` không ph?i `,`) |
| Invalid volume | Volume < 0.1 | Clamp volume: min=0.1, max=2.0 |

---

### **Exit Code: -1 (Process Failed)**
```
[FFMPEG-ERROR-CODE] Exit Code: -1
[FFMPEG-ERROR-ANALYSIS] ERROR -1: FFmpeg process failed to start.
```
**? FFmpeg.exe không tìm th?y ho?c không execute ???c**
**? S?a:** Copy `ffmpeg.exe` và `ffprobe.exe` vào folder TitanEngine

---

### **Exit Code: -2 (Input Not Found)**
```
[FFMPEG-ERROR-CODE] Exit Code: -2
[FFMPEG-ERROR-ANALYSIS] ERROR -2: Input file not found or cannot be read.
```
**? File video/audio input không t?n t?i**
**? S?a:** Ki?m tra ???ng d?n file, ??m b?o file ch?a b? xóa/move

---

### **Exit Code: 127 (Command Not Found)**
```
[FFMPEG-ERROR-CODE] Exit Code: 127
```
**? FFmpeg.exe không tìm th?y trong PATH**
**? S?a:** Ch?n manual `ffmpeg.exe` t? dialog, ho?c copy vào app folder

---

### **Exit Code: 126 (Permission Denied)**
```
[FFMPEG-ERROR-CODE] Exit Code: 126
```
**? FFmpeg.exe tìm th?y nh?ng không có quy?n execute**
**? S?a:** Ki?m tra file permissions, ch?y app as Administrator

---

## ?? Real-world Error Examples

### ? Example 1: Filter Syntax Error
```
[FFMPEG-ERROR-DETAILS]
Option split not found.
[FFMPEG-ERROR-ANALYSIS] ERROR -22: Invalid filter_complex syntax. Check filter chain.
```
**L?i:** Filter chain syntax sai (d?u `,` thay vì `;` ho?c parameter format)
**S?a:** Review `BuildFxFilterChain()` function - check d?u `:` vs `,`

---

### ? Example 2: Invalid Volume Value
```
[FFMPEG-ERROR-DETAILS]
Option volume: value 0.0 is invalid
[FFMPEG-ERROR-ANALYSIS] ERROR -22: Invalid volume filter value. Must be between 0.1 and 2.0.
```
**L?i:** Volume = 0.0 (slider ? 0%)
**S?a:** ? ?ã fix - volume t? ??ng clamp min=0.1

---

### ? Example 3: Encoder Not Supported
```
[FFMPEG-ERROR-DETAILS]
Unknown encoder 'h264_nvenc'
[FFMPEG-ERROR-ANALYSIS] ERROR -22: Unknown video encoder. Check -c:v codec name.
```
**L?i:** NVIDIA NVENC không ???c support (FFmpeg không có NVIDIA support)
**S?a:** 
- Ch?n "Auto (CPU)" thay vì NVIDIA
- Ho?c cài FFmpeg v?i NVIDIA support

---

### ? Example 4: Enable Clause Syntax
```
[FFMPEG-ERROR-DETAILS]
Unrecognized option 'enable='between(t,0.00,1.34)','.
[FFMPEG-ERROR-ANALYSIS] ERROR -22: Invalid enable clause in filter. Must be: enable='between(t,start,end)'
```
**L?i:** Enable clause có d?u `,` thay vì `:`
**S?a:** ? ?ã fix - s? d?ng `:` ?? append enable clause

---

## ??? Debugging Workflow

### Step 1: ??c Log Message
```
[FFMPEG-ERROR-CODE] Exit Code: -22
[FFMPEG-ERROR-DETAILS] <ffmpeg stderr output>
[FFMPEG-ERROR-ANALYSIS] <machine-parsed analysis>
```

### Step 2: Tìm Error Pattern
| Pattern | Nguyên nhân |
|---------|-----------|
| "Unknown encoder" | Encoder không support |
| "Option ... not found" | Tham s? filter sai |
| "Invalid data found" | File corrupt |
| "split/overlay/amix" | Filter syntax sai |
| "volume: value" | Volume range invalid |

### Step 3: Review FFmpeg Command
```
[FFMPEG-CMD] -y -i "input.mp4" -c:v libx264 ... -filter_complex "..." -map ... -c:a aac ...
```
Ki?m tra:
- T?t c? input files t?n t?i
- Encoder ???c support
- Filter syntax ?úng
- Các tham s? h?p l?

### Step 4: Fix & Re-run
1. S?a l?i theo error analysis
2. Test l?i v?i video khác (?? lo?i tr? file corrupt)
3. Xem log ?? confirm s?a ?úng

---

## ?? Key Fixes Implemented

### ? Fix 1: Enable Clause Syntax (Exit Code -22)
```csharp
// ? SAI: boxblur=lr=3:lb=3,enable='between(t,0.00,1.34)'
// ? ?ÚNG: boxblur=lr=3:lb=3:enable='between(t,0.00,1.34)'
```
Enable ph?i là **parameter** c?a filter (dùng `:`), không ph?i filter riêng (`,`)

### ? Fix 2: Volume Range Clamp
```csharp
// ? SAI: volume=0.0 ? FFmpeg reject
// ? ?ÚNG: volume=Math.Max(0.1, Math.Min(2.0, value))
```
FFmpeg volume: min=0.0+ nh?ng th?c t? min=0.1 ?? safe

### ? Fix 3: Full Error Capture
```csharp
// Capture full stderr output ? log ? analyze ? suggest fix
stderrOutput.AppendLine(e.Data);  // Accumulate stderr
string errorSummary = AnalyzeFfmpegError(exitCode, stderrOutput);
```

---

## ?? Testing FFmpeg Command Manually

?? test FFmpeg command tr?c ti?p (không qua Titan):
```powershell
cd "path\to\ffmpeg"

# Test 1: Simple copy
ffmpeg -i "input.mp4" -c copy "output.mp4"

# Test 2: With filter
ffmpeg -i "input.mp4" -filter_complex "[0:v]boxblur=lr=3:lb=3:enable='between(t,0.00,4.33)'[v_proc];[0:a]volume=0.1[aout]" -map "[v_proc]" -map "[aout]" "output.mp4"

# Test 3: With encoder
ffmpeg -i "input.mp4" -c:v libx264 -preset veryslow -c:a aac "output.mp4"
```

---

## ?? Common Error Patterns & Solutions

| Pattern | Cause | Solution |
|---------|-------|----------|
| `split[a][b][c]` error | RGB Glitch filter too complex | Simplify or skip this FX |
| `Unknown encoder h264_nvenc` | NVIDIA not available | Use libx264 (CPU) |
| `volume: value 0.0` | Slider at 0% | Already clamped to 0.1 |
| `enable='between(t,...),'` | Comma instead of colon | Use `:` separator |
| `Option ... not found` | Filter parameter invalid | Check FFmpeg syntax |
| Invalid scale/fps values | Non-numeric or out-of-range | Validate before passing |

---

## ?? Quick Fix Checklist

- [ ] Is `ffmpeg.exe` in the correct folder?
- [ ] Are input files valid MP4/WAV/PNG?
- [ ] Is the video encoder supported? (libx264 always works, NVIDIA need check)
- [ ] Is filter_complex syntax valid? (`:` for params, `;` for chain, `,` for next filter)
- [ ] Is volume between 0.1 and 2.0?
- [ ] Is enable clause formatted correctly? `enable='between(t,start,end)'`
- [ ] Run FFmpeg command manually to isolate issue?

---

## ?? Report Bug
Khi report bug, hãy include:
1. **Full log output** (especially [FFMPEG-ERROR-DETAILS])
2. **[FFMPEG-CMD]** line (full command)
3. **Input file info** (format, codec, duration)
4. **Settings used** (encoder, filters, effects)

---

**Version:** Titan Engine v88.0-FX-STUDIO-MASTER-DEBUG
**Last Updated:** 2025 Q1
