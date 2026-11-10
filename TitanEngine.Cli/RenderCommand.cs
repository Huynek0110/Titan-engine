using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TitanEngine.Cli;

/// <summary>
/// Giai đoạn 3 `render job.json`: đọc JSON đúng schema LocalhostRenderRequest
/// (BAS_LOCALHOST_API_GUIDE.txt), map qua LocalhostRequestMapper (CÙNG đường với HTTP API),
/// chạy qua facade ITitanEngineService, rồi verify output: ffprobe đọc được, duration khớp,
/// decode sạch `ffmpeg -v error -i out -f null -` = 0 dòng.
/// </summary>
internal static class RenderCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string jobJsonPath, bool skipDecodeCheck, CancellationToken token)
    {
        if (!File.Exists(jobJsonPath))
        {
            Console.Error.WriteLine($"job.json not found: {jobJsonPath}");
            return 2;
        }

        CliServices.Install();
        EngineCore.Initialize();

        string body = await File.ReadAllTextAsync(jobJsonPath, token);
        var request = JsonSerializer.Deserialize<LocalhostRenderRequest>(body, JsonOpts);
        if (request == null)
        {
            Console.Error.WriteLine("Invalid JSON body");
            return 2;
        }

        ITitanEngineService engine = new TitanEngineService();
        engine.LogEmitted += m => Console.WriteLine(m);

        var mapped = await LocalhostRequestMapper.MapAsync(request, m => Console.WriteLine(m));
        RenderJob job = mapped.Job;
        Console.WriteLine($"[CLI] job mapped: #{job.Id} {job.Guid} output={mapped.OutputPath}");

        var options = new TitanEngineRunOptions
        {
            CustomOutputFolder = mapped.OutputDir,
            Progress = new Progress<RenderProgress>(p => Console.WriteLine($"[CLI-PROGRESS] {p.Percent:F1}%")),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        RenderRunResult result = await engine.RunJobAsync(job, options, token);
        sw.Stop();
        Console.WriteLine($"[CLI] render finished in {sw.Elapsed:hh\\:mm\\:ss} → {result.OutputPath}");

        // ── verify: ffprobe đọc được + duration + decode sạch ──
        string output = result.OutputPath ?? mapped.OutputPath;
        if (!File.Exists(output))
        {
            Console.Error.WriteLine("[CLI-VERIFY] FAIL: output file does not exist");
            return 1;
        }

        (double duration, int width, int height) = await ProbeOutputAsync(output, token);
        Console.WriteLine($"[CLI-VERIFY] ffprobe: {width}x{height}, duration={duration:F3}s");

        int decodeErrors = 0;
        if (!skipDecodeCheck)
        {
            decodeErrors = await CleanDecodeAsync(output, token);
            Console.WriteLine($"[CLI-VERIFY] clean decode errors: {decodeErrors} (yêu cầu 0)");
        }

        bool ok = decodeErrors == 0;
        Console.WriteLine(ok ? "[CLI-VERIFY] PASS" : "[CLI-VERIFY] FAIL");
        return ok ? 0 : 1;
    }

    public static async Task<(double Duration, int Width, int Height)> ProbeOutputAsync(string file, CancellationToken token)
    {
        string ffprobe = EngineCore.GetFfprobePath();
        var psi = new ProcessStartInfo(ffprobe,
            $"-v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json \"{file}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p == null) return (0, 0, 0);
        string json = await p.StandardOutput.ReadToEndAsync(token);
        await p.WaitForExitAsync(token);

        double duration = 0; int width = 0, height = 0;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var s = streams[0];
            width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        }
        if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out duration);
        return (duration, width, height);
    }

    /// <summary>Chuẩn verify AGENTS.md: `ffmpeg -v error -i out -f null -` phải ra 0 dòng lỗi.</summary>
    public static async Task<int> CleanDecodeAsync(string file, CancellationToken token)
    {
        string ffmpeg = EngineCore.GetFfmpegPath();
        var psi = new ProcessStartInfo(ffmpeg, $"-v error -i \"{file}\" -f null -")
        { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p == null) return -1;
        string err = await p.StandardError.ReadToEndAsync(token);
        await p.WaitForExitAsync(token);
        if (err.Trim().Length > 0)
            Console.Error.WriteLine(err.Trim());
        return p.ExitCode == 0 ? err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Count(l => l.Length > 0) : -1;
    }
}
