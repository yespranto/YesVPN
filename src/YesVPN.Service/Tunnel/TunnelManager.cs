using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using YesVPN.Core.Ipc;
using YesVPN.Service.Engine;
using YesVPN.Service.KillSwitch;
using YesVPN.Service.Logging;
using YesVPN.Service.Proxy;

namespace YesVPN.Service.Tunnel;

/// <summary>
/// Coordinates tunnel start/stop, kill-switch enforcement, auto-reconnect with backoff,
/// and network/power change recovery.
/// </summary>
public sealed class TunnelManager : IDisposable
{
    private readonly ISingBoxRunner _runner;
    private readonly IKillSwitchManager _killSwitch;
    private readonly LogRingBuffer _logs;
    private readonly TunnelSpeedSampler _speedSampler = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IpcRequest? _lastConnectRequest;
    private TunnelState _state = TunnelState.Disconnected;
    private DateTimeOffset? _connectedSinceUtc;
    private string? _lastError;
    private bool _userRequestedConnect;
    private CancellationTokenSource? _reconnectCts;
    private DateTimeOffset _lastRecoveryTriggerUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTunnelStartUtc = DateTimeOffset.MinValue;
    private bool _networkWasDown;
    private Socks5LoadBalancer? _loadBalancer;

    public TunnelManager(
        ISingBoxRunner runner,
        IKillSwitchManager killSwitch,
        LogRingBuffer logs)
    {
        _runner = runner;
        _killSwitch = killSwitch;
        _logs = logs;

        _runner.UnexpectedExit += OnRunnerUnexpectedExit;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch
        {
            // SystemEvents may not attach in headless test containers
        }
    }

    public async Task InitializeAndCleanupAsync(CancellationToken ct = default)
    {
        _logs.Append("Service", "Running startup cleanup of stale processes and kill-switch rules...");
        await _runner.CleanupOrphansAsync(ct).ConfigureAwait(false);
        await _killSwitch.CleanupStaleRulesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ServiceStatus> ConnectAsync(IpcRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
        {
            throw new ArgumentException("ConfigJson is required for Connect.", nameof(request));
        }

        CancelReconnectLoop();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _userRequestedConnect = true;
            _lastConnectRequest = request;
            _state = TunnelState.Connecting;
            _lastError = null;

            await StartTunnelInternalAsync(request, ct).ConfigureAwait(false);
            return GetStatusSnapshotUnsafe();
        }
        catch (Exception ex)
        {
            StopLoadBalancerUnsafe();
            _userRequestedConnect = false;
            _lastConnectRequest = null;
            _connectedSinceUtc = null;
            _state = TunnelState.Error;
            _lastError = ex.Message;
            _logs.Append("Service", $"Connect failed: {ex.Message}");
            if (!request.KillSwitch)
            {
                await _killSwitch.DisableAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceStatus> DisconnectAsync(CancellationToken ct = default)
    {
        CancelReconnectLoop();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _userRequestedConnect = false;
            _lastConnectRequest = null;
            _connectedSinceUtc = null;
            _lastError = null;

            await _runner.StopAsync(ct).ConfigureAwait(false);
            StopLoadBalancerUnsafe();
            _speedSampler.Stop();
            await _killSwitch.DisableAsync(ct).ConfigureAwait(false);
            _state = TunnelState.Disconnected;
            _logs.Append("Service", "Disconnected and normal networking restored.");
            return GetStatusSnapshotUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ServiceStatus GetStatus()
    {
        return GetStatusSnapshotUnsafe();
    }

    public IReadOnlyList<string> GetLogs(int tail) => _logs.GetTail(tail);

    private async Task StartTunnelInternalAsync(IpcRequest request, CancellationToken ct)
    {
        _lastTunnelStartUtc = DateTimeOffset.UtcNow;
        StopLoadBalancerUnsafe();
        _speedSampler.Stop();

        int? loadBalancerPort = null;
        if (string.Equals(request.Mode, "LoadBalance", StringComparison.OrdinalIgnoreCase) &&
            request.LoadBalanceProxies is { Count: > 0 } proxies)
        {
            _logs.RegisterSecrets(proxies.SelectMany(p => new[] { p.Password, p.Username }));
            var balancer = new Socks5LoadBalancer(proxies, _logs);
            balancer.Start();
            _loadBalancer = balancer;
            loadBalancerPort = balancer.Port;
        }

        var clashApiPort = FindFreeLoopbackPort();
        var effectiveConfigJson = PatchRuntimeEndpoints(request.ConfigJson!, loadBalancerPort, clashApiPort);

        if (request.KillSwitch)
        {
            var exePath = _runner.SingBoxPath ?? string.Empty;
            var hosts = request.ProxyHostsToAllow ?? new List<string>();
            await _killSwitch.EnableAsync(exePath, hosts, request.BlockNonDnsUdp, ct).ConfigureAwait(false);
        }
        else
        {
            await _killSwitch.DisableAsync(ct).ConfigureAwait(false);
        }

        await _runner.StartAsync(effectiveConfigJson, ct).ConfigureAwait(false);
        _speedSampler.Start(clashApiPort);
        _lastTunnelStartUtc = DateTimeOffset.UtcNow;
        _state = TunnelState.Connected;
        _connectedSinceUtc = DateTimeOffset.UtcNow;
        _lastError = null;
        _logs.Append("Service", $"Connected ({request.Mode ?? "Auto"}): {request.ActiveProxyLabel ?? "pool"}.");
    }

    private void StopLoadBalancerUnsafe()
    {
        var lb = Interlocked.Exchange(ref _loadBalancer, null);
        lb?.Dispose();
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string PatchRuntimeEndpoints(string configJson, int? loadBalancerPort, int clashApiPort)
    {
        var root = JsonNode.Parse(configJson)?.AsObject();
        if (root is null)
        {
            return configJson;
        }

        if (loadBalancerPort.HasValue && root["outbounds"] is JsonArray outbounds)
        {
            foreach (var node in outbounds)
            {
                if (node is JsonObject obj &&
                    string.Equals(obj["tag"]?.GetValue<string>(), "lb", StringComparison.Ordinal))
                {
                    obj["server_port"] = loadBalancerPort.Value;
                    break;
                }
            }
        }

        if (root["inbounds"] is not null)
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{clashApiPort}"
                }
            };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void OnRunnerUnexpectedExit(int exitCode)
    {
        _speedSampler.Stop();
        if (!_userRequestedConnect || _lastConnectRequest is null)
        {
            _state = TunnelState.Disconnected;
            return;
        }

        _state = TunnelState.Reconnecting;
        _connectedSinceUtc = null;
        _lastError = $"sing-box exited (code {exitCode}); attempting reconnect.";
        StartReconnectLoop();
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
        {
            _networkWasDown = true;
            _logs.Append("Network", "Network became unavailable.");
            return;
        }

        if (_networkWasDown)
        {
            _networkWasDown = false;
            TriggerConnectionRecovery("network availability restored");
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Ignore Wintun's own interface address notifications during/shortly after tunnel startup
        if ((DateTimeOffset.UtcNow - _lastTunnelStartUtc) < TimeSpan.FromSeconds(15))
        {
            return;
        }

        if (_userRequestedConnect && _state == TunnelState.Reconnecting && !_runner.IsRunning)
        {
            TriggerConnectionRecovery("network address changed");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            TriggerConnectionRecovery("system resumed from sleep");
        }
    }

    private void TriggerConnectionRecovery(string reason)
    {
        if (!_userRequestedConnect || _lastConnectRequest is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastTunnelStartUtc) < TimeSpan.FromSeconds(15) ||
            (now - _lastRecoveryTriggerUtc) < TimeSpan.FromSeconds(10))
        {
            return;
        }
        _lastRecoveryTriggerUtc = now;

        _logs.Append("Network", $"Detected {reason}; refreshing tunnel.");
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_userRequestedConnect && _lastConnectRequest is not null)
                {
                    _state = TunnelState.Reconnecting;
                    await StartTunnelInternalAsync(_lastConnectRequest, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _state = TunnelState.Reconnecting;
                StartReconnectLoop();
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void StartReconnectLoop()
    {
        CancelReconnectLoop();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        _ = Task.Run(async () =>
        {
            int[] backoffSeconds = [1, 2, 4, 8, 15, 30];
            var attempt = 0;

            while (!cts.Token.IsCancellationRequested && _userRequestedConnect)
            {
                var delaySec = backoffSeconds[Math.Min(attempt, backoffSeconds.Length - 1)];
                attempt++;
                _logs.Append("Service", $"Reconnect attempt #{attempt} in {delaySec}s...");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (!_userRequestedConnect || _lastConnectRequest is null)
                    {
                        break;
                    }

                    _state = TunnelState.Reconnecting;
                    await StartTunnelInternalAsync(_lastConnectRequest, cts.Token).ConfigureAwait(false);
                    _logs.Append("Service", $"Reconnected successfully on attempt #{attempt}.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _lastError = $"Reconnect attempt #{attempt} failed: {ex.Message}";
                    _logs.Append("Service", _lastError);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }, cts.Token);
    }

    private void CancelReconnectLoop()
    {
        var cts = Interlocked.Exchange(ref _reconnectCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private ServiceStatus GetStatusSnapshotUnsafe()
    {
        var speed = _state == TunnelState.Connected
            ? _speedSampler.Sample()
            : new SpeedSnapshot(0, 0, 0, 0);

        return new ServiceStatus
        {
            State = _state,
            ActiveProxyLabel = _lastConnectRequest?.ActiveProxyLabel,
            Mode = _lastConnectRequest?.Mode,
            KillSwitchActive = _killSwitch.IsActive,
            SingBoxPid = _runner.ProcessId,
            ConnectedSinceUtc = _connectedSinceUtc,
            LastError = _lastError,
            SingBoxVersion = _runner.SingBoxVersion,
            IsElevated = IsProcessElevated(),
            DownloadBytesPerSec = speed.DownloadBytesPerSec,
            UploadBytesPerSec = speed.UploadBytesPerSec,
            TotalDownloadBytes = speed.TotalDownloadBytes,
            TotalUploadBytes = speed.TotalUploadBytes
        };
    }

    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
    }

    public void Dispose()
    {
        CancelReconnectLoop();
        StopLoadBalancerUnsafe();
        _speedSampler.Dispose();
        _runner.UnexpectedExit -= OnRunnerUnexpectedExit;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        _gate.Dispose();
    }
}
