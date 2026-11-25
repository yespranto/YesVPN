# YesVPN: Build Plan (for Antigravity)

> Goal: a minimal, easy, powerful Windows app that routes **all system traffic** through **multiple SOCKS5 proxies** with automatic selection and failover.
> Place this file in the repo root. Point every Antigravity agent at it: "Read plan.md first."

---

## 1. Product summary

| Item | Decision |
|---|---|
| Name | YesVPN |
| Platform | Windows 10/11 (x64) |
| What it does | Captures system traffic via a virtual adapter and forwards it through a pool of SOCKS5 proxies |
| Principles | One big button, sensible defaults, no jargon, safe by default |
| Non-goals (v1) | Own proxy servers, accounts/billing, macOS/Linux/mobile, custom protocols |

### Important truths (keep in the README and UI copy)
- SOCKS5 is **not encrypted**. YesVPN routes traffic through the user's proxies; it does not make it private from the proxy operator or the path to the proxy. Do not market it as "secure" or "no-log" unless encrypted transport is added.
- Many SOCKS5 proxies do **not** support UDP. v1 must handle this explicitly (see 6.3).
- Quality depends entirely on the proxies the user supplies.

---

## 2. Architecture

```
┌────────────────┐  named pipe  ┌─────────────────────┐   spawns   ┌──────────────┐
│  YesVPN.App    │ ───────────► │  YesVPN.Service     │ ─────────► │  sing-box    │
│  (WPF UI+tray) │ ◄─────────── │  (Windows service,  │            │  (core)      │
└────────────────┘   status     │   runs as SYSTEM)   │            └──────┬───────┘
                                └─────────────────────┘                   │ Wintun TUN
                                                                          ▼
                                                            SOCKS5 proxy A / B / C ...
```

| Component | Tech | Responsibility |
|---|---|---|
| `YesVPN.App` | .NET 8, WPF (or WinUI 3) | UI, tray icon, settings, proxy list management |
| `YesVPN.Service` | .NET 8 Worker Service | Runs privileged: start/stop core, kill switch, route cleanup |
| `YesVPN.Core` (library) | .NET 8 class library | Proxy model, parser, config generator, health checker, IPC contracts |
| `YesVPN.Tests` | xUnit | Parser, config generator, health-check logic |
| Engine | **sing-box** + Wintun (bundled binaries) | TUN capture, SOCKS5 outbounds, DNS, routing |

Why a service: TUN and firewall rules need admin rights. A service installed once avoids a UAC prompt on every connect.

### Engine capability notes (verify against the pinned version)
- sing-box provides: TUN inbound, SOCKS outbound, `selector` (manual), `urltest` (fastest by latency, automatic fallback), DNS routing, per-app/per-domain rules.
- sing-box does **not** provide true per-connection round-robin load balancing out of the box. v1 modes are therefore: **Auto (fastest/failover)** and **Manual**. "Round-robin" is a v1.1 stretch goal (options: app-side rotation, or switch core to Xray which has balancers).

---

## 3. Repo layout

```
YesVPN/
├─ plan.md
├─ README.md
├─ .agent/                    # Antigravity rules & workflows (see section 4)
│  ├─ rules/
│  └─ workflows/
├─ src/
│  ├─ YesVPN.App/
│  ├─ YesVPN.Service/
│  └─ YesVPN.Core/
├─ tests/
│  └─ YesVPN.Tests/
├─ third_party/
│  ├─ sing-box/               # pinned binary + LICENSE + version.txt
│  └─ wintun/                 # wintun.dll + LICENSE
├─ installer/                 # Inno Setup or WiX
├─ docs/
│  ├─ ipc.md
│  └─ leak-test-checklist.md
└─ YesVPN.sln
```

---

## 4. Antigravity setup

> Antigravity is an agent-first IDE with an Editor view and an Agent Manager that can run several agents in parallel and produce reviewable artifacts (plans, task lists, walkthroughs). Folder names and settings below reflect the current conventions I know of; confirm against the Antigravity docs for your installed version.

### 4.1 Workspace rules (`.agent/rules/yesvpn.md`)
Paste this as a persistent rule:

```markdown
# YesVPN project rules
- Always read /plan.md before starting a task and follow its architecture.
- Stack: .NET 8, C#, WPF (UI), Worker Service (Windows service), sing-box core. Do not introduce other frameworks without asking.
- Keep the UI minimal: one primary action per screen. No settings sprawl.
- Never log proxy credentials. Mask passwords in logs, UI, and exported files.
- Never hardcode secrets. Store proxy credentials with Windows DPAPI (ProtectedData, CurrentUser/LocalMachine as appropriate).
- Every feature needs unit tests where logic is testable (parsing, config generation, health checks).
- Do not modify files under third_party/ except to update the pinned version and its LICENSE.
- Before declaring a task done: run `dotnet build` and `dotnet test`, and summarize results.
- Prefer small commits with clear messages. Do not rewrite unrelated files.
- Anything touching routes, firewall, or the TUN adapter must have a documented cleanup path.
```

### 4.2 Workflows (`.agent/workflows/`)
Create these as reusable commands:

- `build-and-test.md`: run `dotnet restore`, `dotnet build -c Release`, `dotnet test`, then report failures.
- `leak-check.md`: guide through `docs/leak-test-checklist.md` and record results.
- `release.md`: bump version, build installer, verify signing, produce checksums.

### 4.3 How to run the build in Antigravity
1. Open the repo folder as the workspace.
2. In **Agent Manager**, start with **Planning mode** for each milestone so the agent produces an implementation plan artifact for you to review before it writes code.
3. Run independent milestones in parallel agents only when they touch different projects (for example, `Core` library and installer scaffolding). Do not parallelize work that edits the same files.
4. Review each agent's task list and walkthrough artifact. Comment on them before approving.
5. Keep terminal permissions conservative: allow `dotnet`, `git`, and file operations; require approval for anything that touches network settings, services, or the registry.
6. Use the browser agent only for things like checking leak-test sites, never for entering real credentials.

---

## 5. Milestones and agent prompts

Each milestone has a prompt to paste into a new Antigravity agent. Complete in order; each must pass its acceptance criteria before moving on.

### M0: Spike: prove the tunnel by hand (do this manually first)
Not an agent task. Verify before automating:
1. Download a pinned sing-box release and Wintun.
2. Hand-write a config (starter below) with 2 real SOCKS5 proxies.
3. Run elevated; confirm public IP changes and DNS does not leak.

**Starter config (verify field names against the pinned sing-box version):**
```json
{
  "log": { "level": "info" },
  "dns": {
    "servers": [
      { "tag": "remote", "address": "tls://1.1.1.1", "detour": "auto" }
    ]
  },
  "inbounds": [
    {
      "type": "tun",
      "tag": "tun-in",
      "address": ["172.19.0.1/30"],
      "auto_route": true,
      "strict_route": true,
      "stack": "mixed"
    }
  ],
  "outbounds": [
    { "type": "socks", "tag": "p1", "server": "PROXY1_HOST", "server_port": 1080, "username": "USER", "password": "PASS" },
    { "type": "socks", "tag": "p2", "server": "PROXY2_HOST", "server_port": 1080, "username": "USER", "password": "PASS" },
    { "type": "urltest", "tag": "auto", "outbounds": ["p1", "p2"], "url": "https://www.gstatic.com/generate_204", "interval": "1m", "tolerance": 50 },
    { "type": "direct", "tag": "direct" }
  ],
  "route": {
    "auto_detect_interface": true,
    "final": "auto",
    "rules": [
      { "protocol": "dns", "action": "hijack-dns" },
      { "ip_is_private": true, "outbound": "direct" }
    ]
  }
}
```
**Acceptance:** IP changes; ipleak.net / dnsleaktest.com show no ISP DNS; stopping sing-box restores normal networking.

### M1: Solution scaffold
**Prompt:**
> Read plan.md. Create the .NET 8 solution with projects YesVPN.App (WPF), YesVPN.Service (Worker Service configured as a Windows service), YesVPN.Core (class library), and YesVPN.Tests (xUnit). Add project references per the architecture diagram, a .gitignore, and a README stub. Make `dotnet build` and `dotnet test` pass. Do not implement features yet.

**Acceptance:** solution builds; one placeholder test passes.

### M2: Core library: proxy model, parser, config generator
**Prompt:**
> In YesVPN.Core implement: (1) `ProxyEntry` model (host, port, username, password, label); (2) a parser accepting `socks5://user:pass@host:port`, `host:port`, and `host:port:user:pass`, one per line, ignoring blanks and `#` comments, with clear error messages per bad line; (3) `SingBoxConfigBuilder` that takes proxies + mode (Auto, Manual) + options (kill switch, split-tunnel bypass list) and returns config JSON modeled on the starter config in plan.md section M0. Add thorough xUnit tests including malformed input, IPv6 hosts, special characters in passwords, and a single-proxy case. Never log passwords.

**Acceptance:** all tests pass; generated JSON validated with `sing-box check` in a test or script.

### M3: Windows service and IPC
**Prompt:**
> In YesVPN.Service implement start/stop/status of the bundled sing-box process. Expose a named-pipe IPC (document the protocol in docs/ipc.md) with commands: Connect(config), Disconnect, GetStatus, GetLogs(tail). Secure the pipe so only local interactive users can connect. Capture sing-box stdout/stderr into a ring buffer with passwords masked. On service stop or crash, ensure sing-box is terminated and routes are cleaned up. Add integration-style tests where feasible.

**Acceptance:** from a test client, connect/disconnect works; killing the service leaves no orphan sing-box process and no stuck routes.

### M4: Health checker and auto-selection
**Prompt:**
> In YesVPN.Core add a proxy health checker that measures latency and success per proxy (TCP connect + SOCKS5 handshake + a test request) with timeouts, run on demand and periodically. Expose results to the UI. Mark proxies Dead after N consecutive failures and exclude them from the generated config until they recover. Unit test the state machine with a fake clock.

**Acceptance:** dead proxies are excluded; latency values update; no UI-thread blocking.

### M5: Minimal UI
**Prompt:**
> Build the WPF UI. Main window: a large Connect/Disconnect button, status text (Disconnected / Connecting / Connected via <label>), current public IP, and a mode dropdown (Auto-fastest, Manual). Second tab: proxy list with live latency, add (paste multiple lines), delete, test-all. Add a system tray icon with Connect/Disconnect/Exit and minimize-to-tray. Use a dark theme, one accent color, no clutter. All text in plain language. Communicate with the service only via the IPC from docs/ipc.md.

**Acceptance:** a non-technical user can paste proxies and connect in under 30 seconds without instructions.

### M6: Safety features
**Prompt:**
> Implement a kill switch using Windows Filtering Platform (or the engine's strict_route plus firewall rules) that blocks non-tunnel traffic when enabled and the tunnel drops, with a guaranteed cleanup on disconnect and on service start (remove stale rules). Enforce DNS-through-tunnel and block outbound port 53 outside the tunnel. Add split tunneling: bypass list of domains/IPs/apps editable in a simple settings panel. Document the cleanup path.

**Acceptance:** with kill switch on, pulling the proxy connection leaves no leaks; after disconnect, normal internet is fully restored; leak checklist passes.

### M7: Reliability and polish
**Prompt:**
> Add auto-reconnect with backoff, start-with-Windows (service auto-start, tray app via Run key), graceful handling of all proxies dead (clear message, kill switch behavior respected), network change detection (Wi-Fi switch, sleep/resume), and an exportable diagnostics file with credentials masked.

**Acceptance:** survives sleep/resume and Wi-Fi switching; diagnostics file contains no secrets.

### M8: Installer and release
**Prompt:**
> Create the installer (Inno Setup or WiX) that installs App, Service, sing-box, and Wintun, registers and starts the service, adds the Start Menu entry, and fully uninstalls (service, routes, firewall rules, adapter). Add a release workflow: version bump, Release build, installer build, SHA-256 checksums, and a signing step.

**Acceptance:** clean install and uninstall on a fresh Windows VM; no leftovers; SmartScreen behavior documented (use a code-signing certificate).

### M9: Stretch (v1.1+)
- Round-robin / least-connections balancing (app-side or Xray core)
- Encrypted transport option (proxies behind Shadowsocks/Trojan/TLS/SSH tunnel)
- Per-app tunneling UI, import/export profiles, auto-update, localization

---

## 6. Technical details to get right

### 6.1 Credentials
- Store proxy passwords encrypted with DPAPI; never in plain-text config on disk. Generate the sing-box config in memory or in a locked-down temp location and delete on disconnect.

### 6.2 Cleanup guarantees
- On service start: remove stale YesVPN firewall rules and ensure no leftover TUN adapter or routes.
- On disconnect/crash: same cleanup. Treat "internet broken after quitting" as a release-blocking bug.

### 6.3 UDP handling
- Test whether each proxy supports UDP ASSOCIATE during health checks.
- If a proxy does not, route DNS over TCP/DoT through the tunnel and warn the user that QUIC/games/voice may not work. Offer an option to block UDP (except DNS) to avoid leaks.

### 6.4 Privacy and honesty
- UI copy should say "routes your traffic through your proxies," not "encrypted" unless an encrypted transport is configured.
- Add a first-run note explaining the proxy operator can see unencrypted traffic.

### 6.5 Licensing
- sing-box is GPL-3.0 licensed (check the current license text and any additional terms). Bundling it affects how you can distribute YesVPN. Decide early: open-source YesVPN, run the core as an unmodified separate process with proper notices, or choose a differently licensed core. Get legal advice if distributing commercially.
- Include LICENSE files for sing-box and Wintun in the installer and an About screen.

### 6.6 Legal
- VPN-style software is restricted in some countries. Check where you intend to distribute.

---

## 7. Testing and acceptance

### Automated
- `dotnet test` passes: parser, config builder, health state machine, IPC contract tests.
- `sing-box check -c generated.json` passes for all generated configs.

### Manual (record results in docs/leak-test-checklist.md)
- [ ] Public IP shows a proxy IP when connected, real IP when disconnected
- [ ] DNS leak test: no ISP resolvers
- [ ] WebRTC leak test in a browser
- [ ] IPv6 leak test (block or tunnel IPv6; no leaks either way)
- [ ] Kill switch: drop proxy, confirm traffic blocked
- [ ] Failover: kill proxy 1, confirm switch to proxy 2 within the expected time
- [ ] Sleep/resume and Wi-Fi switch recovery
- [ ] Quit/crash/uninstall leaves normal networking intact
- [ ] Fresh Windows 10 and Windows 11 VMs

---

## 8. Definition of done (v1.0)

1. Install, paste proxies, click Connect: works with no configuration.
2. Auto mode picks the fastest healthy proxy and fails over automatically.
3. Kill switch and DNS protection on by default and verified.
4. No credentials in logs, diagnostics, or plain-text files.
5. Clean uninstall with no leftover service, rules, adapter, or routes.
6. Signed installer; licenses included; README states the SOCKS5 encryption caveat plainly.

---

## 9. Suggested timeline

| Week | Work |
|---|---|
| 1 | M0 spike, M1 scaffold, M2 core library |
| 2 | M3 service + IPC, M4 health checks |
| 3 | M5 UI |
| 4 | M6 safety features, M7 reliability |
| 5 | M8 installer, full leak testing, release candidate |
