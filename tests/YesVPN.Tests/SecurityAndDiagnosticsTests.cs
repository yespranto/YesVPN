using Xunit;
using YesVPN.Core.Diagnostics;
using YesVPN.Core.Ipc;
using YesVPN.Core.Models;
using YesVPN.Core.Security;

namespace YesVPN.Tests;

public sealed class SecurityAndDiagnosticsTests
{
    [Fact]
    public void CredentialMasker_RedactsSocksUrisJsonAndSecrets()
    {
        const string raw = """
            Connecting socks5://alice:MyTopSecret@198.51.100.1:1080
            {"password": "AnotherSecret\"123"}
            198.51.100.2:1080:bob:ColonSecret
            """;

        var masked = CredentialMasker.Mask(raw, ["MyTopSecret", "AnotherSecret\"123"]);

        Assert.DoesNotContain("MyTopSecret", masked);
        Assert.DoesNotContain("AnotherSecret", masked);
        Assert.DoesNotContain("ColonSecret", masked);
        Assert.Contains("***", masked);
    }

    [Fact]
    public void DpapiSecretStore_RoundTripsPlaintext()
    {
        var secret = "{\"password\":\"p@ssw0rd!\"}";
        var protectedText = DpapiSecretStore.ProtectString(secret);
        Assert.NotEqual(secret, protectedText);

        var restored = DpapiSecretStore.UnprotectString(protectedText);
        Assert.Equal(secret, restored);
    }

    [Fact]
    public void DiagnosticsExporter_NeverIncludesProxyPasswords()
    {
        var proxies = new List<ProxyEntry>
        {
            new() { Host = "198.51.100.1", Port = 1080, Username = "alice", Password = "UltraSecretPassword", Label = "Node1" }
        };
        var status = new ServiceStatus { State = TunnelState.Connected, ActiveProxyLabel = "Node1", LastError = "err UltraSecretPassword" };
        var logs = new[] { "Auth failed for socks5://alice:UltraSecretPassword@198.51.100.1:1080" };

        var report = DiagnosticsExporter.BuildReport(status, proxies, new TunnelOptions(), logs);

        Assert.DoesNotContain("UltraSecretPassword", report);
        Assert.Contains("Node1", report);
    }
}
