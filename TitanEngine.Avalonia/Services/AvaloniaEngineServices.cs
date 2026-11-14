using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    // ── Giai đoạn 4: vẽ thật bằng SkiaSharp (không cần WPF/STA) ──
    public Task<SnowfallGeneratedOverlay> GenerateSnowfallOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, SnowfallOverlayRecipe recipe, string? tag,
        CancellationToken token, Action<string> onLog)
    {
        try
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"snowfall_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "snow_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            double renderScale = Math.Min(1.0, 1280.0 / Math.Max(safeW, safeH));
            int renderW = Math.Max(320, (int)Math.Round(safeW * renderScale));
            int renderH = Math.Max(320, (int)Math.Round(safeH * renderScale));
            if ((renderW & 1) != 0) renderW--;
            if ((renderH & 1) != 0) renderH--;
            int frameCount = Math.Clamp((int)Math.Ceiling(Math.Max(2.0, recipe.CycleSeconds) * safeFps), 36, 90);
            var renderLayout = new OverlayContentLayout
            {
                CanvasWidth = renderW,
                CanvasHeight = renderH,
                // Snow behaves like an environmental overlay, not a media-content-only frame:
                // particles cover the entire render canvas.
                ContentRect = new PixelRect(0, 0, renderW, renderH)
            };

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(previewDir);

            var sprites = SnowflakeSpriteCache.Value;

            for (int i = 0; i < frameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                double progress = frameCount <= 1 ? 0.0 : i / (double)frameCount;

                using var bitmap = new SKBitmap(renderW, renderH);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);

                DrawSnowLayers(canvas, renderW, renderH, recipe, progress, sprites);

                string overlayFile = SnowfallOverlayPreset.FormatSequenceFilePath(overlayPattern, i + 1);
                SavePng(bitmap, overlayFile);

                if (i < 8)
                {
                    string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                    SavePng(bitmap, previewFile);
                }
            }

            onLog($"[SNOWFALL] Generated particle overlay @ {renderW}x{renderH} (target {safeW}x{safeH}), fps={safeFps:0.00}, frames={frameCount}, preview={previewDir}");

            return Task.FromResult(new SnowfallGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                Width = renderW,
                Height = renderH,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot },
            });
        }
        catch (Exception ex)
        {
            onLog($"[SNOWFALL-ERROR] {ex.Message} — lớp overlay bị bỏ qua.");
            return Task.FromResult(new SnowfallGeneratedOverlay());
        }
    }

    private static readonly Lazy<SKImage[]> SnowflakeSpriteCache =
        new(CreateSnowflakeSprites, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void DrawSnowLayers(SKCanvas canvas, int canvasW, int canvasH,
        SnowfallOverlayRecipe recipe, double progress, SKImage[] sprites)
    {
        foreach (SnowOverlayLayerRecipe layer in recipe.Layers)
        {
            for (int i = 0; i < layer.Count; i++)
                DrawSnowParticle(canvas, canvasW, canvasH, recipe, layer, i, progress, sprites);
        }
    }

    private static void DrawSnowParticle(SKCanvas canvas, int canvasW, int canvasH,
        SnowfallOverlayRecipe recipe, SnowOverlayLayerRecipe layer, int index, double progress, SKImage[] sprites)
    {
        // Giữ nguyên thứ tự gọi Random.NextDouble() với cùng seed để determinism identical với WPF.
        var rng = new Random(SnowfallOverlayPreset.HashSeed(recipe.PresetName, layer.Name, index));
        double size = SnowfallOverlayPreset.Lerp(layer.MinSize, layer.MaxSize, rng.NextDouble());
        double opacityPulse = 0.96 + (0.22 * Math.Sin((progress * Math.PI * 2.0 * (1.0 + rng.NextDouble())) + (rng.NextDouble() * Math.PI * 2.0)));
        double opacity = Math.Clamp(
            SnowfallOverlayPreset.Lerp(layer.MinOpacity, layer.MaxOpacity, rng.NextDouble()) * opacityPulse,
            0.05, 1.0);
        double verticalLoops = SnowfallOverlayPreset.Lerp(layer.MinVerticalLoops, layer.MaxVerticalLoops, rng.NextDouble());
        double sway = SnowfallOverlayPreset.Lerp(layer.MinSway, layer.MaxSway, rng.NextDouble());
        double drift = SnowfallOverlayPreset.Lerp(layer.MinDrift, layer.MaxDrift, rng.NextDouble());
        double baseX = rng.NextDouble() * canvasW;
        double baseY = rng.NextDouble();
        double phase = rng.NextDouble() * Math.PI * 2.0;
        double frequency = 1.0 + (rng.NextDouble() * 2.5);
        double margin = Math.Max(12.0, size * 3.0);
        double rangeH = canvasH + (margin * 2.0);
        double rangeW = canvasW + (margin * 2.0);

        double totalFall = (baseY * rangeH) + (verticalLoops * progress * rangeH);
        double fallPosition = SnowfallOverlayPreset.PositiveMod(totalFall, rangeH);
        double descentTravel = baseY + (verticalLoops * progress);
        double flutter =
            (Math.Sin((progress * Math.PI * 2.0 * frequency) + phase) * sway * 0.12) +
            (Math.Cos((progress * Math.PI * 2.0 * (0.45 + (frequency * 0.18))) + phase) * sway * 0.04);
        double windPush = drift * (0.35 + (descentTravel * 4.20));
        double particleX = 0 - margin + SnowfallOverlayPreset.PositiveMod(baseX + windPush + flutter + margin, rangeW);
        double particleY = 0 - margin + fallPosition;

        if (layer.UseStreaks)
        {
            DrawStreakParticle(canvas, particleX, particleY, size, opacity, layer, phase, progress);
            return;
        }

        SKImage sprite = sprites[Math.Abs(SnowfallOverlayPreset.HashSeed("snowflake", layer.Name, index)) % sprites.Length];
        double spriteSize = Math.Max(8.0, size * 4.60);
        double rotation = (rng.NextDouble() * 360.0) + (Math.Sin((progress * Math.PI * 2.0 * (0.7 + rng.NextDouble())) + phase) * 8.0);

        if (layer.UseGlow || layer.BlurMultiplier > 0.01)
        {
            double glowSize = spriteSize * (1.24 + (layer.BlurMultiplier * 0.12));
            DrawSnowflakeSprite(canvas, sprite, particleX, particleY, glowSize, opacity * 0.26, rotation);
        }

        DrawSnowflakeSprite(canvas, sprite, particleX, particleY, spriteSize, opacity, rotation);
    }

    private static void DrawStreakParticle(SKCanvas canvas, double x, double y, double size, double opacity,
        SnowOverlayLayerRecipe layer, double phase, double progress)
    {
        double angle = (72.0 + (Math.Sin((progress * Math.PI * 2.0 * 0.75) + phase) * 4.0)) * (Math.PI / 180.0);
        double length = Math.Max(size * 2.0, size * layer.StreakLengthMultiplier);
        double dx = Math.Cos(angle) * length * 0.5;
        double dy = Math.Sin(angle) * length * 0.5;
        float startX = (float)(x - dx), startY = (float)(y - dy);
        float endX = (float)(x + dx), endY = (float)(y + dy);

        using var glowPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(1.0, size * 0.95),
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(opacity * 0.24 * 255.0), 0, 255)),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        using var corePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(1.0, size * 0.56),
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(Math.Min(1.0, opacity * 0.82) * 255.0), 0, 255)),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(startX, startY, endX, endY, glowPaint);
        canvas.DrawLine(startX, startY, endX, endY, corePaint);

        float tailX = (float)(x - (dx * 0.70));
        float tailY = (float)(y - (dy * 0.70));
        using var tailPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(opacity * 0.16 * 255.0), 0, 255)),
            IsAntialias = true,
        };
        canvas.DrawCircle(tailX, tailY, (float)(size * 0.92), tailPaint);
    }

    private static SKImage[] CreateSnowflakeSprites()
    {
        var sprites = new SKImage[5];
        for (int variant = 0; variant < 5; variant++)
            sprites[variant] = CreateSnowflakeSprite(128, variant);
        return sprites;
    }

    private static SKImage CreateSnowflakeSprite(int canvasSize, int variant)
    {
        double center = canvasSize / 2.0;
        SKPoint pivot = new SKPoint((float)center, (float)center);
        double radius = canvasSize * (0.28 + (variant * 0.012));
        double branchInsetA = 0.42 + (variant * 0.03);
        double branchInsetB = 0.66 + (variant * 0.02);
        double branchLengthA = canvasSize * (0.09 + (variant * 0.006));
        double branchLengthB = canvasSize * (0.07 + (variant * 0.005));
        double branchAngleA = (26.0 + (variant * 2.0)) * (Math.PI / 180.0);
        double branchAngleB = (18.0 + (variant * 1.5)) * (Math.PI / 180.0);
        double coreThickness = Math.Max(1.9, canvasSize * 0.030);
        double branchThickness = Math.Max(1.2, coreThickness * 0.56);

        using var bitmap = new SKBitmap(canvasSize, canvasSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var mainPen = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)coreThickness,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(0.96 * 255.0), 0, 255)),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using var branchPen = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)branchThickness,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(0.90 * 255.0), 0, 255)),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using var sparklePen = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(1.0, branchThickness * 0.62),
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(0.68 * 255.0), 0, 255)),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        for (int arm = 0; arm < 6; arm++)
        {
            double baseAngle = (Math.PI / 3.0 * arm) + (variant * 0.045);
            SKPoint armStart = PointOnCircle(pivot, radius * 0.08, baseAngle + Math.PI);
            SKPoint armEnd = PointOnCircle(pivot, radius, baseAngle);
            canvas.DrawLine(armStart, armEnd, mainPen);

            DrawBranchPair(canvas, pivot, baseAngle, radius * branchInsetA, branchLengthA, branchAngleA, branchPen);
            DrawBranchPair(canvas, pivot, baseAngle, radius * branchInsetB, branchLengthB, branchAngleB, branchPen);

            if ((variant % 2) == 0)
            {
                double miniInset = radius * (0.78 + (arm % 2 == 0 ? 0.00 : 0.04));
                double miniLength = branchLengthB * 0.58;
                DrawBranchPair(canvas, pivot, baseAngle, miniInset, miniLength, branchAngleB * 0.8, sparklePen);
            }
        }

        double innerRadius = canvasSize * (0.072 + (variant * 0.004));
        using var innerBrush = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(0.90 * 255.0), 0, 255)),
            IsAntialias = true,
        };
        canvas.DrawCircle(pivot, (float)innerRadius, innerBrush);

        using var outerBrush = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(0.18 * 255.0), 0, 255)),
            IsAntialias = true,
        };
        canvas.DrawCircle(pivot, (float)(innerRadius * 1.80), outerBrush);

        return SKImage.FromBitmap(bitmap);
    }

    private static void DrawBranchPair(SKCanvas canvas, SKPoint pivot, double baseAngle,
        double inset, double branchLength, double branchAngle, SKPaint pen)
    {
        SKPoint origin = PointOnCircle(pivot, inset, baseAngle);
        SKPoint first = PointOnCircle(origin, branchLength, baseAngle + Math.PI - branchAngle);
        SKPoint second = PointOnCircle(origin, branchLength, baseAngle + Math.PI + branchAngle);
        canvas.DrawLine(origin, first, pen);
        canvas.DrawLine(origin, second, pen);
    }

    private static SKPoint PointOnCircle(SKPoint pivot, double radius, double angle)
        => new SKPoint(
            (float)(pivot.X + (Math.Cos(angle) * radius)),
            (float)(pivot.Y + (Math.Sin(angle) * radius)));

    private static void DrawSnowflakeSprite(SKCanvas canvas, SKImage sprite, double centerX, double centerY,
        double size, double opacity, double rotationDegrees)
    {
        if (opacity <= 0.001 || size <= 1.0) return;

        double safeSize = Math.Max(2.0, size);
        float left = (float)(centerX - (safeSize / 2.0));
        float top = (float)(centerY - (safeSize / 2.0));
        SKRect rect = new SKRect(left, top, left + (float)safeSize, top + (float)safeSize);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(opacity * 255.0), 0, 255)),
        };

        canvas.Save();
        canvas.Translate((float)centerX, (float)centerY);
        canvas.RotateDegrees((float)rotationDegrees);
        canvas.Translate(-(float)centerX, -(float)centerY);
        canvas.DrawImage(sprite, rect, paint);
        canvas.Restore();
    }

    public Task<DreamyDotGeneratedOverlay> GenerateDreamyDotOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, double outputDurationSeconds, RenderJob job,
        string? tag, CancellationToken token, Action<string> onLog)
    {
        // Phase 4: vẽ thật bằng SkiaSharp (không cần WPF/STA). Giữ contract DTO
        // (TempRoot, OverlayPattern dreamy_dot_overlay_%04d.png, FrameCount/FrameRate/Width/Height/Variant/AssetCacheKey)
        // + preview_01..preview_08.png. Determinism giữ nguyên: cùng HashSeed(layer, index) → cùng
        // thứ tự Random.NextDouble → cùng vị trí/chất particle của bản WPF.
        try
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"dreamydot_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "dreamy_dot_overlay_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);

            var plan = DreamyDotOverlayPreset.CreateOverlayPlan(safeW, safeH, frameRate, outputDurationSeconds, job);
            var settings = plan.Settings;
            double safeFps = plan.SafeFps;
            int renderW = plan.RenderWidth;
            int renderH = plan.RenderHeight;
            int frameCount = plan.FrameCount;
            string assetCacheKey = plan.AssetCacheKey;

            var renderLayout = new OverlayContentLayout
            {
                CanvasWidth = renderW,
                CanvasHeight = renderH,
                ContentRect = new PixelRect(0, 0, renderW, renderH)
            };

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(previewDir);

            using var bitmap = new SKBitmap(renderW, renderH);
            using var canvas = new SKCanvas(bitmap);

            for (int i = 0; i < frameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                double progress = frameCount <= 1 ? 0.0 : i / (double)frameCount;
                canvas.Clear(SKColors.Transparent);

                DrawDotLayers(canvas, renderW, renderH, settings, progress);

                string overlayFile = DreamyDotOverlayPreset.FormatSequenceFilePath(overlayPattern, i + 1);
                SavePng(bitmap, overlayFile);

                if (i < 8)
                {
                    string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                    SavePng(bitmap, previewFile);
                }
            }

            onLog($"[DREAMY-DOT] Generated dreamy dot drift overlay @ {renderW}x{renderH}, fps={safeFps:0.00}, frames={frameCount}, preview={previewDir}");

            return Task.FromResult(new DreamyDotGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                Width = renderW,
                Height = renderH,
                Variant = settings.Variant,
                AssetCacheKey = assetCacheKey,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot },
            });
        }
        catch (Exception ex)
        {
            onLog($"[DREAMY-DOT-ERROR] {ex.Message} — lớp overlay bị bỏ qua.");
            return Task.FromResult(new DreamyDotGeneratedOverlay());
        }
    }

    private static void DrawDotLayers(SKCanvas canvas, int canvasW, int canvasH,
        DreamyDotOverlaySettings settings, double progress)
    {
        foreach (DreamyDotLayerRecipe layer in settings.Layers)
        {
            for (int i = 0; i < layer.Count; i++)
                DrawParticle(canvas, canvasW, canvasH, settings, layer, i, progress);
        }
    }

    private static void DrawParticle(SKCanvas canvas, int canvasW, int canvasH,
        DreamyDotOverlaySettings settings, DreamyDotLayerRecipe layer, int particleIndex, double progress)
    {
        // Giữ nguyên thứ tự gọi Random.NextDouble() với cùng seed để determinism identical với WPF.
        var rng = new Random(DreamyDotOverlayPreset.HashSeed(layer.Name, particleIndex));
        double radius = DreamyDotOverlayPreset.Lerp(layer.MinRadius, layer.MaxRadius, rng.NextDouble());
        double opacityPulse = 0.90 + (0.24 * Math.Sin((progress * Math.PI * 2.0 * (0.55 + (rng.NextDouble() * 1.4))) + (rng.NextDouble() * Math.PI * 2.0)));
        double opacity = Math.Clamp(
            DreamyDotOverlayPreset.Lerp(layer.MinOpacity, layer.MaxOpacity, rng.NextDouble()) * opacityPulse,
            0.01, 1.0);
        double travelLoops = DreamyDotOverlayPreset.Lerp(layer.MinTravelLoops, layer.MaxTravelLoops, rng.NextDouble());
        double stretch = DreamyDotOverlayPreset.Lerp(layer.MinStretch, layer.MaxStretch, rng.NextDouble());
        SKPoint particleCenter;
        SKPoint direction;

        if (settings.Variant.Equals("chaos", StringComparison.OrdinalIgnoreCase))
        {
            particleCenter = ResolveChaosParticleCenter(canvasW, canvasH, settings, layer, particleIndex, progress, radius, travelLoops, out direction);
        }
        else
        {
            direction = ResolveParticleDirection(settings, layer, particleIndex, progress, rng);
            SKPoint perpendicular = new SKPoint((float)(-direction.Y), (float)direction.X);
            double diagonal = Math.Sqrt((canvasW * canvasW) + (canvasH * canvasH));
            double margin = Math.Max(radius * (5.0 + layer.GlowScale), 24.0);
            double pathSpan = diagonal + (margin * 2.0);
            double acrossSpan = diagonal + (margin * 2.0);
            double centerX = canvasW * 0.5;
            double centerY = canvasH * 0.5;
            double alongStart = (rng.NextDouble() * pathSpan) - (pathSpan * 0.5);
            double acrossStart = (rng.NextDouble() * acrossSpan) - (acrossSpan * 0.5);
            double alongPosition = DreamyDotOverlayPreset.PositiveMod(alongStart + (progress * travelLoops * pathSpan) + (pathSpan * 0.5), pathSpan) - (pathSpan * 0.5);
            double driftPulse = Math.Sin((progress * Math.PI * 2.0 * (0.24 + (rng.NextDouble() * 0.42))) + (rng.NextDouble() * Math.PI * 2.0)) * radius * 0.45;
            particleCenter = new SKPoint(
                (float)(centerX + (direction.X * alongPosition) + (perpendicular.X * (acrossStart + driftPulse))),
                (float)(centerY + (direction.Y * alongPosition) + (perpendicular.Y * (acrossStart + driftPulse))));
        }

        double angleDegrees = Math.Atan2(direction.Y, direction.X) * (180.0 / Math.PI);

        int paletteIndex = SelectPaletteIndex(layer.Name, particleIndex);
        SKColor color = DreamyPalette[paletteIndex];

        double haloRadiusX = radius * stretch * (2.0 + (layer.GlowScale * 0.45));
        double haloRadiusY = radius * (2.0 + (layer.GlowScale * 0.45));
        double coreRadiusX = radius * stretch;
        double coreRadiusY = radius;

        float haloAlpha = (float)Math.Clamp(Math.Round(opacity * (0.56 + (layer.GlowScale * 0.12)) * 255.0), 0, 255);
        float coreAlpha = (float)Math.Clamp(Math.Round(opacity * 1.12 * 255.0), 0, 255);

        using var haloPaint = MakeRadialPaint(color, 0.84, 0.28, 0.08, Math.Max(haloRadiusX, haloRadiusY), (byte)haloAlpha);
        using var corePaint = MakeRadialPaint(color, 1.00, 0.80, 0.20, Math.Max(coreRadiusX, coreRadiusY), (byte)coreAlpha);

        canvas.Save();
        canvas.Translate(particleCenter.X, particleCenter.Y);
        canvas.RotateDegrees((float)angleDegrees);

        canvas.DrawOval(new SKRect((float)(-haloRadiusX), (float)(-haloRadiusY), (float)haloRadiusX, (float)haloRadiusY), haloPaint);
        canvas.DrawOval(new SKRect((float)(-coreRadiusX), (float)(-coreRadiusY), (float)coreRadiusX, (float)coreRadiusY), corePaint);

        if (layer.DrawCore)
        {
            double coreGlowRadius = Math.Max(1.2, radius * 0.28);
            using var sparkPaint = MakeSolidPaint(color, opacity * 1.06);
            canvas.DrawOval(
                new SKRect(
                    (float)(-coreGlowRadius * Math.Max(1.0, stretch * 0.75)),
                    (float)(-coreGlowRadius),
                    (float)(coreGlowRadius * Math.Max(1.0, stretch * 0.75)),
                    (float)coreGlowRadius),
                sparkPaint);
        }

        canvas.Restore();
    }

    private static SKPoint ResolveChaosParticleCenter(int canvasW, int canvasH,
        DreamyDotOverlaySettings settings, DreamyDotLayerRecipe layer, int particleIndex, double progress,
        double radius, double travelLoops, out SKPoint direction)
    {
        double margin = Math.Max(radius * (6.0 + layer.GlowScale), 28.0);
        double spanX = canvasW + (margin * 2.0);
        double spanY = canvasH + (margin * 2.0);
        double sampleStep = 0.0025;
        int stableSeed = DreamyDotOverlayPreset.HashSeed(layer.Name, particleIndex);

        SKPoint current = EvaluateChaosParticlePosition(canvasW, canvasH, settings, layer, progress, radius, travelLoops, new Random(stableSeed), spanX, spanY, margin);
        SKPoint ahead = EvaluateChaosParticlePosition(canvasW, canvasH, settings, layer, DreamyDotOverlayPreset.PositiveMod(progress + sampleStep, 1.0), radius, travelLoops, new Random(stableSeed), spanX, spanY, margin);
        direction = NormalizeDirection(new SKPoint((float)(ahead.X - current.X), (float)(ahead.Y - current.Y)));
        return current;
    }

    private static SKPoint EvaluateChaosParticlePosition(int canvasW, int canvasH,
        DreamyDotOverlaySettings settings, DreamyDotLayerRecipe layer, double progress,
        double radius, double travelLoops, Random rng, double spanX, double spanY, double margin)
    {
        double diagonal = Math.Sqrt((canvasW * canvasW) + (canvasH * canvasH));
        double layerBias = DreamyDotOverlayPreset.GetChaosLayerBias(layer.Name);
        double phase = rng.NextDouble();
        double localProgress = DreamyDotOverlayPreset.PositiveMod((progress * travelLoops) + phase, 1.0);
        double anchorX = (rng.NextDouble() * spanX) - margin;
        double anchorY = (rng.NextDouble() * spanY) - margin;

        double driftAngle = (rng.NextDouble() * Math.PI * 2.0) + layerBias;
        double driftDistance = diagonal * (0.10 + (rng.NextDouble() * 0.38) + (travelLoops * 0.18));
        double driftSpeed = 0.40 + (rng.NextDouble() * 0.90);

        double ampX1 = radius * (3.2 + settings.SwirlAmount + (rng.NextDouble() * 3.2));
        double ampX2 = radius * (1.8 + (rng.NextDouble() * 2.6));
        double ampY1 = radius * (3.0 + (settings.SwirlAmount * 1.15) + (rng.NextDouble() * 3.6));
        double ampY2 = radius * (1.7 + (rng.NextDouble() * 2.9));

        double freqX1 = 0.45 + (rng.NextDouble() * 1.75);
        double freqX2 = 0.70 + (rng.NextDouble() * 2.20);
        double freqY1 = 0.42 + (rng.NextDouble() * 1.82);
        double freqY2 = 0.66 + (rng.NextDouble() * 2.10);

        double phaseX1 = rng.NextDouble() * Math.PI * 2.0;
        double phaseX2 = rng.NextDouble() * Math.PI * 2.0;
        double phaseY1 = rng.NextDouble() * Math.PI * 2.0;
        double phaseY2 = rng.NextDouble() * Math.PI * 2.0;

        double drift = localProgress * Math.PI * 2.0 * driftSpeed;
        double driftX = Math.Cos(driftAngle) * driftDistance * Math.Sin(drift + phaseX1) * 0.55;
        double driftY = Math.Sin(driftAngle) * driftDistance * Math.Cos(drift + phaseY1) * 0.55;

        double motionX =
            (Math.Sin((localProgress * Math.PI * 2.0 * freqX1) + phaseX1) * ampX1) +
            (Math.Cos((localProgress * Math.PI * 2.0 * freqX2) + phaseX2) * ampX2);
        double motionY =
            (Math.Cos((localProgress * Math.PI * 2.0 * freqY1) + phaseY1) * ampY1) +
            (Math.Sin((localProgress * Math.PI * 2.0 * freqY2) + phaseY2) * ampY2);

        double x = DreamyDotOverlayPreset.PositiveMod(anchorX + driftX + motionX, spanX) - margin;
        double y = DreamyDotOverlayPreset.PositiveMod(anchorY + driftY + motionY, spanY) - margin;
        return new SKPoint((float)(x), (float)(y));
    }

    private static SKPoint ResolveParticleDirection(DreamyDotOverlaySettings settings,
        DreamyDotLayerRecipe layer, int particleIndex, double progress, Random rng)
    {
        SKPoint baseDirection = new SKPoint((float)settings.Direction.X, (float)settings.Direction.Y);
        if (!settings.Variant.Equals("chaos", StringComparison.OrdinalIgnoreCase))
            return baseDirection;

        double baseAngle = Math.Atan2(settings.Direction.Y, settings.Direction.X);
        double randomAngle = (rng.NextDouble() * Math.PI * 2.0) - Math.PI;
        double wobble = Math.Sin((progress * Math.PI * 2.0 * (0.42 + (rng.NextDouble() * 0.86))) + (particleIndex * 0.37)) * settings.DirectionJitter;
        double layerBias = layer.Name switch
        {
            "dust" => -0.22,
            "soft" => 0.18,
            "bokeh" => -0.10,
            _ => 0.0
        };

        double finalAngle = baseAngle + randomAngle + wobble + layerBias;
        return NormalizeDirection(new SKPoint((float)Math.Cos(finalAngle), (float)Math.Sin(finalAngle)));
    }

    private static SKPoint NormalizeDirection(SKPoint direction)
    {
        double len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (len < 0.0001)
            return new SKPoint(1.0f, 0.0f);
        return new SKPoint((float)(direction.X / len), (float)(direction.Y / len));
    }

    private static readonly SKColor[] DreamyPalette =
    {
        new SKColor(255, 255, 255),
        new SKColor(247, 239, 219),
        new SKColor(221, 242, 255),
        new SKColor(255, 231, 214),
    };

    private static SKPaint MakeRadialPaint(SKColor color, double centerAlpha, double midAlpha, double outerAlpha, double radius, byte alpha)
    {
        SKColor[] colors =
        {
            color.WithAlpha((byte)Math.Clamp(Math.Round(centerAlpha * 255.0), 0, 255)),
            color.WithAlpha((byte)Math.Clamp(Math.Round(midAlpha * 255.0), 0, 255)),
            color.WithAlpha((byte)Math.Clamp(Math.Round(outerAlpha * 255.0), 0, 255)),
            new SKColor(color.Red, color.Green, color.Blue, 0),
        };
        float[] positions = { 0.0f, 0.34f, 0.76f, 1.0f };
        return new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, alpha),
            Shader = SKShader.CreateRadialGradient(SKPoint.Empty, (float)Math.Max(1.0, radius), colors, positions, SKShaderTileMode.Clamp),
        };
    }

    private static SKPaint MakeSolidPaint(SKColor color, double alpha)
    {
        return new SKPaint
        {
            IsAntialias = true,
            Color = color.WithAlpha((byte)Math.Clamp(Math.Round(alpha * 255.0), 0, 255)),
        };
    }

    private static int SelectPaletteIndex(string layerName, int particleIndex)
    {
        int hash = Math.Abs(DreamyDotOverlayPreset.HashSeed(layerName, particleIndex));
        if (layerName.Equals("dust", StringComparison.OrdinalIgnoreCase))
            return hash % 2;
        if (layerName.Equals("soft", StringComparison.OrdinalIgnoreCase))
            return hash % 3;
        return hash % DreamyPalette.Length;
    }

    public Task<RgbPolaroidLedGeneratedOverlay> GenerateRgbPolaroidLedOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, double outputDurationSeconds, RenderJob job,
        string? tag, CancellationToken token, Action<string> onLog)
    {
        // Phase 4: vẽ thật bằng SkiaSharp (không cần WPF/STA). Giữ contract DTO
        // (TempRoot, OverlayPattern rgb_border_%04d.png, FrameCount=Clamp(Ceiling(CycleSeconds*fps),42,96),
        // FrameRate=safeFps, CycleSeconds=settings.CycleSeconds) + preview_01..preview_06.png.
        try
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"rgbpolaroid_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "rgb_border_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            var settings = RgbPolaroidLedPreset.FromJob(job, safeLayout);
            int frameCount = Math.Clamp((int)Math.Ceiling(settings.CycleSeconds * safeFps), 42, 96);

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(previewDir);

            double contentX = safeLayout.ContentX;
            double contentY = safeLayout.ContentY;
            double contentW = safeLayout.ContentWidth;
            double contentH = safeLayout.ContentHeight;

            double inset = settings.Inset;
            double w = Math.Max(32.0, contentW - inset * 2.0);
            double h = Math.Max(32.0, contentH - inset * 2.0);
            double radius = Math.Min(settings.CornerRadius, Math.Min(w, h) * 0.25);
            var frameLayout = new FrameLayoutData(contentX + inset, contentY + inset, w, h, radius);

            double sampleCount = Math.Max(260.0, Math.Ceiling(frameLayout.Perimeter / settings.SampleSpacing));

            double outerStroke = OverlayLayoutHelper.ScaleFromHdReference(safeLayout, 5.6, 2.0, 16.0);
            double shadowStroke = OverlayLayoutHelper.ScaleFromHdReference(safeLayout, 6.6, 2.0, 18.0);
            SKColor paperColor = new SKColor(255, 249, 240, 238);
            SKColor shadowColor = new SKColor(0, 0, 0, 38);

            using var bitmap = new SKBitmap(safeW, safeH);
            using var canvas = new SKCanvas(bitmap);

            for (int i = 0; i < frameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                canvas.Clear(SKColors.Transparent);

                double progress = frameCount <= 1 ? 0.0 : (double)i / frameCount;
                double travel = (progress * settings.AnimationSpeed) % 1.0;
                double hueBands = Math.Max(1.05, settings.HueSpeed);

                // Khung giấy (màu giấy + bóng đệm) — vẽ 1 lần/frame, không phụ thuộc progress.
                DrawRgbPaperFrame(canvas, frameLayout, settings, outerStroke, shadowStroke, paperColor, shadowColor);

                for (int si = 0; si < sampleCount; si++)
                {
                    double distance = (si / sampleCount) * frameLayout.Perimeter;
                    SKPoint point = frameLayout.GetPointAt(distance);
                    double frac = distance / frameLayout.Perimeter;
                    double hue = (((frac * hueBands) + travel) % 1.0) * 360.0;
                    double wave = 0.5 + (0.5 * Math.Sin(((frac * Math.PI * 2.0 * 3.0) - (travel * Math.PI * 2.0))));
                    double vibrance = 0.90 + (0.10 * wave);
                    double glowAlpha = settings.GlowOpacity * (0.88 + (0.20 * wave));
                    double coreAlpha = settings.CoreOpacity * (0.92 + (0.10 * wave));
                    double bloomRadius = settings.GlowSize * (1.02 + (0.14 * wave));
                    double glowRadius = Math.Max(settings.BorderThickness * 1.22, settings.GlowSize * (0.70 + (0.08 * wave)));
                    double coreRadius = Math.Max(1.8, settings.BorderThickness * (0.44 + (0.06 * wave)));

                    SKColor baseColor = ColorFromHsv(hue, 0.98, vibrance);
                    SKColor glowColor = ColorFromHsv(hue + 10.0, 0.94, Math.Min(1.0, 0.94 + (0.04 * wave)));
                    SKColor coreColor = ColorFromHsv(hue, 0.82, 1.0);

                    DrawSoftDisc(canvas, point, bloomRadius, baseColor, (float)(glowAlpha * 0.26 * 255.0));
                    DrawSoftDisc(canvas, point, glowRadius, glowColor, (float)(glowAlpha * 255.0));
                    DrawSoftDisc(canvas, point, coreRadius, coreColor, (float)(coreAlpha * 255.0));
                }

                string overlayFile = RgbPolaroidLedPreset.FormatSequenceFilePath(overlayPattern, i + 1);
                SavePng(bitmap, overlayFile);

                if (i < 6)
                {
                    string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                    SavePng(bitmap, previewFile);
                }
            }

            onLog($"[RGB-POLAROID] Generated {frameCount} LED border frames @ {safeW}x{safeH}. Preview={previewDir}");

            return Task.FromResult(new RgbPolaroidLedGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                CycleSeconds = settings.CycleSeconds,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot },
            });
        }
        catch (Exception ex)
        {
            onLog($"[RGB-POLAROID-ERROR] {ex.Message} — lớp overlay bị bỏ qua.");
            return Task.FromResult(new RgbPolaroidLedGeneratedOverlay());
        }
    }

    private static void DrawRgbPaperFrame(SKCanvas canvas, FrameLayoutData layout, RgbPolaroidLedSettings settings,
        double outerStroke, double shadowStroke, SKColor paperColor, SKColor shadowColor)
    {
        double offset = Math.Max(2.0, settings.BorderThickness * 0.40);
        SKRect shadowRect = new SKRect(
            (float)(layout.X + offset), (float)(layout.Y + offset),
            (float)(layout.X + offset + layout.W), (float)(layout.Y + offset + layout.H));
        using var shadowPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)shadowStroke,
            Color = shadowColor,
            IsAntialias = true,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawRoundRect(shadowRect, (float)layout.R, (float)layout.R, shadowPaint);

        SKRect paperRect = new SKRect(
            (float)layout.X, (float)layout.Y,
            (float)(layout.X + layout.W), (float)(layout.Y + layout.H));
        using var paperPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)outerStroke,
            Color = paperColor,
            IsAntialias = true,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawRoundRect(paperRect, (float)layout.R, (float)layout.R, paperPaint);
    }

    private static void DrawSoftDisc(SKCanvas canvas, SKPoint center, double radius, SKColor color, float alpha)
    {
        if (radius <= 0.1 || alpha <= 0.0f) return;

        using var paint = new SKPaint { IsAntialias = true };
        SKColor[] colors =
        {
            color.WithAlpha((byte)Math.Clamp(Math.Round(alpha), 0, 255)),
            color.WithAlpha((byte)Math.Clamp(Math.Round(alpha * 0.62), 0, 255)),
            color.WithAlpha((byte)Math.Clamp(Math.Round(alpha * 0.16), 0, 255)),
            new SKColor(color.Red, color.Green, color.Blue, 0),
        };
        float[] positions = { 0.0f, 0.38f, 0.72f, 1.0f };
        paint.Shader = SKShader.CreateRadialGradient(center, (float)radius, colors, positions, SKShaderTileMode.Clamp);
        canvas.DrawCircle(center.X, center.Y, (float)radius, paint);
    }

    private static SKColor ColorFromHsv(double hue, double saturation, double value)
    {
        double h = hue % 360.0;
        if (h < 0) h += 360.0;
        double s = Math.Clamp(saturation, 0.0, 1.0);
        double v = Math.Clamp(value, 0.0, 1.0);
        double c = v * s;
        double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
        double m = v - c;

        double r, g, b;
        if (h < 60)
            (r, g, b) = (c, x, 0);
        else if (h < 120)
            (r, g, b) = (x, c, 0);
        else if (h < 180)
            (r, g, b) = (0, c, x);
        else if (h < 240)
            (r, g, b) = (0, x, c);
        else if (h < 300)
            (r, g, b) = (x, 0, c);
        else
            (r, g, b) = (c, 0, x);

        return new SKColor(
            (byte)Math.Clamp(Math.Round((r + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255.0), 0, 255));
    }

    public Task<PolaroidScrapbook2GeneratedOverlay> GeneratePolaroidScrapbook2OverlayAsync(int width, int height,
        OverlayContentLayout? layout, RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
    {
        // Phase 4: vẽ thật bằng SkiaSharp (không cần WPF/STA). Giữ nguyên contract DTO +
        // pattern polaroid2_%04d.png, FrameCount = 1, FrameRate = 1.0.
        try
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"polaroid2_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "polaroid2_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            var settings = PolaroidScrapbook2Preset.FromJob(job, safeLayout);

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(previewDir);

            double contentX = safeLayout.ContentX;
            double contentY = safeLayout.ContentY;
            double contentW = safeLayout.ContentWidth;
            double contentH = safeLayout.ContentHeight;
            float inset = (float)settings.Inset;
            float cornerRadius = (float)settings.CornerRadius;

            float frameLeft = (float)(contentX + inset);
            float frameTop = (float)(contentY + inset);
            float frameW = (float)Math.Max(24.0, contentW - inset * 2.0);
            float frameH = (float)Math.Max(24.0, contentH - inset * 2.0);
            SKRect frameRect = new SKRect(frameLeft, frameTop, frameLeft + frameW, frameTop + frameH);

            float shadowOffset = (float)settings.ShadowOffset;
            SKRect shadowRect = new SKRect(
                frameLeft + shadowOffset, frameTop + shadowOffset,
                frameLeft + shadowOffset + frameW, frameTop + shadowOffset + frameH);

            using var bitmap = new SKBitmap(safeW, safeH);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            // Shadow (stroke only, offset xuống phải so khung).
            using var shadowPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)settings.ShadowThickness,
                Color = new SKColor(0, 0, 0, (byte)Math.Round(settings.ShadowOpacity * 255.0)),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(shadowRect, cornerRadius, cornerRadius, shadowPaint);

            // Khung (stroke only).
            using var framePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)settings.StrokeThickness,
                Color = new SKColor(255, 249, 240, (byte)Math.Round(settings.FrameOpacity * 255.0)),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(frameRect, cornerRadius, cornerRadius, framePaint);

            // Tape (fill, xoay -6.5° quanh tâm của nó).
            using var tapePaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(231, 211, 168, (byte)Math.Round(settings.TapeOpacity * 255.0)),
                IsAntialias = true,
            };
            float tapeX = frameLeft + inset * 0.65f;
            float tapeY = Math.Max(10.0f, frameTop - (float)settings.TapeHeight * 0.35f);
            float tapeW = (float)settings.TapeWidth;
            float tapeH = (float)settings.TapeHeight;
            SKRect tapeRect = new SKRect(tapeX, tapeY, tapeX + tapeW, tapeY + tapeH);
            SKPoint tapeCenter = new SKPoint(tapeX + tapeW * 0.5f, tapeY + tapeH * 0.5f);
            DrawRotatedRoundRect(canvas, tapeRect, tapeH * 0.22f, tapeH * 0.22f, -6.5f, tapeCenter, tapePaint);

            // Sticky (fill, xoay 4° quanh tâm của nó).
            using var stickyPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(214, 240, 228, (byte)Math.Round(settings.StickyOpacity * 255.0)),
                IsAntialias = true,
            };
            float stickyX = frameLeft + frameW - (float)settings.StickyWidth - inset * 0.7f;
            float stickyY = frameTop + frameH - (float)settings.StickyHeight - Math.Max(18.0f, inset * 0.35f);
            float stickyW = (float)settings.StickyWidth;
            float stickyH = (float)settings.StickyHeight;
            SKRect stickyRect = new SKRect(stickyX, stickyY, stickyX + stickyW, stickyY + stickyH);
            SKPoint stickyCenter = new SKPoint(stickyX + stickyW * 0.5f, stickyY + stickyH * 0.5f);
            DrawRotatedRoundRect(canvas, stickyRect, stickyH * 0.18f, stickyH * 0.18f, 4.0f, stickyCenter, stickyPaint);

            string outputFile = PolaroidScrapbook2Preset.FormatSequenceFilePath(overlayPattern, 1);
            SavePng(bitmap, outputFile);
            SavePng(bitmap, Path.Combine(previewDir, "preview_01.png"));

            onLog($"[POLAROID-2] Generated rounded scrapbook overlay @ {safeW}x{safeH}. Preview={previewDir}");

            return Task.FromResult(new PolaroidScrapbook2GeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = 1,
                FrameRate = 1.0,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot },
            });
        }
        catch (Exception ex)
        {
            onLog($"[POLAROID-2-ERROR] {ex.Message} — lớp overlay bị bỏ qua.");
            return Task.FromResult(new PolaroidScrapbook2GeneratedOverlay());
        }
    }

    private static void DrawRotatedRoundRect(SKCanvas canvas, SKRect rect, float rx, float ry, float degrees,
        SKPoint center, SKPaint paint)
    {
        canvas.Save();
        canvas.Translate(center.X, center.Y);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-center.X, -center.Y);
        canvas.DrawRoundRect(rect, rx, ry, paint);
        canvas.Restore();
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    public Task<DashedPolaroidGeneratedOverlay> GenerateDashedPolaroidOverlayAsync(int width, int height,
        OverlayContentLayout? layout, double frameRate, RenderJob job, string? tag,
        CancellationToken token, Action<string> onLog)
    {
        // Phase 4: vẽ thật bằng SkiaSharp (không cần WPF/STA). Giữ contract DTO +
        // pattern dashed_frame_%04d.png, frameCount = Clamp(Ceiling(CycleSeconds*fps), 20, 72).
        try
        {
            string safeTag = string.IsNullOrWhiteSpace(tag)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Regex.Replace(tag.Trim(), @"[^\w\-]+", "_");
            string tempRoot = Path.Combine(Path.GetTempPath(), "TitanEngine", $"dashedpolaroid_{safeTag}");
            string overlayPattern = Path.Combine(tempRoot, "dashed_frame_%04d.png");
            string previewDir = Path.Combine(tempRoot, "preview");
            int safeW = Math.Max(2, width);
            int safeH = Math.Max(2, height);
            double safeFps = Math.Clamp(frameRate, 12.0, 60.0);
            OverlayContentLayout safeLayout = layout ?? OverlayLayoutHelper.Create(safeW, safeH, safeW, safeH);
            var settings = DashedPolaroidFramePreset.FromJob(job, safeLayout);
            int frameCount = Math.Clamp((int)Math.Ceiling(settings.CycleSeconds * safeFps), 20, 72);

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(previewDir);

            double contentX = safeLayout.ContentX;
            double contentY = safeLayout.ContentY;
            double contentW = safeLayout.ContentWidth;
            double contentH = safeLayout.ContentHeight;

            double inset = settings.Inset;
            double w = Math.Max(24.0, contentW - inset * 2.0);
            double h = Math.Max(24.0, contentH - inset * 2.0);
            double radius = Math.Min(settings.CornerRadius, Math.Min(w, h) * 0.25);
            var frameLayout = new FrameLayoutData(contentX + inset, contentY + inset, w, h, radius);

            double shadowOffset = Math.Max(2.0, settings.StrokeThickness * 0.38);
            var shadowLayout = new FrameLayoutData(
                contentX + inset + shadowOffset, contentY + inset + shadowOffset, w, h, radius);

            double step = Math.Max(4.0, settings.DashLength + settings.DashGap);
            int dashCount = Math.Max(8, (int)Math.Ceiling(frameLayout.Perimeter / step));
            double dashStride = frameLayout.Perimeter / dashCount;
            double dashLength = Math.Min(settings.DashLength, Math.Max(8.0, dashStride * 0.78));

            // Bút vẽ bóng (biến đổi không theo dash → hoisting).
            byte shadowAlpha = (byte)Math.Clamp(Math.Round(settings.ShadowOpacity * 255.0), 0, 255);
            using var shadowPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)settings.ShadowThickness,
                Color = new SKColor(0, 0, 0, shadowAlpha),
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };

            using var bitmap = new SKBitmap(safeW, safeH);
            using var canvas = new SKCanvas(bitmap);

            for (int i = 0; i < frameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                canvas.Clear(SKColors.Transparent);

                double progress = frameCount <= 1 ? 0.0 : (double)i / frameCount;
                double runner = progress * dashCount;

                for (int di = 0; di < dashCount; di++)
                {
                    double startDistance = (di * dashStride) + Math.Max(0.0, (dashStride - dashLength) * 0.5);
                    SKPath? shadowPath = CreateSegmentPath(shadowLayout, startDistance, dashLength, settings.SampleSpacing);
                    SKPath? basePath = CreateSegmentPath(frameLayout, startDistance, dashLength, settings.SampleSpacing);

                    double distToRunner = DashedPolaroidFramePreset.CircularDistance(di, runner, dashCount);
                    double activeWeight = Math.Max(0.0, 1.0 - (distToRunner / 1.15));
                    double trailWeight = Math.Max(0.0, 1.0 - (distToRunner / 2.35));

                    // Bóng đệm (stroke, offset).
                    if (shadowPath != null && !shadowPath.IsEmpty)
                        canvas.DrawPath(shadowPath, shadowPaint);

                    // Glow outer + inner (màu trắng, alpha/dày dặn thay đổi theo activeWeight).
                    if (trailWeight > 0.01 && basePath != null && !basePath.IsEmpty)
                    {
                        double outerAlpha = settings.GlowOpacity * (0.34 + (1.02 * activeWeight));
                        double innerAlpha = settings.GlowOpacity * (0.22 + (0.82 * activeWeight));
                        double outerThickness = settings.GlowThickness * (1.02 + (0.24 * activeWeight));
                        double innerThickness = settings.GlowThickness * (0.66 + (0.14 * activeWeight));

                        using var glowOuterPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = (float)outerThickness,
                            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(outerAlpha * 255.0), 0, 255)),
                            IsAntialias = true,
                            StrokeCap = SKStrokeCap.Round,
                            StrokeJoin = SKStrokeJoin.Round,
                        };
                        using var glowInnerPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = (float)innerThickness,
                            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(innerAlpha * 255.0), 0, 255)),
                            IsAntialias = true,
                            StrokeCap = SKStrokeCap.Round,
                            StrokeJoin = SKStrokeJoin.Round,
                        };
                        canvas.DrawPath(basePath, glowOuterPaint);
                        canvas.DrawPath(basePath, glowInnerPaint);
                    }

                    // Khung chính (màu dominant, alpha/độ sáng thay đổi theo activeWeight/trailWeight).
                    if (basePath != null && !basePath.IsEmpty)
                    {
                        double brightBoost = 0.84 + (0.34 * trailWeight) + (0.10 * activeWeight);
                        byte frameAlpha = (byte)Math.Clamp(Math.Round(settings.FrameOpacity * brightBoost * 255.0), 0, 255);
                        byte g = (byte)Math.Clamp(Math.Round(250 + (5 * activeWeight)), 0, 255);
                        byte b = (byte)Math.Clamp(Math.Round(244 + (11 * activeWeight)), 0, 255);

                        using var framePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = (float)settings.StrokeThickness,
                            Color = new SKColor(255, g, b, frameAlpha),
                            IsAntialias = true,
                            StrokeCap = SKStrokeCap.Round,
                            StrokeJoin = SKStrokeJoin.Round,
                        };
                        canvas.DrawPath(basePath, framePaint);
                    }

                    shadowPath?.Dispose();
                    basePath?.Dispose();
                }

                string overlayFile = DashedPolaroidFramePreset.FormatSequenceFilePath(overlayPattern, i + 1);
                SavePng(bitmap, overlayFile);

                if (i < 6)
                {
                    string previewFile = Path.Combine(previewDir, $"preview_{i + 1:D2}.png");
                    SavePng(bitmap, previewFile);
                }
            }

            onLog($"[DASHED-POLAROID] Generated rounded dashed frame overlay @ {safeW}x{safeH} with {frameCount} pulse frames. Preview={previewDir}");

            return Task.FromResult(new DashedPolaroidGeneratedOverlay
            {
                TempRoot = tempRoot,
                OverlayPattern = overlayPattern,
                FrameCount = frameCount,
                FrameRate = safeFps,
                PreviewDirectory = previewDir,
                TempArtifacts = new List<string> { tempRoot },
            });
        }
        catch (Exception ex)
        {
            onLog($"[DASHED-POLAROID-ERROR] {ex.Message} — lớp overlay bị bỏ qua.");
            return Task.FromResult(new DashedPolaroidGeneratedOverlay());
        }
    }

    /// <summary>Bố cục khung bo góc (mirror FrameLayout trong WpfOverlayArt): tính chu vi + lấy điểm theo khoảng cách dọc theo đường viền.</summary>
    private readonly struct FrameLayoutData
    {
        public double X { get; }
        public double Y { get; }
        public double W { get; }
        public double H { get; }
        public double R { get; }
        public double Line { get; }
        public double Arc { get; }
        public double Perimeter { get; }

        public FrameLayoutData(double x, double y, double width, double height, double radius)
        {
            X = x;
            Y = y;
            W = Math.Max(16.0, width);
            H = Math.Max(16.0, height);
            R = Math.Max(0.0, Math.Min(radius, Math.Min(W, H) * 0.25));
            Line = Math.Max(0.0, W - (2.0 * R));
            Arc = (Math.PI * R) / 2.0;
            Perimeter = (2.0 * Line) + (2.0 * Math.Max(0.0, H - (2.0 * R))) + (4.0 * Arc);
        }

        public SKPoint GetPointAt(double distance)
        {
            double d = distance % Perimeter;
            if (d < 0) d += Perimeter;

            double rightX = X + W - R;
            double bottomY = Y + H - R;
            double vertical = Math.Max(0.0, H - (2.0 * R));

            if (d <= Line)
                return new SKPoint((float)(X + R + d), (float)Y);

            d -= Line;
            if (d <= Arc)
                return ArcPoint(rightX, Y + R, R, -90.0 + ((d / Arc) * 90.0));

            d -= Arc;
            if (d <= vertical)
                return new SKPoint((float)(X + W), (float)(Y + R + d));

            d -= vertical;
            if (d <= Arc)
                return ArcPoint(rightX, bottomY, R, ((d / Arc) * 90.0));

            d -= Arc;
            if (d <= Line)
                return new SKPoint((float)(X + W - R - d), (float)(Y + H));

            d -= Line;
            if (d <= Arc)
                return ArcPoint(X + R, bottomY, R, 90.0 + ((d / Arc) * 90.0));

            d -= Arc;
            if (d <= vertical)
                return new SKPoint((float)X, (float)(Y + H - R - d));

            d -= vertical;
            return ArcPoint(X + R, Y + R, R, 180.0 + ((d / Arc) * 90.0));
        }

        private static SKPoint ArcPoint(double cx, double cy, double radius, double angleDegrees)
        {
            if (radius <= 0.001)
                return new SKPoint((float)cx, (float)cy);
            double radians = angleDegrees * Math.PI / 180.0;
            return new SKPoint(
                (float)(cx + (Math.Cos(radians) * radius)),
                (float)(cy + (Math.Sin(radians) * radius)));
        }
    }

    /// <summary>Xây SKPath mô tả một đoạn trên đường viền khung (mẫu điểm, stroke), mirror StreamGeometry gốc.</summary>
    private static SKPath? CreateSegmentPath(FrameLayoutData layout, double startDistance, double segmentLength, double sampleSpacing)
    {
        double safeSpacing = Math.Max(0.8, sampleSpacing);
        int subdivisions = Math.Max(3, (int)Math.Ceiling(segmentLength / safeSpacing));
        var path = new SKPath();
        path.MoveTo(layout.GetPointAt(startDistance));
        for (int stepIndex = 1; stepIndex <= subdivisions; stepIndex++)
        {
            double t = stepIndex / (double)subdivisions;
            double distance = startDistance + (segmentLength * t);
            path.LineTo(layout.GetPointAt(distance));
        }
        return path;
    }

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
