---
description: Bump version, build Release artifacts, build installer, sign, and produce SHA-256 checksums
---
1. Update `<Version>` in `Directory.Build.props` and `installer/YesVPN.iss`.
2. Run `powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version <version>`.
3. Verify `artifacts/SHA256SUMS.txt` and installer signature (if signing cert is configured).
4. Record release notes and verification results.
