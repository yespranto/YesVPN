using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using YesVPN.Core.Config;
using YesVPN.Core.Ipc;
using YesVPN.Service.Logging;

namespace YesVPN.Service.Proxy;

/// <summary>
/// High-throughput local SOCKS5 TCP load balancer that distributes connections round-robin
/// across all active upstream SOCKS5 proxies with automatic per-connection failover and
/// direct physical-NIC socket binding to bypass the TUN adapter.
/// </summary>
public sealed class Socks5LoadBalancer : IAsyncDisposable, IDisposable
{
    private const int IpUnicastIfOption = 31; // WS2IPDEF IP_UNICAST_IF

    private readonly IReadOnlyList<ProxyEndpointSpec> _upstreams;
    private readonly LogRingBuffer _logs;
    private readonly TimeSpan _upstreamTimeout;
    private readonly long[] _dispatchCounts;
    private readonly int[] _consecutiveFailures;
    private readonly long[] _cooldownUntilTicks;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _nextIndex = -1;
    private long _totalConnections;

    public int Port { get; private set; }
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public IReadOnlyList<long> DispatchCounts => _dispatchCounts;

    public Socks5LoadBalancer(
        IReadOnlyList<ProxyEndpointSpec> upstreams,
        LogRingBuffer logs,
        TimeSpan? upstreamTimeout = null)
    {
        if (upstreams is null || upstreams.Count == 0)
        {
            throw new ArgumentException("At least one upstream proxy is required.", nameof(upstreams));
        }

        _upstreams = upstreams;
        _logs = logs;
        _upstreamTimeout = upstreamTimeout ?? TimeSpan.FromSeconds(4);
        _dispatchCounts = new long[upstreams.Count];
        _consecutiveFailures = new int[upstreams.Count];
        _cooldownUntilTicks = new long[upstreams.Count];
    }

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 256);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logs.Append("LoadBalance", $"Started local SOCKS5 balancer on 127.0.0.1:{Port} across {_upstreams.Count} proxies.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                var accepted = client;
                client = null;
                _ = Task.Run(() => HandleClientAsync(accepted, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                _logs.Append("LoadBalance", $"Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            await using var clientStream = client.GetStream();

            // 1. Local SOCKS5 greeting (no auth on loopback)
            var greet = new byte[2];
            await ReadExactAsync(clientStream, greet, ct).ConfigureAwait(false);
            if (greet[0] != 0x05)
            {
                return;
            }

            var methods = new byte[greet[1]];
            await ReadExactAsync(clientStream, methods, ct).ConfigureAwait(false);
            await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, ct).ConfigureAwait(false);

            // 2. Read SOCKS5 request header (VER, CMD, RSV, ATYP) + target address + port
            var reqHead = new byte[4];
            await ReadExactAsync(clientStream, reqHead, ct).ConfigureAwait(false);
            if (reqHead[0] != 0x05 || reqHead[1] != 0x01)
            {
                await SendLocalReplyAsync(clientStream, 0x07, ct).ConfigureAwait(false);
                return;
            }

            var addrAndPort = await ReadAddressAndPortBytesAsync(clientStream, reqHead[3], ct).ConfigureAwait(false);
            var fullConnectPacket = new byte[4 + addrAndPort.Length];
            Buffer.BlockCopy(reqHead, 0, fullConnectPacket, 0, 4);
            Buffer.BlockCopy(addrAndPort, 0, fullConnectPacket, 4, addrAndPort.Length);

            // 3. Round-robin upstream selection, preferring non-cooled-down proxies first
            var startIdx = Math.Abs(Interlocked.Increment(ref _nextIndex)) % _upstreams.Count;
            var nowTicks = Environment.TickCount64;
            var orderedIndices = new List<int>(_upstreams.Count);
            var cooledDownIndices = new List<int>();

            for (var i = 0; i < _upstreams.Count; i++)
            {
                var idx = (startIdx + i) % _upstreams.Count;
                if (Interlocked.Read(ref _cooldownUntilTicks[idx]) > nowTicks)
                {
                    cooledDownIndices.Add(idx);
                }
                else
                {
                    orderedIndices.Add(idx);
                }
            }
            orderedIndices.AddRange(cooledDownIndices);

            TcpClient? upstreamClient = null;
            byte[]? upstreamReply = null;
            string? lastErr = null;

            foreach (var idx in orderedIndices)
            {
                var proxy = _upstreams[idx];
                try
                {
                    var (connected, replyBytes) = await ConnectUpstreamAsync(proxy, fullConnectPacket, ct).ConfigureAwait(false);
                    upstreamClient = connected;
                    upstreamReply = replyBytes;
                    Interlocked.Exchange(ref _consecutiveFailures[idx], 0);
                    Interlocked.Exchange(ref _cooldownUntilTicks[idx], 0);
                    Interlocked.Increment(ref _dispatchCounts[idx]);
                    Interlocked.Increment(ref _totalConnections);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lastErr = ex.Message;
                    var fails = Interlocked.Increment(ref _consecutiveFailures[idx]);
                    if (fails >= 2)
                    {
                        Interlocked.Exchange(ref _cooldownUntilTicks[idx], Environment.TickCount64 + 15_000);
                    }
                }
            }

            if (upstreamClient is null || upstreamReply is null)
            {
                if (!string.IsNullOrEmpty(lastErr))
                {
                    _logs.Append("LoadBalance", $"All {_upstreams.Count} upstreams failed for connection: {lastErr}");
                }
                await SendLocalReplyAsync(clientStream, 0x05, ct).ConfigureAwait(false);
                return;
            }

            using (upstreamClient)
            {
                await using var upstreamStream = upstreamClient.GetStream();
                await clientStream.WriteAsync(upstreamReply, ct).ConfigureAwait(false);

                // 4. Full-duplex TCP relay preserving half-close (FIN) in each direction
                var upTask = RelayOneDirectionAsync(clientStream, upstreamStream, upstreamClient.Client, ct);
                var downTask = RelayOneDirectionAsync(upstreamStream, clientStream, client.Client, ct);
                await Task.WhenAll(upTask, downTask).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or cancelled
        }
        catch (IOException)
        {
            // Normal socket close
        }
        catch (Exception ex)
        {
            _logs.Append("LoadBalance", $"Relay error: {ex.Message}");
        }
    }

    private static async Task RelayOneDirectionAsync(
        Stream source,
        Stream destination,
        Socket destinationSocket,
        CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    try { destinationSocket.Shutdown(SocketShutdown.Send); } catch { }
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            try { destinationSocket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private async Task<(TcpClient Client, byte[] ReplyPacket)> ConnectUpstreamAsync(
        ProxyEndpointSpec proxy,
        byte[] connectPacket,
        CancellationToken parentCt)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        timeoutCts.CancelAfter(_upstreamTimeout);
        var ct = timeoutCts.Token;

        var isV6 = IPAddress.TryParse(proxy.Host, out var hostIp) && hostIp.AddressFamily == AddressFamily.InterNetworkV6;
        var upstream = new TcpClient(isV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            // Bind directly to the active physical NIC for non-loopback IPv4 proxies so upstream dialing
            // bypasses YesVPN-TUN completely and avoids double-encapsulation or route loops.
            if (!isV6 && !IsLoopbackHost(proxy.Host))
            {
                TryBindToPhysicalInterface(upstream.Client);
            }

            await upstream.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            var stream = upstream.GetStream();

            var hasCreds = !string.IsNullOrEmpty(proxy.Username);
            byte[] greeting = hasCreds ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
            await stream.WriteAsync(greeting, ct).ConfigureAwait(false);

            var methodReply = new byte[2];
            await ReadExactAsync(stream, methodReply, ct).ConfigureAwait(false);
            if (methodReply[0] != 0x05 || methodReply[1] == 0xFF)
            {
                throw new UnauthorizedAccessException("Upstream proxy rejected SOCKS5 greeting.");
            }

            if (methodReply[1] == 0x02)
            {
                var uBytes = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
                var pBytes = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
                var auth = new byte[3 + uBytes.Length + pBytes.Length];
                auth[0] = 0x01;
                auth[1] = (byte)uBytes.Length;
                Buffer.BlockCopy(uBytes, 0, auth, 2, uBytes.Length);
                auth[2 + uBytes.Length] = (byte)pBytes.Length;
                Buffer.BlockCopy(pBytes, 0, auth, 3 + uBytes.Length, pBytes.Length);
                await stream.WriteAsync(auth, ct).ConfigureAwait(false);

                var authReply = new byte[2];
                await ReadExactAsync(stream, authReply, ct).ConfigureAwait(false);
                if (authReply[1] != 0x00)
                {
                    throw new UnauthorizedAccessException("Upstream proxy authentication failed.");
                }
            }

            await stream.WriteAsync(connectPacket, ct).ConfigureAwait(false);

            var replyHead = new byte[4];
            await ReadExactAsync(stream, replyHead, ct).ConfigureAwait(false);
            if (replyHead[0] != 0x05)
            {
                throw new InvalidDataException("Invalid SOCKS5 reply version from upstream.");
            }

            var addrAndPort = await ReadAddressAndPortBytesAsync(stream, replyHead[3], ct).ConfigureAwait(false);
            if (replyHead[1] != 0x00)
            {
                throw new InvalidOperationException($"Upstream SOCKS5 rejected target (REP=0x{replyHead[1]:X2}).");
            }

            var fullReply = new byte[4 + addrAndPort.Length];
            Buffer.BlockCopy(replyHead, 0, fullReply, 0, 4);
            Buffer.BlockCopy(addrAndPort, 0, fullReply, 4, addrAndPort.Length);
            return (upstream, fullReply);
        }
        catch
        {
            upstream.Dispose();
            throw;
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static void TryBindToPhysicalInterface(Socket socket)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                if (string.Equals(nic.Name, SingBoxConfigBuilder.TunInterfaceName, StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("YesVPN", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (ipv4 is null)
                {
                    continue;
                }

                var index = props.GetIPv4Properties().Index;
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIfOption, IPAddress.HostToNetworkOrder(index));
                socket.Bind(new IPEndPoint(ipv4, 0));
                return;
            }
        }
        catch
        {
            // Fall back to default OS routing if interface enumeration fails
        }
    }

    private static async Task<byte[]> ReadAddressAndPortBytesAsync(Stream stream, byte atyp, CancellationToken ct)
    {
        switch (atyp)
        {
            case 0x01:
            {
                var buf = new byte[6];
                await ReadExactAsync(stream, buf, ct).ConfigureAwait(false);
                return buf;
            }
            case 0x04:
            {
                var buf = new byte[18];
                await ReadExactAsync(stream, buf, ct).ConfigureAwait(false);
                return buf;
            }
            case 0x03:
            {
                var lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
                var buf = new byte[1 + lenBuf[0] + 2];
                buf[0] = lenBuf[0];
                await ReadExactAsync(stream, buf.AsMemory(1, lenBuf[0] + 2), ct).ConfigureAwait(false);
                return buf;
            }
            default:
                throw new InvalidDataException($"Unsupported SOCKS5 ATYP 0x{atyp:X2}.");
        }
    }

    private static Task SendLocalReplyAsync(Stream stream, byte rep, CancellationToken ct) =>
        stream.WriteAsync(new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Unexpected EOF during SOCKS5 handshake.");
            }
            offset += n;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _listener = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        _listener = null;
    }
}
