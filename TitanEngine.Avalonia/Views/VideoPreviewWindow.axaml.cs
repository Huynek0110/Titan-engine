using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.Views;

/// <summary>
/// Video preview ban 1 - thanh thoi gian + trich frame ffmpeg co debounce +
/// cache mot vai frame gan nhat (giong watermark preview).
///
/// Mo cua so doc lap (self-contained), khong thu nhap MainWindowViewModel.
/// Goi: new VideoPreviewWindow(videoPath).ShowDialog(owner)
/// </summary>
public partial class VideoPreviewWindow : Window
{
    private readonly string _videoPath;
    private readonly double _durationSec;

    private readonly Image _frameImage;
    private readonly Slider _timeline;
    private readonly TextBlock _timeLabel;
    private readonly TextBlock _durationLabel;

    // Cache mot vai frame gan nhat: thoi gian (s) -> duong dan file anh tam.
    // Giu toi da MaxCachedFrames de khong lap day o dia.
    private const int MaxCachedFrames = 5;
    private readonly SortedDictionary<double, string> _frameCache = new();
    private readonly string _cacheDir;

    private CancellationTokenSource? _pendingExtract;
    private readonly DispatcherTimer _debounce;
    private bool _suppressSliderEvent;

    public VideoPreviewWindow() : this(string.Empty) { }

    public VideoPreviewWindow(string videoPath, double? durationSecOverride = null)
    {
        _videoPath = videoPath ?? throw new ArgumentNullException(nameof(videoPath));
        _cacheDir = Path.Combine(Path.GetTempPath(), "TitanEngine", "VideoPreview");
        Directory.CreateDirectory(_cacheDir);

        _frameImage = this.FindControl<Image>("FrameImage")!;
        _timeline = this.FindControl<Slider>("TimelineSlider")!;
        _timeLabel = this.FindControl<TextBlock>("TimeLabel")!;
        _durationLabel = this.FindControl<TextBlock>("DurationLabel")!;
        var btnClose = this.FindControl<Button>("BtnClose")!;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += DebounceTick;

        _timeline.ValueChanged += Timeline_ValueChanged;
        btnClose.Click += (_, _) => Close();

        // Thoi luong video: uu tien gia tri truy vao, nguoc lai probe ffmpeg.
        _durationSec = durationSecOverride ?? ProbeDuration();
        if (_durationSec > 0)
        {
            _timeline.Maximum = _durationSec;
            _durationLabel.Text = FormatTime(_durationSec);
        }
        else
        {
            _timeline.IsEnabled = false;
            _durationLabel.Text = "--:--";
        }

        // Trich frame dau (t=0) ngay khi cua so mo, giong watermark preview.
        Opened += async (_, _) => await ExtractAndShow(0.0);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private double ProbeDuration()
    {
        try
        {
            var meta = EngineCore.AnalyzeMediaDetailsAsync(_videoPath).GetAwaiter().GetResult();
            return meta.Duration > 0 ? meta.Duration : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void Timeline_ValueChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvent) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void DebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        await ExtractAndShow(_timeline.Value);
    }

    private async Task ExtractAndShow(double timeSec)
    {
        if (_durationSec <= 0) return;
        timeSec = Math.Clamp(timeSec, 0, _durationSec);
        _suppressSliderEvent = true;
        _timeline.Value = timeSec;
        _suppressSliderEvent = false;
        _timeLabel.Text = $"{FormatTime(timeSec)} / {FormatTime(_durationSec)}";

        // Huy extract pending (neu user keo nhanh, chi lay diem cuoi).
        _pendingExtract?.Cancel();
        _pendingExtract = new CancellationTokenSource();

        string framePath;
        lock (_frameCache)
        {
            // Tim khung cache gan nhat (sai so < 0.5s) de tranh extract lai.
            var nearest = _frameCache.Keys.OrderBy(k => Math.Abs(k - timeSec)).FirstOrDefault();
            if (_frameCache.Count > 0 && Math.Abs(nearest - timeSec) < 0.5)
            {
                framePath = _frameCache[nearest];
                timeSec = nearest; // hien thi thoi gian thuc cua khung cache
                _timeLabel.Text = $"{FormatTime(timeSec)} / {FormatTime(_durationSec)}";
            }
            else
            {
                framePath = Path.Combine(_cacheDir, $"preview_{Guid.NewGuid():N}.jpg");
            }
        }

        if (!File.Exists(framePath))
        {
            bool ok = await EngineCore.ExtractFrameToImageAsync(_videoPath, timeSec, framePath, _pendingExtract.Token);
            if (!ok || _pendingExtract.Token.IsCancellationRequested) return;
            lock (_frameCache)
            {
                _frameCache[timeSec] = framePath;
                while (_frameCache.Count > MaxCachedFrames)
                {
                    var oldest = _frameCache.First();
                    _frameCache.Remove(oldest.Key);
                    try { File.Delete(oldest.Value); } catch { }
                }
            }
        }

        if (_pendingExtract.Token.IsCancellationRequested) return;

        // Hien thi anh (copy data khoi stream roi dong stream, giu file cache).
        try
        {
            await using var fs = File.OpenRead(framePath);
            _frameImage.Source = new Bitmap(fs);
        }
        catch { }
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Stop();
        _debounce.Tick -= DebounceTick;
        _pendingExtract?.Cancel();
        _pendingExtract?.Dispose();
        // Giu cache files de cua so sau co the tai su dung (evict tu luc khi qua MaxCachedFrames).
        base.OnClosed(e);
    }
}
