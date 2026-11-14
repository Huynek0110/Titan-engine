using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TitanEngine.Avalonia.Services;
using TitanEngine.Core;

// Timeline vẽ bằng Shapes.Path/Rectangle — nhưng code cũng dùng System.IO.Path:
// ghim alias để không mơ hồ (CS0104).
using Path = System.IO.Path;

namespace TitanEngine.Avalonia.Views;

/// <summary>
/// Port của AudioEditorWindow (WPF) theo điểm 2 đã duyệt: MediaPlayer (WPF-stay) thay bằng
/// ffplay subprocess (<see cref="FfplayAudioPreview"/>); timeline vẽ bằng Rectangle/Line của
/// Avalonia (không cần bộ vẽ WPF). Playhead = Stopwatch (drift nhỏ so ffplay + seek có độ trễ
/// restart — đã ghi chú trong FfplayAudioPreview và PORT-PROGRESS.md). Editor KHÔNG có nghe thử
/// hiệu ứng (volume/fade) trong bản gốc — chỉ cắt segment — nên không phát sinh điều kiện 2c.
/// </summary>
public partial class AudioEditorWindow : Window
{
    private readonly string _audioPath = null!;
    private double _durationSeconds;
    private readonly FfplayAudioPreview _player = null!;
    private readonly DispatcherTimer _timer = null!;
    private bool _isPlaying;
    private bool _isUpdatingSlider;
    private bool _isLoadedSuccessfully;
    private double _singleTrimDefaultStartSeconds;
    private double _singleTrimDefaultEndSeconds;

    public ObservableCollection<AudioEditSegment> Segments { get; } = new();

    private readonly TaskCompletionSource<List<AudioEditSegment>?> _tcs = new();

    public AudioEditorWindow()
    {
        InitializeComponent();
    }

    public AudioEditorWindow(string audioPath, double durationSeconds, List<AudioEditSegment> initialSegments) : this()
    {
        _audioPath = audioPath;
        _player = new FfplayAudioPreview(audioPath);

        Title = $"AUDIO EDITOR - {Path.GetFileName(_audioPath)}";
        TxtAudioMeta.Text = BuildAudioMetaText();
        TxtTotalTime.Text = AudioEditHelpers.FormatTime(_durationSeconds);

        _durationSeconds = Math.Max(0.0, durationSeconds);
        if (_durationSeconds <= 0.0)
            _durationSeconds = 1.0;

        SldPlayback.Minimum = 0.0;
        SldPlayback.Maximum = _durationSeconds;
        SldPlayback.Value = 0.0;
        TxtRangeStart.Text = AudioEditHelpers.FormatTime(0.0);
        TxtRangeEnd.Text = AudioEditHelpers.FormatTime(_durationSeconds);
        _singleTrimDefaultStartSeconds = 0.0;
        _singleTrimDefaultEndSeconds = _durationSeconds;

        DgSegments.ItemsSource = Segments;

        Segments.CollectionChanged += Segments_CollectionChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += Timer_Tick;

        _player.PlaybackEnded += Player_PlaybackEnded;

        if (initialSegments != null)
        {
            ReplaceSegments(AudioEditHelpers.NormalizeSegments(initialSegments, _durationSeconds));
        }

        Opened += AudioEditorWindow_Loaded;
        Closed += AudioEditorWindow_Closed;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == ClientSizeProperty) RedrawTimeline();
        };

        TxtHelp.Text = "Click the timeline to seek. Add one trim or build multiple cut segments, then save.";
        RedrawTimeline();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private TextBlock TxtAudioMeta => this.FindControl<TextBlock>("txtAudioMeta")!;
    private TextBlock TxtTotalTime => this.FindControl<TextBlock>("txtTotalTime")!;
    private TextBlock TxtCurrentTime => this.FindControl<TextBlock>("txtCurrentTime")!;
    private Slider SldPlayback => this.FindControl<Slider>("sldPlayback")!;
    private TextBox TxtRangeStart => this.FindControl<TextBox>("txtRangeStart")!;
    private TextBox TxtRangeEnd => this.FindControl<TextBox>("txtRangeEnd")!;
    private ListBox DgSegments => this.FindControl<ListBox>("dgSegments")!;
    private Canvas CvsTimeline => this.FindControl<Canvas>("cvsTimeline")!;
    private TextBlock TxtHelp => this.FindControl<TextBlock>("txtHelp")!;
    private Button BtnPlayPause => this.FindControl<Button>("btnPlayPause")!;

    // ── AudioEditorWindow_Loaded: mirror "Unable to open audio preview" — ffplay thiếu → báo rõ + đóng ──
    private async void AudioEditorWindow_Loaded(object? sender, EventArgs e)
    {
        try
        {
            if (!File.Exists(_audioPath))
                throw new FileNotFoundException($"Audio file not found: {_audioPath}");

            if (!FfplayAudioPreview.IsAvailable())
                throw new InvalidOperationException(
                    "ffplay không tìm thấy trên PATH. Cài ffmpeg đầy đủ (Arch/CachyOS: sudo pacman -S ffmpeg). " +
                    "Trên Windows: ffplay.exe phải nằm cạnh ffmpeg.exe.");
        }
        catch (Exception ex)
        {
            UserPrompt.Alert("Audio Editor", $"Unable to open audio preview:\n{ex.Message}", error: true);
            _tcs.TrySetResult(null);
            Close();
            return;
        }

        _isLoadedSuccessfully = true;
        _timer.Start();
        await Task.CompletedTask;
    }

    private void AudioEditorWindow_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _player.Dispose();
        Segments.CollectionChanged -= Segments_CollectionChanged;
    }

    private void Player_PlaybackEnded()
    {
        _isPlaying = false;
        BtnPlayPause.Content = "Play";
        SeekToSeconds(0.0, updateSlider: true);
    }

    // ── Timer_Tick: playhead = ffplay Stopwatch ──
    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_isLoadedSuccessfully)
            return;

        double position = Math.Max(0.0, _player.PositionSeconds);
        if (!_isUpdatingSlider)
        {
            _isUpdatingSlider = true;
            SldPlayback.Value = Math.Min(position, SldPlayback.Maximum);
            _isUpdatingSlider = false;
        }

        TxtCurrentTime.Text = AudioEditHelpers.FormatTime(position);
        RedrawTimeline(position);
    }

    private void Segments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (object item in e.OldItems)
                if (item is AudioEditSegment seg) seg.PropertyChanged -= Segment_PropertyChanged;

        if (e.NewItems != null)
            foreach (object item in e.NewItems)
                if (item is AudioEditSegment seg) seg.PropertyChanged += Segment_PropertyChanged;

        RedrawTimeline();
        UpdateMetaText();
    }

    // ── Segment_PropertyChanged: đảo start/end nếu ngược ──
    private void Segment_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AudioEditSegment segment)
        {
            if (segment.EndSeconds < segment.StartSeconds)
            {
                double tmp = segment.StartSeconds;
                segment.StartSeconds = segment.EndSeconds;
                segment.EndSeconds = tmp;
            }
        }
        RedrawTimeline();
        UpdateMetaText();
    }

    private void btnPlayPause_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isLoadedSuccessfully) return;

        if (_isPlaying)
        {
            _player.Pause();
            _isPlaying = false;
            BtnPlayPause.Content = "Play";
        }
        else
        {
            _player.Play(_player.PositionSeconds);
            _isPlaying = true;
            BtnPlayPause.Content = "Pause";
        }
    }

    private void btnStop_Click(object? sender, RoutedEventArgs e)
    {
        _player.Stop();
        _isPlaying = false;
        BtnPlayPause.Content = "Play";
        SeekToSeconds(0.0, updateSlider: true);
    }

    private void btnJumpBack_Click(object? sender, RoutedEventArgs e) => SeekRelative(-5.0);
    private void btnJumpForward_Click(object? sender, RoutedEventArgs e) => SeekRelative(5.0);

    private void sldPlayback_ValueChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingSlider || !_isLoadedSuccessfully) return;
        SeekToSeconds(SldPlayback.Value, updateSlider: false);
    }

    private void btnSetStartCurrent_Click(object? sender, RoutedEventArgs e)
    {
        TxtRangeStart.Text = AudioEditHelpers.FormatTime(Math.Max(0.0, _player.PositionSeconds));
    }

    private void btnSetEndCurrent_Click(object? sender, RoutedEventArgs e)
    {
        TxtRangeEnd.Text = AudioEditHelpers.FormatTime(Math.Max(0.0, _player.PositionSeconds));
    }

    private void btnAddSegment_Click(object? sender, RoutedEventArgs e)
    {
        if (TryReadRange(out double start, out double end))
        {
            Segments.Add(new AudioEditSegment { StartSeconds = start, EndSeconds = end });
            NormalizeAndReplaceSegments();
        }
    }

    private void btnApplySingleTrim_Click(object? sender, RoutedEventArgs e)
    {
        if (TryReadRange(out double start, out double end))
        {
            ReplaceSegments(new List<AudioEditSegment>
            {
                new AudioEditSegment { StartSeconds = start, EndSeconds = end }
            });
        }
    }

    private void btnClearCuts_Click(object? sender, RoutedEventArgs e)
    {
        Segments.Clear();
        UpdateMetaText();
    }

    private void btnNormalize_Click(object? sender, RoutedEventArgs e) => NormalizeAndReplaceSegments();

    private void btnRemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (DgSegments.SelectedItem is AudioEditSegment selected)
            Segments.Remove(selected);
    }

    private void btnSave_Click(object? sender, RoutedEventArgs e)
    {
        List<AudioEditSegment> finalSegments = AudioEditHelpers.NormalizeSegments(Segments.ToList(), _durationSeconds);

        // Single Trim fallback (btnSave_Click gốc: nếu tab 0 và range khác mặc định).
        if (finalSegments.Count == 0 && TabMode?.SelectedIndex == 0 && TryReadRange(out double start, out double end))
        {
            bool differsFromDefault =
                Math.Abs(start - _singleTrimDefaultStartSeconds) > 0.001 ||
                Math.Abs(end - _singleTrimDefaultEndSeconds) > 0.001;

            if (differsFromDefault)
            {
                finalSegments = AudioEditHelpers.NormalizeSegments(new[]
                {
                    new AudioEditSegment { StartSeconds = start, EndSeconds = end }
                }, _durationSeconds);
            }
        }

        _tcs.TrySetResult(finalSegments);
        Close();
    }

    private void btnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(null);
        Close();
    }

    private TabControl? TabMode => this.FindControl<TabControl>("tabMode");

    // ── cvsTimeline_MouseLeftButtonDown: click để seek ──
    private void CvsTimeline_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_durationSeconds <= 0.0 || CvsTimeline.Bounds.Width <= 1.0) return;

        Point pos = e.GetPosition(CvsTimeline);
        double ratio = Math.Clamp(pos.X / CvsTimeline.Bounds.Width, 0.0, 1.0);
        SeekToSeconds(_durationSeconds * ratio, updateSlider: true);
        e.Handled = true;
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (!_isLoadedSuccessfully) return;
        SeekToSeconds(Math.Max(0.0, _player.PositionSeconds + deltaSeconds), updateSlider: true);
    }

    private void SeekToSeconds(double seconds, bool updateSlider)
    {
        if (!_isLoadedSuccessfully) return;

        double clamped = Math.Clamp(seconds, 0.0, Math.Max(0.0, SldPlayback.Maximum));
        _player.Seek(clamped);

        TxtCurrentTime.Text = AudioEditHelpers.FormatTime(clamped);
        if (updateSlider)
        {
            _isUpdatingSlider = true;
            SldPlayback.Value = clamped;
            _isUpdatingSlider = false;
        }

        RedrawTimeline(clamped);
    }

    private bool TryReadRange(out double startSeconds, out double endSeconds)
    {
        startSeconds = 0.0;
        endSeconds = 0.0;

        if (!AudioEditHelpers.TryParseFlexibleTime(TxtRangeStart.Text, out startSeconds))
        {
            UserPrompt.Alert("Audio Editor", "Invalid start time.");
            return false;
        }

        if (!AudioEditHelpers.TryParseFlexibleTime(TxtRangeEnd.Text, out endSeconds))
        {
            UserPrompt.Alert("Audio Editor", "Invalid end time.");
            return false;
        }

        startSeconds = Math.Max(0.0, startSeconds);
        endSeconds = Math.Max(0.0, endSeconds);

        if (_durationSeconds > 0.0)
        {
            startSeconds = Math.Min(startSeconds, _durationSeconds);
            endSeconds = Math.Min(endSeconds, _durationSeconds);
        }

        if (endSeconds <= startSeconds + 0.001)
        {
            UserPrompt.Alert("Audio Editor", "End time must be greater than start time.");
            return false;
        }

        return true;
    }

    private void NormalizeAndReplaceSegments() => ReplaceSegments(AudioEditHelpers.NormalizeSegments(Segments.ToList(), _durationSeconds));

    private void ReplaceSegments(IEnumerable<AudioEditSegment> segments)
    {
        foreach (AudioEditSegment seg in Segments)
            seg.PropertyChanged -= Segment_PropertyChanged;

        Segments.Clear();

        foreach (AudioEditSegment seg in segments)
        {
            AudioEditSegment clone = new AudioEditSegment
            {
                StartSeconds = seg.StartSeconds,
                EndSeconds = seg.EndSeconds
            };
            clone.PropertyChanged += Segment_PropertyChanged;
            Segments.Add(clone);
        }

        UpdateMetaText();
        RedrawTimeline();
    }

    private string BuildAudioMetaText()
    {
        string fileName = Path.GetFileName(_audioPath);
        string durationText = AudioEditHelpers.FormatTime(_durationSeconds);
        return $"Loaded: {fileName} | Duration: {durationText} | Segments: {Segments.Count}";
    }

    private void UpdateMetaText() => TxtAudioMeta.Text = BuildAudioMetaText();

    // ── RedrawTimeline: Rectangle/Line Avalonia (mirror màu/độ lệch của bản WPF) ──
    private void RedrawTimeline(double playheadSeconds = -1.0)
    {
        if (CvsTimeline == null) return;

        double width = CvsTimeline.Bounds.Width;
        if (width <= 2.0)
            width = Math.Max(400.0, CvsTimeline.Width > 0 ? CvsTimeline.Width : 900.0);

        double height = CvsTimeline.Bounds.Height;
        if (height <= 2.0)
            height = Math.Max(48.0, CvsTimeline.Height > 0 ? CvsTimeline.Height : 64.0);

        CvsTimeline.Children.Clear();

        var background = new Rectangle
        {
            Width = width - 2,
            Height = height - 2,
            RadiusX = 4,
            RadiusY = 4,
            Fill = new SolidColorBrush(Color.FromRgb(16, 16, 22)),
            Stroke = new SolidColorBrush(Color.FromRgb(52, 52, 62)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(background, 1);
        Canvas.SetTop(background, 1);
        CvsTimeline.Children.Add(background);

        double trackTop = 18.0;
        double trackHeight = Math.Max(18.0, height - 36.0);
        double usableDuration = Math.Max(_durationSeconds, 0.001);

        var baseTrack = new Rectangle
        {
            Width = Math.Max(0.0, width - 14),
            Height = trackHeight,
            RadiusX = 4,
            RadiusY = 4,
            Fill = new SolidColorBrush(Color.FromRgb(28, 28, 38))
        };
        Canvas.SetLeft(baseTrack, 7);
        Canvas.SetTop(baseTrack, trackTop);
        CvsTimeline.Children.Add(baseTrack);

        foreach (AudioEditSegment segment in Segments.OrderBy(s => s.StartSeconds))
        {
            double left = 7 + ((segment.StartSeconds / usableDuration) * (width - 14));
            double segWidth = Math.Max(3.0, ((segment.EndSeconds - segment.StartSeconds) / usableDuration) * (width - 14));
            segWidth = Math.Min(segWidth, Math.Max(0.0, width - 14 - (left - 7)));

            var segRect = new Rectangle
            {
                Width = segWidth,
                Height = trackHeight,
                RadiusX = 4,
                RadiusY = 4,
                Fill = new SolidColorBrush(Color.FromArgb(230, 0, 186, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 133, 235, 255)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(segRect, left);
            Canvas.SetTop(segRect, trackTop);
            CvsTimeline.Children.Add(segRect);

            if (segWidth > 72.0)
            {
                var label = new TextBlock
                {
                    Text = $"{segment.StartText} - {segment.EndText}",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold
                };
                Canvas.SetLeft(label, left + 6);
                Canvas.SetTop(label, trackTop + 2);
                CvsTimeline.Children.Add(label);
            }
        }

        double currentSeconds = playheadSeconds >= 0.0 ? playheadSeconds : Math.Max(0.0, _player.PositionSeconds);
        double playheadX = 7 + ((currentSeconds / usableDuration) * (width - 14));
        playheadX = Math.Clamp(playheadX, 7, width - 7);

        var playhead = new Line
        {
            StartPoint = new Point(playheadX, 10),
            EndPoint = new Point(playheadX, height - 10),
            Stroke = new SolidColorBrush(Color.FromRgb(255, 102, 102)),
            StrokeThickness = 2
        };
        CvsTimeline.Children.Add(playhead);
    }

    protected override void OnClosed(EventArgs e)
    {
        // No-op nếu Save/Cancel đã set (TrySetResult không ghi đè result đã có).
        _tcs.TrySetResult(null);
        base.OnClosed(e);
    }

    public Task<List<AudioEditSegment>?> ShowResultAsync(Window owner)
        => ShowDialog(owner).ContinueWith(_ => _tcs.Task).Unwrap();
}
