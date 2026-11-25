<div align="center">

![YesVPN Banner](docs/assets/banner.png)

# YesVPN — Windows System-Wide SOCKS5 Proxy VPN

**Route all Windows 10 & 11 traffic through multiple SOCKS5 proxies with automatic failover, round-robin load balancing, a live network speed meter, and a WFP kill switch.**

[![CI](https://github.com/yespranto/YesVPN/actions/workflows/ci.yml/badge.svg)](https://github.com/yespranto/YesVPN/actions/workflows/ci.yml)
[![Release](https://github.com/yespranto/YesVPN/actions/workflows/release.yml/badge.svg)](https://github.com/yespranto/YesVPN/actions/workflows/release.yml)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D6?logo=windows)
![Tech](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?logo=dotnet)
![Engine](https://img.shields.io/badge/Core-sing--box%20%2B%20Wintun-22C55E)
![License](https://img.shields.io/badge/License-MIT-blue)

[Download Installer](../../releases) • [Features](#key-features) • [Routing Modes](#routing-modes-explained) • [FAQ](#frequently-asked-questions-faq) • [Build from Source](#build-from-source)

</div>

---

## Why YesVPN?

Windows only offers basic per-app HTTP/PAC proxy settings that many games, CLI tools, Electron apps, and system services completely ignore. **YesVPN** solves this by creating a virtual **Wintun TUN network adapter** powered by **sing-box**, capturing **100% of system traffic** across Windows 10/11 (x64) and forwarding it through your **SOCKS5 proxy pool**.

Unlike legacy `tun2socks` wrappers, YesVPN runs a privileged background Windows Service (`YesVPNService`), gives you a clean dark-mode WPF desktop & system tray interface, protects against DNS/IPv6 leaks with a **Windows Filtering Platform (WFP) kill switch**, and supports **multi-proxy Load Balancing** out of the box.

---

## Table of Contents

- [Key Features](#key-features)
- [Routing Modes Explained](#routing-modes-explained)
- [Supported SOCKS5 Proxy Formats](#supported-socks5-proxy-formats)
- [Important Privacy & Technical Notes](#important-privacy--technical-notes)
- [Download & Installation](#download--installation)
- [How It Works (Architecture)](#how-it-works-architecture)
- [Build from Source](#build-from-source)
- [Frequently Asked Questions (FAQ)](#frequently-asked-questions-faq)
- [Author & License](#author--license)

---

## Key Features

- **System-Wide SOCKS5 Tunneling (Wintun TUN):** Routes browsers, terminals, Node/Python/Git, desktop apps, and background services through your SOCKS5 proxies without per-app configuration.
- **3 Routing Modes:** **Auto (Fastest + Failover)**, **Load Balance (Merge All Proxies)**, and **Manual** selection.
- **Live Download & Upload Speed Meter:** Real-time independent `↓` Download and `↑` Upload speeds plus session totals streamed directly from `sing-box` Clash API (`/traffic`).
- **WFP Kill Switch & DNS Leak Protection:** Kernel-level dynamic Windows Filtering Platform (WFP) rules block non-tunnel outbound traffic and port 53 DNS leaks, and auto-clean if the service exits or crashes.
- **Smart Proxy Pool Management:**
  - Paste dozens of proxies in one click with per-line validation.
  - **Quick Test** (fast parallel check with live latency updates) and **Full Test** (includes `UDP ASSOCIATE` capability detection).
  - Bulk **Select All**, **Mark Dead**, `Ctrl+A`, and `Delete` cleanup.
  - Automatic exclusion of Dead proxies with cooldown recovery.
- **Split Tunneling:** Bypass specific domains (`*.local`), IPs/CIDRs (`192.168.1.0/24`), or executables (`slack.exe`).
- **DPAPI Encrypted Storage:** Proxy passwords are encrypted at rest using Windows DPAPI (`ProtectedData`) and masked in all logs and diagnostics exports.
- **Zero UAC Prompt on Connect:** Once installed, `YesVPNService` runs as `LocalSystem` and communicates over an ACL-secured named pipe (`\\.\pipe\YesVPN.Service.IPC`).

---

## Routing Modes Explained

| Mode | How It Works | Best For |
|---|---|---|
| **Auto (Fastest + Failover)** | Probes your proxy pool and automatically routes traffic through the lowest-latency healthy SOCKS5 proxy, failing over if it drops. | Everyday browsing & reliability |
| **Load Balance (Merge All Proxies)** | Distributes TCP connections round-robin across **all active healthy proxies** via a local high-throughput SOCKS5 balancer (`Socks5LoadBalancer`) with automatic failover and direct physical-NIC binding. | Multi-connection downloads, scraping, parallel streams, boosting combined throughput |
| **Manual** | Pins all traffic to one specific SOCKS5 proxy you choose. | Region-locked tasks or dedicated static IPs |

---

## Supported SOCKS5 Proxy Formats

Paste one proxy per line in the **Proxies** tab (blank lines and `#` comments are ignored):

```text
# URI format (with optional #Label)
socks5://username:password@203.0.113.10:1080#US-East

# Host and port only (no auth)
203.0.113.11:1080

# Colon-delimited credentials (supports special characters in passwords)
203.0.113.12:1080:username:p@ss:with:colons

# IPv6 hosts
socks5://user:pass@[2001:db8::1]:1080#IPv6-Node
[2001:db8::2]:1080:user:pass
```

---

## Important Privacy & Technical Notes

- **SOCKS5 is not encrypted.** YesVPN forwards your traffic through the SOCKS5 proxies you provide; traffic between your PC and the proxy server is not encrypted by SOCKS5 itself. HTTPS/TLS websites remain encrypted end-to-end, but the proxy operator can see unencrypted traffic and destination hostnames/IPs.
- **UDP handling:** Many SOCKS5 proxies do not support `UDP ASSOCIATE`. YesVPN tests UDP support during **Full Test**, routes DNS over TLS (`1.1.1.1`) inside the tunnel, and automatically rejects non-DNS UDP when proxies are TCP-only so browsers switch immediately to TCP/HTTPS instead of stalling on QUIC.
- **Quality depends on your proxies.** Throughput and uptime depend on the proxy servers you supply.

---

## Download & Installation

1. Download **`YesVPN-Setup-1.0.0-x64.exe`** from the [GitHub Releases](../../releases) page.
2. Run the installer (registers `YesVPNService`, bundles `sing-box` + `Wintun`, and adds Start Menu shortcuts).
3. Open **YesVPN**, paste your SOCKS5 proxies in the **Proxies** tab, and click **Connect**.

> **Clean Uninstall:** Uninstalling YesVPN automatically stops `YesVPNService`, removes the service registration, deletes any `YesVPN_*` firewall rules, and restores normal networking. See [docs/leak-test-checklist.md](docs/leak-test-checklist.md) for manual verification and emergency cleanup commands.

---

## How It Works (Architecture)

```text
┌────────────────┐  named pipe  ┌─────────────────────┐   spawns   ┌──────────────┐
│  YesVPN.App    │ ───────────► │  YesVPN.Service     │ ─────────► │  sing-box    │
│  (WPF UI+tray) │ ◄─────────── │  (Windows service,  │            │  (core)      │
└────────────────┘   status     │   runs as SYSTEM)   │            └──────┬───────┘
                                └──────────┬──────────┘                   │ Wintun TUN
                                           │ LoadBalance (127.0.0.1)      ▼
                                           └──────────────────────► SOCKS5 Proxy Pool
```

- **[src/YesVPN.Core](src/YesVPN.Core):** Proxy parser, `SingBoxConfigBuilder`, health checker & state machine, DPAPI store, credential masker, diagnostics exporter, and IPC contracts.
- **[src/YesVPN.Service](src/YesVPN.Service):** Windows Worker Service, named-pipe IPC server, `sing-box` process runner, `Socks5LoadBalancer`, WFP kill switch, Clash API speed sampler, and sleep/network recovery.
- **[src/YesVPN.App](src/YesVPN.App):** .NET 8 WPF dark-mode UI and system tray controller.
- **[tests/YesVPN.Tests](tests/YesVPN.Tests):** 21 xUnit unit & integration tests (including live `sing-box check -c` validation).

---

## Build from Source

**Requirements:** Windows 10/11 (x64), [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and optionally [Inno Setup 6](https://jrsoftware.org/isinfo.php) for `.exe` installer packaging.

```powershell
# 1. Fetch pinned third-party binaries (sing-box v1.14.4 + wintun 0.14.1)
powershell -ExecutionPolicy Bypass -File scripts\fetch-third-party.ps1

# 2. Restore, build, and run tests
dotnet restore YesVPN.sln
dotnet build YesVPN.sln -c Release --no-restore
dotnet test YesVPN.sln -c Release --no-build

# 3. Build self-contained binaries, Windows installer, and SHA256SUMS.txt
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 1.0.0 -SelfContained
```

---

## Frequently Asked Questions (FAQ)

<details>
<summary><strong>Does YesVPN route all Windows apps, or only browsers?</strong></summary>

All Windows system traffic—browsers, terminals, package managers, games, and desktop apps—is captured via the `YesVPN-TUN` Wintun virtual network adapter, unless explicitly excluded in **Settings → Split Tunneling**.
</details>

<details>
<summary><strong>How does Load Balance mode boost speed?</strong></summary>

`Socks5LoadBalancer` distributes each new TCP connection round-robin across all healthy proxies in your pool while binding directly to your physical network adapter. Multi-stream downloads, concurrent browser requests, and parallel tasks utilize the combined bandwidth of multiple proxies simultaneously.
</details>

<details>
<summary><strong>What happens if YesVPN crashes while Kill Switch is enabled?</strong></summary>

`YesVPN.Service` uses a **dynamic** Windows Filtering Platform session (`FWPM_SESSION_FLAG_DYNAMIC`). If the service crashes or is terminated, the Windows kernel automatically removes the YesVPN WFP kill-switch filters so your internet does not stay permanently locked.
</details>

<details>
<summary><strong>How is YesVPN different from Proxifier or raw tun2socks?</strong></summary>

YesVPN is open-source, uses `sing-box` + `Wintun` for modern high-performance TUN routing, supports multi-proxy Auto failover and Load Balancing, encrypts credentials with Windows DPAPI, and enforces WFP kill-switch + DNS leak protection natively.
</details>

---

## Documentation

- [Build Plan](plan.md)
- [Named-Pipe IPC Protocol](docs/ipc.md)
- [Leak Test & Emergency Cleanup Checklist](docs/leak-test-checklist.md)
- [Installer & Signing Guide](installer/README.md)
- [Changelog](CHANGELOG.md)
- [Security Policy](SECURITY.md)

---

## Author & License

- **Author:** Pranto Biswas
- **Website:** [www.yespranto.com](https://www.yespranto.com)
- **Email:** [hello@yespranto.com](mailto:hello@yespranto.com)

YesVPN source code is licensed under the [MIT License](LICENSE). Bundled third-party components (`sing-box` under GPL-3.0 and `Wintun`) remain under their respective licenses — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
