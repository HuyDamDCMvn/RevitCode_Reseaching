# ============================================================================
# HD Extension - Deploy to Shared Folder
# ============================================================================
# Copies the packaged ZIP + SHA256 hash to a shared network folder
# and creates a latest.txt for version tracking.
# ============================================================================
#
# Usage:
#   .\deploy.ps1 -SharedPath "\\server\share\HD-Extension" -Version "1.0.0"
#   .\deploy.ps1 -SharedPath "D:\SharedDrive\HD-Extension" -Version "1.0.0" -Build
#
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$SharedPath,
    [string]$Version = "1.0.0",
    [switch]$Build  # Build + Package before deploying
)

$ErrorActionPreference = "Stop"

$RootDir = $PSScriptRoot
$DistDir = Join-Path $RootDir "dist"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  HD Extension - Deploy v$Version" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# 1. Build + Package if requested
# ---------------------------------------------------------------------------
if ($Build) {
    Write-Host "[1/4] Building and packaging..." -ForegroundColor Yellow
    & "$RootDir\package-release.ps1" -Build -Version $Version
    Write-Host ""
} else {
    Write-Host "[1/4] Skipping build (use -Build to include)" -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# 2. Find ZIP and hash files
# ---------------------------------------------------------------------------
Write-Host "[2/4] Locating package files..." -ForegroundColor Yellow

$DateStamp = Get-Date -Format "yyyyMMdd"
$ZipName = "HD.extension-v$Version-$DateStamp.zip"
$HashName = "HD.extension-v$Version-$DateStamp.sha256"

$ZipPath = Join-Path $DistDir $ZipName
$HashPath = Join-Path $DistDir $HashName

if (-not (Test-Path $ZipPath)) {
    # Try to find any ZIP with this version
    $Found = Get-ChildItem $DistDir -Filter "HD.extension-v$Version-*.zip" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($Found) {
        $ZipPath = $Found.FullName
        $ZipName = $Found.Name
        $HashName = [System.IO.Path]::GetFileNameWithoutExtension($ZipName) + ".sha256"
        $HashPath = Join-Path $DistDir $HashName
        Write-Host "   Found: $ZipName" -ForegroundColor Gray
    } else {
        Write-Host "   ERROR: No package found in dist/" -ForegroundColor Red
        Write-Host "   Run: .\package-release.ps1 -Build -Version $Version" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "   Found: $ZipName" -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# 3. Validate shared path
# ---------------------------------------------------------------------------
Write-Host "[3/4] Validating shared folder..." -ForegroundColor Yellow

if (-not (Test-Path $SharedPath)) {
    Write-Host "   Creating folder: $SharedPath" -ForegroundColor Gray
    try {
        New-Item -ItemType Directory -Path $SharedPath -Force | Out-Null
    } catch {
        Write-Host "   ERROR: Cannot create folder: $SharedPath" -ForegroundColor Red
        Write-Host "   Check network path and permissions." -ForegroundColor Red
        exit 1
    }
}

Write-Host "   Target: $SharedPath" -ForegroundColor Gray

# ---------------------------------------------------------------------------
# 4. Deploy files
# ---------------------------------------------------------------------------
Write-Host "[4/4] Deploying..." -ForegroundColor Yellow

# Copy ZIP
Write-Host "   Copying: $ZipName" -ForegroundColor Gray
Copy-Item $ZipPath $SharedPath -Force

# Copy hash file (if exists)
if (Test-Path $HashPath) {
    Write-Host "   Copying: $HashName" -ForegroundColor Gray
    Copy-Item $HashPath $SharedPath -Force
} else {
    Write-Host "   WARNING: Hash file not found ($HashName)" -ForegroundColor Yellow
}

# Create/Update latest.txt
$LatestFile = Join-Path $SharedPath "latest.txt"
@"
version=$Version
file=$ZipName
date=$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
sha256_file=$HashName
"@ | Out-File $LatestFile -Encoding UTF8

Write-Host "   Updated: latest.txt" -ForegroundColor Gray

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  DEPLOY COMPLETE" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Shared folder: $SharedPath" -ForegroundColor White
Write-Host ""
Write-Host "Files deployed:" -ForegroundColor Yellow
Get-ChildItem $SharedPath -File | Sort-Object LastWriteTime -Descending | Select-Object -First 5 | ForEach-Object {
    $size = "{0:N0} KB" -f ($_.Length / 1KB)
    Write-Host "   $($_.Name) ($size)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "Users can now download from: $SharedPath" -ForegroundColor Cyan
Write-Host ""
