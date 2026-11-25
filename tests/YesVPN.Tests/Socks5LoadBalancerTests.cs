using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using YesVPN.Core.Ipc;
using YesVPN.Service.Logging;
using YesVPN.Service.Proxy;

namespace YesVPN.Tests;

public sealed class Socks5LoadBalancerTests
{
    [Fact]
    public async Task LoadBalancer_DistributesRoundRobinAcrossActiveProxies_AndFailsOver()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Target TCP echo server
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        var echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;
        var echoTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var c = await echoListener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        await using (var s = c.GetStream())
                        {
                            var buf = new byte[256];
                            var n = await s.ReadAsync(buf, cts.Token);
                            if (n > 0) await s.WriteAsync(buf.AsMemory(0, n), cts.Token);
                        }
                    }, cts.Token);
                }
                catch { break; }
            }
        }, cts.Token);

        // Two mock SOCKS5 upstreams that forward to echoPort
        var upstream1 = StartMockSocks5Upstream(echoPort, cts.Token);
        using var _1 = upstream1.Listener;
        var upstream2 = StartMockSocks5Upstream(echoPort, cts.Token);
        using var _2 = upstream2.Listener;

        var logs = new LogRingBuffer(100);
        await using var lb = new Socks5LoadBalancer(
            [
                new ProxyEndpointSpec { Host = "127.0.0.1", Port = upstream1.Port, Username = "u1", Password = "p1" },
                new ProxyEndpointSpec { Host = "127.0.0.1", Port = upstream2.Port, Username = "u2", Password = "p2" }
            ],
            logs);
        lb.Start();

        // 4 sequential requests -> 2 to upstream1, 2 to upstream2
        for (var i = 0; i < 4; i++)
        {
            var echoed = await SendViaSocks5Async(lb.Port, "127.0.0.1", echoPort, $"ping-{i}", cts.Token);
            Assert.Equal($"ping-{i}", echoed);
        }

        Assert.Equal(4, lb.TotalConnections);
        Assert.Equal(2, lb.DispatchCounts[0]);
        Assert.Equal(2, lb.DispatchCounts[1]);

        // Stop upstream1 -> next connection automatically fails over to upstream2
        upstream1.Listener.Stop();
        var failoverEcho = await SendViaSocks5Async(lb.Port, "127.0.0.1", echoPort, "failover-ok", cts.Token);
        Assert.Equal("failover-ok", failoverEcho);
        Assert.Equal(3, lb.DispatchCounts[1]);

        cts.Cancel();
        echoListener.Stop();
        await Task.WhenAll(echoTask, upstream1.Task, upstream2.Task);
    }

    private static async Task<string> SendViaSocks5Async(int socksPort, string targetHost, int targetPort, string payload, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socksPort, ct);
        await using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var greetReply = new byte[2];
        await stream.ReadExactlyAsync(greetReply, ct);
        Assert.Equal(0x00, greetReply[1]);

        var hostBytes = Encoding.ASCII.GetBytes(targetHost);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;
        req[1] = 0x01;
        req[2] = 0x00;
        req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, req, 5, hostBytes.Length);
        req[5 + hostBytes.Length] = (byte)(targetPort >> 8);
        req[6 + hostBytes.Length] = (byte)(targetPort & 0xFF);
        await stream.WriteAsync(req, ct);

        var connReply = new byte[10];
        await stream.ReadExactlyAsync(connReply, ct);
        Assert.Equal(0x00, connReply[1]);

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(payloadBytes, ct);
        var respBuf = new byte[payloadBytes.Length];
        await stream.ReadExactlyAsync(respBuf, ct);
        return Encoding.UTF8.GetString(respBuf);
    }

    private static (TcpListener Listener, int Port, Task Task) StartMockSocks5Upstream(int echoPort, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var task = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        await using (var s = client.GetStream())
                        {
                            var head = new byte[2];
                            await s.ReadExactlyAsync(head, ct);
                            var m = new byte[head[1]];
                            await s.ReadExactlyAsync(m, ct);
                            await s.WriteAsync(new byte[] { 0x05, 0x02 }, ct);

                            // Auth
                            var aHead = new byte[2];
                            await s.ReadExactlyAsync(aHead, ct);
                            var u = new byte[aHead[1]];
                            await s.ReadExactlyAsync(u, ct);
                            var pLen = new byte[1];
                            await s.ReadExactlyAsync(pLen, ct);
                            var p = new byte[pLen[0]];
                            await s.ReadExactlyAsync(p, ct);
                            await s.WriteAsync(new byte[] { 0x01, 0x00 }, ct);

                            // Connect
                            var req = new byte[4];
                            await s.ReadExactlyAsync(req, ct);
                            if (req[3] == 0x03)
                            {
                                var dLen = new byte[1];
                                await s.ReadExactlyAsync(dLen, ct);
                                var rest = new byte[dLen[0] + 2];
                                await s.ReadExactlyAsync(rest, ct);
                            }

                            using var target = new TcpClient();
                            await target.ConnectAsync(IPAddress.Loopback, echoPort, ct);
                            await using var ts = target.GetStream();
                            await s.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);

                            var t1 = s.CopyToAsync(ts, ct);
                            var t2 = ts.CopyToAsync(s, ct);
                            await Task.WhenAny(t1, t2);
                        }
                    }, ct);
                }
                catch { break; }
            }
        }, ct);

        return (listener, port, task);
    }
}
