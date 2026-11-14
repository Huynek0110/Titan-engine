using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
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
        => throw new NotSupportedException(NotSupported);

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
