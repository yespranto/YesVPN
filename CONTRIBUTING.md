# Contributing to YesVPN

Thank you for contributing to YesVPN!

## Development Setup

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10/11 (x64).
2. Fetch the pinned `sing-box` and `Wintun` binaries:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\fetch-third-party.ps1
   ```
3. Build and run all tests:
   ```powershell
   dotnet restore YesVPN.sln
   dotnet build YesVPN.sln -c Release --no-restore
   dotnet test YesVPN.sln -c Release --no-build
   ```

## Guidelines

- Follow the architecture and conventions in [plan.md](plan.md) and [.agent/rules/yesvpn.md](.agent/rules/yesvpn.md).
- Never log, export, or commit proxy credentials or secrets.
- Add xUnit tests for parser, config builder, health-check, or IPC changes.
- Any change touching routes, WFP filters, or the Wintun adapter must include a documented cleanup path.
- Ensure `dotnet build -c Release` succeeds with 0 warnings/errors and `dotnet test -c Release` passes before opening a PR.
