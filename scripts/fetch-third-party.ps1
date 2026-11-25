param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ThirdPartyDir = Join-Path $RepoRoot "third_party"

$singBoxDir = Join-Path $ThirdPartyDir "sing-box"
$wintunDir = Join-Path $ThirdPartyDir "wintun"
New-Item -ItemType Directory -Force -Path $singBoxDir, $wintunDir | Out-Null

$sbVersion = if (Test-Path (Join-Path $singBoxDir "version.txt")) {
    (Get-Content (Join-Path $singBoxDir "version.txt") -Raw).Trim()
} else {
    "v1.14.4"
}
$sbNumVersion = $sbVersion.TrimStart('v')

$wtVersion = if (Test-Path (Join-Path $wintunDir "version.txt")) {
    (Get-Content (Join-Path $wintunDir "version.txt") -Raw).Trim()
} else {
    "0.14.1"
}

$tmp = Join-Path $env:TEMP "yesvpn_third_party_$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    $sbExe = Join-Path $singBoxDir "sing-box.exe"
    if ($Force -or -not (Test-Path $sbExe)) {
        Write-Host "Downloading sing-box $sbVersion (windows-amd64)..." -ForegroundColor Cyan
        $sbZip = Join-Path $tmp "sing-box.zip"
        $sbUrl = "https://github.com/SagerNet/sing-box/releases/download/$sbVersion/sing-box-$sbNumVersion-windows-amd64.zip"
        curl.exe -sSL --retry 3 -o $sbZip $sbUrl
        if ($LASTEXITCODE -ne 0) { throw "Failed to download sing-box from $sbUrl" }

        $sbExtract = Join-Path $tmp "sb"
        Expand-Archive -Path $sbZip -DestinationPath $sbExtract -Force
        $innerDir = Get-ChildItem $sbExtract -Directory | Select-Object -First 1
        Copy-Item (Join-Path $innerDir.FullName "sing-box.exe") $sbExe -Force
        if (Test-Path (Join-Path $innerDir.FullName "LICENSE")) {
            Copy-Item (Join-Path $innerDir.FullName "LICENSE") (Join-Path $singBoxDir "LICENSE") -Force
        }
        $sbVersion | Set-Content (Join-Path $singBoxDir "version.txt") -Encoding UTF8
    }

    $wtDll = Join-Path $wintunDir "wintun.dll"
    if ($Force -or -not (Test-Path $wtDll)) {
        Write-Host "Downloading Wintun $wtVersion (amd64)..." -ForegroundColor Cyan
        $wtZip = Join-Path $tmp "wintun.zip"
        $wtUrl = "https://www.wintun.net/builds/wintun-$wtVersion.zip"
        curl.exe -sSL --retry 3 -o $wtZip $wtUrl
        if ($LASTEXITCODE -ne 0) { throw "Failed to download Wintun from $wtUrl" }

        $wtExtract = Join-Path $tmp "wt"
        Expand-Archive -Path $wtZip -DestinationPath $wtExtract -Force
        Copy-Item (Join-Path $wtExtract "wintun\bin\amd64\wintun.dll") $wtDll -Force
        if (Test-Path (Join-Path $wtExtract "wintun\LICENSE.txt")) {
            Copy-Item (Join-Path $wtExtract "wintun\LICENSE.txt") (Join-Path $wintunDir "LICENSE") -Force
        }
        $wtVersion | Set-Content (Join-Path $wintunDir "version.txt") -Encoding UTF8
    }

    Write-Host "third_party binaries ready." -ForegroundColor Green
}
finally {
    if (Test-Path $tmp) {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
