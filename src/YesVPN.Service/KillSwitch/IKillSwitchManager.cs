namespace YesVPN.Service.KillSwitch;

/// <summary>
/// Controls system-level kill switch and DNS leak protection rules.
/// </summary>
public interface IKillSwitchManager : IDisposable
{
    bool IsActive { get; }

    /// <summary>
    /// Enables kill-switch filters blocking non-tunnel traffic and outbound port 53 outside the tunnel.
    /// </summary>
    Task EnableAsync(
        string singBoxExePath,
        IReadOnlyList<string> proxyHostsToAllow,
        bool blockNonDnsUdp,
        CancellationToken ct = default);

    /// <summary>
    /// Removes all YesVPN kill-switch filters and restores normal networking.
    /// </summary>
    Task DisableAsync(CancellationToken ct = default);

    /// <summary>
    /// Cleans up any stale firewall rules or WFP sublayers from previous runs/crashes.
    /// </summary>
    Task CleanupStaleRulesAsync(CancellationToken ct = default);
}
