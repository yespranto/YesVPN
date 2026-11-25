using YesVPN.Service.Ipc;
using YesVPN.Service.Logging;
using YesVPN.Service.Tunnel;

namespace YesVPN.Service;

public sealed class Worker : BackgroundService
{
    private readonly TunnelManager _tunnelManager;
    private readonly IpcPipeServer _ipcServer;
    private readonly LogRingBuffer _logs;
    private readonly ILogger<Worker> _logger;

    public Worker(
        TunnelManager tunnelManager,
        IpcPipeServer ipcServer,
        LogRingBuffer logs,
        ILogger<Worker> logger)
    {
        _tunnelManager = tunnelManager;
        _ipcServer = ipcServer;
        _logs = logs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logs.Append("Service", "YesVPN.Service starting.");
        await _tunnelManager.InitializeAndCleanupAsync(stoppingToken).ConfigureAwait(false);
        await _ipcServer.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("YesVPN.Service stopping; disconnecting tunnel and cleaning up rules.");
        _logs.Append("Service", "YesVPN.Service stopping; performing guaranteed cleanup.");
        try
        {
            await _tunnelManager.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during shutdown cleanup.");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
