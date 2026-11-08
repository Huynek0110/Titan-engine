using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TitanEngine
{
    public class AudioEditSegment : INotifyPropertyChanged
    {
        private double _startSeconds;
        private double _endSeconds;

        public double StartSeconds
        {
            get => _startSeconds;
            set
            {
                value = Math.Max(0.0, value);
                if (Math.Abs(_startSeconds - value) < 0.0001)
                    return;

                _startSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartText));
                OnPropertyChanged(nameof(DurationSeconds));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }

        public double EndSeconds
        {
            get => _endSeconds;
            set
            {
                value = Math.Max(0.0, value);
                if (Math.Abs(_endSeconds - value) < 0.0001)
                    return;

                _endSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndText));
                OnPropertyChanged(nameof(DurationSeconds));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }

        [JsonIgnore]
        public string StartText
        {
            get => AudioEditHelpers.FormatTime(StartSeconds);
            set
            {
                if (AudioEditHelpers.TryParseFlexibleTime(value, out double seconds))
                    StartSeconds = seconds;
            }
        }

        [JsonIgnore]
        public string EndText
        {
            get => AudioEditHelpers.FormatTime(EndSeconds);
            set
            {
                if (AudioEditHelpers.TryParseFlexibleTime(value, out double seconds))
                    EndSeconds = seconds;
            }
        }

        [JsonIgnore]
        public double DurationSeconds => Math.Max(0.0, EndSeconds - StartSeconds);

        [JsonIgnore]
        public string DurationText => $"{DurationSeconds:0.###}s";

        [JsonIgnore]
        public string DisplayLabel => $"{StartText} - {EndText} ({DurationText})";

        public override string ToString() => DisplayLabel;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public static class AudioEditHelpers
    {
        public static bool TryParseFlexibleTime(string? input, out double seconds)
        {
            seconds = 0.0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            string trimmed = input.Trim();

            if (TimeSpan.TryParse(trimmed, out TimeSpan parsed))
            {
                seconds = Math.Max(0.0, parsed.TotalSeconds);
                return true;
            }

            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out seconds) ||
                double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out seconds) ||
                double.TryParse(trimmed.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out seconds))
            {
                seconds = Math.Max(0.0, seconds);
                return true;
            }

            return false;
        }

        public static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                seconds = 0.0;

            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            if (Math.Abs(seconds - Math.Round(seconds)) < 0.0005)
                return ts.ToString(@"hh\:mm\:ss");

            return ts.ToString(@"hh\:mm\:ss\.fff");
        }

        public static List<AudioEditSegment> NormalizeSegments(IEnumerable<AudioEditSegment>? segments, double? maxDurationSeconds = null)
        {
            var normalized = new List<AudioEditSegment>();
            if (segments == null)
                return normalized;

            double maxDuration = maxDurationSeconds.HasValue && maxDurationSeconds.Value > 0.0
                ? maxDurationSeconds.Value
                : double.PositiveInfinity;

            foreach (var segment in segments)
            {
                if (segment == null)
                    continue;

                double start = Math.Max(0.0, segment.StartSeconds);
                double end = Math.Max(0.0, segment.EndSeconds);

                if (double.IsNaN(start) || double.IsInfinity(start) ||
                    double.IsNaN(end) || double.IsInfinity(end))
                {
                    continue;
                }

                if (maxDuration < double.PositiveInfinity)
                {
                    start = Math.Min(start, maxDuration);
                    end = Math.Min(end, maxDuration);
                }

                if (end <= start + 0.001)
                    continue;

                normalized.Add(new AudioEditSegment
                {
                    StartSeconds = start,
                    EndSeconds = end
                });
            }

            if (normalized.Count == 0)
                return normalized;

            normalized = normalized
                .OrderBy(s => s.StartSeconds)
                .ThenBy(s => s.EndSeconds)
                .ToList();

            var merged = new List<AudioEditSegment> { normalized[0] };
            for (int i = 1; i < normalized.Count; i++)
            {
                var current = normalized[i];
                var last = merged[merged.Count - 1];

                if (current.StartSeconds <= last.EndSeconds + 0.001)
                {
                    if (current.EndSeconds > last.EndSeconds)
                        last.EndSeconds = current.EndSeconds;
                }
                else
                {
                    merged.Add(current);
                }
            }

            return merged;
        }

        public static bool IsFullTrackSelection(IReadOnlyList<AudioEditSegment>? segments, double durationSeconds, double epsilon = 0.05)
        {
            if (segments == null || segments.Count != 1 || durationSeconds <= 0.0)
                return false;

            var seg = segments[0];
            return seg.StartSeconds <= epsilon && seg.EndSeconds >= durationSeconds - epsilon;
        }
    }
}
