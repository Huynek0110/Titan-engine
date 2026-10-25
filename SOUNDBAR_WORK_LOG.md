# SOUNDBAR WORK LOG — giao tiếp cho agent sau

> Cập nhật cuối: 2026-10-25 · renderer `soundbar-r34` · mọi thay đổi đã commit trên `master`
> (các commit `8158b0c` → `50b6a11` + commit r34 cuối). Chưa push — user tự quyết định khi push.

## 1. File nào làm gì

| File | Vai trò |
|---|---|
| `SoundBarStyleRenderer.cs` | Toàn bộ hình học 13 style + helpers dùng chung (peak caps, beat envelope, floor spill, bloom, baseline, reflection...) |
| `SoundBarVisualizerPreset.cs` | Sinh chuỗi PNG loop + cache SHA-256 + graph ffmpeg composite. **`RendererVersion` ở đây PHẢI bump mỗi khi đổi pixel** |
| `SoundBarPalette.cs` | 30 palette; default per-style: audio-ring rỗng → `ncs-house-yellow` |
| `AudioSpectrumAnalyzer.cs` | PCM → FFT → bands (adaptive dB map) + onset detector. Bands là chuẩn 0..1 per-frame |
| `MainWindow.xaml.cs` | Fast path ảnh 1-pass, thread cap ffmpeg, CPU retry NVENC segfault, soundbar wiring |

## 2. Kiến trúc quan trọng (đừng phá)

- **Seam loop = tính chất cấu trúc**: mọi chuyển động thời gian phải là HÀM THUẦN của frame array.
  Rotation/pulse/peak-cap nhìn lùi qua `FrameAt()` (wrap-around) → frame cuối nối frame đầu không pop.
  Nguyên tắc: rotation nguyên số vòng/loop; pulse có attack ramp 2-frame (`AudioRingPulseAttackLevel`).
- **`OnsetPulseEnvelope(ctx)`** = envelope beat dùng chung mọi style (attack 0.6→1.0, decay 0.80/frame, tail 9 frames).
- **`SampleBandPeak()`** = peak-hold cap chuẩn PPM (hold 9 frames rồi rơi tuyến tính 0.012/frame).
- **Envelope follower bất đối xứng** trong `SampleBandRange`: attack k=0.75, release k=0.40 tại Smoothing=1.
  Invariant: Smoothing=0 → k=1.0 cả 2 nhánh (không smoothing).
- **Bucketing**: AudioRing sphere quantize 24 bucket (halo+core stream riêng, FILLED discs —
  KHÔNG dùng stroked ellipse: stroke 2r trên circle r là đĩa 2r, bug "chấm to" từng ải nhiều vòng).
- **Overlay render 1:1**: `MaxRenderEdge = 1920` → job 1080×1920 composite đúng pixel, không upscale.
  Đổi số này = đổi thời gian gen PNG + blend (đã đo: e2e 12s job ≈ 16s cold cache).
- **ffmpeg composite**: yuv420p base + yuva420p overlay (đừng quay lại rgba — chậm 5×, đã đo r16).

## 3. Đã làm (tóm tắt theo version)

- r22: thay lưới 2D bằng **quả cầu hạt 3D** cho audio-ring (latitude rings, tilt 18°, weak perspective,
  frequency theo vĩ độ, jitter per-dot, bass breathing). Sửa bug pen chấm 2×.
- r23: ring "đập" theo nhạc (onset pulse + band-driven ring).
- r24: mượt hoá chuẩn "smooth circular spectrum" (waveform-dominant, cửa sổ 8, hạt mịn hơn).
- r25: 12 style còn lại nhận tính năng tham chiếu (peak caps, LED bottom-up + peak dot, polar pulse+hub,
  ripple energy, ribbon ghost, dB ticks, star contrast).
- r26: 5 vòng polish — bar punch, envelope attack/release, cap hold-then-fall, DualRings màu/tầng, cap glow.
- r27: "nội thất pro" — floor spill, ambient bloom, mirror axis.
- r28: dựng lại style yếu — WaveRibbon đối xứng+seam+playhead, CenterColumn 9 segment LED,
  PeakStars Starglow cross-flare, DenseColumns curtain hue ngang, baseline circles, echo rings, filament.
- r29: dust field quanh ring, spoke tip dots, star flash, DualRings 2 turn/loop, glossy reflections.
- r30/r31: render 1:1 1080×1920 + dust scale theo canvas.
- r32/r33: envelope curve Mirror, vòng gauge RadialRing, pond boundary Ripple, light pool CenterColumn, baseline DotMatrix.
- r34 (cuối): 3 style cuối có beat coupling — DotMatrix (led cao hơn + phồng theo kick),
  WaveRibbon (amplitude thở theo bass + kick), AudioRing **pulse halo** (bloom sáng +35%/rộng +25% trên hit).

## 4. Nghi thức làm việc đã chứng minh hiệu quả

1. **Research trước code** — mỗi tính năng đều có nguồn (NCS Wiki, SoundCloud blog, Maxon Starglow,
   BBC PPM, LM3915, LimePulse/Videobolt templates, ENTTEC ELM). Ghi nguồn vào doc comment.
2. **Harness test**: `%TEMP%\SbSphere` (project ngoài repo, tham chiếu Debug DLL).
   Các mode: `all <suffix>` (13 preview, thư mục fresh mỗi suffix để tránh cache),
   `loopall` (3-frame kick/decay/quiet), `e2e <style>` (chạy thật `EngineCore.ExecuteRenderAsync`),
   `beat`, `freqmap`, `prescaletest`. Preview cache key ăn theo `RendererVersion` —
   muốn thấy pixel mới PHẢI bump version hoặc dùng suffix mới.
3. **Kiểm thị giác bằng vision**: compose contact sheet (nền tối) → analyze_image → hỏi PASS/FAIL có đo đạc.
4. **Verify ffmpeg**: e2e exit=0 + `ffmpeg -v error -i out -f null -` phải 0 dòng lỗi.
5. Build Debug lẫn Release đều phải 0 warning/0 error trước khi commit.

## 5. Bẫy đã biết (đừng giẫm lại)

- Quên bump `RendererVersion` → cache cũ, tưởng code không chạy (đã xảy ra 6+ lần theo PROGRESS_REPORT).
- `cmd.Insert(postInputInsertPoint, ...)` dùng offset số — nếu Replace làm đổi độ dài cmd TRƯỚC khi
  Insert chạy thì phải cộng delta vào offset (bug exit -22 đã sửa r30-era, có comment đầy đủ).
- h264_nvenc + scale biểu thức động (eval=frame) **segfault 8/8** trên bản ffmpeg gyan 2026-01-22 —
  đã có CPU retry trong `RenderTemplateSegmentAsync`; cân nhắc đổi build ffmpeg (BtbN) để lấy lại GPU.
- MSBuild lock: nếu `dotnet run` treo vô hạn, kill dotnet rồi `dotnet build` tay + `dotnet run --no-build`.
- Thumbnail cover-art: file scratch phải nằm trong `TitanEngineTemp`, không cạnh output (đã sửa —
  file `.thumb.jpg` sót cạnh MP4 từng là bug user báo).

## 6. Tầng việc kế tiếp (feature mới, cần user duyệt — không phải polish)

- Palette riêng per-style hoặc auto-palette theo cover art.
- Preview động trong UI picker (hiện chỉ 1 frame tĩnh).
- Nâng frame cap loop >180 (6s) cho loop dài hơn (đổi cả `ResolveFrameCount` + chi phí PNG).
- Bỏ cơ chế loop 5s để soundbar theo nhạc thật suốt bài (việc lớn — đổi kiến trúc cache).
