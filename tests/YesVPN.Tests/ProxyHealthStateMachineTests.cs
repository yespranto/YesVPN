using Xunit;
using YesVPN.Core.Health;
using YesVPN.Core.Models;

namespace YesVPN.Tests;

public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _utcNow = start ?? new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}

public sealed class ProxyHealthStateMachineTests
{
    [Fact]
    public void RecordFailure_TransitionsToDeadAfterThreshold_AndRecoversAfterCooldown()
    {
        var clock = new FakeTimeProvider();
        var sm = new ProxyHealthStateMachine(clock, failureThreshold: 3, deadCooldown: TimeSpan.FromSeconds(60));
        var proxy = new ProxyEntry { Host = "198.51.100.1", Port = 1080, Password = "secretPassword123" };

        sm.RecordFailure(proxy, "Connect failed with secretPassword123");
        Assert.Equal(ProxyHealthStatus.Degraded, proxy.HealthStatus);
        Assert.Equal(1, proxy.ConsecutiveFailures);
        Assert.DoesNotContain("secretPassword123", proxy.LastError);

        clock.Advance(TimeSpan.FromSeconds(5));
        sm.RecordFailure(proxy, "Timeout");
        Assert.Equal(ProxyHealthStatus.Degraded, proxy.HealthStatus);
        Assert.Equal(2, proxy.ConsecutiveFailures);

        clock.Advance(TimeSpan.FromSeconds(5));
        sm.RecordFailure(proxy, "Timeout");
        Assert.Equal(ProxyHealthStatus.Dead, proxy.HealthStatus);
        Assert.Equal(3, proxy.ConsecutiveFailures);

        // Within cooldown: ShouldProbe is false unless forced
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(sm.ShouldProbe(proxy));
        Assert.True(sm.ShouldProbe(proxy, force: true));

        // After cooldown: ShouldProbe becomes true and success restores Healthy
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(sm.ShouldProbe(proxy));

        sm.RecordSuccess(proxy, latencyMs: 42, supportsUdp: true);
        Assert.Equal(ProxyHealthStatus.Healthy, proxy.HealthStatus);
        Assert.Equal(0, proxy.ConsecutiveFailures);
        Assert.Equal(42, proxy.LatencyMs);
        Assert.True(proxy.SupportsUdp);
        Assert.Null(proxy.LastError);
    }

    [Fact]
    public void GetUsableProxiesOrdered_ExcludesDeadAndSortsByHealthAndLatency()
    {
        var pDead = new ProxyEntry { Host = "1.1.1.1", Port = 1080, HealthStatus = ProxyHealthStatus.Dead, LatencyMs = 5 };
        var pSlow = new ProxyEntry { Host = "2.2.2.2", Port = 1080, HealthStatus = ProxyHealthStatus.Degraded, LatencyMs = 200 };
        var pFast = new ProxyEntry { Host = "3.3.3.3", Port = 1080, HealthStatus = ProxyHealthStatus.Healthy, LatencyMs = 25 };
        var pMid = new ProxyEntry { Host = "4.4.4.4", Port = 1080, HealthStatus = ProxyHealthStatus.Healthy, LatencyMs = 80 };

        var ordered = ProxyHealthStateMachine.GetUsableProxiesOrdered([pDead, pSlow, pMid, pFast]);

        Assert.Equal(3, ordered.Count);
        Assert.Equal(pFast.Id, ordered[0].Id);
        Assert.Equal(pMid.Id, ordered[1].Id);
        Assert.Equal(pSlow.Id, ordered[2].Id);
    }
}
