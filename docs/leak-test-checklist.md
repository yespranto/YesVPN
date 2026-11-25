# YesVPN Leak Test & Safety Checklist

Verify all items on a Windows 10 and Windows 11 x64 machine before cutting a release.

## Automated Checks

- [ ] `dotnet build -c Release` passes with 0 errors
- [ ] `dotnet test -c Release` passes (parser, config builder, health state machine, IPC contract tests)
- [ ] `sing-box check -c <generated.json>` succeeds for Auto, Manual, Single-Proxy, and Split-Tunnel configs

## Manual Network & Leak Verification

| Check | Procedure | Expected Result | Status | Notes |
|---|---|---|---|---|
| **Public IP** | Connect with a healthy SOCKS5 proxy, visit `https://ifconfig.me` or `https://api.ipify.org` | Shows proxy exit IP; after Disconnect, shows real ISP IP | [ ] | |
| **DNS Leak** | Connect, run extended test at `https://dnsleaktest.com` and `https://ipleak.net` | Zero ISP DNS resolvers appear; only tunnel DNS resolver shown | [ ] | |
| **WebRTC Leak** | Connect, open `https://browserleaks.com/webrtc` in Chrome/Edge | No local LAN or real public ISP IP leaked via WebRTC | [ ] | |
| **IPv6 Leak** | Connect, visit `https://test-ipv6.com` and `https://ipleak.net` | IPv6 is either tunneled or blocked (no direct ISP IPv6 leak) | [ ] | |
| **Kill Switch** | Enable Kill Switch, connect, then stop/block the upstream proxy or terminate `sing-box.exe` | Non-tunnel traffic and outbound port 53 outside tunnel are blocked until user clicks Disconnect | [ ] | |
| **Auto Failover** | Configure 2+ proxies in Auto mode, kill proxy 1 | Traffic fails over to proxy 2 within health-check interval / urltest window | [ ] | |
| **UDP Handling** | Test with a TCP-only SOCKS5 proxy | App warns UDP ASSOCIATE is unsupported; DNS still resolves over TCP/DoT; optional UDP block stops UDP leaks | [ ] | |
| **Sleep / Resume & Wi-Fi Switch** | Put PC to sleep for 30s or switch Wi-Fi networks while connected | Service detects power/network event and reconnects with backoff | [ ] | |
| **Clean Exit / Crash / Uninstall** | Disconnect, kill service, and uninstall | Normal internet fully restored; no leftover `YesVPN_*` firewall rules, TUN adapter, or orphan `sing-box.exe` | [ ] | |

## Documented Cleanup Path

If YesVPN crashes or is terminated forcibly while connected with Kill Switch enabled:

1. **Automatic cleanup:** Restarting `YesVPN.Service` runs `CleanupStaleStateAsync()` on startup, which terminates any orphan YesVPN `sing-box.exe` process, deletes temporary runtime configs, and removes all Windows Firewall rules prefixed with `YesVPN_`.
2. **Manual emergency cleanup (elevated PowerShell):**
   ```powershell
   Stop-Process -Name "sing-box" -Force -ErrorAction SilentlyContinue
   netsh advfirewall firewall delete rule name="YesVPN_KillSwitch_BlockOut"
   netsh advfirewall firewall delete rule name="YesVPN_KillSwitch_AllowLoopback"
   netsh advfirewall firewall delete rule name="YesVPN_KillSwitch_AllowTun"
   netsh advfirewall firewall delete rule name="YesVPN_KillSwitch_AllowProxy"
   netsh advfirewall firewall delete rule name="YesVPN_KillSwitch_BlockDns"
   netsh advfirewall firewall delete rule name="YesVPN_BlockNonDnsUdp"
   ```
   Or remove all `YesVPN_*` rules in one command:
   ```powershell
   Get-NetFirewallRule -DisplayName "YesVPN_*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
   ```
