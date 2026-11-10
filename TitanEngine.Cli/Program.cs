namespace TitanEngine.Cli;

/// <summary>
/// Giai đoạn 3: headless CLI cho TitanEngine.Core.
///   doctor               — kiểm tra môi trường (tool, encoder thật, libplacebo/Vulkan, whisper, upscayl, fonts)
///   render job.json      — render 1 job JSON theo LocalhostRenderRequest + verify output
///   host --port 5555     — HTTP host headless (cùng endpoints như host mode của WPF)
///   test-escape          — test escape đường dẫn filter graph bằng ffmpeg thật
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (command)
            {
                case "doctor":
                    return DoctorCommand.Run();

                case "render":
                {
                    string? jobJson = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
                    if (jobJson == null)
                    {
                        Console.Error.WriteLine("Usage: titan render <job.json> [--skip-decode]");
                        return 2;
                    }
                    bool skipDecode = args.Contains("--skip-decode", StringComparer.Ordinal);
                    return await RenderCommand.RunAsync(jobJson, skipDecode, cts.Token);
                }

                case "host":
                {
                    int port = 5555;
                    for (int i = 1; i < args.Length - 1; i++)
                    {
                        if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out int parsed))
                            port = parsed;
                    }
                    return await HostCommand.RunAsync(port, cts.Token);
                }

                case "test-escape":
                    return await EscapeTestsCommand.RunAsync(cts.Token);

                case "help":
                case "--help":
                case "-h":
                    Console.WriteLine("""
                        TitanEngine CLI (Giai đoạn 3 — Linux port)

                          doctor                  Kiểm tra môi trường: tool, encoder test encode thật,
                                                  libplacebo/Vulkan, whisper, upscayl, fonts
                          render <job.json>       Render 1 job JSON (schema LocalhostRenderRequest),
                                                  verify output: ffprobe + decode sạch. --skip-decode để bỏ
                          host [--port 5555]      HTTP host headless: /api/render, /api/render/async,
                                                  /api/render/wait, /api/queue/add, /api/queue/start,
                                                  /api/render/status?jobId=
                          test-escape             Test escape đường dẫn filter graph (ass=, drawtext=,
                                                  movie=, custom_shader_path=) với khoảng trắng /
                                                  tiếng Việt / dấu ' / dấu :
                        """);
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]} (xem: titan help)");
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[CLI] cancelled");
            return 130;
        }
    }
}
