using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.Views;

/// <summary>
/// Trình chỉnh vị trí watermark — port của gridWatermarkOverlay (MainWindow:1539) theo phương án
/// đã duyệt (điểm 1): frame tĩnh extract ffmpeg tại t=0.0 (bản gốc KHÔNG scrub —
/// RefreshWatermarkPreviewFrameAsync dùng ExtractFrameToImageAsync(videoPath, 0.0, ...)),
/// logo kéo-thả trên Canvas, slider Scale/Rotation/Opacity, SAVE trả X/Y %.
/// Khung preview theo kích thước SOURCE (BtnOpenWatermarkEditor_Click:688-697) — bản gốc cũng
/// không composite crop/template; khác biệt khung output là trạng thái có sẵn của bản gốc.
/// </summary>
public partial class WatermarkEditorWindow : Window
{
    private readonly string _videoPath = null!;
    private readonly string _watermarkPath = null!;

    private double _baseLogoWidth = 150;
    private double _baseLogoHeight = 150;
    private double _aspect = 1.0;
    private bool _dragging;
    private Point _dragStart;

    public double Scale { get; private set; } = 1.0;
    public double Rotation { get; private set; }
    public double WatermarkOpacity { get; private set; } = 1.0;
    public double XPercent { get; private set; } = 0.05;
    public double YPercent { get; private set; } = 0.05;
    public bool Saved { get; private set; }

    private readonly TaskCompletionSource<(double Scale, double Rotation, double Opacity, double X, double Y, bool Saved)> _tcs = new();

    public WatermarkEditorWindow()
    {
        InitializeComponent();
    }

    public WatermarkEditorWindow(string videoPath, string watermarkPath,
        double scale, double rotation, double opacity, double xPercent, double yPercent) : this()
    {
        _videoPath = videoPath;
        _watermarkPath = watermarkPath;
        Scale = scale;
        Rotation = rotation;
        WatermarkOpacity = opacity;
        XPercent = xPercent;
        YPercent = yPercent;

        SldScale.Value = Math.Clamp(scale, 0.1, 3.0);
        SldRotate.Value = Math.Clamp(rotation, -180, 180);
        SldOpacity.Value = Math.Clamp(opacity, 0.1, 1.0);
        ApplyTransforms();

        Opened += async (_, _) => await LoadPreviewAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private Canvas PreviewCanvas => this.FindControl<Canvas>("cvsWatermarkPreview")!;
    private Image FrameImage => this.FindControl<Image>("imgWatermarkVideoFrame")!;
    private Image LogoImage => this.FindControl<Image>("imgWatermarkPreview")!;
    private Slider SldScale => this.FindControl<Slider>("sldWmkScale")!;
    private Slider SldRotate => this.FindControl<Slider>("sldWmkRotate")!;
    private Slider SldOpacity => this.FindControl<Slider>("sldWmkOpacity")!;

    // ── BtnOpenWatermarkEditor_Click: khung = kích thước source; logo = 20% rộng khung ──
    private async Task LoadPreviewAsync()
    {
        try
        {
            var meta = await EngineCore.AnalyzeMediaDetailsAsync(_videoPath);
            int w = meta.Width > 0 ? meta.Width : 1920;
            int h = meta.Height > 0 ? meta.Height : 1080;

            PreviewCanvas.Width = w;
            PreviewCanvas.Height = h;
            FrameImage.Width = w;
            FrameImage.Height = h;
            FrameImage.Source = null;

            await using (var logoStream = File.OpenRead(_watermarkPath))
            {
                LogoImage.Source = new Bitmap(logoStream);
            }
            if (LogoImage.Source is Bitmap bmp && bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
                _aspect = (double)bmp.PixelSize.Width / bmp.PixelSize.Height;

            _baseLogoWidth = Math.Max(24.0, w * 0.20);
            _baseLogoHeight = Math.Max(24.0, _baseLogoWidth / Math.Max(0.01, _aspect));
            if (_baseLogoHeight > h * 0.8)
            {
                _baseLogoHeight = h * 0.8;
                _baseLogoWidth = _baseLogoHeight * _aspect;
            }
            LogoImage.Width = _baseLogoWidth;
            LogoImage.Height = _baseLogoHeight;

            double left = (Math.Clamp(XPercent, 0.0, 1.0) * w) - (_baseLogoWidth / 2.0);
            double top = (Math.Clamp(YPercent, 0.0, 1.0) * h) - (_baseLogoHeight / 2.0);
            left = Math.Clamp(left, -_baseLogoWidth / 2.0, w - (_baseLogoWidth / 2.0));
            top = Math.Clamp(top, -_baseLogoHeight / 2.0, h - (_baseLogoHeight / 2.0));
            Canvas.SetLeft(LogoImage, left);
            Canvas.SetTop(LogoImage, top);

            // Frame t=0.0 (mirror RefreshWatermarkPreviewFrameAsync:767).
            string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngine", "WatermarkPreview");
            Directory.CreateDirectory(tempDir);
            string framePath = Path.Combine(tempDir, $"watermark_{Guid.NewGuid():N}.jpg");
            bool extracted = await EngineCore.ExtractFrameToImageAsync(_videoPath, 0.0, framePath, System.Threading.CancellationToken.None);
            if (extracted && File.Exists(framePath))
            {
                await using (var fs = File.OpenRead(framePath))
                {
                    FrameImage.Source = new Bitmap(fs);
                }
                try { File.Delete(framePath); } catch { }
            }
            else
            {
                // Mirror bản gốc: "Preview frame unavailable; using black background."
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WATERMARK-EDITOR] {ex.Message}");
        }
    }

    // ── SldWmk_ValueChanged: preview transform ──
    private void ApplyTransforms()
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(SldScale.Value, SldScale.Value));
        group.Children.Add(new RotateTransform(SldRotate.Value));
        LogoImage.RenderTransform = group;
        LogoImage.Opacity = SldOpacity.Value;
    }

    private void Sld_ValueChanged(object? sender, RoutedEventArgs e) => ApplyTransforms();

    // ── ImgWatermark_MouseDown/Move/Up ──
    private void Logo_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(PreviewCanvas);
        e.Pointer.Capture(LogoImage);
        e.Handled = true;
    }

    private void Logo_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || PreviewCanvas.Width <= 0) return;
        Point current = e.GetPosition(PreviewCanvas);
        double deltaX = current.X - _dragStart.X;
        double deltaY = current.Y - _dragStart.Y;

        double newLeft = Canvas.GetLeft(LogoImage) + deltaX;
        double newTop = Canvas.GetTop(LogoImage) + deltaY;

        double imgHalfWidth = LogoImage.Width / 2;
        double imgHalfHeight = LogoImage.Height / 2;
        newLeft = Math.Max(-imgHalfWidth, Math.Min(newLeft, PreviewCanvas.Width - imgHalfWidth));
        newTop = Math.Max(-imgHalfHeight, Math.Min(newTop, PreviewCanvas.Height - imgHalfHeight));

        Canvas.SetLeft(LogoImage, newLeft);
        Canvas.SetTop(LogoImage, newTop);
        _dragStart = current;
    }

    private void Logo_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    // ── BtnSaveWatermark_Click: tâm logo / khung → X/Y % ──
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Scale = SldScale.Value;
        Rotation = SldRotate.Value;
        WatermarkOpacity = SldOpacity.Value;

        double canvasWidth = PreviewCanvas.Width;
        double canvasHeight = PreviewCanvas.Height;
        double imgWidth = LogoImage.Width;
        double imgHeight = LogoImage.Height;
        if (canvasWidth > 0 && canvasHeight > 0 && imgWidth > 0 && imgHeight > 0)
        {
            double imgLeft = Canvas.GetLeft(LogoImage);
            double imgTop = Canvas.GetTop(LogoImage);
            if (double.IsNaN(imgLeft)) imgLeft = 0;
            if (double.IsNaN(imgTop)) imgTop = 0;
            double centerX = imgLeft + (imgWidth / 2);
            double centerY = imgTop + (imgHeight / 2);
            XPercent = Math.Clamp(centerX / canvasWidth, 0, 1);
            YPercent = Math.Clamp(centerY / canvasHeight, 0, 1);
        }

        Saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _tcs.TrySetResult((Scale, Rotation, WatermarkOpacity, XPercent, YPercent, Saved));
        base.OnClosed(e);
    }

    public Task<(double Scale, double Rotation, double Opacity, double X, double Y, bool Saved)> ShowResultAsync(Window owner)
        => ShowDialog(owner).ContinueWith(_ => _tcs.Task).Unwrap();
}
