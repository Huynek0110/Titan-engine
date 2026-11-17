using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.Views;

/// <summary>
/// Crop preview — port mou dua cua bdrCropFrame (WPF MainWindow) sang Avalonia.
/// Hien thi frame video tai t=0, border vung crop co the keo/thay doi kich thuoc bang 4 thumb goc.
/// Ket qua: CropX/Y/Width/HeightPercent (0..1). Goi: new CropPreviewWindow(videoPath).ShowDialog(owner)
/// </summary>
public partial class CropPreviewWindow : Window
{
    private readonly string _videoPath;
    private readonly double _videoW;
    private readonly double _videoH;

    private readonly Canvas _canvas;
    private readonly Image _frameImage;
    private readonly Border _cropBorder;
    private readonly Thumb _hdTl, _hdTr, _hdBl, _hdBr;
    private readonly TextBlock _percentLabel;
    private readonly TextBlock _sizeLabel;

    private readonly double _initX, _initY, _initW, _initH;
    private bool _dragBorder, _dragThumb;
    private Point _dragStart;
    private double _startLeft, _startTop, _startW, _startH;
    private string _dragThumbTag = "";

    public double CropXPercent { get; private set; } = 0.0;
    public double CropYPercent { get; private set; } = 0.0;
    public double CropWidthPercent { get; private set; } = 1.0;
    public double CropHeightPercent { get; private set; } = 1.0;
    public bool Applied { get; private set; }

    public CropPreviewWindow() : this(string.Empty, 0, 0, 1, 1) { }

    public CropPreviewWindow(string videoPath,
        double initX = 0, double initY = 0, double initW = 1, double initH = 1)
    {
        _videoPath = videoPath ?? string.Empty;
        _initX = initX; _initY = initY; _initW = initW; _initH = initH;

        _canvas = this.FindControl<Canvas>("CropCanvas")!;
        _frameImage = this.FindControl<Image>("FrameImage")!;
        _cropBorder = this.FindControl<Border>("CropBorder")!;
        _hdTl = this.FindControl<Thumb>("HdTL")!;
        _hdTr = this.FindControl<Thumb>("HdTR")!;
        _hdBl = this.FindControl<Thumb>("HdBL")!;
        _hdBr = this.FindControl<Thumb>("HdBR")!;
        _percentLabel = this.FindControl<TextBlock>("PercentLabel")!;
        _sizeLabel = this.FindControl<TextBlock>("SizeLabel")!;

        var btnReset = this.FindControl<Button>("BtnReset")!;
        var btnApply = this.FindControl<Button>("BtnApply")!;
        var btnClose = this.FindControl<Button>("BtnClose")!;

        btnReset.Click += (_, _) => ResetCrop();
        btnApply.Click += (_, _) => { Applied = true; Close(); };
        btnClose.Click += (_, _) => Close();

        // Kich thuoc video: probe ffmpeg; mac dinh 1920x1080.
        var meta = EngineCore.AnalyzeMediaDetailsAsync(_videoPath).GetAwaiter().GetResult();
        _videoW = meta.Width > 0 ? meta.Width : 1920;
        _videoH = meta.Height > 0 ? meta.Height : 1080;

        _canvas.Width = Math.Min(_videoW, 920);
        _canvas.Height = Math.Min(_videoH, 520);

        Opened += async (_, _) =>
        {
            await LoadFrameAsync();
            if (_initW > 0 && _initH > 0)
            {
                // Ap dung gia tri crop luu truoc do (phan tram cua canvas).
                SetBorder(_initX * _canvas.Width, _initY * _canvas.Height,
                    _initW * _canvas.Width, _initH * _canvas.Height);
            }
            else
            {
                ResetCrop();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task LoadFrameAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath)) return;
        string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngine", "CropPreview");
        Directory.CreateDirectory(tempDir);
        string framePath = Path.Combine(tempDir, $"crop_{Guid.NewGuid():N}.jpg");
        bool ok = await EngineCore.ExtractFrameToImageAsync(_videoPath, 0.0, framePath, System.Threading.CancellationToken.None);
        if (ok && File.Exists(framePath))
        {
            await using var fs = File.OpenRead(framePath);
            _frameImage.Source = new Bitmap(fs);
            try { File.Delete(framePath); } catch { }
        }
    }

    private void ResetCrop()
    {
        // Mac dinh: crop o trung tam, 60% chieu rong, 33% chieu cao (9:16 khoang).
        double cw = _canvas.Width * 0.60;
        double ch = _canvas.Height * 0.33;
        double cl = (_canvas.Width - cw) / 2;
        double ct = (_canvas.Height - ch) / 2;
        SetBorder(cl, ct, cw, ch);
        UpdatePercent();
    }

    private void SetBorder(double left, double top, double width, double height)
    {
        width = Math.Clamp(width, 40, _canvas.Width);
        height = Math.Clamp(height, 40, _canvas.Height);
        left = Math.Clamp(left, 0, _canvas.Width - width);
        top = Math.Clamp(top, 0, _canvas.Height - height);
        Canvas.SetLeft(_cropBorder, left);
        Canvas.SetTop(_cropBorder, top);
        _cropBorder.Width = width;
        _cropBorder.Height = height;
        PlaceThumbs(left, top, width, height);
        UpdatePercent();
    }

    private void PlaceThumbs(double left, double top, double width, double height)
    {
        Canvas.SetLeft(_hdTl, left - 7);
        Canvas.SetTop(_hdTl, top - 7);
        Canvas.SetLeft(_hdTr, left + width - 7);
        Canvas.SetTop(_hdTr, top - 7);
        Canvas.SetLeft(_hdBl, left - 7);
        Canvas.SetTop(_hdBl, top + height - 7);
        Canvas.SetLeft(_hdBr, left + width - 7);
        Canvas.SetTop(_hdBr, top + height - 7);
    }

    private void UpdatePercent()
    {
        double left = Canvas.GetLeft(_cropBorder);
        double top = Canvas.GetTop(_cropBorder);
        double w = _cropBorder.Width;
        double h = _cropBorder.Height;
        double cw = _canvas.Width > 0 ? _canvas.Width : 1;
        double ch = _canvas.Height > 0 ? _canvas.Height : 1;
        CropXPercent = Math.Clamp(left / cw, 0, 1);
        CropYPercent = Math.Clamp(top / ch, 0, 1);
        CropWidthPercent = Math.Clamp(w / cw, 0, 1);
        CropHeightPercent = Math.Clamp(h / ch, 0, 1);
        _percentLabel.Text = $"X: {CropXPercent:P0}  Y: {CropYPercent:P0}  W: {CropWidthPercent:P0}  H: {CropHeightPercent:P0}";
        _sizeLabel.Text = $"{Math.Round(w)}x{Math.Round(h)}";
    }

    private void CropBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragBorder = true;
        _dragStart = e.GetPosition(_canvas);
        _startLeft = Canvas.GetLeft(_cropBorder);
        _startTop = Canvas.GetTop(_cropBorder);
        e.Pointer.Capture(_cropBorder);
        e.Handled = true;
    }

    private void CropBorder_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragBorder) return;
        Point cur = e.GetPosition(_canvas);
        double dx = cur.X - _dragStart.X;
        double dy = cur.Y - _dragStart.Y;
        SetBorder(_startLeft + dx, _startTop + dy, _cropBorder.Width, _cropBorder.Height);
    }

    private void CropBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragBorder = false;
        e.Pointer.Capture(null);
    }

    private void HandleThumb_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Thumb h) return;
        _dragThumb = true;
        _dragThumbTag = h.Tag as string ?? "";
        _dragStart = e.GetPosition(_canvas);
        _startLeft = Canvas.GetLeft(_cropBorder);
        _startTop = Canvas.GetTop(_cropBorder);
        _startW = _cropBorder.Width;
        _startH = _cropBorder.Height;
        e.Pointer.Capture(h);
        e.Handled = true;
    }

    private void HandleThumb_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragThumb) return;
        Point cur = e.GetPosition(_canvas);
        double dx = cur.X - _dragStart.X;
        double dy = cur.Y - _dragStart.Y;
        double left = _startLeft, top = _startTop, w = _startW, h = _startH;
        double min = 40;
        switch (_dragThumbTag)
        {
            case "TL":
                w = Math.Max(min, _startW - dx); h = Math.Max(min, _startH - dy);
                left = _startLeft + dx; top = _startTop + dy; break;
            case "TR":
                w = Math.Max(min, _startW + dx); h = Math.Max(min, _startH - dy);
                top = _startTop + dy; break;
            case "BL":
                w = Math.Max(min, _startW - dx); h = Math.Max(min, _startH + dy);
                left = _startLeft + dx; break;
            case "BR":
                w = Math.Max(min, _startW + dx); h = Math.Max(min, _startH + dy); break;
        }
        SetBorder(left, top, w, h);
    }

    private void HandleThumb_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragThumb = false;
        e.Pointer.Capture(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
    }
}
