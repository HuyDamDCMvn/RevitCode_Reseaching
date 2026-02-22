# ============================================================================
# HD Extension - GitHub Release
# ============================================================================
# Creates a GitHub Release with the packaged ZIP + SHA256 hash.
# Requires: gh CLI (https://cli.github.com) authenticated.
#
# Usage:
#   .\github-release.ps1 -Version "1.0.0"
#   .\github-release.ps1 -Version "1.0.0" -Build
#   .\github-release.ps1 -Version "1.0.0" -Build -Draft
#   .\github-release.ps1 -Version "1.0.0" -Build -Prerelease
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$Build,
    [switch]$Draft,
    [switch]$Prerelease
)

$ErrorActionPreference = "Stop"

$RootDir = $PSScriptRoot
$DistDir = Join-Path $RootDir "dist"
$Tag = "v$Version"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  HD Extension - GitHub Release $Tag" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ── 1. Verify gh CLI ──────────────────────────────────────────────────────
Write-Host "[1/4] Checking prerequisites..." -ForegroundColor Yellow

$ghPath = Get-Command gh -ErrorAction SilentlyContinue
if (-not $ghPath) {
    Write-Host "   ERROR: gh CLI not found." -ForegroundColor Red
    Write-Host "   Install from: https://cli.github.com" -ForegroundColor Red
    exit 1
}

$null = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "   ERROR: gh CLI not authenticated." -ForegroundColor Red
    Write-Host "   Run: gh auth login" -ForegroundColor Red
    exit 1
}
Write-Host "   gh CLI: OK" -ForegroundColor Gray

# ── 2. Build + Package if requested ──────────────────────────────────────
if ($Build) {
    Write-Host "[2/4] Building and packaging..." -ForegroundColor Yellow
    & "$RootDir\package-release.ps1" -Build -Version $Version
    Write-Host ""
} else {
    Write-Host "[2/4] Skipping build (use -Build to include)" -ForegroundColor Gray
}

# ── 3. Locate artifacts ──────────────────────────────────────────────────
Write-Host "[3/4] Locating artifacts..." -ForegroundColor Yellow

if (-not (Test-Path $DistDir)) {
    Write-Host "   ERROR: dist/ folder not found. Run with -Build first." -ForegroundColor Red
    exit 1
}

$ZipFile = Get-ChildItem $DistDir -Filter "HD.extension-v$Version-*.zip" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $ZipFile) {
    Write-Host "   ERROR: No ZIP found for v$Version in dist/" -ForegroundColor Red
    Write-Host "   Run: .\package-release.ps1 -Build -Version $Version" -ForegroundColor Red
    exit 1
}

$HashFile = Get-ChildItem $DistDir -Filter "$([System.IO.Path]::GetFileNameWithoutExtension($ZipFile.Name)).sha256" -ErrorAction SilentlyContinue |
    Select-Object -First 1

Write-Host "   ZIP:    $($ZipFile.Name)" -ForegroundColor Gray
if ($HashFile) {
    Write-Host "   SHA256: $($HashFile.Name)" -ForegroundColor Gray
}

$ZipSizeMB = "{0:N2}" -f ($ZipFile.Length / 1MB)
Write-Host "   Size:   $ZipSizeMB MB" -ForegroundColor Gray

# ── 4. Create GitHub Release ─────────────────────────────────────────────
Write-Host "[4/4] Creating GitHub Release..." -ForegroundColor Yellow

$releaseNotes = @"
## HD Extension $Tag

### What's Included
- **RevitChatLocal** — AI chatbot (Ollama, local, free)
- **RevitChat** — AI chatbot (OpenAI API)
- **SmartTag** — Auto labeling & dimensioning
- **CommonFeature** — Element info, isolate, section box
- **CheckCode** — Code checking utilities

### Requirements
- Revit 2025 or 2026
- pyRevit 4.8+
- **For AI Chatbot**: Ollama installed + model pulled (``ollama pull qwen2.5:7b``)

### Installation
1. Download ``$($ZipFile.Name)`` below
2. Extract the ZIP
3. Double-click ``install.bat``
4. Reload pyRevit or restart Revit

### SHA256
See the ``.sha256`` file for checksum verification.
"@

$assets = @($ZipFile.FullName)
if ($HashFile) {
    $assets += $HashFile.FullName
}

$ghArgs = @("release", "create", $Tag)
$ghArgs += "--title"
$ghArgs += "HD Extension $Tag"
$ghArgs += "--notes"
$ghArgs += $releaseNotes

if ($Draft) { $ghArgs += "--draft" }
if ($Prerelease) { $ghArgs += "--prerelease" }

foreach ($asset in $assets) {
    $ghArgs += $asset
}

try {
    gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
} catch {
    Write-Host ""
    Write-Host "   ERROR: Failed to create release." -ForegroundColor Red
    Write-Host "   $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "   If the tag already exists, delete it first:" -ForegroundColor Yellow
    Write-Host "   gh release delete $Tag --yes && git tag -d $Tag && git push --delete origin $Tag" -ForegroundColor Gray
    exit 1
}

# ── Summary ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  RELEASE PUBLISHED" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Tag:     $Tag" -ForegroundColor White
Write-Host "Assets:  $($assets.Count) file(s)" -ForegroundColor White
Write-Host ""

$repoUrl = gh repo view --json url -q ".url" 2>$null
if ($repoUrl) {
    Write-Host "View release: $repoUrl/releases/tag/$Tag" -ForegroundColor Cyan
}
Write-Host ""
