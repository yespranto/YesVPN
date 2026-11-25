namespace YesVPN.Core.Models;

/// <summary>
/// Health status of a configured SOCKS5 proxy.
/// </summary>
public enum ProxyHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Dead = 3
}

/// <summary>
/// Selection mode for outbound proxy routing.
/// </summary>
public enum ProxyMode
{
    Auto = 0,
    Manual = 1,
    LoadBalance = 2
}

/// <summary>
/// Represents a single SOCKS5 proxy endpoint and its latest health telemetry.
/// </summary>
public sealed class ProxyEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Label { get; set; }
    public bool IsEnabled { get; set; } = true;

    public ProxyHealthStatus HealthStatus { get; set; } = ProxyHealthStatus.Unknown;
    public int? LatencyMs { get; set; }
    public bool? SupportsUdp { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public string? LastError { get; set; }

    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label.Trim() : FormatHostPort(Host, Port);

    public bool HasCredentials => !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password);

    public string ToMaskedEndpoint() => FormatHostPort(Host, Port);

    public override string ToString() => $"{DisplayLabel} ({FormatHostPort(Host, Port)})";

    public static string FormatHostPort(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return $":{port}";
        }

        return host.Contains(':') && !host.StartsWith('[')
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
    }
}
