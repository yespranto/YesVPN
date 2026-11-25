using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using YesVPN.Core.Ipc;
using YesVPN.Service.Logging;
using YesVPN.Service.Tunnel;

namespace YesVPN.Service.Ipc;

/// <summary>
/// Named-pipe IPC server exposing Connect, Disconnect, GetStatus, GetLogs, and Ping commands.
/// </summary>
public sealed class IpcPipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TunnelManager _tunnelManager;
    private readonly LogRingBuffer _logs;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly string _pipeName;
    private readonly PipeSecurity? _pipeSecurity;

    [ActivatorUtilitiesConstructor]
    public IpcPipeServer(
        TunnelManager tunnelManager,
        LogRingBuffer logs,
        IHostApplicationLifetime lifetime)
        : this(tunnelManager, logs, lifetime, IpcConstants.DefaultPipeName, null)
    {
    }

    public IpcPipeServer(
        TunnelManager tunnelManager,
        LogRingBuffer logs,
        IHostApplicationLifetime? lifetime = null,
        string pipeName = IpcConstants.DefaultPipeName,
        PipeSecurity? pipeSecurity = null)
    {
        _tunnelManager = tunnelManager;
        _logs = logs;
        _lifetime = lifetime;
        _pipeName = pipeName;
        _pipeSecurity = pipeSecurity ?? IpcPipeAcl.CreateLocalInteractivePipeSecurity();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logs.Append("IPC", $"Listening on named pipe \\\\.\\pipe\\{_pipeName}");

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 65536,
                    outBufferSize: 65536,
                    _pipeSecurity);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Handle connection concurrently so GetStatus/GetLogs never block on Connect
                var connectedPipe = server;
                server = null;
                _ = Task.Run(() => HandleClientAsync(connectedPipe, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _logs.Append("IPC", $"Pipe accept error: {ex.Message}");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        var utf8NoBom = new UTF8Encoding(false);
        using var reader = new StreamReader(pipe, utf8NoBom, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8NoBom, leaveOpen: true) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            IpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<IpcRequest>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                var errResp = new IpcResponse { Ok = false, Error = $"Invalid JSON request: {ex.Message}" };
                await writer.WriteLineAsync(JsonSerializer.Serialize(errResp, JsonOptions)).ConfigureAwait(false);
                return;
            }

            if (request is null)
            {
                return;
            }

            var response = await DispatchAsync(request, ct).ConfigureAwait(false);
            var responseJson = JsonSerializer.Serialize(response, JsonOptions);
            await writer.WriteLineAsync(responseJson.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown
        }
        catch (Exception ex)
        {
            _logs.Append("IPC", $"Client handling error: {ex.Message}");
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommand.Ping:
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Status = _tunnelManager.GetStatus()
                    };

                case IpcCommand.GetStatus:
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Status = _tunnelManager.GetStatus()
                    };

                case IpcCommand.Connect:
                {
                    var status = await _tunnelManager.ConnectAsync(request, ct).ConfigureAwait(false);
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Status = status
                    };
                }

                case IpcCommand.Disconnect:
                {
                    var status = await _tunnelManager.DisconnectAsync(ct).ConfigureAwait(false);
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Status = status
                    };
                }

                case IpcCommand.GetLogs:
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Status = _tunnelManager.GetStatus(),
                        Logs = _tunnelManager.GetLogs(request.Tail)
                    };

                case IpcCommand.ShutdownIfUnprivileged:
                    if (TunnelManager.IsProcessElevated())
                    {
                        return new IpcResponse
                        {
                            Id = request.Id,
                            Ok = false,
                            Error = "Cannot shut down an elevated or system service over IPC."
                        };
                    }

                    _logs.Append("Service", "Shutting down unprivileged service instance for elevated restart.");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        _lifetime?.StopApplication();
                    });
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Status = _tunnelManager.GetStatus()
                    };

                default:
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = false,
                        Error = $"Unknown IPC command: {request.Command}"
                    };
            }
        }
        catch (Exception ex)
        {
            return new IpcResponse
            {
                Id = request.Id,
                Ok = false,
                Error = ex.Message,
                Status = _tunnelManager.GetStatus()
            };
        }
    }
}
