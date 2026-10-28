# 20 — Soundbar (audio visualizer)

> Hệ thống overlay động: đọc audio → FFT → vẽ 13 kiểu hình → PNG loop → composite lên video.
> Trạng thái hiện tại: **`soundbar-r38`**. Đọc [`10-RENDER-PIPELINE.md`](10-RENDER-PIPELINE.md) §7
> để biết vị trí trong filter graph.

## 1. File nào làm gì

| File | Vai trò |
|---|---|
| `AudioSpectrumAnalyzer.cs` | Decode PCM → FFT → bands + waveform + `Voice` + onset; timeline audibility scan |
| `AudioAnalysisModels.cs` | `SoundBarStyleId` (13), `SoundBarAudioSource`, `AudioFrameData`, `AudioInterval` |
| `SoundBarStyleRenderer.cs` | Toàn bộ hình học 13 style + helper dùng chung (peak caps, beat envelope, floor spill, bloom, reflection, dust…) |
| `SoundBarVisualizerPreset.cs` | Sinh chuỗi PNG loop, cache SHA-256, dựng graph `overlay` composite. **`RendererVersion` ở đây** |
| `SoundBarPalette.cs` | 30 palette NCS; default `ncs-cyan`, riêng `audio-ring` → `ncs-house-yellow` |
| `MainWindow.xaml.cs` | Wiring: analyze → generate → chèn overlay vào graph; fast path ảnh; thread cap |

## 2. Luồng dữ liệu

```
audio source (Video / External / Audio1 / Audio2 / Auto)
   │  ffmpeg: -ac 1 (mid) + side, 22050 Hz, s16le
   ▼
PCM → FFT 2048 (Hann, hop = rate/fps)
   ├── 64 log bands 40–16000 Hz  → bands[] 0..1
   ├── 512 waveform columns
   ├── Voice 0..1 (mid/side centre extraction)
   ├── onset detector
   └── RMS / peak / bass / mid / treble
   ▼
AudioFrameData[] (mỗi frame render)
   ▼
SoundBarStyleRenderer.DrawFrame(style, bands, waveform, voice, …)
   ▼
PNG sequence (24–180 frame) → cache SHA-256
   ▼
ffmpeg: base yuv420p + overlay yuva420p, overlay=…:enable=<gate>
```

### 2.1 Chọn nguồn audio

`SoundBarAudioSource`: `Auto` (external nếu có, không thì audio video), `External`, `Video`,
`Audio1`, `Audio2`. Token API: `audio1`, `audio2`, `voice`, `vocal`, `a1`, `a2`.

- Lịch sử: trước r35 token `audio1` bị nuốt vào `external` (track **trộn**) — đó là lý do user
  không bao giờ nghe riêng được track vocal.
- Thiếu track được chọn → log WARN + fallback `Auto`. **Soundbar không bao giờ làm chết render.**

### 2.2 Hợp đồng phân tích

| Hằng số | Giá trị |
|---|---|
| Sample rate | `22050` Hz (literal lặp 2 chỗ: `BuildDecodeArguments` và `DefaultSampleRate`) |
| FFT | `2048`, Hann |
| Bands | 64, log-spaced `40–16000 Hz` |
| Waveform | 512 cột min/max |
| Envelope attack / release | `0.06 s` / `0.130 s` |
| Onset | EMA tau `50/30 s`, sensitivity `1.6×`, refractory `0.13 s`, floor RMS `1e-3` (−60 dBFS) |
| Digital silence | `|x| < 1e-4` (−80 dBFS) |
| Loop (analyzer) | `0.5–60 s` (`MinLoopSeconds` / `MaxLoopSeconds`) |
| Loop (renderer) | `LoopSeconds` clamp 0.25–30 s; frame count clamp 24–180 |

Bands là chuẩn `0..1` **per frame**. Envelope follower bất đối xứng trong `SampleBandRange`:
attack `k=0.75`, release `k=0.40` tại `Smoothing=1`; `Smoothing=0` → `k=1.0` cả hai nhánh.

## 3. 13 style

| # | id | Tên | Nguồn điều khiển |
|---|---|---|---|
| 0 | `neon-bars` | Neon Bars | FFT |
| 1 | `mirror-spectrum` | Mirror Spectrum | FFT |
| 2 | `pill-bars` | Pill Bars | FFT |
| 3 | `dense-columns` | Dense Columns | FFT (tới 256 hairline) |
| 4 | `dot-matrix` | Dot Matrix | FFT |
| 5 | `radial-ring` | Radial Ring | FFT |
| 6 | `dual-rings` | Dual Rings | FFT |
| 7 | `ripple-rings` | Ripple Rings | onset, không phải spectrum |
| 8 | `spoke-lines` | Spoke Lines | FFT |
| 9 | `wave-ribbon` | Wave Ribbon | waveform + bass |
| 10 | `center-column` | Center Column | RMS + peak-hold |
| 11 | `peak-stars` | Peak Stars | FFT local maxima |
| 12 | `audio-ring` | Audio Ring (NCS) | FFT + waveform; quả cầu hạt 3D |

## 4. Bất biến hình học — seam loop

Overlay là **loop ngắn** (mặc định 5 s), ffmpeg lặp suốt video. Vì vậy frame cuối phải nối frame
đầu mà không pop.

1. **Mọi chuyển động thời gian là hàm thuần của frame array.** Rotation/pulse/peak-cap nhìn lùi qua
   `FrameAt()` (wrap-around), không dùng state ngoài.
2. **Rotation nguyên số vòng/loop.** Không có "gần đúng".
3. **Pulse có attack ramp 2 frame** (`AudioRingPulseAttackLevel`) để không nhảy.
4. **`OnsetPulseEnvelope(ctx)`** dùng chung mọi style: attack `0.6→1.0`, decay `0.80/frame`, tail 9 frame.
5. **`SampleBandPeak()`** là peak-hold chuẩn PPM: giữ 9 frame rồi rơi tuyến tính `0.012/frame`.
6. **AudioRing bucket 24** (halo + core stream riêng), vẽ **FILLED disc** — không dùng stroked ellipse:
   stroke `2r` trên circle `r` là đĩa `2r`, bug "chấm to" từng lặp nhiều vòng.
7. **`ReadSample` luôn wrap vòng** — mọi mảng mới (side, voice) phải wrap y hệt.

## 5. Audibility — hai tầng

### 5.1 r37 — absolute audibility (theo frame)

Trước r37, mọi giá trị được normalize theo **peak của chính frame**, nên room tone −50 dBFS
nhảy y như chorus. r37 nhân toàn bộ giá trị đã map (bands, bass/mid/treble, RMS, peak, voice) với
một fade tuyệt đối đo từ **frame RMS**:

```
tắt hoàn toàn tại −48 dBFS  →  đầy đủ tại −38 dBFS
```

- 1% yên nhất của một track master thật nằm quanh **−35 dBFS RMS**, nên nhạc thật không bị đụng.
- Dither/hiss/đoạn fade bị đóng băng.
- Onset bị chặn dưới nửa ngưỡng audibility; pill-bars chỉ "thở" khi có audio để thở.

### 5.2 r38 — timeline gate (theo cả video)

r37 chỉ giải quyết **bên trong loop window**. Analyzer luôn chọn cửa sổ loop to nhất (auto scan),
nên trên render thật 3:24 có nhạc bắt đầu ở 0:18, loop vẫn khiêu vũ trên 16 giây im lặng
(frame 2 s và 7 s giống hệt nhau, audio −91 dBFS).

r38 chạy `silencedetect` trên **toàn bộ source** và biến phần nghe được thành `overlay=enable=`:

| Hằng số | Giá trị | Ý nghĩa |
|---|---|---|
| `TimelineSilenceDb` | `-48 dB` | ngưỡng im lặng, khớp `AudibleFloorDb` |
| `TimelineSilenceMinSeconds` | `1.0 s` | khoảng ngắn hơn là pause nhạc, không gate |
| `TimelineAudiblePadSeconds` | `±0.2 s` | không cắt mất attack đầu / đuôi reverb |
| `TimelineAudibleMergeSeconds` | `0.5 s` | hai đoạn nghe được gần nhau thì gộp |
| minimum stretch | `0.05 s` (trước pad) | bỏ rounding tail, tránh nháy 0.2 s ở cuối |

- `sourceToOutputScale` xử lý slow motion (0.5 cho render 2× chậm).
- Scan lỗi / thiếu ffmpeg / không rõ duration → trả `null` → **fail-open**, overlay luôn bật như cũ.
- Nếu toàn bộ timeline nghe được → trả một interval `[0, duration]` và bỏ hẳn `enable` expression
  (graph giống hệt trước r38).
- **Cache PNG không đổi.** Gate nằm ở composite, không nằm ở pixel.

Kết quả đo trên file thật (render 3:24):

```
audible intervals: [17.677..68.908] [79.800..81.340] [86.573..200.780]
t=2 s  → 27.93 dB (visible)  →  gated: inf (hidden)
t=20 s → 24.64 dB (vẫn visible)
synthetic 4 s + 6 s → [3.800..10.000]
```

## 6. Vocal catch — r36

### 6.1 Vấn đề của r35

r35 thêm `MidSwellFraction = 0.03` theo dải mid 150–2000 Hz. Nhưng:

- Dải đó chứa cả harmonic kick, thân snare, guitar/synth/piano — **không phải dải vocal**.
- `Mid` là **tương đối theo frame** (map theo peak của frame), nên đo "độ giữa của mix",
  không đo năng lượng giọng.
- 3% chiều cao thanh là trần tối đa; thực tế vài pixel → không nhìn thấy.

### 6.2 Cách sửa

1. **Decode mid/side** thay vì mono:

```
-af "aformat=channel_layouts=stereo,pan=stereo|c0=0.5*c0+0.5*c1|c1=0.5*c0-0.5*c1"
-ac 2 -ar 22050 -c:a pcm_s16le
```

`aformat` **phải đứng trước** `pan`: input mono không có `c1`, gọi `pan` trực tiếp sẽ lỗi graph.
Sau upmix, side = 0. Khi downmix về mid, hệ số trùng `-ac 1` cũ → bands/RMS/waveform/onset
**không đổi**, chỉ thêm thông tin side.

2. **Feature `Voice`** trong `AudioFrameData`:

| Hằng số | Giá trị |
|---|---|
| Dải vocal | `350–3050 Hz` |
| Công thức | `voiceMag[k] = max(0, midMag[k] − sideMag[k])` |
| Chuẩn hoá | percentile **p40–p95 của cả loop** (không phải p15: p15 dính noise bed, mean voice đoạn instrumental lên 0.62) |
| Guard | `p95 − p40 < 0.5 dB` → coi như loop không có giọng, `Voice ≡ 0` |
| Envelope | attack `0.06 s`, release `0.20 s` |
| Rendering | `VoiceSwellFraction = 0.10`, **không** nhân Sensitivity (Voice đã tự chuẩn hoá) |

3. **Cả 13 style đều phản ứng**, mỗi style theo "ngôn ngữ hình" của nó:
   bars/wave-ribbon/dot-matrix cao thêm 10%, họ radial scale 10%, ripple đẩy ra 8%,
   center-column nâng reading 10%, peak-stars sáng thêm 30%.

4. **Mid swell giữ nguyên 3%** — nó trả lời giai điệu; Voice trả lời giọng hát.

### 6.3 Kết quả đo

Trên stem vocal tách bằng model (bài test thật):

| Chỉ số | r35 (Mid) | r36 (Voice) |
|---|---|---|
| `corr` với vocal stem | 0.1973 | **0.5126** |
| Mean đoạn instrumental | — | **0.3289** |
| Mean đoạn hát | — | **0.5686** |

## 7. Lịch sử r22–r38

| Version | Thay đổi |
|---|---|
| r22 | audio-ring: lưới 2D → **quả cầu hạt 3D** (latitude rings, tilt 18°, perspective nhẹ, jitter, bass breathing). Sửa bug pen chấm 2× |
| r23 | ring đập theo nhạc (onset pulse + band-driven ring) |
| r24 | mượt hoá "smooth circular spectrum" (waveform-dominant, cửa sổ 8) |
| r25 | 12 style còn lại nhận peak caps, LED bottom-up + peak dot, polar pulse + hub, ripple energy, ribbon ghost, dB ticks, star contrast |
| r26 | 5 vòng polish: bar punch, envelope attack/release, cap hold-then-fall, DualRings màu/tầng, cap glow |
| r27 | floor spill, ambient bloom, mirror axis |
| r28 | dựng lại style yếu: WaveRibbon đối xứng + seam + playhead, CenterColumn 9 segment LED, PeakStars cross-flare, DenseColumns hue ngang, baseline circles, echo rings, filament |
| r29 | dust field quanh ring, spoke tip dots, star flash, DualRings 2 turn/loop, glossy reflections |
| r30 | render 1:1 tới 1920 px (`MaxRenderEdge` 1280 → 1920) |
| r31 | dust scale theo canvas (1.0–3.0 px) |
| r32 | mirror envelope curve, radial outer gauge, ripple pond boundary |
| r33 | center-column light pool, dot-matrix lit baseline |
| r34 | beat coupling cho 3 style cuối (dot-matrix, wave-ribbon, audio-ring pulse halo) |
| r35 | chọn nguồn Audio1/Audio2 + mid swell 3% |
| r36 | mid/side centre extraction + `Voice` + swell 10% cho cả 13 style |
| r37 | absolute audibility fade −48…−38 dBFS |
| r38 | timeline gate bằng `silencedetect` + `overlay=enable` |

## 8. Cache và `RendererVersion`

- Cache key = SHA-256 trên **mọi thứ có thể đổi pixel**: renderer version, style, palette (kể cả stops),
  geometry, fps, frame count, tunables. Lấy **24 hex** cho render, **12 hex** cho preview.
- Cache nằm trong temp tree và **phải sống qua cleanup** — cache hit trả `TempArtifacts` rỗng có chủ đích.
- **Bump `RendererVersion` mỗi khi đổi pixel.** Không bump → preview/render dùng PNG cũ, tưởng code
  không chạy. Đây là bẫy số 1 của dự án (đã xảy ra 6+ lần).
- Preview picker chỉ có 1 frame tĩnh (`MaxPreviewFrames = 6` cho contact sheet nội bộ).

## 9. Harness và nghi thức verify

Harness ngoài repo: `%TEMP%\SbSphere` (project riêng, tham chiếu Debug DLL), các mode:

| Mode | Việc |
|---|---|
| `all <suffix>` | render 13 preview; thư mục fresh mỗi suffix để tránh cache |
| `loopall` | 3 frame kick/decay/quiet — kiểm seam |
| `e2e <style>` | chạy thật `EngineCore.ExecuteRenderAsync` |
| `beat`, `freqmap`, `prescaletest` | kiểm onset / map tần số / scale |

Quy trình đã chứng minh:

1. **Research trước code** — mỗi tính năng có nguồn (NCS Wiki, BBC PPM, LM3915, ENTTEC ELM…),
   ghi nguồn vào doc comment.
2. **Đo, không nhìn suông.** Ví dụ: `corr(Voice, vocal stem)` thay vì "trông có vẻ bắt vocal".
3. **Kiểm thị giác có đo**: contact sheet nền tối → analyze → PASS/FAIL kèm số.
4. **Verify ffmpeg**: `ffmpeg -v error -i out.mp4 -f null -` phải **0 dòng lỗi**; exit 0.
5. Build Debug **và** Release đều 0 warning / 0 error trước khi commit.

## 10. Bẫy

1. **Quên bump `RendererVersion`** → cache cũ. Đã cắn 6+ lần.
2. **Auto loop-start scan dời cửa sổ khi `startSeconds ≈ 0`** → khi đo ground truth phải truyền start dương.
3. **Literal `22050` lặp 2 chỗ** — đổi phải đồng bộ cả `DefaultSampleRate`.
4. **State đệ quy (envelope, onset, voice) phải chạy cả 2 warmup pass**; chỉ gate bước **lưu**.
5. **`ReadSample` wrap vòng** là bất biến seam-loop; mảng side/voice phải wrap y hệt.
6. **Nhánh im lặng phải reset envelope** — voice cũng phải reset.
7. **`pan` trên input mono lỗi nếu thiếu `aformat`**.
8. **3 nhánh decode** (direct / auto-scan / retry) — sửa một phải sửa cả ba.
9. **`VoiceLevel` không nhân Sensitivity** — double-scale.
10. **`MaxLoopSeconds = 60`** — mọi mảng frame phải nằm trong giới hạn này.
11. **h264_nvenc + scale động segfault** — dùng CPU retry cho template segment.
12. **Đừng gate theo từng khoảng lặng ngắn** — sẽ nháy mỗi lần lấy hơi; ngưỡng là 1.0 s.
