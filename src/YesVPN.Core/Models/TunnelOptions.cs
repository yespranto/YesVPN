namespace YesVPN.Core.Models;

/// <summary>
/// Split-tunnel bypass rules (traffic matching any rule goes direct outside the tunnel).
/// </summary>
public sealed class SplitTunnelOptions
{
    public List<string> BypassDomains { get; set; } = new();
    public List<string> BypassIps { get; set; } = new();
    public List<string> BypassApps { get; set; } = new();
}

/// <summary>
/// Tunnel and routing configuration options used to build a sing-box configuration.
/// </summary>
public sealed class TunnelOptions
{
    public ProxyMode Mode { get; set; } = ProxyMode.Auto;
    public Guid? ManualProxyId { get; set; }
    public bool KillSwitchEnabled { get; set; } = true;
    public bool BlockNonDnsUdp { get; set; } = false;
    public bool StrictRoute { get; set; } = true;
    public string DnsServer { get; set; } = "1.1.1.1";
    public string UrlTestUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public string UrlTestInterval { get; set; } = "1m";
    public int UrlTestToleranceMs { get; set; } = 50;
    public string LogLevel { get; set; } = "info";
    public SplitTunnelOptions SplitTunnel { get; set; } = new();
}
