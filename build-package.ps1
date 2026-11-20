[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Offline
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-Location $PSScriptRoot

[xml]$props = Get-Content "Directory.Build.props"
$Version = $props.Project.PropertyGroup.Version
if (-not $Version) { $Version = "1.0.0" }

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Spectra Realtek Studio v$Version — Release Packaging Pipeline" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$restoreArgs = @()
if ($Offline) {
    $localCache = Join-Path $env:USERPROFILE ".nuget\packages"
    if (Test-Path $localCache) {
        $restoreArgs += "--source"
        $restoreArgs += $localCache
        $restoreArgs += "--ignore-failed-sources"
    }
}

Write-Host "`n[1/5] Restoring & building solution ($Configuration)..." -ForegroundColor Yellow
dotnet restore SpectraRealtekStudio.sln @restoreArgs -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) {
    if (-not $Offline) {
        Write-Warning "Online restore failed; retrying against local NuGet cache..."
        $localCache = Join-Path $env:USERPROFILE ".nuget\packages"
        dotnet restore SpectraRealtekStudio.sln --source $localCache --ignore-failed-sources -m:1 --disable-build-servers
        if ($LASTEXITCODE -ne 0) { Write-Error "Restore failed!"; exit $LASTEXITCODE }
    } else {
        Write-Error "Offline restore failed!"; exit $LASTEXITCODE
    }
}

dotnet build SpectraRealtekStudio.sln -c $Configuration --no-restore --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed!"; exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "`n[2/5] Running automated hardware & APO self-verification..." -ForegroundColor Yellow
    $cliDll = Join-Path $PSScriptRoot "src\SpectraRealtekStudio.Cli\bin\$Configuration\net8.0-windows\SpectraRealtekStudio.Cli.dll"
    dotnet exec $cliDll test
    if ($LASTEXITCODE -ne 0) { Write-Error "Self-verification failed!"; exit $LASTEXITCODE }
} else {
    Write-Host "`n[2/5] Skipping verification (-SkipTests specified)." -ForegroundColor DarkGray
}

Write-Host "`n[3/5] Publishing self-contained win-x64 App & CLI binaries..." -ForegroundColor Yellow
if (Test-Path "publish") { Remove-Item -Recurse -Force "publish" }

$pubRestoreArgs = @()
if ($Offline) {
    $localCache = Join-Path $env:USERPROFILE ".nuget\packages"
    $pubRestoreArgs += "--source"
    $pubRestoreArgs += $localCache
    $pubRestoreArgs += "--ignore-failed-sources"
}

dotnet restore src/SpectraRealtekStudio.App/SpectraRealtekStudio.App.csproj -r win-x64 @pubRestoreArgs -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) { Write-Error "Restore win-x64 App failed!"; exit $LASTEXITCODE }
dotnet restore src/SpectraRealtekStudio.Cli/SpectraRealtekStudio.Cli.csproj -r win-x64 @pubRestoreArgs -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) { Write-Error "Restore win-x64 CLI failed!"; exit $LASTEXITCODE }

dotnet publish src/SpectraRealtekStudio.App/SpectraRealtekStudio.App.csproj `
    -c $Configuration -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    --disable-build-servers -m:1 -o publish/app
if ($LASTEXITCODE -ne 0) { Write-Error "Publish App failed!"; exit $LASTEXITCODE }

dotnet publish src/SpectraRealtekStudio.Cli/SpectraRealtekStudio.Cli.csproj `
    -c $Configuration -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    --disable-build-servers -m:1 -o publish/app
if ($LASTEXITCODE -ne 0) { Write-Error "Publish CLI failed!"; exit $LASTEXITCODE }

Copy-Item "docs" "publish/app/docs" -Recurse -Force
Copy-Item "README.md", "LICENSE" "publish/app/" -Force

# Add convenient portable CLI status launcher
$cliHelperBat = @"
@echo off
title Spectra Realtek Studio — CLI Hardware Status
"%~dp0SpectraRealtekStudio.Cli.exe" status
echo.
pause
"@
Set-Content -Path "publish\app\Show-Realtek-Status.bat" -Value $cliHelperBat -Encoding ASCII

New-Item -ItemType Directory -Force -Path "publish/output" | Out-Null

Write-Host "`n[4/5] Compiling Inno Setup 6 installer..." -ForegroundColor Yellow
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$isccPath = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $isccPath) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $isccPath = $cmd.Source }
}

$setupExe = Join-Path (Resolve-Path "publish/output").Path "SpectraRealtekStudio-v$Version-Setup.exe"
if (-not $isccPath) {
    Write-Warning "Inno Setup 6 compiler (ISCC.exe) not found. Skipping installer creation."
} else {
    Write-Host "Using Inno Setup compiler: $isccPath" -ForegroundColor DarkGray
    & $isccPath "/DMyAppVersion=$Version" "installer\SpectraRealtekStudio.iss"
    if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE."; exit $LASTEXITCODE }
    Write-Host "Installer created: $setupExe" -ForegroundColor Green
}

Write-Host "`n[5/5] Creating Portable ZIP & SHA-256 checksums..." -ForegroundColor Yellow
$zipPath = Join-Path (Resolve-Path "publish/output").Path "SpectraRealtekStudio-v$Version-Portable.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path "publish/app").Path,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$hashes = @()
if (Test-Path $setupExe) {
    $setupHash = Get-FileHash $setupExe -Algorithm SHA256
    $hashes += "$($setupHash.Hash)  $(Split-Path $setupExe -Leaf)"
    $setupSizeMb = [Math]::Round((Get-Item $setupExe).Length / 1MB, 2)
    Write-Host "Installer EXE   : $(Split-Path $setupExe -Leaf) ($setupSizeMb MB)" -ForegroundColor Green
    Write-Host "  SHA-256       : $($setupHash.Hash)" -ForegroundColor Cyan
}

$zipHash = Get-FileHash $zipPath -Algorithm SHA256
$hashes += "$($zipHash.Hash)  $(Split-Path $zipPath -Leaf)"
$zipSizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "Portable ZIP    : $(Split-Path $zipPath -Leaf) ($zipSizeMb MB)" -ForegroundColor Green
Write-Host "  SHA-256       : $($zipHash.Hash)" -ForegroundColor Cyan

Set-Content -Path "publish/output/SHA256SUMS.txt" -Value ($hashes -join "`r`n") -Encoding ASCII

Write-Host "`nRelease Packaging Complete! Artifacts are ready in publish\output\" -ForegroundColor Green
