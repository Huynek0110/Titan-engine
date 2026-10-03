using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace TitanEngine
{
    public partial class AudioEditorWindow : Window
    {
        private readonly string _audioPath;
        private double _durationSeconds;
        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _isPlaying;
        private bool _isUpdatingSlider;
        private bool _isDraggingTimeline;
        private bool _isLoadedSuccessfully;
        private double _singleTrimDefaultStartSeconds;
        private double _singleTrimDefaultEndSeconds;

        public ObservableCollection<AudioEditSegment> Segments { get; } = new ObservableCollection<AudioEditSegment>();
        public List<AudioEditSegment> ResultSegments { get; private set; } = new List<AudioEditSegment>();

        public AudioEditorWindow(string audioPath, double durationSeconds, List<AudioEditSegment> initialSegments)
        {
            InitializeComponent();

            _audioPath = audioPath;
            _durationSeconds = Math.Max(0.0, durationSeconds);

            Title = $"AUDIO EDITOR - {Path.GetFileName(_audioPath)}";
            txtAudioMeta.Text = BuildAudioMetaText();
            txtTotalTime.Text = AudioEditHelpers.FormatTime(_durationSeconds);

            if (_durationSeconds <= 0.0)
                _durationSeconds = 1.0;

            sldPlayback.Minimum = 0.0;
            sldPlayback.Maximum = _durationSeconds;
            sldPlayback.Value = 0.0;
            txtRangeStart.Text = AudioEditHelpers.FormatTime(0.0);
            txtRangeEnd.Text = AudioEditHelpers.FormatTime(_durationSeconds);
            _singleTrimDefaultStartSeconds = 0.0;
            _singleTrimDefaultEndSeconds = _durationSeconds;

            dgSegments.ItemsSource = Segments;

            Segments.CollectionChanged += Segments_CollectionChanged;
            _timer.Interval = TimeSpan.FromMilliseconds(40);
            _timer.Tick += Timer_Tick;

            _player.MediaOpened += Player_MediaOpened;
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaFailed += Player_MediaFailed;

            if (initialSegments != null)
            {
                ReplaceSegments(AudioEditHelpers.NormalizeSegments(initialSegments, _durationSeconds));
            }

            Loaded += AudioEditorWindow_Loaded;
            SizeChanged += (_, __) => RedrawTimeline();
            Closed += AudioEditorWindow_Closed;

            txtHelp.Text = "Click the timeline to seek. Add one trim or build multiple cut segments, then save.";
            RedrawTimeline();
        }

        private void AudioEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_audioPath))
                {
                    _player.Open(new Uri(_audioPath, UriKind.Absolute));
                }
                else
                {
                    throw new FileNotFoundException($"Audio file not found: {_audioPath}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open audio preview:\n{ex.Message}", "Audio Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
                Close();
                return;
            }

            _timer.Start();
        }

        private void AudioEditorWindow_Closed(object? sender, EventArgs e)
        {
            _timer.Stop();

            try
            {
                _player.Close();
            }
            catch
            {
            }

            Segments.CollectionChanged -= Segments_CollectionChanged;
        }

        private void Player_MediaOpened(object? sender, EventArgs e)
        {
            if (_player.NaturalDuration.HasTimeSpan)
            {
                double naturalDuration = Math.Max(0.0, _player.NaturalDuration.TimeSpan.TotalSeconds);
                if (naturalDuration > 0.0)
                {
                    _durationSeconds = naturalDuration;
                    txtTotalTime.Text = AudioEditHelpers.FormatTime(_durationSeconds);
                    sldPlayback.Maximum = _durationSeconds;
                    txtRangeStart.Text = AudioEditHelpers.FormatTime(0.0);
                    txtRangeEnd.Text = AudioEditHelpers.FormatTime(_durationSeconds);
                    _singleTrimDefaultStartSeconds = 0.0;
                    _singleTrimDefaultEndSeconds = _durationSeconds;
                    UpdateMetaText();
                    RedrawTimeline();
                }
            }

            _isLoadedSuccessfully = true;
        }

        private void Player_MediaEnded(object? sender, EventArgs e)
        {
            _isPlaying = false;
            btnPlayPause.Content = "Play";
            SeekToSeconds(0.0, true);
        }

        private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            MessageBox.Show($"Audio preview failed:\n{e.ErrorException.Message}", "Audio Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_isLoadedSuccessfully)
                return;

            double position = Math.Max(0.0, _player.Position.TotalSeconds);
            if (!_isDraggingTimeline)
            {
                _isUpdatingSlider = true;
                sldPlayback.Value = Math.Min(position, sldPlayback.Maximum);
                _isUpdatingSlider = false;
            }

            txtCurrentTime.Text = AudioEditHelpers.FormatTime(position);
            RedrawTimeline(position);
        }

        private void Segments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (object item in e.OldItems)
                {
                    if (item is AudioEditSegment seg)
                        seg.PropertyChanged -= Segment_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (object item in e.NewItems)
                {
                    if (item is AudioEditSegment seg)
                        seg.PropertyChanged += Segment_PropertyChanged;
                }
            }

            RedrawTimeline();
            UpdateMetaText();
        }

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

        private void btnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoadedSuccessfully)
                return;

            if (_isPlaying)
            {
                _player.Pause();
                _isPlaying = false;
                btnPlayPause.Content = "Play";
            }
            else
            {
                _player.Play();
                _isPlaying = true;
                btnPlayPause.Content = "Pause";
            }
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            _player.Stop();
            _isPlaying = false;
            btnPlayPause.Content = "Play";
            SeekToSeconds(0.0, true);
        }

        private void btnJumpBack_Click(object sender, RoutedEventArgs e) => SeekRelative(-5.0);

        private void btnJumpForward_Click(object sender, RoutedEventArgs e) => SeekRelative(5.0);

        private void sldPlayback_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSlider || !_isLoadedSuccessfully)
                return;

            SeekToSeconds(e.NewValue, false);
        }

        private void btnSetStartCurrent_Click(object sender, RoutedEventArgs e)
        {
            txtRangeStart.Text = AudioEditHelpers.FormatTime(Math.Max(0.0, _player.Position.TotalSeconds));
        }

        private void btnSetEndCurrent_Click(object sender, RoutedEventArgs e)
        {
            txtRangeEnd.Text = AudioEditHelpers.FormatTime(Math.Max(0.0, _player.Position.TotalSeconds));
        }

        private void btnAddSegment_Click(object sender, RoutedEventArgs e)
        {
            if (TryReadRange(out double start, out double end))
            {
                Segments.Add(new AudioEditSegment
                {
                    StartSeconds = start,
                    EndSeconds = end
                });

                NormalizeAndReplaceSegments();
            }
        }

        private void btnApplySingleTrim_Click(object sender, RoutedEventArgs e)
        {
            if (TryReadRange(out double start, out double end))
            {
                ReplaceSegments(new List<AudioEditSegment>
                {
                    new AudioEditSegment
                    {
                        StartSeconds = start,
                        EndSeconds = end
                    }
                });
            }
        }

        private void btnClearCuts_Click(object sender, RoutedEventArgs e)
        {
            Segments.Clear();
            UpdateMetaText();
        }

        private void btnNormalize_Click(object sender, RoutedEventArgs e)
        {
            NormalizeAndReplaceSegments();
        }

        private void btnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (dgSegments.SelectedItem is AudioEditSegment selected)
            {
                Segments.Remove(selected);
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            List<AudioEditSegment> finalSegments = AudioEditHelpers.NormalizeSegments(Segments.ToList(), _durationSeconds);

            if (finalSegments.Count == 0 && tabMode?.SelectedIndex == 0 && TryReadRange(out double start, out double end))
            {
                bool differsFromDefault =
                    Math.Abs(start - _singleTrimDefaultStartSeconds) > 0.001 ||
                    Math.Abs(end - _singleTrimDefaultEndSeconds) > 0.001;

                if (differsFromDefault)
                {
                    finalSegments = AudioEditHelpers.NormalizeSegments(new[]
                    {
                        new AudioEditSegment
                        {
                            StartSeconds = start,
                            EndSeconds = end
                        }
                    }, _durationSeconds);
                }
            }

            ResultSegments = finalSegments;
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void cvsTimeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_durationSeconds <= 0.0 || cvsTimeline.ActualWidth <= 1.0)
                return;

            Point pos = e.GetPosition(cvsTimeline);
            double ratio = Math.Clamp(pos.X / cvsTimeline.ActualWidth, 0.0, 1.0);
            SeekToSeconds(_durationSeconds * ratio, true);
            _isDraggingTimeline = false;
        }

        private void SeekRelative(double deltaSeconds)
        {
            if (!_isLoadedSuccessfully)
                return;

            SeekToSeconds(Math.Max(0.0, _player.Position.TotalSeconds + deltaSeconds), true);
        }

        private void SeekToSeconds(double seconds, bool updateSlider)
        {
            if (!_isLoadedSuccessfully)
                return;

            double clamped = Math.Clamp(seconds, 0.0, Math.Max(0.0, sldPlayback.Maximum));
            try
            {
                _player.Position = TimeSpan.FromSeconds(clamped);
            }
            catch
            {
            }

            txtCurrentTime.Text = AudioEditHelpers.FormatTime(clamped);
            if (updateSlider)
            {
                _isUpdatingSlider = true;
                sldPlayback.Value = clamped;
                _isUpdatingSlider = false;
            }

            RedrawTimeline(clamped);
        }

        private bool TryReadRange(out double startSeconds, out double endSeconds)
        {
            startSeconds = 0.0;
            endSeconds = 0.0;

            if (!AudioEditHelpers.TryParseFlexibleTime(txtRangeStart.Text, out startSeconds))
            {
                MessageBox.Show("Invalid start time.", "Audio Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!AudioEditHelpers.TryParseFlexibleTime(txtRangeEnd.Text, out endSeconds))
            {
                MessageBox.Show("Invalid end time.", "Audio Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show("End time must be greater than start time.", "Audio Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void NormalizeAndReplaceSegments()
        {
            ReplaceSegments(AudioEditHelpers.NormalizeSegments(Segments.ToList(), _durationSeconds));
        }

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

        private double GetRangeEndFallback()
        {
            if (AudioEditHelpers.TryParseFlexibleTime(txtRangeEnd.Text, out double parsed))
                return parsed;
            return Math.Min(5.0, _durationSeconds);
        }

        private string BuildAudioMetaText()
        {
            string fileName = Path.GetFileName(_audioPath);
            string durationText = AudioEditHelpers.FormatTime(_durationSeconds);
            return $"Loaded: {fileName} | Duration: {durationText} | Segments: {Segments.Count}";
        }

        private void UpdateMetaText()
        {
            txtAudioMeta.Text = BuildAudioMetaText();
        }

        private void RedrawTimeline(double playheadSeconds = -1.0)
        {
            if (cvsTimeline == null)
                return;

            double width = cvsTimeline.ActualWidth;
            if (width <= 2.0)
                width = Math.Max(400.0, cvsTimeline.Width > 0 ? cvsTimeline.Width : 900.0);

            double height = cvsTimeline.ActualHeight;
            if (height <= 2.0)
                height = Math.Max(48.0, cvsTimeline.Height > 0 ? cvsTimeline.Height : 64.0);

            cvsTimeline.Children.Clear();

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
            cvsTimeline.Children.Add(background);

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
            cvsTimeline.Children.Add(baseTrack);

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
                cvsTimeline.Children.Add(segRect);

                if (segWidth > 72.0)
                {
                    var label = new TextBlock
                    {
                        Text = $"{segment.StartText} - {segment.EndText}",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    };
                    Canvas.SetLeft(label, left + 6);
                    Canvas.SetTop(label, trackTop + 2);
                    cvsTimeline.Children.Add(label);
                }
            }

            double currentSeconds = playheadSeconds >= 0.0 ? playheadSeconds : Math.Max(0.0, _player.Position.TotalSeconds);
            double playheadX = 7 + ((currentSeconds / usableDuration) * (width - 14));
            playheadX = Math.Clamp(playheadX, 7, width - 7);

            var playhead = new Line
            {
                X1 = playheadX,
                X2 = playheadX,
                Y1 = 10,
                Y2 = height - 10,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 102, 102)),
                StrokeThickness = 2
            };
            cvsTimeline.Children.Add(playhead);
        }
    }
}
