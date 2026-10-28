# AGENTS.md — TitanEngine

Repo này là một **WPF video editor / render engine** (.NET 8, Windows) dùng FFmpeg CLI.
Toàn bộ tài liệu nằm trong 5 file; đọc đúng file theo việc bạn sắp làm — không đọc hết.

## Đọc gì trước

| Việc bạn sắp làm | Đọc |
|---|---|
| Định hướng repo, file nào làm gì, sửa gì ở đâu | [`docs/00-OVERVIEW.md`](docs/00-OVERVIEW.md) |
| Pipeline render, filter chain, audio, encoder, bitrate, overlay, upscale | [`docs/10-RENDER-PIPELINE.md`](docs/10-RENDER-PIPELINE.md) |
| Soundbar / visualizer / audio analysis / vocal catch / timeline gate | [`docs/20-SOUNDBAR.md`](docs/20-SOUNDBAR.md) |
| HTTP API, request/response fields | [`docs/30-API.md`](docs/30-API.md) |
| Quy trình làm việc, verify, git, bẫy | file này |

## Luật cứng

1. **`MainWindow.xaml.cs` là trung tâm** (~18 200 dòng): UI, queue, API, FFmpeg command builder,
   audio mix, overlay normalization. Sửa ở đó phải trace call site, không đoán.
2. **Không suy luận loại overlay từ tên file.** Probe asset thật qua `OverlayAssetBackgroundAnalysis`
   (cache theo path + size + mtime).
3. **Rain / Light Leak / Light Leak Burn là asset-only.** Thiếu asset thì fail nhanh, cấm thêm lại
   procedural fallback.
4. **Đổi pixel soundbar ⇒ bump `RendererVersion`** trong `SoundBarVisualizerPreset.cs`.
   Quên là cache PNG cũ và tưởng code không chạy (đã xảy ra 6+ lần).
5. **Thứ tự filter chain là bất biến:**
   `[Crop] → [Scale/Upscale] → [Motion] → [FX] → [Color] → [Text/Caption] → [Brightness] → [Fade] → [Overlay] → [Watermark]`.
6. **`amix` luôn phải có `normalize=0`**, mix thật thêm `alimiter=limit=0.95`. Mặc định FFmpeg
   (`normalize=1`) chia đôi mỗi input, làm volume 200% thành 100%.
7. **Build Release với app đã đóng.** Không đóng sẽ ra `MSB3026/MSB3027` (khoá file exe),
   không phải lỗi code.
8. **Report cho user trước khi commit. Chỉ push khi user bảo rõ ràng.**

## Lệnh chuẩn

```powershell
# build (từ repo root)
dotnet build TitanEngine\TitanEngine.csproj -c Release

# chạy
TitanEngine\bin\Release\net8.0-windows\TitanEngine.exe
```

- `ffmpeg.exe` / `ffprobe.exe` phải nằm cạnh exe.
- `dotnet run` treo vô hạn → kill dotnet → `dotnet build` tay → `dotnet run --no-build`.
- Build phải **0 warning / 0 error** ở cả Debug và Release trước khi commit.

## Chuẩn verify

| Việc | Cách kiểm |
|---|---|
| Build | 0 warning, 0 error |
| Render | job thật, `exit code = 0` |
| Output | `ffprobe` đọc được; duration/frame khớp |
| Decode sạch | `ffmpeg -v error -i out.mp4 -f null -` → **0 dòng lỗi** |
| Soundbar | bump version → cache bust → so contact sheet **có đo** |
| API | gọi thật, đọc `status`, `progress`, `finalOutput` |

**Đo, đừng nhìn suông.** Kiểm loop reset bằng dBFS tại `t=0` vs `t=4.5s`; kiểm vocal bằng
`corr(Voice, vocal stem)`; kiểm gate bằng PSNR giữa 2 frame + dBFS của audio.

## Git

- **Report cho user trước khi commit**: file nào đổi, verify ra sao.
- **Chỉ push khi user bảo rõ ràng.**
- Commit nhỏ, tách theo vùng (r36 / r37 / r38 là 3 commit riêng).
- Message nêu **hành vi**, không nêu tên file.
- Trước commit: `git status` sạch (trừ file user đang viết), đọc lại `git diff`.
- Không commit file tạm, DLL build, log, hay handoff chưa được yêu cầu.

## Kỷ luật task

1. Làm đúng yêu cầu. Không tự mở rộng scope; không biến “phân tích” thành “sửa”.
2. Inspect trước khi edit; trace caller; không đoán API/tên hàm.
3. Thay đổi nhỏ nhất đúng; giữ behavior không liên quan.
4. Verify rồi mới nói xong.
5. Thiếu thông tin nhỏ thì giả định hợp lý và tiếp tục; chỉ hỏi khi hướng làm thay đổi materially.
6. Không bịa. Không nói “đã build/đã test” nếu tool result không chứng minh.

## Harness

### Soundbar — `%TEMP%\SbSphere`

Project ngoài repo, tham chiếu Debug DLL:

```
all <suffix>     render 13 preview (fresh folder mỗi suffix để tránh cache)
loopall          3 frame kick/decay/quiet — kiểm seam
e2e <style>      chạy thật ExecuteRenderAsync
beat / freqmap / prescaletest
```

Preview cache key ăn theo `RendererVersion`; muốn thấy pixel mới **phải bump version** hoặc dùng
suffix mới. `%TEMP%\SbLoop` là harness cũ, có thể chỉ còn bin/obj/out.

### Đo vocal

Cần stem vocal thật (model tách) + full mix cùng bài. Gọi
`AnalyzeAsync(..., startSeconds > 0, ...)` — **start = 0 kích hoạt auto loop scan** và làm lệch
cửa sổ ground truth. So `corr(Mid, vGround)` với `corr(Voice, vGround)`.

## Bẫy đã cắn người thật

### Soundbar

1. **Quên bump `RendererVersion`** → cache cũ, tưởng code không chạy.
2. **Auto loop-start scan dời cửa sổ khi start ≈ 0** → lệch ground truth khi đo.
3. **Literal `22050` lặp 2 chỗ** — đổi phải đồng bộ `DefaultSampleRate`.
4. **State đệ quy (envelope/onset/voice) phải chạy cả 2 warmup pass**, chỉ gate bước lưu.
5. **`ReadSample` wrap vòng** — mảng mới phải wrap y hệt.
6. **Nhánh im lặng phải reset envelope**, kể cả voice.
7. **`pan` trên input mono lỗi nếu thiếu `aformat`**.
8. **3 nhánh decode** (direct/scan/retry) — sửa một phải sửa cả ba.
9. **`VoiceLevel` không nhân Sensitivity** — double-scale.
10. **Gate theo khoảng lặng ngắn** → nháy mỗi lần lấy hơi; ngưỡng là 1.0 s.

### FFmpeg / pipeline

11. **`amix` thiếu `normalize=0`** → volume 200% thành 100%.
12. **`cmd.Insert(postInputInsertPoint, …)` dùng offset số** — `Replace` đổi độ dài phải cộng delta.
13. **`h264_nvenc` + scale động segfault 8/8** trên build gyan 2026-01-22 — giữ CPU retry.
14. **`enable` phải là parameter** (`:enable=`), không phải filter nối bằng `,`.
15. **`volume=0.0`** bị FFmpeg từ chối trong một số chain — clamp.
16. **Scratch/thumbnail cạnh output** → user thấy file rác; luôn dùng temp dir.
17. **`-pix_fmt` sai** → mất alpha hoặc chậm gấp 5 lần (`rgba` vs `yuva420p`).

### Công cụ

18. **MSBuild lock** — đóng app trước khi build.
19. **PowerShell 5.1 đọc `.ps1` không BOM là Latin-1** → tiếng Việt bị mangle. Truyền text qua
    UTF-8 JSON + `[System.IO.File]::ReadAllText`; ghi bằng `UTF8Encoding($false)` để không có BOM.
20. **ffmpeg `sine` source mặc định amplitude 0.1** → “−30 dBFS” thực ra là −51.5 dBFS.
21. **`ripgrep` không có trên PATH** trên máy này — dùng grep tool của harness.
22. **PowerShell chặn `.ps1` shim** — dùng `opencode.cmd`, `npm.cmd`, `npx.cmd`.

## Debug loop

1. Đọc log thật (`[FFMPEG-ERROR-DETAILS]`, `[FFMPEG-CMD]`, `[FFMPEG-FILTER-COMPLEX]`).
2. Xác định stage: probe / timeline / upscale / filter / overlay / audio / encoder.
3. Cô lập: chạy command FFmpeg tay; hoặc tắt từng nhánh overlay.
4. Sửa **root cause**, không che symptom.
5. Verify lại bằng đúng phép đo đã dùng để phát hiện bug.
6. Hai vòng sửa không tiến bộ → dừng đoán, đọc đủ code path hoặc đổi giả thuyết.
