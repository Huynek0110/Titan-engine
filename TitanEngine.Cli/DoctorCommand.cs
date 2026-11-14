using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TitanEngine.Cli;

/// <summary>
/// Giai đoạn 3 `doctor`: OS, tool + version thật, encoder test encode thật,
/// libplacebo/Vulkan, whisper, upscayl, fonts. Thoát 0 khi mọi thứ bắt buộc OK.
/// </summary>
internal static class DoctorCommand
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string label, string detail)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "MISS")}] {label}: {detail}");
            if (!ok) failures++;
        }

        Console.WriteLine("== TitanEngine doctor ==");
        Console.WriteLine($"OS: {TitanPlatform.OsName} — {RuntimeInformation.OSDescription.Trim()}");
        Console.WriteLine($"Runtime: .NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"App dir: {AppContext.BaseDirectory}");
        Console.WriteLine();

        Console.WriteLine("-- Tools --");
        string ffmpeg = string.Empty, ffprobe = string.Empty;
        try
        {
            EngineCore.Initialize();
            ffmpeg = EngineCore.GetFfmpegPath();
            ffprobe = EngineCore.GetFfprobePath();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [MISS] engine init: {ex.Message}");
            failures++;
        }

        bool ffmpegOk = File.Exists(ffmpeg) && TitanPlatform.IsExecutable(ffmpeg);
        Check(ffmpegOk, "ffmpeg", ffmpeg);
        Check(File.Exists(ffprobe) && TitanPlatform.IsExecutable(ffprobe), "ffprobe", ffprobe);
        if (ffmpegOk)
            Check(true, "ffmpeg version", FirstLine(ffmpeg, "-version"));

        string ffplay = TitanPlatform.FindOnPath("ffplay") ?? Path.Combine(AppContext.BaseDirectory, TitanPlatform.BinaryName("ffplay"));
        // Điểm 2a đã duyệt: ffplay là player của Audio Editor (Avalonia) — thiếu thì editor báo rõ khi mở.
        Check(File.Exists(ffplay), "ffplay (tuỳ chọn — thiếu thì Audio Editor không nghe thử được)", ffplay);

        string upscayl = EngineCore.GetUpscaylExecutablePath();
        Check(File.Exists(upscayl) && TitanPlatform.IsExecutable(upscayl), "upscayl (tuỳ chọn — không có thì fallback internal)", upscayl);

        string whisper = Path.Combine(AppContext.BaseDirectory, "Assets", "Whisper", TitanPlatform.BinaryName("whisper-cli"));
        Check(File.Exists(whisper) && TitanPlatform.IsExecutable(whisper), "whisper-cli (tuỳ chọn — thiếu thì caption bị skip)", whisper);

        Console.WriteLine();
        Console.WriteLine("-- Encoders (probe bằng test encode thật ~1s) --");
        if (ffmpegOk)
        {
            bool nvenc = EngineCore.HasNvencSupport();
            string nvencDetail = nvenc ? "encode test pass" : NvencFailReason();
            Check(nvenc, "h264_nvenc", nvencDetail);
            Check(EngineCore.HasHevcNvencSupport(), "hevc_nvenc", "");
            Check(EngineCore.HasAv1NvencSupport(), "av1_nvenc (RTX trở lên)", "");
            Check(EngineCore.HasH264VaapiSupport(), "h264_vaapi (AMD/Intel)", $"node={EngineCore.GetVaapiRenderNode() ?? "none"}");
            Check(EngineCore.HasH264QsvSupport(), "h264_qsv (Intel)", "");
            Check(true, "libx264", "baseline");
        }

        Console.WriteLine();
        Console.WriteLine("-- Internal upscale (libplacebo/Vulkan) --");
        if (ffmpegOk)
        {
            bool hasFilter = FilterListContains(ffmpeg, "libplacebo");
            Check(hasFilter, "ffmpeg filter libplacebo", "");
            string shader = Path.Combine(AppContext.BaseDirectory, "shaders", "FSRCNNX.glsl");
            Check(File.Exists(shader), "FSRCNNX.glsl", shader);
            bool vulkan = VulkanInitTest(ffmpeg);
            Check(vulkan, "Vulkan device init + libplacebo test", vulkan ? "test pass" : "init/test fail");
        }

        Console.WriteLine();
        Console.WriteLine("-- Fonts --");
        string font = EngineCore.ResolveFontFileForDoctor();
        Check(!string.IsNullOrEmpty(font), "drawtext fontfile", string.IsNullOrEmpty(font) ? "no system font found" : font);
        Console.WriteLine($"  bundled caption fonts: {ListDir(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"))}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "DOCTOR: ALL REQUIRED OK" : $"DOCTOR: {failures} item(s) missing (tuỳ chọn được ghi MISS nhưng không chặn render)");
        return 0;
    }

    private static string FirstLine(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return "start failed";
            string first = p.StandardOutput.ReadLine() ?? p.StandardError.ReadLine() ?? "";
            p.WaitForExit(10000);
            return first;
        }
        catch (Exception ex) { return $"error: {ex.Message}"; }
    }

    private static bool FilterListContains(string ffmpeg, string filter)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -filters") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return false;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Test thật: init Vulkan device + chạy libplacebo 1s (không shader) — ffmpeg yêu cầu Vulkan device khi libplacebo chạy.</summary>
    private static bool VulkanInitTest(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -init_hw_device vulkan=vk -f lavfi -i color=c=black:s=320x240:r=30 -t 1 -vf libplacebo=w=320:h=240 -f null -")
            { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return false;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            if (p.ExitCode != 0)
                Console.WriteLine($"          vulkan test stderr: {err.Trim()}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"          vulkan test error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Chạy test encode thật h264_nvenc 1s, trả dòng stderr đầu tiên là lý do nếu fail.</summary>
    private static string NvencFailReason()
    {
        string ffmpeg = EngineCore.GetFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
            return "ffmpeg không tìm thấy";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=320x240:r=30 -t 1 -c:v h264_nvenc -preset medium -cq 18 -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "khởi chạy ffmpeg thất bại";
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode == 0) return "encode test pass";
            foreach (string line in err.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return $"ffmpeg exit {p.ExitCode} (không có stderr chi tiết)";
        }
        catch (Exception ex)
        {
            return $"lỗi probe: {ex.Message}";
        }
    }

    private static string ListDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "(không có)";
            return string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        catch { return "(lỗi đọc)"; }
    }
}
