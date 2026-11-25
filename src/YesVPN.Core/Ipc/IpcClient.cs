using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace YesVPN.Core.Ipc;

/// <summary>
/// Named-pipe IPC client used by YesVPN.App and integration tests to control YesVPN.Service.
/// </summary>
public sealed class IpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public IpcClient(string pipeName = IpcConstants.DefaultPipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public Task<IpcResponse> PingAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Command = IpcCommand.Ping }, ct);

    public Task<IpcResponse> GetStatusAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Command = IpcCommand.GetStatus }, ct);

    public Task<IpcResponse> ConnectAsync(
        string configJson,
        string? activeProxyLabel,
        string mode,
        bool killSwitch,
        bool blockNonDnsUdp,
        IEnumerable<string>? proxyHostsToAllow = null,
        IEnumerable<YesVPN.Core.Models.ProxyEntry>? loadBalanceProxies = null,
        CancellationToken ct = default) =>
        SendAsync(new IpcRequest
        {
            Command = IpcCommand.Connect,
            ConfigJson = configJson,
            ActiveProxyLabel = activeProxyLabel,
            Mode = mode,
            KillSwitch = killSwitch,
            BlockNonDnsUdp = blockNonDnsUdp,
            ProxyHostsToAllow = proxyHostsToAllow?.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            LoadBalanceProxies = loadBalanceProxies?.Select(p => new ProxyEndpointSpec
            {
                Host = p.Host,
                Port = p.Port,
                Username = p.Username,
                Password = p.Password,
                Label = p.DisplayLabel
            }).ToList()
        }, ct);

    public Task<IpcResponse> DisconnectAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Command = IpcCommand.Disconnect }, ct);

    public Task<IpcResponse> GetLogsAsync(int tail = 200, CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Command = IpcCommand.GetLogs, Tail = tail }, ct);

    public Task<IpcResponse> ShutdownIfUnprivilegedAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Command = IpcCommand.ShutdownIfUnprivileged }, ct);

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)_connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);

        var utf8NoBom = new UTF8Encoding(false);
        await using var writer = new StreamWriter(client, utf8NoBom, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, utf8NoBom, leaveOpen: true);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("YesVPN service closed the IPC pipe without returning a response.");
        }

        return JsonSerializer.Deserialize<IpcResponse>(line, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize IPC response from YesVPN service.");
    }
}
