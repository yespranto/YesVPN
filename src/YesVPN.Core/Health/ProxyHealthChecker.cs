using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using YesVPN.Core.Models;

namespace YesVPN.Core.Health;

public sealed record ProxyProbeOutcome(
    bool Success,
    int LatencyMs,
    bool SupportsUdp,
    string? Error);

/// <summary>
/// Measures latency and availability for SOCKS5 proxies (TCP connect + SOCKS5 auth + HTTP test + UDP ASSOCIATE).
/// </summary>
public sealed class ProxyHealthChecker
{
    private readonly ProxyHealthStateMachine _stateMachine;
    private readonly TimeSpan _timeout;
    private readonly string _testHost;
    private readonly int _testPort;
    private readonly string _testPath;

    public ProxyHealthChecker(
        ProxyHealthStateMachine? stateMachine = null,
        TimeSpan? timeout = null,
        string testHost = "connectivitycheck.gstatic.com",
        int testPort = 80,
        string testPath = "/generate_204")
    {
        _stateMachine = stateMachine ?? new ProxyHealthStateMachine();
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _testHost = testHost;
        _testPort = testPort;
        _testPath = testPath;
    }

    public async Task<IReadOnlyList<ProxyEntry>> CheckAllAsync(
        IReadOnlyList<ProxyEntry> proxies,
        bool force = false,
        int maxConcurrency = 8,
        CancellationToken cancellationToken = default,
        bool checkUdp = true,
        TimeSpan? timeoutOverride = null,
        Action<ProxyEntry>? onProxyChecked = null)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = proxies.Select(async proxy =>
        {
            if (!_stateMachine.ShouldProbe(proxy, force))
            {
                return proxy;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckSingleAsync(proxy, cancellationToken, checkUdp, timeoutOverride).ConfigureAwait(false);
                onProxyChecked?.Invoke(proxy);
                return proxy;
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<ProxyProbeOutcome> CheckSingleAsync(
        ProxyEntry proxy,
        CancellationToken cancellationToken = default,
        bool checkUdp = true,
        TimeSpan? timeoutOverride = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutOverride ?? _timeout);
        var ct = timeoutCts.Token;

        var sw = Stopwatch.StartNew();
        try
        {
            await ProbeConnectAndHttpAsync(proxy, ct).ConfigureAwait(false);
            sw.Stop();

            var latencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds);
            var supportsUdp = checkUdp
                ? await ProbeUdpAssociateAsync(proxy, ct).ConfigureAwait(false)
                : (proxy.SupportsUdp ?? false);

            _stateMachine.RecordSuccess(proxy, latencyMs, supportsUdp);
            return new ProxyProbeOutcome(true, latencyMs, supportsUdp, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            const string msg = "Timed out waiting for proxy response.";
            _stateMachine.RecordFailure(proxy, msg);
            return new ProxyProbeOutcome(false, 0, false, msg);
        }
        catch (Exception ex)
        {
            _stateMachine.RecordFailure(proxy, ex.Message);
            return new ProxyProbeOutcome(false, 0, false, proxy.LastError);
        }
    }

    private async Task ProbeConnectAndHttpAsync(ProxyEntry proxy, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
        await using var stream = tcp.GetStream();

        await PerformSocks5HandshakeAsync(stream, proxy, ct).ConfigureAwait(false);
        await SendSocks5ConnectAsync(stream, _testHost, _testPort, ct).ConfigureAwait(false);

        var httpRequest = $"GET {_testPath} HTTP/1.1\r\nHost: {_testHost}\r\nConnection: close\r\n\r\n";
        var reqBytes = Encoding.ASCII.GetBytes(httpRequest);
        await stream.WriteAsync(reqBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read <= 0)
        {
            throw new IOException("Proxy closed connection before returning HTTP response.");
        }

        var responseHead = Encoding.ASCII.GetString(buffer, 0, read);
        if (!responseHead.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected non-HTTP response through proxy.");
        }

        var firstLineEnd = responseHead.IndexOf('\n');
        var statusLine = firstLineEnd > 0 ? responseHead[..firstLineEnd] : responseHead;
        if (!statusLine.Contains(" 204 ", StringComparison.Ordinal) &&
            !statusLine.Contains(" 200 ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Probe HTTP status failed: {statusLine.Trim()}");
        }
    }

    private static async Task<bool> ProbeUdpAssociateAsync(ProxyEntry proxy, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            await using var stream = tcp.GetStream();

            await PerformSocks5HandshakeAsync(stream, proxy, ct).ConfigureAwait(false);

            // CMD = 0x03 (UDP ASSOCIATE), ATYP = 0x01 (IPv4 0.0.0.0:0)
            byte[] udpReq = [0x05, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
            await stream.WriteAsync(udpReq, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var reply = await ReadSocks5ReplyHeaderAsync(stream, ct).ConfigureAwait(false);
            return reply == 0x00;
        }
        catch
        {
            return false;
        }
    }

    private static async Task PerformSocks5HandshakeAsync(NetworkStream stream, ProxyEntry proxy, CancellationToken ct)
    {
        var hasCreds = !string.IsNullOrEmpty(proxy.Username);
        byte[] greeting = hasCreds
            ? [0x05, 0x02, 0x00, 0x02]
            : [0x05, 0x01, 0x00];

        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var methodReply = new byte[2];
        await ReadExactAsync(stream, methodReply, ct).ConfigureAwait(false);

        if (methodReply[0] != 0x05)
        {
            throw new InvalidOperationException($"Invalid SOCKS version byte: 0x{methodReply[0]:X2}.");
        }

        if (methodReply[1] == 0xFF)
        {
            throw new UnauthorizedAccessException("SOCKS5 proxy rejected authentication methods.");
        }

        if (methodReply[1] == 0x02)
        {
            var userBytes = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
            var passBytes = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
            if (userBytes.Length > 255 || passBytes.Length > 255)
            {
                throw new InvalidOperationException("SOCKS5 username or password exceeds 255 bytes.");
            }

            var authPacket = new byte[3 + userBytes.Length + passBytes.Length];
            authPacket[0] = 0x01; // RFC 1929 subnegotiation version
            authPacket[1] = (byte)userBytes.Length;
            Buffer.BlockCopy(userBytes, 0, authPacket, 2, userBytes.Length);
            authPacket[2 + userBytes.Length] = (byte)passBytes.Length;
            Buffer.BlockCopy(passBytes, 0, authPacket, 3 + userBytes.Length, passBytes.Length);

            await stream.WriteAsync(authPacket, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var authReply = new byte[2];
            await ReadExactAsync(stream, authReply, ct).ConfigureAwait(false);
            if (authReply[1] != 0x00)
            {
                throw new UnauthorizedAccessException("SOCKS5 username/password authentication failed.");
            }
        }
        else if (methodReply[1] != 0x00)
        {
            throw new InvalidOperationException($"Unsupported SOCKS5 auth method: 0x{methodReply[1]:X2}.");
        }
    }

    private static async Task SendSocks5ConnectAsync(NetworkStream stream, string targetHost, int targetPort, CancellationToken ct)
    {
        var hostBytes = Encoding.ASCII.GetBytes(targetHost);
        if (hostBytes.Length > 255)
        {
            throw new ArgumentException("Target host exceeds 255 bytes.", nameof(targetHost));
        }

        var packet = new byte[7 + hostBytes.Length];
        packet[0] = 0x05; // VER
        packet[1] = 0x01; // CMD = CONNECT
        packet[2] = 0x00; // RSV
        packet[3] = 0x03; // ATYP = DOMAINNAME
        packet[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, packet, 5, hostBytes.Length);
        packet[5 + hostBytes.Length] = (byte)(targetPort >> 8);
        packet[6 + hostBytes.Length] = (byte)(targetPort & 0xFF);

        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var rep = await ReadSocks5ReplyHeaderAsync(stream, ct).ConfigureAwait(false);
        if (rep != 0x00)
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
    }

    private static async Task<byte> ReadSocks5ReplyHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        if (header[0] != 0x05)
        {
            throw new InvalidOperationException($"Invalid SOCKS5 reply version: 0x{header[0]:X2}.");
        }

        var remaining = header[3] switch
        {
            0x01 => 4 + 2, // IPv4 + port
            0x04 => 16 + 2, // IPv6 + port
            0x03 => await ReadDomainLengthAsync(stream, ct).ConfigureAwait(false) + 2,
            _ => throw new InvalidOperationException($"Unknown SOCKS5 ATYP: 0x{header[3]:X2}.")
        };

        var discard = new byte[remaining];
        await ReadExactAsync(stream, discard, ct).ConfigureAwait(false);
        return header[1];
    }

    private static async Task<int> ReadDomainLengthAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[1];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        return lenBuf[0];
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Unexpected EOF from SOCKS5 proxy.");
            }
            offset += n;
        }
    }
}
