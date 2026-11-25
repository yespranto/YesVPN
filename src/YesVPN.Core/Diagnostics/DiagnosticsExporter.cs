using System.Text;
using System.Text.Json;
using YesVPN.Core.Ipc;
using YesVPN.Core.Models;
using YesVPN.Core.Parsing;
using YesVPN.Core.Security;

namespace YesVPN.Core.Diagnostics;

/// <summary>
/// Produces a shareable diagnostics report with all proxy credentials masked.
/// </summary>
public static class DiagnosticsExporter
{
    public static string BuildReport(
        ServiceStatus? serviceStatus,
        IReadOnlyList<ProxyEntry> proxies,
        TunnelOptions options,
        IEnumerable<string> serviceLogs)
    {
        var secrets = proxies
            .SelectMany(p => new[] { p.Password, p.Username })
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("=== YesVPN Diagnostics Report ===");
        sb.AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine();

        sb.AppendLine("--- Service Status ---");
        if (serviceStatus is null)
        {
            sb.AppendLine("Service unreachable or not running.");
        }
        else
        {
            sb.AppendLine($"State: {serviceStatus.State}");
            sb.AppendLine($"Mode: {serviceStatus.Mode}");
            sb.AppendLine($"Active Proxy: {serviceStatus.ActiveProxyLabel ?? "(none)"}");
            sb.AppendLine($"Kill Switch Active: {serviceStatus.KillSwitchActive}");
            sb.AppendLine($"sing-box PID: {serviceStatus.SingBoxPid?.ToString() ?? "(none)"}");
            sb.AppendLine($"sing-box Version: {serviceStatus.SingBoxVersion ?? "(unknown)"}");
            sb.AppendLine($"Last Error: {CredentialMasker.Mask(serviceStatus.LastError, secrets)}");
        }
        sb.AppendLine();

        sb.AppendLine("--- Tunnel Options ---");
        sb.AppendLine($"Mode: {options.Mode}");
        sb.AppendLine($"KillSwitchEnabled: {options.KillSwitchEnabled}");
        sb.AppendLine($"BlockNonDnsUdp: {options.BlockNonDnsUdp}");
        sb.AppendLine($"StrictRoute: {options.StrictRoute}");
        sb.AppendLine($"DNS Server: {options.DnsServer}");
        sb.AppendLine($"Bypass Domains: {string.Join(", ", options.SplitTunnel.BypassDomains)}");
        sb.AppendLine($"Bypass IPs: {string.Join(", ", options.SplitTunnel.BypassIps)}");
        sb.AppendLine($"Bypass Apps: {string.Join(", ", options.SplitTunnel.BypassApps)}");
        sb.AppendLine();

        sb.AppendLine($"--- Configured Proxies ({proxies.Count}) ---");
        foreach (var proxy in proxies)
        {
            sb.AppendLine(
                $"- {proxy.DisplayLabel} | {ProxyParser.FormatMasked(proxy)} | Enabled={proxy.IsEnabled} | Health={proxy.HealthStatus} | LatencyMs={proxy.LatencyMs?.ToString() ?? "-"} | UDP={proxy.SupportsUdp?.ToString() ?? "?"} | Failures={proxy.ConsecutiveFailures} | Error={CredentialMasker.Mask(proxy.LastError, secrets)}");
        }
        sb.AppendLine();

        sb.AppendLine("--- Service Logs (Masked) ---");
        foreach (var line in serviceLogs)
        {
            sb.AppendLine(CredentialMasker.Mask(line, secrets));
        }

        return sb.ToString();
    }
}
