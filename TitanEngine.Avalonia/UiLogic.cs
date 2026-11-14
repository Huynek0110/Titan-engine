using System;
using System.Globalization;
using System.Text.RegularExpressions;
using TitanEngine.Core;

namespace TitanEngine.Avalonia;

/// <summary>
/// Giai đoạn 5 — logic UI dùng chung, mirror 1:1 các helper private của MainWindow (WPF).
/// Không có logic mới: mỗi hàm là bản chép hành vi của bản gốc (tên hàm WPF ghi ở comment).
/// </summary>
public static class UiLogic
{
    /// <summary>
    /// Mirror SoundBarStyleRenderer.UsesSquarePlot (TitanEngine/SoundBarStyleRenderer.cs:3999,
    /// internal của bản WPF). Sáu style này vẽ vòng/tròn được "vuông hoá theo pixel" — WIDTH
    /// không thể kéo dài hình, giới hạn bởi cạnh nhỏ hơn (đo thật, xem comment gốc; PeakStars
    /// bị loại vì mực trải hết chiều rộng plot). Không thêm style mới vào danh sách này.
    /// </summary>
    public static bool SoundBarUsesSquarePlot(SoundBarStyleId style)
        => style is SoundBarStyleId.RadialRing
            or SoundBarStyleId.DualRings
            or SoundBarStyleId.RippleRings
            or SoundBarStyleId.SpokeLines
            or SoundBarStyleId.CenterColumn
            or SoundBarStyleId.AudioRing;

    // ── MainWindow: SupportedMediaDialogFilter / AudioSourceExtensions / Is*SourcePath ──
    public const string SupportedMediaPatterns =
        "*.mp4;*.mov;*.avi;*.mkv;*.webm;*.m4v;*.wmv;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp;*.flv;*.f4v;*.vob;*.ogv;*.gif;*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.bmp;*.webp;*.tif;*.tiff";

    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg", ".ts",
        ".m2ts", ".mts", ".3gp", ".flv", ".f4v", ".vob", ".ogv"
    };

    public static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".webp", ".tif", ".tiff", ".gif"
    };

    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".wav", ".aac", ".m4a", ".m4b", ".flac", ".ogg", ".oga", ".opus",
        ".wma", ".aiff", ".aif", ".aifc", ".alac", ".ape", ".wv", ".mka", ".m4r",
        ".ac3", ".eac3", ".dts", ".amr", ".aaw", ".tta", ".tak", ".mpc", ".ra",
        ".rm", ".gsm", ".voc", ".spx", ".caf", ".au", ".snd", ".mid", ".midi"
    };

    public static string AudioSourceFilterPattern => "*" + string.Join(";*", AudioExtensions);

    public static bool IsVideoSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Array.IndexOf(VideoExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    public static bool IsImageSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    public static bool IsAudioSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Array.IndexOf(AudioExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    // ── MainWindow: TryExtractPlaybackSpeed ──
    public static bool TryExtractPlaybackSpeed(string? text, out double speed)
    {
        speed = 1.0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        Match match = Regex.Match(text, @"([0-9]+(?:[.,][0-9]+)?)\s*x", RegexOptions.IgnoreCase);
        if (match.Success)
            return LocalhostRequestMapper.TryParseFlexibleDouble(match.Groups[1].Value, out speed) && speed > 0.0;

        return LocalhostRequestMapper.TryParseFlexibleDouble(text, out speed) && speed > 0.0;
    }

    // ── MainWindow: ParsePercent (nhận cả '.' và ',') ──
    public static double ParsePercent(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        string normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0.0
            ? v
            : fallback;
    }

    // ══════════ SOUNDBAR — mirror chính xác các hằng/hàm internal của MainWindow ══════════

    public const double SoundBarAnchorMarginFraction = 0.045;
    public const string SoundBarCustomPositionTag = "custom";
    public const string SoundBarSizeCustomTag = "custom";
    public const double SoundBarBaseWidthPercent = 62.0;
    public const double SoundBarBaseHeightPercent = 26.0;
    public const double SoundBarBaseAspect = SoundBarBaseWidthPercent / SoundBarBaseHeightPercent;

    public const double SoundBarHalfWidthCeiling = 0.5 - SoundBarAnchorMarginFraction; // 0.455
    public static readonly double SoundBarWidthPercentCap = 2.0 * SoundBarHalfWidthCeiling * 100.0; // 91.0
    public const double SoundBarHeightPercentCap = 95.5;
    public const double SoundBarSizeBoxMinPercent = 2.0;
    public const double SoundBarSizeMin = 20.0;

    public static readonly double SoundBarMaxScaleFraction =
        2.0 * SoundBarHalfWidthCeiling / SoundBarBaseWidthPercent * 100.0; // 1.46774

    public static readonly double SoundBarSizeMax =
        Math.Floor(SoundBarMaxScaleFraction * 10000.0) / 100.0; // 146.77

    public const double SoundBarSizePresetMatchTolerance = 0.5;
    public const double SoundBarCanvasAspectFallback = 16.0 / 9.0;
    public const double SoundBarCanvasAspectMin = 0.2;
    public const double SoundBarCanvasAspectMax = 5.0;

    // ── MainWindow: SoundBarStyleDisplayLabels (nhãn combo style) ──
    public static readonly System.Collections.Generic.Dictionary<string, string> SoundBarStyleDisplayLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["neon-bars"] = "Neon Bars (đáy)",
            ["mirror-spectrum"] = "Center Mirror Spectrum",
            ["pill-bars"] = "Pill Bar Row",
            ["dense-columns"] = "Dense Thin Columns",
            ["dot-matrix"] = "Dot Matrix Grid",
            ["radial-ring"] = "Radial Spectrum Ring",
            ["dual-rings"] = "Dual Counter Rings",
            ["ripple-rings"] = "Ripple Pulse Rings",
            ["spoke-lines"] = "Radial Spoke Lines",
            ["wave-ribbon"] = "Waveform Ribbon",
            ["center-column"] = "Center Sound Column",
            ["peak-stars"] = "Peak Star Field",
            ["audio-ring"] = "Audio Ring (NCS)"
        };

    // ── MainWindow: BuildSoundBarSizePresetEntries ──
    public static (string Label, string Tag)[] BuildSoundBarSizePresetEntries()
    {
        (string Label, string Tag)[] fixedEntries =
        {
            ("Rất nhỏ (50%)", "50"),
            ("Nhỏ (75%)", "75"),
            ("Vừa (100%)", "100"),
            ("Lớn (125%)", "125"),
        };

        string maxTag = SoundBarSizeMax.ToString("0.##", CultureInfo.InvariantCulture);
        var entries = new (string Label, string Tag)[fixedEntries.Length + 1];
        Array.Copy(fixedEntries, entries, fixedEntries.Length);
        entries[^1] = ($"Tối đa ({maxTag}%)", maxTag);
        return entries;
    }

    public static double ClampSoundBarSizePercent(double percent)
        => Math.Clamp(percent, SoundBarSizeMin, SoundBarSizeMax);

    public static double ClampSoundBarTypedWidthPercent(double widthPercent)
        => Math.Clamp(widthPercent, SoundBarSizeBoxMinPercent, SoundBarWidthPercentCap);

    public static double ClampSoundBarTypedHeightPercent(double heightPercent)
        => Math.Clamp(heightPercent, SoundBarSizeBoxMinPercent, SoundBarHeightPercentCap);

    // ── MainWindow: ComputeSoundBarSizeFootprint ──
    public static void ComputeSoundBarSizeFootprint(double clampedPercent, out double heightPercent, out double widthPercent)
    {
        double scale = clampedPercent / 100.0;
        heightPercent = Math.Clamp(SoundBarBaseHeightPercent * scale, 2.0, SoundBarHeightPercentCap);
        widthPercent = Math.Clamp(heightPercent * SoundBarBaseAspect, 2.0, SoundBarWidthPercentCap);
    }

    // ── MainWindow: ComputeSoundBarPresetAnchor (anchor math, mirror nguyên văn) ──
    public static (double X, double Y) ComputeSoundBarPresetAnchor(
        string tag, double widthPercent, double heightPercent, double canvasAspect, bool usesSquarePlot)
    {
        double aspect = canvasAspect > 0 && !double.IsNaN(canvasAspect) && !double.IsInfinity(canvasAspect)
            ? canvasAspect
            : 16.0 / 9.0;

        double plotWidthFraction = usesSquarePlot
            ? Math.Min(widthPercent, heightPercent / aspect)
            : widthPercent;
        double plotHeightFraction = usesSquarePlot
            ? Math.Min(heightPercent, widthPercent * aspect)
            : heightPercent;

        double margin = SoundBarAnchorMarginFraction;
        double halfWidth = Math.Min(plotWidthFraction * 0.5, SoundBarHalfWidthCeiling);
        double leftX = Math.Clamp(halfWidth + margin, 0.0, 1.0);
        double rightX = Math.Clamp(1.0 - halfWidth - margin, 0.0, 1.0);
        double bottomY = Math.Clamp(1.0 - margin, 0.0, 1.0);
        double topY = Math.Clamp(plotHeightFraction + margin, 0.0, 1.0);
        double centerY = Math.Clamp(0.5 + (plotHeightFraction * 0.5), 0.0, 1.0);

        double x = 0.5;
        double y = bottomY;

        switch (tag)
        {
            case "bottom-left": x = leftX; y = bottomY; break;
            case "bottom-right": x = rightX; y = bottomY; break;
            case "center": x = 0.5; y = centerY; break;
            case "top-center": x = 0.5; y = topY; break;
            case "top-left": x = leftX; y = topY; break;
            case "top-right": x = rightX; y = topY; break;
        }

        return (x, y);
    }

    // ── MainWindow: ResolveSoundBarPreviewCanvas (chỉ dùng cho khung preview) ──
    public static void ResolveSoundBarPreviewCanvas(double canvasAspect, out int width, out int height)
    {
        const int pixelBudget = 480 * 270;
        double aspect = canvasAspect > 0 && !double.IsNaN(canvasAspect) && !double.IsInfinity(canvasAspect)
            ? canvasAspect
            : 16.0 / 9.0;
        width = (int)Math.Round(Math.Sqrt(pixelBudget * aspect), MidpointRounding.AwayFromZero);
        height = (int)Math.Round(Math.Sqrt(pixelBudget / aspect), MidpointRounding.AwayFromZero);
        width = width / 2 * 2;
        height = height / 2 * 2;
    }
}
