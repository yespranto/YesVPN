using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using YesVPN.Core.Health;
using YesVPN.Core.Models;

namespace YesVPN.Tests;

public sealed class ProxyHealthCheckerIntegrationTests
{
    [Fact]
    public async Task CheckSingleAsync_SucceedsWithAuthAndUdpDetectionAgainstLocalMockSocks5()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            // Expect 2 connections: 1 for CONNECT + HTTP, 1 for UDP ASSOCIATE
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();

                // Greeting
                var header = new byte[2];
                await stream.ReadExactlyAsync(header, cts.Token);
                var methods = new byte[header[1]];
                await stream.ReadExactlyAsync(methods, cts.Token);
                await stream.WriteAsync(new byte[] { 0x05, 0x02 }, cts.Token);

                // RFC 1929 Auth
                var authVerLen = new byte[2];
                await stream.ReadExactlyAsync(authVerLen, cts.Token);
                var uBytes = new byte[authVerLen[1]];
                await stream.ReadExactlyAsync(uBytes, cts.Token);
                var pLenBuf = new byte[1];
                await stream.ReadExactlyAsync(pLenBuf, cts.Token);
                var pBytes = new byte[pLenBuf[0]];
                await stream.ReadExactlyAsync(pBytes, cts.Token);
                var ok = Encoding.UTF8.GetString(uBytes) == "demo" && Encoding.UTF8.GetString(pBytes) == "pass";
                await stream.WriteAsync(new byte[] { 0x01, (byte)(ok ? 0x00 : 0x01) }, cts.Token);

                // Request
                var reqHead = new byte[4];
                await stream.ReadExactlyAsync(reqHead, cts.Token);
                if (reqHead[3] == 0x03)
                {
                    var dLen = new byte[1];
                    await stream.ReadExactlyAsync(dLen, cts.Token);
                    var rest = new byte[dLen[0] + 2];
                    await stream.ReadExactlyAsync(rest, cts.Token);
                }
                else
                {
                    var rest = new byte[6];
                    await stream.ReadExactlyAsync(rest, cts.Token);
                }

                // Reply success (0.0.0.0:0)
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cts.Token);

                if (reqHead[1] == 0x01)
                {
                    var httpReq = new byte[512];
                    _ = await stream.ReadAsync(httpReq, cts.Token);
                    var resp = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(resp, cts.Token);
                }
            }
        }, cts.Token);

        var checker = new ProxyHealthChecker(timeout: TimeSpan.FromSeconds(5));
        var entry = new ProxyEntry
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "demo",
            Password = "pass"
        };

        var outcome = await checker.CheckSingleAsync(entry, cts.Token);
        await serverTask;

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(outcome.SupportsUdp);
        Assert.Equal(ProxyHealthStatus.Healthy, entry.HealthStatus);
        Assert.NotNull(entry.LatencyMs);
    }
}
