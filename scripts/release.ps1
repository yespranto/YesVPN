param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.0",

    [Parameter(Mandatory = $false)]
    [string]$CertificateThumbprint = "",

    [Parameter(Mandatory = $false)]
    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

try {
    $userDotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    $hasSdk = $false
    if ($dotnet) {
        $sdks = & $dotnet --list-sdks 2>$null
        if ($sdks) { $hasSdk = $true }
    }
    if (-not $hasSdk -and (Test-Path $userDotnet)) {
        $dotnet = $userDotnet
        $hasSdk = $true
    }
    if (-not $hasSdk) { throw ".NET 8 SDK not found." }

    function Invoke-Checked {
        param([scriptblock]$Action)
        & $Action
        if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }
    }

    Write-Host "=== YesVPN Release Build v$Version ===" -ForegroundColor Cyan

    # Ensure pinned sing-box and wintun binaries are present
    if (-not (Test-Path "third_party\sing-box\sing-box.exe") -or -not (Test-Path "third_party\wintun\wintun.dll")) {
        & (Join-Path $PSScriptRoot "fetch-third-party.ps1")
    }

    # Update Directory.Build.props version
    $propsPath = Join-Path $RepoRoot "Directory.Build.props"
    $propsContent = Get-Content $propsPath -Raw
    $propsContent = [regex]::Replace($propsContent, "<Version>.*?</Version>", "<Version>$Version</Version>")
    $propsContent = [regex]::Replace($propsContent, "<AssemblyVersion>.*?</AssemblyVersion>", "<AssemblyVersion>$Version.0</AssemblyVersion>")
    $propsContent = [regex]::Replace($propsContent, "<FileVersion>.*?</FileVersion>", "<FileVersion>$Version.0</FileVersion>")
    Set-Content -Path $propsPath -Value $propsContent -Encoding UTF8

    Invoke-Checked { & $dotnet restore YesVPN.sln }
    Invoke-Checked { & $dotnet build YesVPN.sln -c Release --no-restore }
    Invoke-Checked { & $dotnet test YesVPN.sln -c Release --no-build }

    $artifactsDir = Join-Path $RepoRoot "artifacts"
    $appOut = Join-Path $artifactsDir "publish\app"
    $svcOut = Join-Path $artifactsDir "publish\service"

    if (Test-Path $artifactsDir) { Remove-Item $artifactsDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $appOut, $svcOut | Out-Null

    $scFlag = if ($SelfContained) { "true" } else { "false" }

    Invoke-Checked { & $dotnet publish src\YesVPN.App\YesVPN.App.csproj -c Release -r win-x64 --self-contained $scFlag -o $appOut }
    Invoke-Checked { & $dotnet publish src\YesVPN.Service\YesVPN.Service.csproj -c Release -r win-x64 --self-contained $scFlag -o $svcOut }

    # Bundle sing-box and wintun into service publish directory (preserving both LICENSE files)
    $sbOut = Join-Path $svcOut "sing-box"
    New-Item -ItemType Directory -Force -Path $sbOut | Out-Null
    Copy-Item "third_party\sing-box\sing-box.exe" $sbOut -Force
    Copy-Item "third_party\sing-box\version.txt" $sbOut -Force
    Copy-Item "third_party\sing-box\LICENSE" (Join-Path $sbOut "LICENSE.sing-box.txt") -Force
    Copy-Item "third_party\wintun\wintun.dll" $sbOut -Force
    Copy-Item "third_party\wintun\LICENSE" (Join-Path $sbOut "LICENSE.wintun.txt") -Force

    # Optional Authenticode signing
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
        if (-not $signtool) { throw "signtool.exe not found in PATH." }

        foreach ($exe in @("$appOut\YesVPN.App.exe", "$svcOut\YesVPN.Service.exe")) {
            & $signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
        }
    }

    # Compile Inno Setup installer if ISCC is available
    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    $isccCandidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    if (-not $iscc) {
        foreach ($candidate in $isccCandidates) {
            if (Test-Path $candidate) { $iscc = $candidate; break }
        }
    }

    if ($iscc) {
        & $iscc "/DAppVersion=$Version" "installer\YesVPN.iss"
        $setupExe = Get-ChildItem "installer\Output\YesVPN-Setup-*.exe" | Select-Object -First 1
        if ($setupExe) {
            $destSetup = Join-Path $artifactsDir $setupExe.Name
            Copy-Item $setupExe.FullName $destSetup -Force
            if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
                & $signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $destSetup
            }
        }
    }
    else {
        Write-Warning "Inno Setup (ISCC.exe) not found; skipping .exe installer compilation. Published binaries are ready in artifacts\publish\."
    }

    # Produce SHA-256 checksums
    $checksumLines = @()
    Get-ChildItem $artifactsDir -Recurse -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $rel = $_.FullName.Substring($artifactsDir.Length + 1).Replace('\', '/')
        $checksumLines += "$hash  $rel"
    }
    $checksumLines | Set-Content (Join-Path $artifactsDir "SHA256SUMS.txt") -Encoding UTF8

    Write-Host "Release artifacts written to $artifactsDir" -ForegroundColor Green
}
finally {
    Pop-Location
}
