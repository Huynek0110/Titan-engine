using System;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine.Cli;

/// <summary>
/// Giai đoạn 3 `host --port 5555`: HTTP host headless. Giai đoạn 5: toàn bộ server tách thành
/// <see cref="TitanEngine.Core.LocalHostServer"/> dùng chung với HOST: OFF/ON của Avalonia —
/// wrapper này chỉ dựng engine + server và đợi Ctrl+C (hành vi giữ nguyên).
/// </summary>
internal static class HostCommand
{
    public static async Task<int> RunAsync(int port, CancellationToken token)
    {
        CliServices.Install();
        EngineCore.Initialize();
        var engine = new TitanEngineService();
        engine.LogEmitted += m => Console.WriteLine(m);

        var server = new TitanEngine.Core.LocalHostServer(
            engine, $"http://127.0.0.1:{port}/",
            log: Console.WriteLine, logError: Console.Error.WriteLine);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HOST] cannot listen on http://127.0.0.1:{port}/: {ex.Message}");
            return 1;
        }

        Console.WriteLine("[HOST] Ctrl+C để dừng.");
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
        }
        server.Stop();
        return 0;
    }
}
