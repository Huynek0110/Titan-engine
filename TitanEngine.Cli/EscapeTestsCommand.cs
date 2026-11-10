using System.Diagnostics;
using System.Text;

namespace TitanEngine.Cli;

/// <summary>
/// Giai đoạn 3 mục 4: test escape đường dẫn trong filter graph (ass=, drawtext fontfile=,
/// custom_shader_path=, movie=) bằng ffmpeg CHẠY THẬT, với đường dẫn có khoảng trắng,
/// tiếng Việt, dấu ' và : (cả kết hợp quote+colon — case làm vỡ thuật toán đầu tiên).
/// Validate FfmpegPathEscape.EscapeForFilterOption theo OS.
/// Công cụ hard-fail (file sai → lỗi thật): ass=, movie=, custom_shader_path=.
/// drawtext fontfile= là SOFT: fontconfig fallback chấp nhận fontfile sai vẫn exit 0
/// (đã chứng minh bằng negative control) → chỉ dùng làm kiểm phụ.
/// Trên Windows không chạy được ffmpeg Windows từ Linux → branch Windows kiểm assert chuỗi.
/// </summary>
internal static class EscapeTestsCommand
{
    private static readonly string[] TrickyDirs =
    {
        "co khoang trang",
        "tiếng Việt có dấu",
        "quote'dir",
        "colon:dir",
        "quote'dir:colon",
        @"back\slash",
    };

    public static async Task<int> RunAsync(CancellationToken token)
    {
        CliServices.Install();
        EngineCore.Initialize();
        string ffmpeg = EngineCore.GetFfmpegPath();
        if (!File.Exists(ffmpeg))
        {
            Console.Error.WriteLine("ffmpeg not found — chạy doctor trước.");
            return 2;
        }

        int failures = 0, skipped = 0;
        void Check(string label, bool ok, string detail)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {label}{(detail.Length > 0 ? $" — {detail}" : "")}");
            if (!ok) failures++;
        }
        void Skip(string label, string reason)
        {
            Console.WriteLine($"  [SKIP] {label} — {reason}");
            skipped++;
        }

        // 0) Thuật toán Windows: assert chuỗi thuần (deterministic, không cần ffmpeg Windows).
        Console.WriteLine("-- Windows escape (assert chuỗi) --");
        Check("EscapeWindows: backslash+colon",
            FfmpegPathEscape.EscapeWindows(@"C:\Users\tôi\a b") == @"C\:/Users/tôi/a b",
            FfmpegPathEscape.EscapeWindows(@"C:\Users\tôi\a b"));
        Check("EscapeWindows: giữ nguyên dấu '",
            FfmpegPathEscape.EscapeWindows(@"a'b") == @"a'b",
            FfmpegPathEscape.EscapeWindows(@"a'b"));
        Check("EscapeForFilterOption (Linux shape): 2 mức, không quote — khớp output đã chứng minh bằng ffmpeg thật",
            TitanPlatform.IsWindows || FfmpegPathEscape.EscapeForFilterOption(@"/a'b:c") == @"/a\\\'b\\:c",
            TitanPlatform.IsWindows ? "(trên Windows)" : FfmpegPathEscape.EscapeForFilterOption(@"/a'b:c"));

        string root = Path.Combine(Path.GetTempPath(), "titan-escape-test");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            foreach (string dir in TrickyDirs)
                Directory.CreateDirectory(Path.Combine(root, dir));

            // Assets trong từng dir khó: font, .ass, .mp4, .glsl
            string? sysFont = EngineCore.ResolveFontFileForDoctor();
            foreach (string dir in TrickyDirs)
            {
                string d = Path.Combine(root, dir);
                if (!string.IsNullOrEmpty(sysFont))
                    File.Copy(sysFont, Path.Combine(d, "font.ttf"), overwrite: true);
                await File.WriteAllTextAsync(Path.Combine(d, "cap.ass"), MinimalAss(), token);
                await File.WriteAllTextAsync(Path.Combine(d, "sh.glsl"), MinimalHookShader(), token);
            }
            string plainVideo = Path.Combine(root, "plain.mp4");
            (bool genOk, string genErr) = await RunFfmpegAsync(ffmpeg,
                    "-y -f lavfi -i color=c=black:s=160x120:r=10 -t 1 -c:v libx264 -preset ultrafast -pix_fmt yuv420p "
                    + $"\"{plainVideo}\"");
            if (!genOk)
            {
                Console.Error.WriteLine($"Tạo video test thất bại: {genErr}");
                return 2;
            }
            foreach (string dir in TrickyDirs)
                File.Copy(plainVideo, Path.Combine(root, dir, "vid.mp4"), overwrite: true);

            Console.WriteLine("-- drawtext fontfile= (SOFT — fontconfig có thể fallback) --");
            foreach (string dir in TrickyDirs)
            {
                string fontPath = Path.Combine(root, dir, "font.ttf");
                if (!File.Exists(fontPath)) { Skip($"drawtext [{dir}]", "không có font hệ thống"); continue; }
                // EscapeForFilterOption trả giá trị option hoàn chỉnh (Windows có '...', Linux không).
                string vf = $"drawtext=fontfile={FfmpegPathEscape.EscapeForFilterOption(fontPath)}:text='Xin chào':fontsize=20";
                Check($"drawtext [{dir}]", await EncodeOkAsync(ffmpeg, vf), Short(vf));
            }

            Console.WriteLine("-- ass= (captions, hard-fail) --");
            foreach (string dir in TrickyDirs)
            {
                string assPath = Path.Combine(root, dir, "cap.ass");
                string fontsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
                string vf = $"ass={FfmpegPathEscape.EscapeForFilterOption(assPath)}:fontsdir={FfmpegPathEscape.EscapeForFilterOption(fontsDir)}";
                Check($"ass [{dir}]", await EncodeOkAsync(ffmpeg, vf), Short(vf));
            }

            Console.WriteLine("-- movie= (hard-fail) --");
            foreach (string dir in TrickyDirs)
            {
                string vidPath = Path.Combine(root, dir, "vid.mp4");
                string vf = "null[base];" +
                            $"movie={FfmpegPathEscape.EscapeForFilterOption(vidPath)}[ov];" +
                            "[base][ov]overlay";
                Check($"movie [{dir}]", await EncodeOkAsync(ffmpeg, vf, useFilterComplex: true), Short(vf));
            }

            Console.WriteLine("-- libplacebo custom_shader_path= (hard-fail) --");
            if (await EncodeOkAsync(ffmpeg, "libplacebo=w=160:h=120")) // libplacebo không shader chạy được?
            {
                foreach (string dir in TrickyDirs)
                {
                    string shaderPath = Path.Combine(root, dir, "sh.glsl");
                    string vf = $"libplacebo=w=160:h=120:custom_shader_path={FfmpegPathEscape.EscapeForFilterOption(shaderPath)}";
                    Check($"shader [{dir}]", await EncodeOkAsync(ffmpeg, vf), Short(vf));
                }
            }
            else
            {
                Skip("libplacebo custom_shader_path", "libplacebo/Vulkan không init được trên máy này (doctor đã ghi riêng)");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* để sạch Temp */ }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"ESCAPE-TESTS: PASS ({skipped} skipped)"
            : $"ESCAPE-TESTS: {failures} FAIL ({skipped} skipped)");
        return failures == 0 ? 0 : 1;
    }

    private static string Short(string s) => s.Length <= 160 ? s : s[..157] + "...";

    private static string MinimalAss() => """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 320
        PlayResY: 240

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,16,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Xin chào Titan
        """;

    /// <summary>Shader hook mpv-style pass-through tối thiểu — libplacebo yêu cầu header //!HOOK///!BIND.</summary>
    private static string MinimalHookShader() => """
        //!HOOK LUMA
        //!BIND HOOKED
        //!WIDTH HOOKED.w
        //!HEIGHT HOOKED.h
        vec4 hook()
        {
            return HOOKED_tex(HOOKED_pos);
        }
        """;

    private static async Task<bool> EncodeOkAsync(string ffmpeg, string vf, bool useFilterComplex = false)
    {
        string filterArg = useFilterComplex
            ? $"-filter_complex \"{vf}\""
            : $"-vf \"{vf}\"";
        string args = $"-y -f lavfi -i color=c=black:s=160x120:r=10 -t 1 {filterArg} -frames:v 5 -f null -";
        return (await RunFfmpegAsync(ffmpeg, args)).Ok;
    }

    private static async Task<(bool Ok, string Stderr)> RunFfmpegAsync(string ffmpeg, string args)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p == null) return (false, "start failed");
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode == 0, stderr);
    }
}
