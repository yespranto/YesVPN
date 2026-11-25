using Xunit;
using YesVPN.Core.Ipc;
using YesVPN.Service.Engine;
using YesVPN.Service.Ipc;
using YesVPN.Service.KillSwitch;
using YesVPN.Service.Logging;
using YesVPN.Service.Tunnel;

namespace YesVPN.Tests;

public sealed class ServiceIpcAndCleanupTests
{
    private sealed class FakeSingBoxRunner : ISingBoxRunner
    {
        public int? ProcessId { get; private set; }
        public bool IsRunning => ProcessId.HasValue;
        public string? SingBoxPath => @"C:\fake\sing-box.exe";
        public string? SingBoxVersion => "v1.14.4";
        public bool OrphanCleanupCalled { get; private set; }

        public event Action<int>? UnexpectedExit;

        public bool ThrowOnStart { get; set; }

        public Task StartAsync(string configJson, CancellationToken ct = default)
        {
            if (ThrowOnStart)
            {
                throw new UnauthorizedAccessException("Administrator privileges are required.");
            }
            ProcessId = 4242;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            ProcessId = null;
            return Task.CompletedTask;
        }

        public Task CleanupOrphansAsync(CancellationToken ct = default)
        {
            OrphanCleanupCalled = true;
            ProcessId = null;
            return Task.CompletedTask;
        }

        public void SimulateCrash(int exitCode = 1)
        {
            ProcessId = null;
            UnexpectedExit?.Invoke(exitCode);
        }

        public void Dispose() { }
    }

    private sealed class FakeKillSwitchManager : IKillSwitchManager
    {
        public bool IsActive { get; private set; }
        public bool StaleCleaned { get; private set; }

        public Task EnableAsync(string singBoxExePath, IReadOnlyList<string> proxyHostsToAllow, bool blockNonDnsUdp, CancellationToken ct = default)
        {
            IsActive = true;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken ct = default)
        {
            IsActive = false;
            return Task.CompletedTask;
        }

        public Task CleanupStaleRulesAsync(CancellationToken ct = default)
        {
            StaleCleaned = true;
            IsActive = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    [Fact]
    public void LogRingBuffer_BoundsCapacityAndMasksCredentials()
    {
        var ring = new LogRingBuffer(capacity: 10);
        ring.RegisterSecrets(["VerySecretPwd"]);

        for (var i = 0; i < 15; i++)
        {
            ring.Append("Test", $"Line {i} socks5://u:VerySecretPwd@1.2.3.4:1080");
        }

        var tail = ring.GetTail(20);
        Assert.Equal(10, tail.Count);
        Assert.All(tail, line => Assert.DoesNotContain("VerySecretPwd", line));
        Assert.Contains("Line 14", tail[^1]);
    }

    [Fact]
    public async Task IpcPipeServer_ConnectGetStatusLogsAndDisconnect_RoundTrips()
    {
        var logs = new LogRingBuffer(100);
        var runner = new FakeSingBoxRunner();
        var killSwitch = new FakeKillSwitchManager();
        using var tunnel = new TunnelManager(runner, killSwitch, logs);
        await tunnel.InitializeAndCleanupAsync();

        Assert.True(runner.OrphanCleanupCalled);
        Assert.True(killSwitch.StaleCleaned);

        var pipeName = $"YesVPN.Test.{Guid.NewGuid():N}";
        var server = new IpcPipeServer(tunnel, logs, lifetime: null, pipeName: pipeName);
        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));

        var client = new IpcClient(pipeName, TimeSpan.FromSeconds(5));

        // Wait briefly for server listener
        IpcResponse? ping = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                ping = await client.PingAsync();
                if (ping.Ok) break;
            }
            catch
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(ping);
        Assert.True(ping!.Ok);
        Assert.Equal(TunnelState.Disconnected, ping.Status!.State);

        var connectResp = await client.ConnectAsync("{\"mock\":true}", "US-1", "Auto", killSwitch: true, blockNonDnsUdp: false);
        Assert.True(connectResp.Ok);
        Assert.Equal(TunnelState.Connected, connectResp.Status!.State);
        Assert.Equal(4242, connectResp.Status.SingBoxPid);
        Assert.True(killSwitch.IsActive);

        var logsResp = await client.GetLogsAsync(50);
        Assert.True(logsResp.Ok);
        Assert.NotEmpty(logsResp.Logs!);

        var disconnectResp = await client.DisconnectAsync();
        Assert.True(disconnectResp.Ok);
        Assert.Equal(TunnelState.Disconnected, disconnectResp.Status!.State);
        Assert.False(killSwitch.IsActive);

        serverCts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task TunnelManager_InitialConnectFailure_DoesNotEnterReconnecting()
    {
        var logs = new LogRingBuffer(50);
        var runner = new FakeSingBoxRunner { ThrowOnStart = true };
        var killSwitch = new FakeKillSwitchManager();
        using var tunnel = new TunnelManager(runner, killSwitch, logs);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tunnel.ConnectAsync(new IpcRequest
            {
                Command = IpcCommand.Connect,
                ConfigJson = "{}",
                KillSwitch = false
            }));

        var status = tunnel.GetStatus();
        Assert.Equal(TunnelState.Error, status.State);
        Assert.Contains("Administrator", status.LastError);
    }

    [Fact]
    public void WfpDataTypeConstants_MatchWindowsSdkFwptypesHeader()
    {
        Assert.Equal(1u, WfpKillSwitchManager.FwpUint8);
        Assert.Equal(2u, WfpKillSwitchManager.FwpUint16);
        Assert.Equal(4u, WfpKillSwitchManager.FwpUint64);
        Assert.Equal(12u, WfpKillSwitchManager.FwpByteBlobType);
        Assert.Equal(256u, WfpKillSwitchManager.FwpV4AddrMask);
        Assert.Equal(257u, WfpKillSwitchManager.FwpV6AddrMask);
    }

    [Fact]
    public void TunnelSpeedSampler_ParsesAsymmetricUpAndDownTrafficLines()
    {
        Assert.True(TunnelSpeedSampler.TryParseTrafficLine("{\"up\":12400,\"down\":2480512}", out var up, out var down));
        Assert.Equal(12400, up);
        Assert.Equal(2480512, down);
        Assert.NotEqual(up, down);
        Assert.False(TunnelSpeedSampler.TryParseTrafficLine("not-json", out _, out _));
    }
}
