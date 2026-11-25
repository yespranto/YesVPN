# YesVPN Installer & Release Packaging

## Prerequisites

- .NET 8 SDK (`win-x64`)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`ISCC.exe`) to compile `YesVPN.iss`
- Optional: Windows Code Signing Certificate (`.pfx` or thumbprint) for SmartScreen reputation and Authenticode signing (`signtool.exe`)

## Build & Package Release

Run from repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 1.0.0
```

Outputs in `artifacts/`:
- `artifacts/publish/app/` — self-contained or framework-dependent `YesVPN.App`
- `artifacts/publish/service/` — `YesVPN.Service` + `sing-box` + `wintun` + licenses
- `artifacts/YesVPN-Setup-<version>-x64.exe` (when Inno Setup `iscc.exe` is installed)
- `artifacts/SHA256SUMS.txt`

## SmartScreen & Code Signing Notes

- Unsigned Windows installers trigger Microsoft Defender SmartScreen ("Windows protected your PC") until the publisher certificate builds reputation or an EV code-signing certificate is used.
- Pass `-CertificateThumbprint <thumbprint>` or `-PfxPath <path> -PfxPassword <pass>` to `scripts/release.ps1` to sign `YesVPN.App.exe`, `YesVPN.Service.exe`, and the final setup `.exe`.
