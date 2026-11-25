using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using YesVPN.Core.Models;

namespace YesVPN.Core.Config;

public sealed class NoHealthyProxiesException : InvalidOperationException
{
    public NoHealthyProxiesException(string message) : base(message) { }
}

/// <summary>
/// Builds a sing-box v1.14+ configuration JSON from YesVPN proxies and tunnel options.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const int DefaultLoadBalancerPort = 18080;
    public const string TunInterfaceName = "YesVPN-TUN";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string BuildJson(
        IReadOnlyList<ProxyEntry> proxies,
        TunnelOptions? options = null,
        int loadBalancerPort = DefaultLoadBalancerPort,
        int? clashApiPort = null)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        options ??= new TunnelOptions();

        var usableProxies = proxies
            .Where(p => p.IsEnabled && p.HealthStatus != ProxyHealthStatus.Dead)
            .ToList();

        if (usableProxies.Count == 0)
        {
            throw new NoHealthyProxiesException(
                proxies.Count == 0
                    ? "No proxies are configured."
                    : "All configured proxies are currently marked Dead or disabled.");
        }

        var primaryOutboundTag = options.Mode switch
        {
            ProxyMode.Auto => "auto",
            ProxyMode.Manual => "proxy",
            ProxyMode.LoadBalance => "lb",
            _ => "auto"
        };

        var proxyTags = new List<string>(usableProxies.Count);
        var outbounds = new List<Dictionary<string, object?>>();

        if (options.Mode == ProxyMode.LoadBalance)
        {
            outbounds.Add(new Dictionary<string, object?>
            {
                ["type"] = "socks",
                ["tag"] = "lb",
                ["server"] = "127.0.0.1",
                ["server_port"] = loadBalancerPort
            });
        }

        for (var i = 0; i < usableProxies.Count; i++)
        {
            var proxy = usableProxies[i];
            var tag = $"p{i + 1}";
            proxyTags.Add(tag);

            var socksOutbound = new Dictionary<string, object?>
            {
                ["type"] = "socks",
                ["tag"] = tag,
                ["server"] = proxy.Host,
                ["server_port"] = proxy.Port
            };

            if (!string.IsNullOrEmpty(proxy.Username))
            {
                socksOutbound["username"] = proxy.Username;
            }

            if (!string.IsNullOrEmpty(proxy.Password))
            {
                socksOutbound["password"] = proxy.Password;
            }

            outbounds.Add(socksOutbound);
        }

        if (options.Mode == ProxyMode.Auto)
        {
            outbounds.Add(new Dictionary<string, object?>
            {
                ["type"] = "urltest",
                ["tag"] = "auto",
                ["outbounds"] = proxyTags,
                ["url"] = options.UrlTestUrl,
                ["interval"] = options.UrlTestInterval,
                ["tolerance"] = options.UrlTestToleranceMs
            });
        }
        else if (options.Mode == ProxyMode.LoadBalance)
        {
            outbounds.Add(new Dictionary<string, object?>
            {
                ["type"] = "urltest",
                ["tag"] = "lb-udp",
                ["outbounds"] = proxyTags,
                ["url"] = options.UrlTestUrl,
                ["interval"] = options.UrlTestInterval,
                ["tolerance"] = options.UrlTestToleranceMs
            });
        }
        else
        {
            var selectedIndex = 0;
            if (options.ManualProxyId.HasValue)
            {
                var found = usableProxies.FindIndex(p => p.Id == options.ManualProxyId.Value);
                if (found >= 0)
                {
                    selectedIndex = found;
                }
            }

            outbounds.Add(new Dictionary<string, object?>
            {
                ["type"] = "selector",
                ["tag"] = "proxy",
                ["outbounds"] = proxyTags,
                ["default"] = proxyTags[selectedIndex]
            });
        }

        outbounds.Add(new Dictionary<string, object?>
        {
            ["type"] = "direct",
            ["tag"] = "direct"
        });

        var dnsServers = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["tag"] = "remote",
                ["type"] = "tls",
                ["server"] = string.IsNullOrWhiteSpace(options.DnsServer) ? "1.1.1.1" : options.DnsServer.Trim(),
                ["detour"] = primaryOutboundTag
            },
            new()
            {
                ["tag"] = "local",
                ["type"] = "local"
            }
        };

        var dnsRules = new List<Dictionary<string, object?>>();
        var routeRules = new List<Dictionary<string, object?>>
        {
            new() { ["action"] = "sniff" },
            new() { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new() { ["ip_is_private"] = true, ["outbound"] = "direct" }
        };

        // Always bypass direct connections to the proxy endpoints themselves so health checks never loop
        var proxyCidrs = NormalizeCidrs(usableProxies.Select(p => p.Host).Where(h => IPAddress.TryParse(h, out _)));
        if (proxyCidrs.Count > 0)
        {
            routeRules.Add(new Dictionary<string, object?>
            {
                ["ip_cidr"] = proxyCidrs,
                ["outbound"] = "direct"
            });
        }

        if (options.Mode == ProxyMode.LoadBalance)
        {
            routeRules.Add(new Dictionary<string, object?>
            {
                ["process_name"] = new[] { "YesVPN.Service.exe" },
                ["outbound"] = "direct"
            });
        }

        // Split tunneling: bypass domains
        var exactDomains = new List<string>();
        var suffixDomains = new List<string>();
        foreach (var rawDomain in options.SplitTunnel.BypassDomains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim()))
        {
            if (rawDomain.StartsWith("*.", StringComparison.Ordinal))
            {
                suffixDomains.Add(rawDomain[2..]);
            }
            else if (rawDomain.StartsWith('.'))
            {
                suffixDomains.Add(rawDomain[1..]);
            }
            else
            {
                exactDomains.Add(rawDomain);
            }
        }

        if (exactDomains.Count > 0 || suffixDomains.Count > 0)
        {
            var dnsRule = new Dictionary<string, object?> { ["server"] = "local" };
            var routeRule = new Dictionary<string, object?> { ["outbound"] = "direct" };

            if (exactDomains.Count > 0)
            {
                dnsRule["domain"] = exactDomains;
                routeRule["domain"] = exactDomains;
            }

            if (suffixDomains.Count > 0)
            {
                dnsRule["domain_suffix"] = suffixDomains;
                routeRule["domain_suffix"] = suffixDomains;
            }

            dnsRules.Add(dnsRule);
            routeRules.Add(routeRule);
        }

        // Split tunneling: bypass IPs / CIDRs
        var bypassCidrs = NormalizeCidrs(options.SplitTunnel.BypassIps);
        if (bypassCidrs.Count > 0)
        {
            routeRules.Add(new Dictionary<string, object?>
            {
                ["ip_cidr"] = bypassCidrs,
                ["outbound"] = "direct"
            });
        }

        // Split tunneling: bypass apps by process name
        var bypassApps = options.SplitTunnel.BypassApps
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => Path.GetFileName(a.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (bypassApps.Count > 0)
        {
            routeRules.Add(new Dictionary<string, object?>
            {
                ["process_name"] = bypassApps,
                ["outbound"] = "direct"
            });
        }

        // Reject non-DNS UDP when requested OR when all usable proxies were verified TCP-only
        // so browsers fall back immediately from QUIC (UDP 443) to HTTPS/TCP instead of stalling.
        var allKnownTcpOnly = usableProxies.All(p => p.SupportsUdp == false);
        if (options.BlockNonDnsUdp || allKnownTcpOnly)
        {
            routeRules.Add(new Dictionary<string, object?>
            {
                ["network"] = "udp",
                ["action"] = "reject"
            });
        }
        else if (options.Mode == ProxyMode.LoadBalance)
        {
            routeRules.Add(new Dictionary<string, object?>
            {
                ["network"] = "udp",
                ["outbound"] = "lb-udp"
            });
        }

        var dnsSection = new Dictionary<string, object?>
        {
            ["servers"] = dnsServers,
            ["final"] = "remote",
            ["strategy"] = "ipv4_only"
        };
        if (dnsRules.Count > 0)
        {
            dnsSection["rules"] = dnsRules;
        }

        // When YesVPN's WFP kill switch is active or LoadBalance uses 127.0.0.1, avoid sing-box's
        // internal strict_route WFP hooks that can block localhost/YesVPN.Service.exe sockets.
        var useSingBoxStrictRoute = options.StrictRoute && !options.KillSwitchEnabled && options.Mode != ProxyMode.LoadBalance;

        var root = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["level"] = string.IsNullOrWhiteSpace(options.LogLevel) ? "info" : options.LogLevel
            },
            ["experimental"] = clashApiPort.HasValue
                ? new Dictionary<string, object?>
                {
                    ["clash_api"] = new Dictionary<string, object?>
                    {
                        ["external_controller"] = $"127.0.0.1:{clashApiPort.Value}"
                    }
                }
                : null,
            ["dns"] = dnsSection,
            ["inbounds"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunInterfaceName,
                    ["address"] = new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" },
                    ["auto_route"] = true,
                    ["strict_route"] = useSingBoxStrictRoute,
                    ["stack"] = "mixed"
                }
            },
            ["outbounds"] = outbounds,
            ["route"] = new Dictionary<string, object?>
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
                ["final"] = primaryOutboundTag,
                ["rules"] = routeRules
            }
        };

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    private static List<string> NormalizeCidrs(IEnumerable<string> rawEntries)
    {
        var cidrs = new List<string>();
        foreach (var raw in rawEntries)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (trimmed.Contains('/'))
            {
                cidrs.Add(trimmed);
            }
            else if (IPAddress.TryParse(trimmed, out var ip))
            {
                var prefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                cidrs.Add($"{ip}/{prefix}");
            }
        }
        return cidrs;
    }
}
