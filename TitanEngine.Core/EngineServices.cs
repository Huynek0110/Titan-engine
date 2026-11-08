using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    /// <summary>
    /// UI prompts the engine may need (phase 2: thay MessageBox/OpenFileDialog trong Core).
    /// WPF implement bằng MessageBox/OpenFileDialog; CLI headless trả về null/false.
    /// </summary>
    public interface IUserPrompt
    {
        /// <summary>Hỏi người dùng chọn file ffmpeg (thay OpenFileDialog "FFmpeg|ffmpeg.exe"). Trả null nếu huỷ.</summary>
        string? PromptForFfmpegPath();

        /// <summary>Hỏi Yes/No (thay MessageBox.Show YesNo). true = Yes.</summary>
        bool Confirm(string title, string message);

        /// <summary>Hiện thông báo / cảnh báo / lỗi (thay MessageBox.Show OK).</summary>
        void Alert(string title, string message, bool error);
    }

    /// <summary>Marshal về UI thread khi cần (thay Dispatcher trong Core).</summary>
    public interface IUiDispatcher
    {
        void Post(Action action);
        Task InvokeAsync(Func<Task> action);
        Task<T> InvokeAsync<T>(Func<T> func);
    }

    /// <summary>Log stream của engine (facade ITitanEngineService dùng để đẩy log ra UI/API).</summary>
    public interface IEngineLogSink
    {
        void Log(string message);
    }

    /// <summary>Kết quả đo text (thay FormattedText/Typeface trong Core; WPF đo bằng FormattedText, phase 4 bằng Skia).</summary>
    public readonly record struct TextMeasurement(double Width, double Height, IReadOnlyList<double> LineWidths);

    public interface ITextMetrics
    {
        /// <summary>Đo khối text nhiều dòng với font/size cho trước (giống cách FormattedText đo trong bản gốc).</summary>
        TextMeasurement Measure(string text, string fontFamilyName, double fontSize);
    }

    /// <summary>Một khung hình đã decode (BGRA32) để phân tích overlay asset / đọc kích thước ảnh.</summary>
    public sealed class OverlayFrameSample
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] BgraPixels { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Toàn bộ máy ảnh/pixel mà Core cần: sinh PNG overlay (WPF dùng RenderTargetBitmap — code
    /// moved verbatim từ preset), decode khung hình phục vụ probe, và sanitize brand logo.
    /// Phase 4 có thể thay bằng Skia trong Core mà không đổi caller.
    /// </summary>
    public interface IImageServices
    {
        Task<SnowfallGeneratedOverlay> GenerateSnowfallOverlayAsync(
            int width, int height, OverlayContentLayout? layout, double frameRate,
            SnowfallOverlayRecipe recipe, string? tag, CancellationToken token, Action<string> onLog);

        Task<DreamyDotGeneratedOverlay> GenerateDreamyDotOverlayAsync(
            int width, int height, OverlayContentLayout? layout, double frameRate, double outputDurationSeconds,
            RenderJob job, string? tag, CancellationToken token, Action<string> onLog);

        Task<RgbPolaroidLedGeneratedOverlay> GenerateRgbPolaroidLedOverlayAsync(
            int width, int height, OverlayContentLayout? layout, double frameRate, double outputDurationSeconds,
            RenderJob job, string? tag, CancellationToken token, Action<string> onLog);

        Task<PolaroidScrapbook2GeneratedOverlay> GeneratePolaroidScrapbook2OverlayAsync(
            int width, int height, OverlayContentLayout? layout,
            RenderJob job, string? tag, CancellationToken token, Action<string> onLog);

        Task<DashedPolaroidGeneratedOverlay> GenerateDashedPolaroidOverlayAsync(
            int width, int height, OverlayContentLayout? layout, double frameRate,
            RenderJob job, string? tag, CancellationToken token, Action<string> onLog);

        Task<ScratchVideoGeneratedOverlay> GenerateScratchVideoOverlayAsync(
            int width, int height, OverlayContentLayout? layout, double frameRate, double outputDurationSeconds,
            RenderJob job, string? tag, CancellationToken token, Action<string> onLog);

        Task<ParticleOverlaySequence> GenerateParticleOverlayAsync(
            int width, int height, double frameRate, double outputDurationSeconds,
            ParticleEffectRecipe recipe, string? tag, CancellationToken token, Action<string> onLog);

        Task<SoundBarGeneratedOverlay?> GenerateSoundBarOverlayAsync(
            AudioAnalysisResult analysis, SoundBarSettings settings, ColorPreset palette,
            int canvasWidth, int canvasHeight, string? tag, Action<string> onLog, CancellationToken token);

        /// <summary>Decode 1 frame ảnh (scale ≤160x90, BGRA32) — mirror của AnalyzeFrame decode gốc. Trả null nếu decode lỗi.</summary>
        OverlayFrameSample? SampleOverlayFrame(string imagePath);

        /// <summary>Đọc (Width, Height) của ảnh tĩnh — mirror TryGetStillImageResolution gốc. (0,0) nếu lỗi.</summary>
        (int Width, int Height) TryGetImageDimensions(string filePath);

        /// <summary>Sanitize brand logo (xoá RGB ẩn dưới alpha=0, re-encode PNG) — mirror TrySanitizeBrandLogoAsset gốc.</summary>
        bool SanitizeBrandLogoAsset(string sourcePath, string destinationPath, Action<string> onLog);
    }

    /// <summary>Service locator của Core. WPF/CLI gán lúc khởi động; null = headless (dùng fallback/throw rõ ràng).</summary>
    public static class EngineServices
    {
        public static IUserPrompt? Prompt { get; set; }
        public static ITextMetrics? Text { get; set; }
        public static IImageServices? Images { get; set; }
        public static IUiDispatcher? Ui { get; set; }
        public static IEngineLogSink? LogSink { get; set; }
    }
}
