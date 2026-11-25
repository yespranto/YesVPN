# YesVPN Named-Pipe IPC Protocol

YesVPN.App communicates with the privileged `YesVPN.Service` Windows service over a local Windows Named Pipe.

## Pipe Name & Security

- **Pipe name:** `YesVPN.Service.IPC` (`\\.\pipe\YesVPN.Service.IPC`)
- **Access Control (ACL):**
  - `LocalSystem` — FullControl
  - `BuiltinAdministrators` — FullControl
  - `Interactive` (`WellKnownSidType.InteractiveSid`) — ReadWrite (restricts access to locally logged-on interactive users; blocks network logons and anonymous clients)
  - Current process user (during local user-mode/test execution) — ReadWrite
- **Transport:** UTF-8 newline-delimited JSON (`\n`), one request and one response per pipe connection.

## Request Schema (`IpcRequest`)

```json
{
  "id": "req-1",
  "command": "Connect",
  "configJson": "{ ... sing-box json ... }",
  "activeProxyLabel": "US-East-1",
  "mode": "Auto",
  "killSwitch": true,
  "blockNonDnsUdp": false,
  "loadBalanceProxies": [],
  "tail": 200
}
```

### Supported Commands

| Command | Required Fields | Description |
|---|---|---|
| `Ping` | — | Health check for the IPC pipe and service availability. |
| `GetStatus` | — | Returns current `ServiceStatus` (state, active proxy label, mode, kill switch, PID, uptime, errors). |
| `Connect` | `configJson` | Cleans up stale routes/rules, writes config to a locked-down temporary file, applies kill-switch rules if enabled, and starts `sing-box`. |
| `Disconnect` | — | Terminates `sing-box`, deletes temporary config, removes firewall rules, and cleans up TUN routes. |
| `GetLogs` | `tail` (optional, default 200) | Returns the latest `tail` lines from the service log ring buffer with proxy credentials masked. |
| `ShutdownIfUnprivileged` | — | Stops an unprivileged dev instance of `YesVPN.Service` so an elevated instance can claim the pipe; rejected when running elevated or as `LocalSystem`. |

## Response Schema (`IpcResponse`)

```json
{
  "id": "req-1",
  "ok": true,
  "error": null,
  "status": {
    "state": "Connected",
    "activeProxyLabel": "US-East-1",
    "mode": "Auto",
    "killSwitchActive": true,
    "singBoxPid": 4210,
    "connectedSinceUtc": "2026-11-24T22:10:00Z",
    "lastError": null
  },
  "logs": []
}
```

## Credential & Cleanup Guarantees

1. `YesVPN.Service` never persists proxy credentials to disk in plain text. When `Connect` runs, `configJson` is written to a temporary file inside `%ProgramData%\YesVPN\runtime\` (or `%TEMP%\YesVPN\runtime\` in non-service mode) with restricted ACLs (`SYSTEM` + `Administrators` + current owner only) and deleted immediately on disconnect, crash recovery, or service shutdown.
2. Logs captured from `sing-box` stdout/stderr pass through `CredentialMasker.Mask(...)` before entering the ring buffer.
3. On service startup, `Disconnect`, or unexpected `sing-box` exit (when kill switch is off), `YesVPN.Service` kills any orphan `sing-box` processes spawned by YesVPN and removes all `YesVPN_*` Windows Firewall rules.
