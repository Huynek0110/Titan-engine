using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace TitanEngine.Avalonia.Services;

/// <summary>
/// Điểm 2 đã duyệt: trình phát audio preview cho AudioEditorWindow bằng ffplay subprocess
/// (thay MediaPlayer WPF — WPF-stay).
/// - Play: `ffplay -nodisp -autoexit -ss <pos> <file>`; Stop/kill để dừng.
/// - Pause: kill + giữ playhead (Stopwatch); Play lại từ playhead (restart ffplay).
/// - Playhead: Stopwatch từ lúc play + offset — GHI CHÚ (đã duyệt): có drift nhỏ giữa playhead
///   hiển thị và ffplay (ffplay start không tức thời, ~0.1-0.5s tuỳ file), và seek (restart
///   ffplay) có độ trễ khởi động lại decoder. Không dùng cho seek chính xác từng mẫu.
/// </summary>
public sealed class FfplayAudioPreview : IDisposable
{
    private Process? _process;
    private readonly Stopwatch _playhead = new();
    private double _positionSeconds;
    private readonly string _audioPath;

    public FfplayAudioPreview(string audioPath) => _audioPath = audioPath;

    /// <summary>ffplay có sẵn trên PATH? (doctor cũng kiểm; thiếu → lỗi rõ ràng khi play).</summary>
    public static bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ffplay", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsPlaying => _process is { HasExited: false };

    public double PositionSeconds
    {
        get
        {
            double pos = _positionSeconds;
            if (IsPlaying && _playhead.IsRunning)
                pos += _playhead.Elapsed.TotalSeconds;
            return pos;
        }
    }

    public event Action? PlaybackEnded;

    public void Play(double fromSeconds)
    {
        StopInternal(keepPosition: false);
        _positionSeconds = Math.Max(0.0, fromSeconds);

        var psi = new ProcessStartInfo("ffplay")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-nodisp");
        psi.ArgumentList.Add("-autoexit");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(_positionSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(_audioPath);

        _process = Process.Start(psi);
        if (_process == null)
            throw new InvalidOperationException("Không khởi động được ffplay — kiểm tra ffplay cài trên PATH (pacman -S ffmpeg).");

        _playhead.Restart();

        // -autoexit: ffplay thoát khi hết file → báo PlaybackEnded.
        var proc = _process;
        Task.Run(() =>
        {
            try
            {
                proc.WaitForExit();
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_process, proc) && proc.HasExited)
                    {
                        _positionSeconds += _playhead.Elapsed.TotalSeconds;
                        _playhead.Reset();
                        PlaybackEnded?.Invoke();
                    }
                });
            }
            catch { /* process killed — bỏ qua */ }
        });
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        _positionSeconds += _playhead.Elapsed.TotalSeconds;
        _playhead.Reset();
        StopInternal(keepPosition: true);
    }

    public void Stop()
    {
        StopInternal(keepPosition: false);
        _positionSeconds = 0;
    }

    /// <summary>Seek = restart ffplay tại vị trí mới (đã ghi chú độ trễ restart ở đầu class).</summary>
    public void Seek(double seconds)
    {
        bool wasPlaying = IsPlaying;
        StopInternal(keepPosition: false);
        _positionSeconds = Math.Max(0.0, seconds);
        if (wasPlaying)
            Play(_positionSeconds);
    }

    private void StopInternal(bool keepPosition)
    {
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
            catch { }
            _process = null;
        }
        _playhead.Reset();
        _ = keepPosition; // vị trí đã được cộng trước khi gọi
    }

    public void Dispose() => StopInternal(keepPosition: false);
}
