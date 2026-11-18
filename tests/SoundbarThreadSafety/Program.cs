using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TitanEngine;
using TitanEngine.Avalonia.Services;

namespace SoundbarThreadSafety;

/// <summary>
/// Stress test for soundbar renderer thread-safety.
///
/// Sequential baseline (13 styles x 30 frames SHA-256) vs parallel (4 tasks x rounds x
/// 13 styles x 30 frames interleaved). Every frame SHA-256 must match; exit code =
/// number of mismatching frames (0 = clean). Native segfault = process exits 139.
///
/// Usage: dotnet run -c Release [repeatCount] [roundsPerTask]
///   repeatCount = how many times to repeat the whole seq-vs-par cycle (default 1)
///   roundsPerTask = how many rounds each of the 4 tasks runs (default 20)
/// </summary>
public static class Program
{
    static readonly string[] Styles = Enum.GetNames(typeof(SoundBarStyleId));
    static readonly int BandCount = 64;
    static readonly int FrameCount = 30;
    static readonly int CanvasW = 640;
    static readonly int CanvasH = 360;

    static int failures;
    static int totalFrames;

    public static async Task<int> Main(string[] args)
    {
        int repeatCount = args.Length > 0 ? int.Parse(args[0]) : 1;
        int roundsPerTask = args.Length > 1 ? int.Parse(args[1]) : 20;

        // Install image services (same as App.axaml.cs does for Avalonia desktop).
        EngineServices.Images = new AvaloniaImageServices();
        Console.WriteLine($"Images={EngineServices.Images?.GetType().Name ?? "NULL"}");

        var analysis = BuildFixedAnalysis();

        int exitCode = 0;
        for (int rep = 1; rep <= repeatCount; rep++)
        {
            Console.WriteLine($"=== Repeat {rep}/{repeatCount} ===");

            // 1. Sequential baseline.
            ClearCache();
            var seq = await RunSequential(analysis);

            // 2. Parallel (4 tasks x roundsPerTask).
            ClearCache();
            var par = await RunParallel(analysis, roundsPerTask);

            // 3. Compare.
            int mismatches = Compare(seq, par);
            failures += mismatches;
            totalFrames += seq.Count;
            Console.WriteLine($"  repeat {rep}: seq={seq.Count} frames, par={par.Count} frames, mismatches={mismatches}");
            if (mismatches > 0) exitCode = 1;
        }

        Console.WriteLine($"TOTAL: frames={totalFrames} mismatches={failures}");
        return exitCode;
    }

    static AudioAnalysisResult BuildFixedAnalysis()
    {
        var frames = new AudioFrameData[FrameCount];
        for (int i = 0; i < FrameCount; i++)
        {
            var bands = new float[BandCount];
            double t = i / (double)Math.Max(1, FrameCount - 1);
            for (int b = 0; b < BandCount; b++)
            {
                double bass = Math.Sin(t * Math.PI * 4 + b * 0.1) * 0.5 + 0.5;
                double treble = Math.Cos(t * Math.PI * 7 - b * 0.05) * 0.4 + 0.6;
                double onset = (i % 7 == 0) ? 1.0 : 0.0;
                double v = bass * 0.5 + treble * 0.3 + onset * 0.2;
                bands[b] = (float)Math.Clamp(v, 0.0, 1.0);
            }
            frames[i] = new AudioFrameData
            {
                Bands = bands,
                WaveMin = new float[BandCount],
                WaveMax = new float[BandCount],
                Rms = 0.6f, Peak = 0.9f, Bass = 0.7f, Mid = 0.5f, Treble = 0.4f, Voice = 0.3f,
                Onset = i % 7 == 0
            };
        }
        return new AudioAnalysisResult
        {
            SampleRate = 44100, FftSize = 2048, BandCount = BandCount,
            EffectiveBandCount = BandCount, WaveformColumns = BandCount,
            FrameDurationSeconds = 1.0 / 30.0, FrameCount = FrameCount,
            Frames = frames, AudioFingerprint = "stress-test-fp",
            SourcePath = "/tmp/soundbar-stress/video.mp4",
            AudioFilePath = "/tmp/soundbar-stress/music.mp3",
        };
    }

    static void ClearCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "TitanEngine", "soundbar_cache");
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // Sequential: 13 styles x 30 frames. Returns (style, frameIndex) -> SHA-256.
    static async Task<Dictionary<(string Style, int Frame), string>> RunSequential(AudioAnalysisResult analysis)
    {
        var result = new Dictionary<(string Style, int Frame), string>();
        foreach (var styleName in Styles)
        {
            Console.WriteLine($"  SEQ style={styleName}");
            var style = Enum.Parse<SoundBarStyleId>(styleName);
            var settings = new SoundBarSettings { Style = style, PaletteId = "ncs-cyan" };
            var overlay = await GenerateOverlay(analysis, settings, $"seq-{styleName}");
            if (overlay == null) continue;
            for (int i = 0; i < overlay.FrameCount; i++)
            {
                var path = Path.Combine(overlay.TempRoot, $"soundbar_{i + 1:D4}.png");
                if (File.Exists(path))
                    result[(styleName, i)] = Sha256File(path);
            }
            Console.WriteLine($"  SEQ style={styleName} done frames={overlay.FrameCount}");
        }
        return result;
    }

    // Parallel: 4 tasks x roundsPerTask, each task renders all 13 styles x 30 frames.
    static async Task<Dictionary<(string Style, int Frame), string>> RunParallel(AudioAnalysisResult analysis, int roundsPerTask)
    {
        var result = new Dictionary<(string Style, int Frame), string>();
        var tasks = Enumerable.Range(0, 4).Select(async taskId =>
        {
            var local = new Dictionary<(string Style, int Frame), string>();
            for (int r = 0; r < roundsPerTask; r++)
            {
                foreach (var styleName in Styles)
                {
                    var style = Enum.Parse<SoundBarStyleId>(styleName);
                    var settings = new SoundBarSettings { Style = style, PaletteId = "ncs-cyan" };
                    var overlay = await GenerateOverlay(analysis, settings, $"par-t{taskId}-r{r}-{styleName}");
                    if (overlay == null) continue;
                    for (int i = 0; i < overlay.FrameCount; i++)
                    {
                        var path = Path.Combine(overlay.TempRoot, $"soundbar_{i + 1:D4}.png");
                        if (File.Exists(path))
                            local[(styleName, i)] = Sha256File(path);
                    }
                }
            }
            return local;
        });
        var all = await Task.WhenAll(tasks);
        foreach (var d in all)
            foreach (var kv in d)
                result[kv.Key] = kv.Value;
        return result;
    }

    static async Task<SoundBarGeneratedOverlay?> GenerateOverlay(AudioAnalysisResult analysis, SoundBarSettings settings, string tag)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        return await EngineServices.Images.GenerateSoundBarOverlayAsync(
            analysis, settings, ColorPresets.Default, CanvasW, CanvasH, tag,
            _ => { }, cts.Token);
    }

    static int Compare(Dictionary<(string Style, int Frame), string> seq, Dictionary<(string Style, int Frame), string> par)
    {
        int mismatches = 0;
        foreach (var kv in seq)
        {
            if (!par.TryGetValue(kv.Key, out var parHash))
            {
                mismatches++;
                Console.WriteLine($"  MISSING in parallel: {kv.Key.Style} frame {kv.Key.Frame}");
                continue;
            }
            if (!string.Equals(kv.Value, parHash, StringComparison.OrdinalIgnoreCase))
            {
                mismatches++;
                if (mismatches <= 10)
                    Console.WriteLine($"  DIFFER: {kv.Key.Style} frame {kv.Key.Frame} seq={kv.Value.Substring(0, 12)} par={parHash.Substring(0, 12)}");
            }
        }
        // Also report frames present in parallel but missing from sequential.
        foreach (var kv in par)
        {
            if (!seq.ContainsKey(kv.Key)) mismatches++;
        }
        return mismatches;
    }

    static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }
}
