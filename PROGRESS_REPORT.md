# TITAN ENGINE — BÁO CÁO TIẾN ĐỘ & TÌNH TRẠNG

Cập nhật: 15/10/2026 · commit `a04793b` · nhánh `master` · cây làm việc sạch

---

## 1. Tổng quan ứng dụng

**TitanEngine** — trình dựng video WPF .NET 8, chạy trên Windows, không dùng NuGet (zero dependency).
Decode/filter/encode bằng **FFmpeg** nằm cạnh file `.exe` (211 MB, `bin\Release\net8.0-windows\ffmpeg.exe`).
Có **HTTP API localhost:5555** để điều khiển từ bên ngoài.

### 1.1 Cấu trúc mã nguồn

| File | Dung lượng | Vai trò |
|---|---|---|
| `MainWindow.xaml.cs` | 1044 KB | **File lõi, ~17 000 dòng.** Toàn bộ pipeline render, 16 nhánh overlay, queue job, HTTP API, toàn bộ logic UI |
| `SoundBarStyleRenderer.cs` | 158 KB | Vẽ 13 kiểu soundbar bằng `DrawingContext` → PNG |
| `AudioSpectrumAnalyzer.cs` | 86 KB | PCM → FFT → dải tần → chuẩn hoá → onset |
| `SoundBarVisualizerPreset.cs` | 53 KB | Sinh chuỗi PNG lặp, cache SHA-256, dựng đồ thị ffmpeg |
| `SnowfallOverlayPreset.cs` | 47 KB | Overlay tuyết rơi |
| `LightLeakBurnPreset.cs` | 45 KB | Hiệu ứng cháy sáng / rò phim |
| `MainWindow.Models.cs` | 37 KB | DTO cho job render + request API |
| `DreamyDotOverlayPreset.cs` | 32 KB | Overlay chấm mơ mộng |
| `EffectRecipeRenderSupport.cs` | 32 KB | Hỗ trợ dựng công thức hiệu ứng |
| `ParticleOverlayGenerator.cs` | 25 KB | Sinh hạt cho overlay |
| `EffectRecipeLibrary.cs` | 24 KB | Thư viện công thức hiệu ứng |
| `SoundBarPalette.cs` | 21 KB | 30 bảng màu NCS |
| `RgbPolaroidLedPreset.cs` | 20 KB | Khung LED RGB |
| `RainOverlayPreset.cs` | 19 KB | Overlay mưa |
| `AudioEditorWindow.xaml.cs` | 19 KB | Cửa sổ biên tập audio |
| `DashedPolaroidFramePreset.cs` | 19 KB | Khung polaroid nét đứt |
| `ScratchVideoPresetPreset.cs` | 18 KB | Overlay xước |
| `LightLeakOverlayPreset.cs` | 16 KB | Overlay rò phim |
| `PolaroidScrapbook2Preset.cs` | 12 KB | Khung polaroid 2 |
| `OverlayAssetBackgroundAnalysis.cs` | 11 KB | Phân tích nền asset overlay |
| `TitanTransitionGraphBuilder.cs` | 9 KB | Dựng đồ thị chuyển cảnh |
| `AudioAnalysisModels.cs` | 8 KB | Enum 13 kiểu soundbar + model phân tích |
| **Tổng** | **~1.8 MB C#** | |

### 1.2 Tài liệu

| File | Dòng | Ghi chú |
|---|---|---|
| `TitanEngine/BAS_LOCALHOST_API_GUIDE.txt` | 1047 | **Bản chuẩn** — csproj đóng gói file này |
| `BAS_LOCALHOST_API_GUIDE.txt` (gốc repo) | — | Bản trùng lặp, **đã lệch**, không ai đóng gói |
| `TITAN_ENGINE_AGENT_HANDOFF.md` | 726 | Bàn giao cho agent |
| `TITAN_ENGINE_SUMMARY.md` | 302 | Tóm tắt tính năng |
| `MainWindow.xaml` | 1150 | Giao diện |

### 1.3 Pipeline render

```
ffmpeg giải mã ──► filter graph (16 nhánh overlay) ──► NVENC (GPU) ──► mp4
                           │
                           └─► soundbar: PCM → FFT → 150 khung PNG trong suốt
                                            ──► overlay= yuv420p + yuva420p
```

---

## 2. Tính năng SOUNDBAR — đã làm được gì

Ra đời ở commit `cc72fbb`, đã qua **8 vòng sửa**. 13 kiểu hình, 30 bảng màu, điều khiển vị trí/kích thước, HTTP API.

### 2.1 Lỗi đã sửa (đều tìm bằng đo, không đoán)

| Vòng | Lỗi | Cách phát hiện |
|---|---|---|
| 1 | Thanh reset mỗi 5 giây | Đo âm thanh video thật: t=0 là **−76 dB**, t=4,5s là **−19,5 dB** → nhảy 47 dB mỗi vòng |
| 1 | Ô "Rộng" chết ở 6/13 kiểu | Đo bbox: y hệt 175px ở W=30/62/90% |
| 1 | `.flac` bị từ chối | 3 danh sách phần mở rộng lệch nhau |
| 2 | Square-plot **chưa bao giờ chạy** | Cân bằng *phần trăm* chứ không phải *pixel* → bán kính y hệt khi không sửa |
| 3 | Onset detector không warm-up | `&&` short-circuit → `Process` không chạy ở pass 0 |
| 3 | `audio-ring` cấp phất 1 485 object/khung | 594 `DrawGeometry`/khung → gom bucket còn 31 |
| 4 | `_soundBarCanvasAspect` **chưa bao giờ được gán** | Quét IL trong DLL: `STFIELD` chỉ 1 lần, trong constructor |
| 4 | Nhập tay rộng ≥92% làm preset sụp về 0,5% lề | Quét 3 457 assertion |
| 4 | `RenderTargetBitmap` ép gen-2 GC mỗi khung chẵn | gen2 33/33/33 trên 50 khung → 1/1/1 |
| 5 | Dải tần vùng cao chết | p10 rơi từ 0,439 dự đoán xuống **0,0006** |
| 5 | Job chạy 2 ffmpeg cùng lúc | Batch UI chỉ **nhìn** khoá, không giữ |

### 2.2 Hiệu năng

| Hạng mục | Trước | Sau |
|---|---|---|
| Job 156s (1080×1920) | 60 s | **16,4 s** (đổi `rgba`→`yuv420p`/`yuva420p`) |
| CPU job đó | 180 s | **34 s** |
| `RenderTargetBitmap` gen-2 GC | 33/50 khung | **1/50 khung** |
| `audio-ring` object/khung | 1 485 | ~28 |

---

## 3. VẤN ĐỀ CÒN LẠI — chưa xong

### 3.1 ⭐ SOUNDBAR NCS (`audio-ring`) — CHƯA GIỐNG BẢN GỐC

**Mức độ: gần đúng cấu trúc, sai tinh thần.** Đây là việc quan trọng nhất còn tồn.

| Đặc điểm | Bản gốc | Hiện tại |
|---|---|---|
| Vòng neon | Tròn, mảnh vừa, sáng đều | ✅ Đã khớp (dày 6,35% đường kính, trơn tru) |
| Chấm bên trong | **Các cung đồng tâm trải khắp đĩa**, xen kẽ dải đặc – khoảng trống lớn | ⚠️ **Quạt đặc dồn một góc**, phần còn lại trống |
| Mật độ biến động theo nhạc | Rất mạnh giữa các khung | ⚠️ Yếu |
| Làn theo tần số | Chấm nằm trên cung, mờ dần theo tần | ⚠️ Chưa rõ |

**Nguyên nhân:** dải quét hiện là **cửa sổ góc** (`window` raised-cosine) rộng 156°, ngoài đó chỉ còn nền `0.42 × energyGate`. Với ngưỡng `AudioRingPresenceFloor = 0.34`, phần lớn chấm ngoài dải bị cắt hết → thành một nêm.

**Cần làm gì:**
- Mở rộng vùng có chấm ra toàn đĩa, giữ dải quét chỉ là phần *đặc hơn*
- Cân bằng lại `AudioRingBaselinePresence` / `AudioRingPresenceFloor` / `AudioRingSweepHalfWidthDegrees`
- Bám sát ảnh mẫu: nhiều ảnh gần như trống (ảnh 3, 6), số khác có dải rõ (ảnh 2, 5, 7) → **mật độ phải dao động mạnh, không cố định**

### 3.2 ⭐ 12 KIỂU SOUNDBAR CÒN LẠI — CHƯA ĐỦ TỐT

**Mức độ: đều render được, nhưng chưa kiểm hình thị giác nghiêm ngặt.**

Mỗi kiểu chỉ được xác nhận bằng: render không lỗi + số đo bbox. **Chưa kiểm nào đối chiếu cảm quan với phong cách NCS.**

Tình trạng từng kiểu:

| Kiểu | Tình trạng | Vấn đề đã biết |
|---|---|---|
| `neon-bars` | Ổn | — |
| `mirror-spectrum` | Ổn | — |
| `pill-bars` | Ổn | — |
| `dense-columns` | Ổn | — |
| `dot-matrix` | Vừa sửa | Ô chấm 4:1 (đổi từ vuông để bớt CPU) — **cần xác nhận mắt** |
| `radial-ring` | Trung bình | Nét mỏng, chưa có cảm giác neon |
| `dual-rings` | Trung bình | — |
| `ripple-rings` | Yếu | Chỉ chiếm **77px trong plot 186px** (~41%) — vẫn nhỏ |
| `spoke-lines` | Trung bình | — |
| `wave-ribbon` | Trung bình | — |
| `center-column` | Yếu | Hẹp 27px — có thể đúng ý, chưa ai xác nhận |
| `peak-stars` | Trung bình | — |
| `audio-ring` | Xem 3.1 | — |

**Việc cần:** mỗi kiểu render ở nhiều BarCount/palette, **xem thật**, so với phong cách NCS. Hiện tại quy trình là "render xong → đo bbox → coi như đạt" — đó là tiêu chuẩn sai cho việc này.

### 3.3 Hai file hướng dẫn API đã lệch nhau

- `TitanEngine/BAS_LOCALHOST_API_GUIDE.txt` (1047 dòng, **được đóng gói**) — chuẩn
- `BAS_LOCALHOST_API_GUIDE.txt` ở gốc repo — thiếu mục `4C) AUDIO 1 VOICE FX`, `4F) AUDIO 1/AUDIO 2 API`, mục cross-clip, 8 ví dụ, và các khối quy tắc audio overlay
- Bản gốc có phần **không có** ở bản chuẩn: `4AA) TEXT OVERLAY` đầy đủ, `4B2) CAPTION SYNC` Whisper, quy tắc drawtext tự hạ cấp

→ **Không file nào là bản siêu tập của file kia.** Gộp cần người dùng quyết định giữ gì.

### 3.4 Chưa kiểm chứng được (cần người dùng tự test)

| Việc | Vì sao tôi không tự làm |
|---|---|
| Con trỏ ô kích thước (gõ `5` → đợi → gõ `0` phải ra `50`) | Cần cây visual thật; tạo instance `MainWindow` → constructor → tìm ffmpeg → **bật dialog** |
| Hình dạng `audio-ring` thực tế | Cần xem trên video thật |
| `DotMatrix` ở `BarCount=256` | Cần xác nhận mắt |
| Chất lượng hình khi ghép YUV thay vì RGB | PSNR 31–36 dB nhưng không đo được cảm quan |

### 3.5 Rủi ro kỹ thuật còn lại

| Vấn đề | Mức |
|---|---|
| `MainWindow.xaml.cs` 17 000 dòng, mọi logic dồn một file | Cao — sửa là dễ phá nhầm |
| `RendererVersion` phải bump tay mỗi khi sửa hàm vẽ | Đã bị 6 lần, mỗi lần thấy ảnh cache cũ và tưởng code không chạy |
| Cache PNG trong `%TEMP%` không tự dọn | Trung bình — mỗi kiểu ~0,3–33 MB |
| `EngineCore.GetVideoResolutionAsync` trả `(1920,1080)` khi probe lỗi | Thấp — không phân biệt được với khung thật |
| Agent hay tự mở app khi test | Đã cấm bằng cơ chế: dùng `RuntimeHelpers.GetUninitializedObject` |

---

## 4. Cách dựng lại

```powershell
dotnet build "TitanEngine\TitanEngine.csproj" -c Release
# chạy: TitanEngine\bin\Release\net8.0-windows\TitanEngine.exe
```

Lưu ý: **đóng app trước khi build**, nếu không báo lỗi `MSB3026/MSB3027` (khoá file exe, không phải lỗi biên dịch).

Harness kiểm thử soundbar: `%TEMP%\SbLoop` (dựng 13 kiểu → ảnh trong `out\`).

---

## 5. 8 commit gần nhất

```
a04793b  Ghép YUV thay rgba, dựng lại lưới chấm audio-ring
0b5aa4c  Lưu canvas aspect, chặn ô rộng, sửa timer phá con trỏ
50ee3f5  Chọn cửa sổ lặp theo mức âm, không cứng t=0
9291c7a  Sửa trần preset, aspect cũ, RenderTargetBitmap mỗi khung
b65e6b5  Sửa square-plot chưa chạy, warm-up onset chết, cấp phát audio-ring
28c6f24  Làm đậm ripple-rings, viết tài liệu audio-ring
858b84c  Sửa reset lặp, thêm điều khiển size/position, kiểu NCS
b795834  Sửa hình học radial, dải tần, rò cache
```

---

## 6. Việc nên làm tiếp — theo thứ tự

1. **`audio-ring` cho giống mẫu** (3.1) — đây là thứ anh đã hỏi nhiều nhất
2. **Rà 12 kiểu còn lại bằng mắt** (3.2) — thay tiêu chuẩn "đo bbox = đạt"
3. **Gộp 2 file guide** (3.3) — cần anh quyết định giữ phần nào
4. Tách `MainWindow.xaml.cs` — việc lớn, nên làm khi không có deadline
