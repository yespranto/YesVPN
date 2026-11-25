param(
    [Parameter(Mandatory = $false)]
    [string]$RepoName = "YesVPN",

    [Parameter(Mandatory = $false)]
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",

    [switch]$CreateRelease
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

try {
    $gh = Get-Command gh -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $gh) {
        $candidates = @(
            "$env:LOCALAPPDATA\Microsoft\WindowsApps\gh.exe",
            "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe",
            "$env:ProgramFiles\GitHub CLI\gh.exe"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { $gh = $c; break }
        }
        if (-not $gh) {
            $gh = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "gh.exe" -Recurse -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (-not $gh) {
        throw "GitHub CLI (gh) not found. Install via: winget install --id GitHub.cli --scope user"
    }

    & $gh auth status
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Please authenticate with GitHub first:" -ForegroundColor Yellow
        & $gh auth login --web -p https
        if ($LASTEXITCODE -ne 0) { throw "GitHub authentication failed." }
    }

    $description = "Route all Windows 10/11 system traffic through multiple SOCKS5 proxies with automatic failover, round-robin load balancing, live speed meter, WFP kill switch, and DNS leak protection."
    $homepage = "https://www.yespranto.com"
    $topics = @(
        "windows", "vpn", "socks5", "socks5-proxy", "proxy-client",
        "sing-box", "wintun", "tun2socks", "load-balancer", "failover",
        "kill-switch", "dns-leak-protection", "split-tunneling", "wpf",
        "dotnet8", "csharp", "windows-service", "wfp", "networking", "open-source"
    )

    $git = "C:\Program Files\Git\cmd\git.exe"
    $remoteExists = & $git remote 2>$null | Where-Object { $_ -eq "origin" }
    if (-not $remoteExists) {
        & $gh repo create $RepoName "--$Visibility" --source . --remote origin --description $description --homepage $homepage --push
        if ($LASTEXITCODE -ne 0) { throw "Failed to create GitHub repository." }
    }
    else {
        & $git push -u origin main
        if ($LASTEXITCODE -ne 0) { throw "Failed to push main branch." }
    }

    # Apply SEO topics, description, and homepage
    $topicArgs = @()
    foreach ($t in $topics) {
        $topicArgs += "--add-topic"
        $topicArgs += $t
    }
    & $gh repo edit --description $description --homepage $homepage @topicArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not update all repository topics." }

    # Push v1.0.0 tag
    & $git push origin v1.0.0
    if ($LASTEXITCODE -ne 0) { Write-Warning "Tag v1.0.0 push skipped or already exists." }

    if ($CreateRelease) {
        $installer = Get-ChildItem "artifacts\YesVPN-Setup-*-x64.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        $checksums = "artifacts\SHA256SUMS.txt"
        $assets = @()
        if ($installer) { $assets += $installer.FullName }
        if (Test-Path $checksums) { $assets += (Resolve-Path $checksums).Path }

        if ($assets.Count -gt 0) {
            & $gh release create v1.0.0 @assets --title "YesVPN v1.0.0" --notes-file CHANGELOG.md
        }
    }

    & $gh repo view --web
}
finally {
    Pop-Location
}
