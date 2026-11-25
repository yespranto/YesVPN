using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;
using YesVPN.Core.Config;
using YesVPN.Core.Models;

namespace YesVPN.Tests;

public sealed class SingBoxConfigBuilderTests
{
    private static List<ProxyEntry> CreateSampleProxies() =>
    [
        new ProxyEntry { Host = "198.51.100.1", Port = 1080, Username = "u1", Password = "p1", Label = "US1" },
        new ProxyEntry { Host = "198.51.100.2", Port = 1080, Username = "u2", Password = "p2", Label = "EU1" }
    ];

    [Fact]
    public void BuildJson_ExcludesDeadAndDisabledProxies_ThrowsWhenNoneUsable()
    {
        var proxies = CreateSampleProxies();
        proxies[0].HealthStatus = ProxyHealthStatus.Dead;
        proxies[1].IsEnabled = false;

        Assert.Throws<NoHealthyProxiesException>(() => SingBoxConfigBuilder.BuildJson(proxies));
    }

    [Fact]
    public void BuildJson_SingleProxyAutoMode_ProducesValidConfig()
    {
        var proxies = new[] { CreateSampleProxies()[0] };
        var json = SingBoxConfigBuilder.BuildJson(proxies, new TunnelOptions { Mode = ProxyMode.Auto });

        using var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds").EnumerateArray().ToList();
        Assert.Contains(outbounds, o => o.GetProperty("type").GetString() == "socks" && o.GetProperty("tag").GetString() == "p1");
        Assert.Contains(outbounds, o => o.GetProperty("type").GetString() == "urltest" && o.GetProperty("tag").GetString() == "auto");

        AssertSingBoxCheckPasses(json);
    }

    [Fact]
    public void BuildJson_ManualModeWithSplitTunnelAndUdpBlock_ValidatesWithSingBox()
    {
        var proxies = CreateSampleProxies();
        var options = new TunnelOptions
        {
            Mode = ProxyMode.Manual,
            ManualProxyId = proxies[1].Id,
            BlockNonDnsUdp = true,
            SplitTunnel = new SplitTunnelOptions
            {
                BypassDomains = ["*.local", "intranet.corp"],
                BypassIps = ["192.168.1.5", "203.0.113.0/24"],
                BypassApps = ["slack.exe"]
            }
        };

        var json = SingBoxConfigBuilder.BuildJson(proxies, options);
        using var doc = JsonDocument.Parse(json);

        var selector = doc.RootElement.GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("type").GetString() == "selector");
        Assert.Equal("p2", selector.GetProperty("default").GetString());

        AssertSingBoxCheckPasses(json);
    }

    [Fact]
    public void BuildJson_AutoModeMultipleProxies_ValidatesWithSingBox()
    {
        var json = SingBoxConfigBuilder.BuildJson(CreateSampleProxies(), new TunnelOptions { Mode = ProxyMode.Auto });
        AssertSingBoxCheckPasses(json);
    }

    [Fact]
    public void BuildJson_LoadBalanceMode_RoutesThroughLocalBalancerAndValidatesWithSingBox()
    {
        var json = SingBoxConfigBuilder.BuildJson(
            CreateSampleProxies(),
            new TunnelOptions { Mode = ProxyMode.LoadBalance },
            loadBalancerPort: 19455,
            clashApiPort: 19090);

        using var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds").EnumerateArray().ToList();
        var lb = outbounds.First(o => o.GetProperty("tag").GetString() == "lb");
        Assert.Equal("127.0.0.1", lb.GetProperty("server").GetString());
        Assert.Equal(19455, lb.GetProperty("server_port").GetInt32());
        Assert.Equal("127.0.0.1:19090", doc.RootElement.GetProperty("experimental").GetProperty("clash_api").GetProperty("external_controller").GetString());
        Assert.Equal("lb", doc.RootElement.GetProperty("route").GetProperty("final").GetString());
        Assert.False(doc.RootElement.GetProperty("inbounds")[0].GetProperty("strict_route").GetBoolean());
        Assert.Equal("YesVPN-TUN", doc.RootElement.GetProperty("inbounds")[0].GetProperty("interface_name").GetString());
        Assert.Equal("ipv4_only", doc.RootElement.GetProperty("dns").GetProperty("strategy").GetString());

        AssertSingBoxCheckPasses(json);
    }

    [Fact]
    public void BuildJson_TcpOnlyProxies_AutoRejectsNonDnsUdpAndValidates()
    {
        var proxies = CreateSampleProxies();
        foreach (var p in proxies) p.SupportsUdp = false;

        var json = SingBoxConfigBuilder.BuildJson(proxies, new TunnelOptions { Mode = ProxyMode.Auto, BlockNonDnsUdp = false });
        using var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToList();
        Assert.Contains(rules, r =>
            r.TryGetProperty("network", out var net) && net.GetString() == "udp" &&
            r.TryGetProperty("action", out var act) && act.GetString() == "reject");

        AssertSingBoxCheckPasses(json);
    }

    private static void AssertSingBoxCheckPasses(string json)
    {
        var singBoxPath = FindSingBoxExecutable();
        if (singBoxPath is null)
        {
            throw new FileNotFoundException("third_party/sing-box/sing-box.exe was not found for validation.");
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"yesvpn-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, json, new UTF8Encoding(false));
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            Assert.True(proc.ExitCode == 0, $"sing-box check failed ({proc.ExitCode}): {stderr}");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static string? FindSingBoxExecutable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "sing-box", "sing-box.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
