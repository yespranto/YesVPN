using YesVPN.Core.Models;
using YesVPN.Core.Security;

namespace YesVPN.Core.Health;

/// <summary>
/// Tracks consecutive proxy check failures, Dead transitions, and recovery cooldowns using TimeProvider.
/// </summary>
public sealed class ProxyHealthStateMachine
{
    private readonly TimeProvider _timeProvider;

    public int FailureThreshold { get; }
    public TimeSpan DeadCooldown { get; }
    public int DegradedLatencyThresholdMs { get; }

    public ProxyHealthStateMachine(
        TimeProvider? timeProvider = null,
        int failureThreshold = 3,
        TimeSpan? deadCooldown = null,
        int degradedLatencyThresholdMs = 1500)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Failure threshold must be at least 1.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        FailureThreshold = failureThreshold;
        DeadCooldown = deadCooldown ?? TimeSpan.FromSeconds(60);
        DegradedLatencyThresholdMs = degradedLatencyThresholdMs;
    }

    public void RecordSuccess(ProxyEntry proxy, int latencyMs, bool supportsUdp)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        proxy.ConsecutiveFailures = 0;
        proxy.LatencyMs = Math.Max(0, latencyMs);
        proxy.SupportsUdp = supportsUdp;
        proxy.LastError = null;
        proxy.LastCheckedUtc = _timeProvider.GetUtcNow();
        proxy.HealthStatus = latencyMs >= DegradedLatencyThresholdMs
            ? ProxyHealthStatus.Degraded
            : ProxyHealthStatus.Healthy;
    }

    public void RecordFailure(ProxyEntry proxy, string error)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        proxy.ConsecutiveFailures++;
        proxy.LatencyMs = null;
        proxy.LastError = CredentialMasker.Mask(error, proxy.Password is null ? null : [proxy.Password]);
        proxy.LastCheckedUtc = _timeProvider.GetUtcNow();
        proxy.HealthStatus = proxy.ConsecutiveFailures >= FailureThreshold
            ? ProxyHealthStatus.Dead
            : ProxyHealthStatus.Degraded;
    }

    public bool ShouldProbe(ProxyEntry proxy, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        if (!proxy.IsEnabled)
        {
            return false;
        }

        if (force || proxy.HealthStatus != ProxyHealthStatus.Dead || proxy.LastCheckedUtc is null)
        {
            return true;
        }

        return (_timeProvider.GetUtcNow() - proxy.LastCheckedUtc.Value) >= DeadCooldown;
    }

    public static IReadOnlyList<ProxyEntry> GetUsableProxiesOrdered(IEnumerable<ProxyEntry> proxies) =>
        proxies
            .Where(p => p.IsEnabled && p.HealthStatus != ProxyHealthStatus.Dead)
            .OrderBy(p => p.HealthStatus switch
            {
                ProxyHealthStatus.Healthy => 0,
                ProxyHealthStatus.Degraded => 1,
                ProxyHealthStatus.Unknown => 2,
                _ => 3
            })
            .ThenBy(p => p.LatencyMs ?? int.MaxValue)
            .ToList();
}
