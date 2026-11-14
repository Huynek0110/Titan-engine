using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using TitanEngine;
using Avalonia.Media.TextFormatting;
using SkiaSharp;

namespace TitanEngine.Avalonia.Services;

/// <summary>
/// Giai đoạn 5: đo text bằng Avalonia TextLayout (tương đương FormattedText của WPF —
/// cùng seam ITextMetrics mà Core định nghĩa; WPF đo FormattedText, CLI dùng heuristic).
/// </summary>
public sealed class AvaloniaTextMetrics : ITextMetrics
{
    public TextMeasurement Measure(string text, string fontFamilyName, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return new TextMeasurement(0, 0, Array.Empty<double>());

        var typeface = new Typeface(fontFamilyName);
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var widths = new List<double>(lines.Length);
        double height = 0;

        foreach (string line in lines)
        {
            var layout = new TextLayout(
                line.Length == 0 ? " " : line,
                typeface,
                fontSize,
                Brushes.White,
                TextAlignment.Left,
                TextWrapping.NoWrap,
                TextTrimming.None);
            widths.Add(layout.WidthIncludingTrailingWhitespace);
            height += layout.Height;
        }

        return new TextMeasurement(widths.Count > 0 ? widths.Max() : 0, height, widths);
    }
}

/// <summary>
/// Giai đoạn 5 — IImageServices cho Avalonia theo quy tắc của user:
/// - Khả năng KHÔNG cần vẽ WPF → chạy THẬT bằng SkiaSharp (engine render của Avalonia):
///   SampleOverlayFrame (decode + scale ≤160x90 BGRA32, mirror AnalyzeFrame gốc),
///   TryGetImageDimensions (đọc header qua SKCodec — không decode full, mirror metadata gốc).
/// - Khả năng CẦN code vẽ WPF-stay (RenderTargetBitmap: *OverlayArt, SoundBarVisualizerArt…)
///   → throw NotSupportedException với thông điệp rõ ràng — KHÔNG fake output, KHÔNG trả null im lặng.
/// </summary>
public sealed class AvaloniaImageServices : IImageServices
{
    private const string NotSupported =
        "Chưa hỗ trợ trên bản này: tính năng sinh overlay/hình ảnh cần bộ vẽ WPF (chờ Giai đoạn 4). " +
        "Hãy tắt hiệu ứng này hoặc dùng bản Windows.";

    // ── 8 khả năng vẽ WPF-stay → lỗi rõ ràng ──
    public Task<SnowfallGeneratedOverlay> GenerateSnowfallOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, SnowfallOverlayRecipe recipe, string? tag,
        CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<DreamyDotGeneratedOverlay> GenerateDreamyDotOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, double outputDurationSeconds, RenderJob job,
        string? tag, CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<RgbPolaroidLedGeneratedOverlay> GenerateRgbPolaroidLedOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, double outputDurationSeconds, RenderJob job,
        string? tag, CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<PolaroidScrapbook2GeneratedOverlay> GeneratePolaroidScrapbook2OverlayAsync(int width, int height,
        OverlayContentLayout? layout, RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<DashedPolaroidGeneratedOverlay> GenerateDashedPolaroidOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, RenderJob job, string? tag,
        CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<ScratchVideoGeneratedOverlay> GenerateScratchVideoOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, double outputDurationSeconds, RenderJob job,
        string? tag, CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<ParticleOverlaySequence> GenerateParticleOverlayAsync(int width, int height,
        double frameRate, double outputDurationSeconds, ParticleEffectRecipe recipe, string? tag,
        CancellationToken token, Action<string> onLog)
        => throw new NotSupportedException(NotSupported);

    public Task<SoundBarGeneratedOverlay?> GenerateSoundBarOverlayAsync(AudioAnalysisResult analysis,
        SoundBarSettings settings, ColorPreset palette, int canvasWidth, int canvasHeight, string? tag,
        Action<string> onLog, CancellationToken token)
    {
        // ── Giai đoạn 4 spike: vẽ thật bằng SkiaSharp (backend Avalonia dùng) ──
        // Style neon-bars (mã 0, đơn giản nhất): cột dọc từ đáy lên, chiều cao theo Bands[],
		// gradient từ palette stops, áp opacity + sensitivity, vẽ glow outer.
        try
        {
            var frames = analysis.Frames;
            if (frames.Length == 0)
            {
                onLog("[SOUNDBAR] Không có frame âm thanh — bỏ qua lớp overlay.");
                return Task.FromResult<SoundBarGeneratedOverlay?>(null);
            }

            int frameCount = Math.Max(1, Math.Min(frames.Length, analysis.FrameCount));
            double frameRate = settings.FrameRate > 0 ? settings.FrameRate : 30.0;

            // Khu vẽ: (X%,Y%) là điểm neo, Width%/Height% là tỉ lệ canvas. Neo dưới (baseY) để cột mọc lên.
            double plotX = Math.Clamp(settings.XPercent, 0, 1) * canvasWidth;
            double plotY = Math.Clamp(settings.YPercent, 0, 1) * canvasHeight;
            double plotW = Math.Clamp(settings.WidthPercent, 0.02, 1.0) * canvasWidth;
            double plotH = Math.Clamp(settings.HeightPercent, 0.02, 1.0) * canvasHeight;
            double baseY = plotY + plotH;

            int barCount = Math.Clamp(settings.BarCount, 8, 256);
            double barW = plotW / barCount;
            double gap = barW * 0.10;
            double innerW = barW - gap;

            double opacity = Math.Clamp(settings.Opacity, 0.0, 1.0);
            double sensitivity = Math.Clamp(settings.Sensitivity, 0.0, 3.0);

            // Thư mục temp cho PNG sequence.
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_preview",
                $"{tag ?? "sb"}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int fi = 0; fi < frameCount; fi++)
            {
                token.ThrowIfCancellationRequested();

                var frame = frames[fi];
                float[] bands = frame.Bands;

                using var bitmap = new SKBitmap(canvasWidth, canvasHeight);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);

                SKColor coreColor = ParseHex(palette.Core);
                SKColor glowColor = ParseHex(palette.Glow);

                for (int bi = 0; bi < barCount; bi++)
                {
                    float band = bi < bands.Length ? bands[bi] : 0f;
                    if (band <= 0f) continue;

                    double level = Math.Clamp(band * sensitivity, 0.0, 1.0);
                    if (level <= 0.0) continue;

                    double barH = level * plotH;
                    double barX = plotX + bi * barW + gap * 0.5;
                    double barY = baseY - barH;

                    SKColor barColor = LerpPalette(palette, level);

                    using var barPaint = new SKPaint
                    {
                        Color = barColor.WithAlpha((byte)(255 * opacity)),
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill,
                    };
                    float cornerRadius = (float)(Math.Min(innerW, (float)barH) * 0.25);
                    canvas.DrawRoundRect(
                        (float)barX, (float)barY, (float)innerW, (float)barH,
                        cornerRadius, cornerRadius, barPaint);

                    using var glowPaint = new SKPaint
                    {
                        Color = glowColor.WithAlpha((byte)(120 * opacity)),
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill,
                    };
                    canvas.DrawRoundRect(
                        (float)(barX - 1), (float)(barY - 1), (float)(innerW + 2), (float)(barH + 2),
                        cornerRadius + 1, cornerRadius + 1, glowPaint);
                }

                string pngPath = Path.Combine(tempRoot, $"frame_{fi:D4}.png");
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(pngPath);
                data.SaveTo(fs);
            }

            sw.Stop();
            onLog($"[SOUNDBAR] Đã tạo {frameCount} khung @ {canvasWidth}x{canvasHeight}, {frameRate:F2}fps, {sw.ElapsedMilliseconds}ms");

            return Task.FromResult<SoundBarGeneratedOverlay?>(new SoundBarGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = Path.Combine(tempRoot, "frame_%04d.png"),
                FrameCount = frameCount,
                FrameRate = frameRate,
                Width = canvasWidth,
                Height = canvasHeight,
                PreviewDirectory = tempRoot,
                TempArtifacts = new List<string> { tempRoot },
            });
        }
        catch (Exception ex)
        {
            onLog($"[SOUNDBAR-ERROR] {ex.Message} — lớp overlay bị bỏ qua.");
            return Task.FromResult<SoundBarGeneratedOverlay?>(null);
        }
    }

    private static SKColor ParseHex(string hex)
    {
        string h = hex.TrimStart('#');
        if (h.Length == 6)
        {
            byte r = byte.Parse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
            byte g = byte.Parse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
            byte b = byte.Parse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            return new SKColor(r, g, b);
        }
        return SKColors.White;
    }

    private static SKColor LerpPalette(ColorPreset palette, double level)
    {
        var stops = palette.Stops;
        if (stops == null || stops.Count == 0) return SKColors.White;
        if (stops.Count == 1) return ParseHex(stops[0].Hex);

        for (int i = 0; i < stops.Count - 1; i++)
        {
            double o0 = stops[i].Offset, o1 = stops[i + 1].Offset;
            if (level >= o0 && level <= o1)
            {
                double t = (level - o0) / (o1 - o0);
                var c0 = ParseHex(stops[i].Hex);
                var c1 = ParseHex(stops[i + 1].Hex);
                byte r = (byte)(c0.Red + (c1.Red - c0.Red) * t);
                byte g = (byte)(c0.Green + (c1.Green - c0.Green) * t);
                byte b = (byte)(c0.Blue + (c1.Blue - c0.Blue) * t);
                return new SKColor(r, g, b);
            }
        }
        return ParseHex(stops[stops.Count - 1].Hex);
    }

    // ── Khả năng không cần vẽ WPF → làm THẬT bằng SkiaSharp ──

    public (int Width, int Height) TryGetImageDimensions(string filePath)
    {
        try
        {
            using var codec = SKCodec.Create(filePath);
            if (codec == null) return (0, 0);
            return (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    public OverlayFrameSample? SampleOverlayFrame(string imagePath)
    {
        try
        {
            using var bmp = SKBitmap.Decode(imagePath);
            if (bmp == null || bmp.Width == 0 || bmp.Height == 0) return null;

            // Mirror AnalyzeFrame gốc: scale ≤160x90 rồi copy BGRA32.
            double scale = Math.Min(1.0, Math.Min(160.0 / bmp.Width, 90.0 / bmp.Height));
            int dstW = Math.Max(1, (int)Math.Round(bmp.Width * scale));
            int dstH = Math.Max(1, (int)Math.Round(bmp.Height * scale));

            var info = new SKImageInfo(dstW, dstH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var scaled = new SKBitmap(info);
            if (!bmp.ScalePixels(scaled, SKFilterQuality.Medium)) return null;

            var pixels = new byte[dstW * dstH * 4];
            Marshal.Copy(scaled.GetPixels(), pixels, 0, pixels.Length);

            return new OverlayFrameSample { Width = dstW, Height = dstH, BgraPixels = pixels };
        }
        catch
        {
            return null;
        }
    }

    // Sanitize brand logo: decode + xoá RGB dưới alpha=0 + re-encode PNG (mirror WPF).
    // Triển khai thật khi dựng khu WATERMARK; hiện trả false như nhánh "không sanitize được"
    // của bản gốc (file copy nguyên trạng) — KHÔNG fake true.
    public bool SanitizeBrandLogoAsset(string sourcePath, string destinationPath, Action<string> onLog)
    {
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            onLog("[BRAND-LOGO] Avalonia: sao chép nguyên trạng (sanitize WPF chưa hỗ trợ trên bản này)");
            return false;
        }
        catch (Exception ex)
        {
            onLog($"[BRAND-LOGO] copy lỗi: {ex.Message}");
            return false;
        }
    }
}
