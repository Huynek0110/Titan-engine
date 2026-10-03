# WORK CONTEXT — Sửa lỗi âm lượng Audio (200% không to)

> File ghi chú ngữ cảnh công việc đang làm. Đọc lại file này nếu context bị reset.

## Task đang làm

**Người dùng báo lỗi:** Ở UI, kéo thanh **Audio 1 lên 200%** nhưng âm lượng gần như không tăng, không đúng như 200%. Yêu cầu fix cả **UI lẫn API**.

## Nguyên nhân gốc (đã xác định)

FFmpeg filter **`amix` mặc định có `normalize=1`** → khi mix 2 input, mỗi input bị chia 2 (1/N).

Trong pipeline chính (`EngineCore.ExecuteRenderAsync`), volume được áp vào từng chain trước amix:
- `[0:a]volume=1.0...,[src_audio]` (audio video gốc, dùng VideoVolume)
- `[1:a]volume=2.0...,[ext_audio]` (Audio 1 = 200%)
- `[src_audio][ext_audio]amix=inputs=2:duration=shortest[mixed_audio]`

Kết quả với normalize=1: `2.0 × 0.5 = 1.0` → **200% bị giảm về 100% hiệu dụng**. Đúng với triệu chứng người dùng mô tả.

## Đã kiểm tra (không phải nguyên nhân)

- UI slider `sldAudioVol`/`sldAudio2Vol`/`sldVideoVol`: Minimum=0, Maximum=200 → `Value/100.0` = tối đa 2.0 ✓ (MainWindow.xaml dòng ~797/803/809)
- UI: `AudioVolume = sldAudioVol.Value / 100.0` ✓ (MainWindow.xaml.cs:12759-12760)
- API: `audioVolume` clamp 0–2.0, alias `audioVol/musicVolume`, Audio2: `audio2Volume/secondaryAudioVolume/audio2Vol` ✓ (MainWindow.xaml.cs:14214-14234)
- `MixAudiosToTempTrackAsync` (mix Audio1+Audio2 sẵn): ĐÃ có `normalize=0` + `alimiter=limit=0.95` ✓ (dòng ~10280)
- `BuildSourceAudioInputFilterChain`/`BuildExternalAudioInputFilterChain`: volume được áp đúng, chỉ clamp 0–2.0 ✓
- `PrepareEditedAudioTrackAsync` (audio edit): không bake volume, volume áp ở chain sau ✓
- `TitanTransitionGraphBuilder`: dùng `acrossfade`, không bị bug amix ✓

## Các thay đổi đã làm

### FIX 1 — ĐÃ HOÀN THÀNH (MainWindow.xaml.cs)

Thay **toàn bộ 34 chỗ** trong pipeline render chính:

- **Trước:** `amix=inputs=2:duration=shortest[mixed_audio]`
- **Sau:** `amix=inputs=2:duration=shortest:normalize=0,alimiter=limit=0.95[mixed_audio]`

(Áp bằng `replaceAll` trong edit tool — 1 lần thay, đã xác nhận "Edit applied successfully")

Lý do có `alimiter=limit=0.95`: khi bỏ normalize, tổng 2 track có thể vượt 0dB → limiter chặn clip, giống hệt cách `MixAudiosToTempTrackAsync` đang làm.

### FIX 2 — CHƯA LÀM (MainWindow.xaml.cs dòng ~9753)

`RenderTemplateSegmentAsync` còn 1 chỗ amix mix **audio thật + anullsrc (silence)**:
- **Trước:** `[a0][a1]amix=inputs=2:duration=first:dropout_transition=0[aout]`
- **Cần sửa thành:** `[a0][a1]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]`

→ Nếu không sửa: audio segment bị giảm 6dB (chia 2) trong template render. **KHÔNG thêm alimiter ở đây** (mix với silence không clip).

## Việc còn lại

1. ✅ FIX 2 đã áp (dòng 9753, `RenderTemplateSegmentAsync`): thêm `:normalize=0`
2. ✅ Build **Debug** + **Release** đều thành công (0 warning, 0 error)
   - `bin\Debug\net8.0-windows\TitanEngine.exe`
   - `bin\Release\net8.0-windows\TitanEngine.exe`
3. ✅ Giải thích cho user: API dùng chung `ExecuteRenderAsync` → fix đã phủ cả UI + API
4. Cập nhật file `TITAN_ENGINE_SUMMARY.md` mục 6.9 Audio (tùy, chưa làm)

## KẾT LUẬN — ĐÃ HOÀN THÀNH (2 fixes)

- **FIX 1:** 34 chỗ `amix=inputs=2:duration=shortest` → thêm `normalize=0,alimiter=limit=0.95` (pipeline render chính)
- **FIX 2:** 1 chỗ `amix=inputs=2:duration=first:dropout_transition=0` → thêm `normalize=0` (template segment, mix với anullsrc silence)
- Nguyên nhân: FFmpeg `amix` mặc định `normalize=1` chia đôi mỗi input khi mix 2 track → volume 2.0 (200%) bị giảm về 1.0 (100%) hiệu dụng
- API không cần sửa riêng: cùng chạy `EngineCore.ExecuteRenderAsync`, clamp volume 0–2.0 đã đúng

## Lưu ý khi build

- Project: `TitanEngine.csproj` (net8.0-windows, WPF)
- Lệnh: `dotnet build` (cwd = `C:\Users\Huy\Desktop\TitanEngine\TitanEngine\TitanEngine`)
- File chỉnh sửa: `C:\Users\Huy\Desktop\TitanEngine\TitanEngine\TitanEngine\MainWindow.xaml.cs`
