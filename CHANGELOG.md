# Changelog

All notable changes to **YesVPN** will be documented in this file.

## [1.0.0] - 2026-11-24

### Added
- **Core Library (`YesVPN.Core`):**
  - Multi-format SOCKS5 parser (`socks5://user:pass@host:port`, `host:port`, `host:port:user:pass`, IPv6, special characters in passwords, masked line errors).
  - `SingBoxConfigBuilder` targeting `sing-box v1.14.4` with `Auto`, `LoadBalance`, and `Manual` modes, DoT DNS (`1.1.1.1`, `ipv4_only`), split-tunnel domain/IP/app bypass rules, automatic TCP-only UDP rejection, and Clash API traffic controller.
  - `ProxyHealthChecker` & `ProxyHealthStateMachine` with TCP/SOCKS5/HTTP/UDP ASSOCIATE probes, Dead state transitions, and recovery cooldowns.
  - DPAPI-encrypted credential storage (`DpapiSecretStore`), `CredentialMasker`, and masked `DiagnosticsExporter`.
- **Windows Service (`YesVPN.Service`):**
  - Local-interactive ACL-secured Named Pipe IPC server (`\\.\pipe\YesVPN.Service.IPC`).
  - `SingBoxProcessRunner` with config validation (`sing-box check`), graceful `CTRL_C_EVENT` teardown, orphan cleanup, and masked log ring buffer.
  - `Socks5LoadBalancer` distributing TCP connections round-robin across all active proxies with failover and direct physical-NIC binding (`IP_UNICAST_IF`).
  - Dynamic-session WFP Kill Switch (`WfpKillSwitchManager`) blocking outside traffic and port 53 DNS leaks.
  - Live per-direction speed sampler (`TunnelSpeedSampler`) streaming `sing-box` Clash API `/traffic`.
  - Auto-reconnect with exponential backoff and sleep/resume/network recovery.
- **WPF Desktop App (`YesVPN.App`):**
  - Dark-themed 680×820 UI with custom shield icon, native dark title bar, animated Connect/Disconnect controls, and system tray integration.
  - Live Download/Upload speed meter with session totals and activity bars.
  - Proxies tab with multi-line paste, **Quick Test**, **Full Test**, bulk checkboxes, **Select All**, **Mark Dead**, and keyboard shortcuts (`Ctrl+A`, `Delete`).
  - Settings tab for Kill Switch, UDP leak blocking, Windows startup, split tunneling, service log viewer, and masked diagnostics export.
- **Packaging & Tests:**
  - Inno Setup 6 x64 installer (`installer/YesVPN.iss`) with service registration and clean uninstall/upgrade support.
  - 21 xUnit unit & integration tests (including live `sing-box check -c` validation).
