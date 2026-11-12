<#
.SYNOPSIS
    Builds libheif (HEIC/HEIF decoder) with vcpkg and copies the DLLs to Lib\libheif\<runtime>.

.DESCRIPTION
    ShareX loads heif.dll at runtime to open iPhone photos (.heic/.heif). Only the LGPL decoder
    parts are built: libheif + libde265. The GPL x265 encoder is not needed and is excluded.

    The DLLs are copied next to ShareX.exe by ShareX.Avalonia.csproj when you build ShareX.

.EXAMPLE
    .\Scripts\Build-LibHeif.ps1
    .\Scripts\Build-LibHeif.ps1 -Platform ARM64
    .\Scripts\Build-LibHeif.ps1 -VcpkgRoot C:\vcpkg
#>
[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    # Uses VCPKG_ROOT / VCPKG_INSTALLATION_ROOT when set, otherwise clones vcpkg into %LOCALAPPDATA%\vcpkg.
    [string]$VcpkgRoot = $(if ($env:VCPKG_ROOT) { $env:VCPKG_ROOT } elseif ($env:VCPKG_INSTALLATION_ROOT) { $env:VCPKG_INSTALLATION_ROOT } else { Join-Path $env:LOCALAPPDATA "vcpkg" })
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$arch = $Platform.ToLowerInvariant()
$triplet = "$arch-windows"
$runtimeId = "win-$arch"
$outputDir = Join-Path $repoRoot "Lib\libheif\$runtimeId"

$vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"

if (-not (Test-Path $vcpkgExe)) {
    if (-not (Test-Path $VcpkgRoot)) {
        Write-Host "Cloning vcpkg into $VcpkgRoot ..."
        git clone --depth 1 https://github.com/microsoft/vcpkg.git $VcpkgRoot
        if ($LASTEXITCODE -ne 0) { throw "git clone failed." }
    }

    Write-Host "Bootstrapping vcpkg ..."
    & (Join-Path $VcpkgRoot "bootstrap-vcpkg.bat") -disableMetrics
    if ($LASTEXITCODE -ne 0) { throw "bootstrap-vcpkg failed." }
}

# core = no default features (default features pull in the GPL x265 encoder); libde265 = HEVC decoder.
Write-Host "Building libheif for $triplet ..."
& $vcpkgExe install "libheif[core,libde265]:$triplet" --classic --recurse
if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed." }

$binDir = Join-Path $VcpkgRoot "installed\$triplet\bin"
$dlls = Get-ChildItem -Path $binDir -Filter "*.dll"

if (-not ($dlls | Where-Object { $_.Name -in @("heif.dll", "libheif.dll") })) {
    throw "heif.dll was not found in $binDir."
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Get-ChildItem -Path $outputDir -Filter "*.dll" | Remove-Item -Force
$dlls | Copy-Item -Destination $outputDir -Force

Write-Host ""
Write-Host "Copied to ${outputDir}:"
Get-ChildItem -Path $outputDir -Filter "*.dll" | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host ""
Write-Host "Now build ShareX (Platform=$Platform); the DLLs are copied next to ShareX.exe."
