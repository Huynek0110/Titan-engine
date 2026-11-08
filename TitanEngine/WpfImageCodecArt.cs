using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TitanEngine
{
    /// <summary>
    /// WPF-stay các helper decode/encode ảnh mà Core gọi qua IImageServices (Giai đoạn 1 đính chính).
    /// Thân hàm chuyển nguyên văn từ: AnalyzeFrame (nửa decode, OverlayAssetBackgroundAnalysis.cs),
    /// TryGetStillImageResolution (EngineCore.Helpers.cs), TrySanitizeBrandLogoAsset (EngineCore.Initializer.cs).
    /// Phase 4 (Skia) sẽ thay các thân này — Core không đổi.
    /// </summary>
    public static class WpfImageCodecArt
    {
        /// <summary>
        /// Decode 1 frame ảnh (scale ≤160x90, BGRA32) — nguyên văn nửa decode của AnalyzeFrame gốc.
        /// Không try/catch: decode lỗi ném ra như bản gốc (caller Core chỉ xử lý null làm safety net).
        /// </summary>
        public static OverlayFrameSample? SampleOverlayFrame(string imagePath)
        {
            using FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            BitmapSource scaled = new TransformedBitmap(source, new ScaleTransform(
                160.0 / Math.Max(1.0, source.PixelWidth),
                90.0 / Math.Max(1.0, source.PixelHeight)));
            BitmapSource rgba = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0.0);

            int width = rgba.PixelWidth;
            int height = rgba.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            rgba.CopyPixels(pixels, stride, 0);

            return new OverlayFrameSample
            {
                Width = width,
                Height = height,
                BgraPixels = pixels
            };
        }

        /// <summary>Đọc (Width, Height) ảnh tĩnh — nguyên văn TryGetStillImageResolution gốc (catch → (0, 0)).</summary>
        public static (int Width, int Height) TryGetImageDimensions(string filePath)
        {
            try
            {
                if (!EngineCore.IsImageSourcePath(filePath) || !File.Exists(filePath))
                    return (0, 0);

                using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? frame = decoder.Frames.FirstOrDefault();
                if (frame != null && frame.PixelWidth > 0 && frame.PixelHeight > 0)
                    return (frame.PixelWidth, frame.PixelHeight);
            }
            catch
            {
            }

            return (0, 0);
        }

        /// <summary>Sanitize brand logo (xoá RGB ẩn dưới alpha=0, re-encode PNG) — nguyên văn TrySanitizeBrandLogoAsset gốc.</summary>
        public static bool SanitizeBrandLogoAsset(string sourcePath, string destinationPath, Action<string> onLog)
        {
            try
            {
                using FileStream stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? frame = decoder.Frames.FirstOrDefault();
                if (frame == null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                    return false;

                BitmapSource rgbaSource = frame.Format == PixelFormats.Pbgra32
                    ? frame
                    : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0.0);

                int width = rgbaSource.PixelWidth;
                int height = rgbaSource.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                rgbaSource.CopyPixels(pixels, stride, 0);

                const byte alphaThreshold = 5;
                bool hiddenRgbDetected = false;

                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = row + (x * 4);
                        byte b = pixels[idx];
                        byte g = pixels[idx + 1];
                        byte r = pixels[idx + 2];
                        byte a = pixels[idx + 3];

                        if (a < alphaThreshold)
                        {
                            if (r != 0 || g != 0 || b != 0)
                                hiddenRgbDetected = true;

                            pixels[idx] = 0;
                            pixels[idx + 1] = 0;
                            pixels[idx + 2] = 0;
                            pixels[idx + 3] = 0;
                        }
                    }
                }

                string fullSource = Path.GetFullPath(sourcePath);
                string fullDestination = Path.GetFullPath(destinationPath);
                bool sameFile = string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase);
                bool needsRewrite = !sameFile || hiddenRgbDetected;
                if (!needsRewrite)
                    return true;

                double dpiX = frame.DpiX > 0 ? frame.DpiX : 96.0;
                double dpiY = frame.DpiY > 0 ? frame.DpiY : 96.0;
                BitmapSource sanitized = BitmapSource.Create(
                    width,
                    height,
                    dpiX,
                    dpiY,
                    PixelFormats.Pbgra32,
                    null,
                    pixels,
                    stride);
                if (sanitized.CanFreeze)
                    sanitized.Freeze();

                string? targetDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                    Directory.CreateDirectory(targetDir);

                string tempPath = sameFile ? destinationPath + ".sanitized.tmp" : destinationPath;
                using (FileStream output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(sanitized));
                    encoder.Save(output);
                }

                if (sameFile)
                {
                    File.Copy(tempPath, destinationPath, overwrite: true);
                    File.Delete(tempPath);
                }

                onLog($"[BRAND-LOGO] Sanitized asset alpha-only cleanup: {destinationPath}");
                return true;
            }
            catch (Exception ex)
            {
                onLog($"[BRAND-LOGO-WARN] Failed to sanitize asset '{sourcePath}': {ex.Message}");
                return false;
            }
        }
    }
}
