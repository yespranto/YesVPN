using System.Text.Json.Serialization;

namespace YesVPN.Core.Ipc;

public static class IpcConstants
{
    public const string DefaultPipeName = "YesVPN.Service.IPC";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpcCommand
{
    Ping = 0,
    GetStatus = 1,
    Connect = 2,
    Disconnect = 3,
    GetLogs = 4,
    ShutdownIfUnprivileged = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TunnelState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Error = 4
}

public sealed class ProxyEndpointSpec
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Label { get; set; }
}

public sealed class IpcRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public IpcCommand Command { get; set; }
    public string? ConfigJson { get; set; }
    public string? ActiveProxyLabel { get; set; }
    public string? Mode { get; set; }
    public bool KillSwitch { get; set; } = true;
    public bool BlockNonDnsUdp { get; set; }
    public List<string>? ProxyHostsToAllow { get; set; }
    public List<ProxyEndpointSpec>? LoadBalanceProxies { get; set; }
    public int Tail { get; set; } = 200;
}

public sealed class ServiceStatus
{
    public TunnelState State { get; set; } = TunnelState.Disconnected;
    public string? ActiveProxyLabel { get; set; }
    public string? Mode { get; set; }
    public bool KillSwitchActive { get; set; }
    public int? SingBoxPid { get; set; }
    public DateTimeOffset? ConnectedSinceUtc { get; set; }
    public string? LastError { get; set; }
    public string? SingBoxVersion { get; set; }
    public bool IsElevated { get; set; }
    public long DownloadBytesPerSec { get; set; }
    public long UploadBytesPerSec { get; set; }
    public long TotalDownloadBytes { get; set; }
    public long TotalUploadBytes { get; set; }
}

public sealed class IpcResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ServiceStatus? Status { get; set; }
    public IReadOnlyList<string>? Logs { get; set; }
}
