# ============================================================================
# HD Extension - Package Release as ZIP
# ============================================================================
# Creates a distributable ZIP file from the release folder
# ============================================================================

param(
    [string]$Version = "1.0.0",
    [switch]$Build  # Also run build-release.ps1 first
)

$ErrorActionPreference = "Stop"

$RootDir = $PSScriptRoot
$ReleaseDir = Join-Path $RootDir "release"
$ExtensionDir = Join-Path $ReleaseDir "HD.extension"
$DistDir = Join-Path $RootDir "dist"

# Build first if requested
if ($Build) {
    Write-Host "Building release..." -ForegroundColor Yellow
    & "$RootDir\build-release.ps1" -Clean -Version $Version
    Write-Host ""
}

# Check if release exists
if (-not (Test-Path $ExtensionDir)) {
    Write-Host "ERROR: Release folder not found. Run build-release.ps1 first." -ForegroundColor Red
    exit 1
}

# Create dist folder
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

# Create ZIP filename with version and date
$DateStamp = Get-Date -Format "yyyyMMdd"
$ZipName = "HD.extension-v$Version-$DateStamp.zip"
$ZipPath = Join-Path $DistDir $ZipName

# Remove old ZIP if exists
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Creating Distribution Package" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Copy install.bat into release folder (alongside HD.extension)
$InstallBat = Join-Path $RootDir "install.bat"
if (Test-Path $InstallBat) {
    Copy-Item $InstallBat $ReleaseDir -Force
    Write-Host "Included: install.bat" -ForegroundColor Gray
} else {
    Write-Host "WARNING: install.bat not found in project root" -ForegroundColor Yellow
}

# Create ZIP (include both HD.extension and install.bat)
Write-Host "Creating ZIP: $ZipName" -ForegroundColor Yellow
$ItemsToZip = @()
$ItemsToZip += $ExtensionDir
$InstallBatRelease = Join-Path $ReleaseDir "install.bat"
if (Test-Path $InstallBatRelease) {
    $ItemsToZip += $InstallBatRelease
}
Compress-Archive -Path $ItemsToZip -DestinationPath $ZipPath -CompressionLevel Optimal

# Get ZIP size
$ZipSize = (Get-Item $ZipPath).Length
$ZipSizeKB = "{0:N0}" -f ($ZipSize / 1KB)
$ZipSizeMB = "{0:N2}" -f ($ZipSize / 1MB)

# Generate SHA256 hash
Write-Host "Generating SHA256 hash..." -ForegroundColor Yellow
$Hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash
$HashFileName = [System.IO.Path]::GetFileNameWithoutExtension($ZipName) + ".sha256"
$HashFilePath = Join-Path $DistDir $HashFileName
"$Hash  $ZipName" | Out-File $HashFilePath -Encoding UTF8 -NoNewline

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  PACKAGE COMPLETE" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output:  $ZipPath" -ForegroundColor White
Write-Host "Size:    $ZipSizeKB KB ($ZipSizeMB MB)" -ForegroundColor White
Write-Host "SHA256:  $Hash" -ForegroundColor White
Write-Host "Hash:    $HashFilePath" -ForegroundColor White
Write-Host ""
Write-Host "Distribution Instructions:" -ForegroundColor Yellow
Write-Host "1. Share ZIP + .sha256 file with users" -ForegroundColor Gray
Write-Host "2. Users extract ZIP and double-click install.bat" -ForegroundColor Gray
Write-Host "3. Reload pyRevit or restart Revit" -ForegroundColor Gray
Write-Host ""
Write-Host "Or deploy to shared folder:" -ForegroundColor Yellow
Write-Host "  .\deploy.ps1 -SharedPath '\\server\share\HD-Extension' -Version $Version" -ForegroundColor Gray
Write-Host ""
