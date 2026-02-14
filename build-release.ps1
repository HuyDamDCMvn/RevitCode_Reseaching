# ============================================================================
# HD Extension - Build & Package Release Script
# ============================================================================
# This script builds DLLs and creates a distributable package
# WITHOUT source code - only compiled DLLs and launcher scripts
# ============================================================================

param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# Paths
$RootDir = $PSScriptRoot
$SrcDir = Join-Path $RootDir "src"
$ReleaseDir = Join-Path $RootDir "release"
$ExtensionDir = Join-Path $ReleaseDir "HD.extension"
$LibNet8Dir = Join-Path $ExtensionDir "lib\net8"
$DevExtensionDir = Join-Path $RootDir "HD.extension"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  HD Extension - Build Release v$Version" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Clean release folder if requested
if ($Clean) {
    Write-Host "[1/5] Cleaning release folder..." -ForegroundColor Yellow
    if (Test-Path $ReleaseDir) {
        Remove-Item $ReleaseDir -Recurse -Force
    }
}

# Create directories
Write-Host "[1/5] Creating release structure..." -ForegroundColor Yellow
$dirs = @(
    "$ExtensionDir\lib\net8",
    "$ExtensionDir\HD.tab\Labeling.panel\SmartTag.pushbutton",
    "$ExtensionDir\HD.tab\WIP.panel\CommonFeature.pushbutton",
    "$ExtensionDir\HD.tab\WIP.panel\CheckCode.pushbutton",
    "$ExtensionDir\HD.tab\General.panel\Reload.pushbutton",
    "$ExtensionDir\HD.tab\General.panel\Extension.pushbutton",
    "$ExtensionDir\HD.tab\General.panel\Setting.pushbutton"
)
foreach ($dir in $dirs) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Write-Host "   Done." -ForegroundColor Green

# Build projects
Write-Host "[2/5] Building projects..." -ForegroundColor Yellow

# Build HD.Core
Write-Host "   Building HD.Core..." -ForegroundColor Gray
dotnet build "$SrcDir\HD.Core\HD.Core.csproj" -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "HD.Core build failed" }

# Build CommonFeature
Write-Host "   Building CommonFeature..." -ForegroundColor Gray
dotnet build "$SrcDir\CommonFeature\CommonFeature.csproj" -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "CommonFeature build failed" }

# Build CheckCode
Write-Host "   Building CheckCode..." -ForegroundColor Gray
dotnet build "$SrcDir\CheckCode\CheckCode.csproj" -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "CheckCode build failed" }

# Build SmartTag (depends on HD.Core)
Write-Host "   Building SmartTag..." -ForegroundColor Gray
dotnet build "$SrcDir\SmartTag\SmartTag.csproj" -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "SmartTag build failed" }

Write-Host "   Done." -ForegroundColor Green

# Copy DLLs (NO PDB, NO source code)
Write-Host "[3/5] Copying DLLs (without debug symbols)..." -ForegroundColor Yellow
$dlls = @(
    "HD.Core.dll",
    "CommonFeature.dll",
    "CheckCode.dll",
    "SmartTag.dll",
    "CommunityToolkit.Mvvm.dll"
)
foreach ($dll in $dlls) {
    $srcPath = Join-Path "$DevExtensionDir\lib\net8" $dll
    if (Test-Path $srcPath) {
        Copy-Item $srcPath $LibNet8Dir -Force
        Write-Host "   Copied: $dll" -ForegroundColor Gray
    } else {
        # SmartTag post-build copies to HD.extension; fallback to bin output
        $binPath = Join-Path "$SrcDir\SmartTag\bin\$Configuration\net8.0-windows" $dll
        if (Test-Path $binPath) {
            Copy-Item $binPath $LibNet8Dir -Force
            Write-Host "   Copied: $dll (from bin)" -ForegroundColor Gray
        }
    }
}

# SmartTag requires Data folder (Rules, Patterns, Training) next to DLLs
$smartTagDataSrc = Join-Path $SrcDir "SmartTag\Data"
$smartTagDataDest = Join-Path $LibNet8Dir "Data"
if (Test-Path $smartTagDataSrc) {
    if (Test-Path $smartTagDataDest) { Remove-Item $smartTagDataDest -Recurse -Force }
    Copy-Item $smartTagDataSrc $smartTagDataDest -Recurse -Force
    Write-Host "   Copied: SmartTag Data (Rules, Patterns, Training)" -ForegroundColor Gray
}
Write-Host "   Done." -ForegroundColor Green

# Copy launcher scripts (minified)
Write-Host "[4/5] Copying launcher scripts..." -ForegroundColor Yellow

# launcher_base.py
Copy-Item "$DevExtensionDir\lib\launcher_base.py" "$ExtensionDir\lib\" -Force

# Copy pushbutton scripts and icons
$pushbuttons = @(
    @{ Src = "HD.tab\Labeling.panel\SmartTag.pushbutton"; Files = @("script.py") },
    @{ Src = "HD.tab\WIP.panel\CommonFeature.pushbutton"; Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\WIP.panel\CheckCode.pushbutton"; Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\General.panel\Reload.pushbutton"; Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\General.panel\Extension.pushbutton"; Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\General.panel\Setting.pushbutton"; Files = @("script.py", "icon.png") }
)

foreach ($pb in $pushbuttons) {
    foreach ($file in $pb.Files) {
        $srcPath = Join-Path $DevExtensionDir "$($pb.Src)\$file"
        $destPath = Join-Path $ExtensionDir "$($pb.Src)\$file"
        if (Test-Path $srcPath) {
            Copy-Item $srcPath $destPath -Force
        }
    }
}
Write-Host "   Done." -ForegroundColor Green

# Create version file and README
Write-Host "[5/5] Creating release info..." -ForegroundColor Yellow

# Version file
@"
HD Extension v$Version
Build Date: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Revit Support: 2025, 2026
Framework: .NET 8.0
"@ | Out-File "$ExtensionDir\VERSION.txt" -Encoding UTF8

# README for users
@"
# HD Extension for Revit

## Installation

1. Copy the entire `HD.extension` folder to your pyRevit extensions folder:
   - Default: `%APPDATA%\pyRevit-Master\extensions\`
   - Or custom location configured in pyRevit settings

2. Reload pyRevit or restart Revit

## Requirements

- Revit 2025 or 2026
- pyRevit 4.8+ (with IronPython or CPython 3)

## Tools Included

- **SmartTag** (Labeling): Auto tag, preview/confirm/undo, export training data, dimensions
- **CommonFeature**: Element information, isolate, section box
- **CheckCode**: Code checking utilities

## Support

Contact: [Your contact info]
Version: $Version

---
(c) $(Get-Date -Format "yyyy") - All rights reserved
"@ | Out-File "$ExtensionDir\README.md" -Encoding UTF8

Write-Host "   Done." -ForegroundColor Green

# Copy install.bat to release folder (alongside HD.extension)
$InstallBat = Join-Path $RootDir "install.bat"
if (Test-Path $InstallBat) {
    Copy-Item $InstallBat $ReleaseDir -Force
    Write-Host "   Copied: install.bat" -ForegroundColor Gray
}
Write-Host "   Done." -ForegroundColor Green

# Summary
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  BUILD COMPLETE" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Release folder: $ReleaseDir" -ForegroundColor White
Write-Host ""

# List files
Write-Host "Files included:" -ForegroundColor Yellow
Get-ChildItem $ReleaseDir -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Replace("$ReleaseDir\", "")
    $size = "{0:N0} KB" -f ($_.Length / 1KB)
    Write-Host "   $relativePath ($size)" -ForegroundColor Gray
}

# Total size
$totalSize = (Get-ChildItem $ReleaseDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host "Total size: $("{0:N0} KB" -f ($totalSize / 1KB))" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  .\package-release.ps1 -Version $Version" -ForegroundColor Gray
Write-Host "  .\deploy.ps1 -SharedPath '\\server\share\HD-Extension' -Version $Version" -ForegroundColor Gray
