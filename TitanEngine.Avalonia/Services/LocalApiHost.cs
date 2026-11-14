using System.Threading;
using TitanEngine.Avalonia.ViewModels;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.Services;

/// <summary>
/// Mirror _localApi của MainWindow (WPF): bọc LocalHostServer (tách dùng chung với CLI)
/// quanh engine của UI. externalRenderLock = _renderLock của batch UI nên HOST và
/// START BATCH không render song song (mirror _localApiRenderLock — BtnStartBatch_Click:4455).
/// </summary>
public sealed class LocalApiHost
{
    private readonly LocalHostServer _server;

    public LocalApiHost(MainWindowViewModel owner, SemaphoreSlim externalRenderLock, string prefix)
    {
        _server = new LocalHostServer(owner.Engine, prefix, externalRenderLock, log: owner.AppendLog);
    }

    public bool IsListening => _server.IsListening;

    public void Start() => _server.Start();

    public void Stop() => _server.Stop();
}
