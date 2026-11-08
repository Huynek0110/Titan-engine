using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace TitanEngine
{
    /// <summary>
    /// WPF implementation của các interface do Core định nghĩa (Giai đoạn 1 đính chính).
    /// Gọi `WpfEngineServices.Install();` trước `EngineCore.Initialize()` (đầu constructor MainWindow).
    /// Core không tham chiếu project WPF — mọi call Core→WPF đi qua EngineServices.*.
    /// Phase 5 (Avalonia): thay bằng implementation Avalonia của cùng interface.
    /// </summary>
    public static class WpfEngineServices
    {
        public static void Install()
        {
            EngineServices.Prompt = new WpfUserPrompt();
            EngineServices.Text = new WpfTextMetrics();
            EngineServices.Images = new WpfImageServices();
        }

        private sealed class WpfUserPrompt : IUserPrompt
        {
            public string? PromptForFfmpegPath()
            {
                var dlg = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe" };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            }

            public bool Confirm(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

            public void Alert(string title, string message, bool error)
                => MessageBox.Show(message, title, MessageBoxButton.OK, error ? MessageBoxImage.Error : MessageBoxImage.Information);
        }

        /// <summary>
        /// Đo text bằng Typeface/FormattedText đúng cách bản gốc đo (pixelsPerDip = 1.0,
        /// FlowDirection.LeftToRight, Brushes.White). LineWidths[i] = 0 cho dòng rỗng
        /// (khớp loop gốc vốn skip dòng rỗng khi lấy max).
        /// </summary>
        private sealed class WpfTextMetrics : ITextMetrics
        {
            public TextMeasurement Measure(string text, string fontFamilyName, double fontSize)
            {
                var typeface = new System.Windows.Media.Typeface(fontFamilyName);
                string[] lines = text.Split('\n');
                var widths = new double[lines.Length];
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0)
                    {
                        widths[i] = 0.0;
                        continue;
                    }

                    var lineFt = new System.Windows.Media.FormattedText(
                        lines[i],
                        CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        System.Windows.Media.Brushes.White,
                        1.0); // 1.0 pixels per dip
                    widths[i] = lineFt.Width;
                }

                var blockFt = new System.Windows.Media.FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    System.Windows.Media.Brushes.White,
                    1.0);
                return new TextMeasurement(blockFt.Width, blockFt.Height, widths);
            }
        }

        /// <summary>Sinh overlay pixel: delegate sang các art class (member moved verbatim từ Core).</summary>
        private sealed class WpfImageServices : IImageServices
        {
            public Task<SnowfallGeneratedOverlay> GenerateSnowfallOverlayAsync(
                int width, int height, OverlayContentLayout? layout, double frameRate,
                SnowfallOverlayRecipe recipe, string? tag, CancellationToken token, Action<string> onLog)
                => SnowfallOverlayArt.GenerateOverlayAsync(width, height, layout, frameRate, recipe, tag, token, onLog);

            public Task<DreamyDotGeneratedOverlay> GenerateDreamyDotOverlayAsync(
                int width, int height, OverlayContentLayout? layout, double frameRate, double outputDurationSeconds,
                RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
                => DreamyDotOverlayArt.GenerateOverlayAsync(width, height, layout, frameRate, outputDurationSeconds, job, tag, token, onLog);

            public Task<RgbPolaroidLedGeneratedOverlay> GenerateRgbPolaroidLedOverlayAsync(
                int width, int height, OverlayContentLayout? layout, double frameRate, double outputDurationSeconds,
                RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
                => RgbPolaroidLedArt.GenerateOverlayAsync(width, height, layout, frameRate, outputDurationSeconds, job, tag, token, onLog);

            public Task<PolaroidScrapbook2GeneratedOverlay> GeneratePolaroidScrapbook2OverlayAsync(
                int width, int height, OverlayContentLayout? layout,
                RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
                => PolaroidScrapbook2Art.GenerateOverlayAsync(width, height, layout, job, tag, token, onLog);

            public Task<DashedPolaroidGeneratedOverlay> GenerateDashedPolaroidOverlayAsync(
                int width, int height, OverlayContentLayout? layout, double frameRate,
                RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
                => DashedPolaroidFrameArt.GenerateOverlayAsync(width, height, layout, frameRate, job, tag, token, onLog);

            public Task<ScratchVideoGeneratedOverlay> GenerateScratchVideoOverlayAsync(
                int width, int height, OverlayContentLayout? layout, double frameRate, double outputDurationSeconds,
                RenderJob job, string? tag, CancellationToken token, Action<string> onLog)
                => ScratchVideoArt.GenerateOverlayAsync(width, height, layout, frameRate, outputDurationSeconds, job, tag, token, onLog);

            public Task<ParticleOverlaySequence> GenerateParticleOverlayAsync(
                int width, int height, double frameRate, double outputDurationSeconds,
                ParticleEffectRecipe recipe, string? tag, CancellationToken token, Action<string> onLog)
                => ParticleOverlayArt.GenerateOverlayAsync(width, height, frameRate, outputDurationSeconds, recipe, tag, token, onLog);

            public Task<SoundBarGeneratedOverlay?> GenerateSoundBarOverlayAsync(
                AudioAnalysisResult analysis, SoundBarSettings settings, ColorPreset palette,
                int canvasWidth, int canvasHeight, string? tag, Action<string> onLog, CancellationToken token)
                => SoundBarVisualizerArt.GenerateOverlayAsync(analysis, settings, palette, canvasWidth, canvasHeight, tag, onLog, token);

            public OverlayFrameSample? SampleOverlayFrame(string imagePath)
                => WpfImageCodecArt.SampleOverlayFrame(imagePath);

            public (int Width, int Height) TryGetImageDimensions(string filePath)
                => WpfImageCodecArt.TryGetImageDimensions(filePath);

            public bool SanitizeBrandLogoAsset(string sourcePath, string destinationPath, Action<string> onLog)
                => WpfImageCodecArt.SanitizeBrandLogoAsset(sourcePath, destinationPath, onLog);
        }
    }
}
