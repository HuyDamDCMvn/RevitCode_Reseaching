# ============================================================================
# HD Extension - Build & Package Release Script
# ============================================================================
# Builds ALL projects via solution file (parallel) and creates a distributable
# package WITHOUT source code - only compiled DLLs and launcher scripts.
# ============================================================================
#
# Optimized for: Ryzen 7 5800X (16 threads), 64 GB RAM, SSD
# Uses: dotnet build -m:16 for max parallel compilation
#
# Usage:
#   .\build-release.ps1                       # Default build
#   .\build-release.ps1 -Clean -Version 1.2.0 # Clean + version
#   .\build-release.ps1 -Quick                # Dev build (skip release copy)
# ============================================================================

param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$Clean,
    [switch]$Quick
)

$ErrorActionPreference = "Stop"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# ── Paths ─────────────────────────────────────────────────────────────────
$RootDir = $PSScriptRoot
$SrcDir = Join-Path $RootDir "src"
$SolutionFile = Join-Path $SrcDir "HDExtension.sln"
$ReleaseDir = Join-Path $RootDir "release"
$ExtensionDir = Join-Path $ReleaseDir "HD.extension"
$LibNet8Dir = Join-Path $ExtensionDir "lib\net8"
$DevExtensionDir = Join-Path $RootDir "HD.extension"
$MaxCpuCount = [Environment]::ProcessorCount

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  HD Extension - Build Release v$Version" -ForegroundColor Cyan
Write-Host "  CPU threads: $MaxCpuCount | Config: $Configuration" -ForegroundColor DarkCyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ── Step 1: Clean ─────────────────────────────────────────────────────────
if ($Clean) {
    Write-Host "[1/6] Cleaning..." -ForegroundColor Yellow
    if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
    dotnet clean $SolutionFile -c $Configuration --nologo -v q 2>$null
    Write-Host "   Done." -ForegroundColor Green
} else {
    Write-Host "[1/6] Skip clean (use -Clean to force)" -ForegroundColor Gray
}

# ── Step 2: NuGet restore ────────────────────────────────────────────────
Write-Host "[2/6] Restoring packages..." -ForegroundColor Yellow
dotnet restore $SolutionFile --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed" }
Write-Host "   Done." -ForegroundColor Green

# ── Step 3: Build all projects (PARALLEL via solution) ───────────────────
Write-Host "[3/6] Building solution (parallel, $MaxCpuCount threads)..." -ForegroundColor Yellow
dotnet build $SolutionFile -c $Configuration --nologo --no-restore -m:$MaxCpuCount -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "   Done." -ForegroundColor Green

if ($Quick) {
    $sw.Stop()
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host "  QUICK BUILD COMPLETE ($("{0:N1}" -f $sw.Elapsed.TotalSeconds)s)" -ForegroundColor Green
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host "  DLLs deployed to: HD.extension\lib\net8\" -ForegroundColor White
    exit 0
}

# ── Step 4: Create release directory structure ───────────────────────────
Write-Host "[4/6] Creating release structure..." -ForegroundColor Yellow
$dirs = @(
    "$ExtensionDir\lib\net8",
    "$ExtensionDir\lib\net8\Data\Config",
    "$ExtensionDir\HD.tab\AI.panel\RevitChat.pushbutton",
    "$ExtensionDir\HD.tab\AI.panel\RevitChatLocal.pushbutton",
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

# ── Step 5: Copy DLLs (NO PDB, NO source) ───────────────────────────────
Write-Host "[5/6] Copying DLLs..." -ForegroundColor Yellow

$dlls = @(
    "HD.Core.dll",
    "CommonFeature.dll",
    "CheckCode.dll",
    "SmartTag.dll",
    "CommunityToolkit.Mvvm.dll",
    "RevitChat.dll",
    "RevitChatLocal.dll",
    "OpenAI.dll",
    "System.ClientModel.dll",
    "System.Memory.Data.dll",
    "System.Net.ServerSentEvents.dll",
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
    "Microsoft.Extensions.Logging.Abstractions.dll"
)

$copied = 0
foreach ($dll in $dlls) {
    $srcPath = Join-Path "$DevExtensionDir\lib\net8" $dll
    if (Test-Path $srcPath) {
        Copy-Item $srcPath $LibNet8Dir -Force
        $copied++
    } else {
        Write-Host "   WARNING: $dll not found" -ForegroundColor Yellow
    }
}
Write-Host "   $copied DLLs copied." -ForegroundColor Green

# SmartTag Data folder
$smartTagDataSrc = Join-Path $SrcDir "SmartTag\Data"
$smartTagDataDest = Join-Path $LibNet8Dir "Data"
if (Test-Path $smartTagDataSrc) {
    if (Test-Path $smartTagDataDest) { Remove-Item $smartTagDataDest -Recurse -Force }
    Copy-Item $smartTagDataSrc $smartTagDataDest -Recurse -Force
    Write-Host "   Copied: SmartTag Data" -ForegroundColor Gray
}

# Default Ollama config
$configDir = Join-Path $LibNet8Dir "Data\Config"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}
@"
{
  "EndpointUrl": "http://localhost:11434",
  "Model": "qwen2.5:7b",
  "MaxTokens": 4096,
  "MaxConversationMessages": 40,
  "ToolSelectionMode": "smart",
  "EnabledSkillPacks": ["Core", "ViewControl", "MEP", "Modeler", "BIMCoordinator", "LinkedModels"]
}
"@ | Out-File (Join-Path $configDir "ollama_config.json") -Encoding UTF8
Write-Host "   Done." -ForegroundColor Green

# ── Step 6: Copy launcher scripts + release info ────────────────────────
Write-Host "[6/6] Copying launchers & creating release info..." -ForegroundColor Yellow

Copy-Item "$DevExtensionDir\lib\launcher_base.py" "$ExtensionDir\lib\" -Force

$pushbuttons = @(
    @{ Src = "HD.tab\AI.panel\RevitChat.pushbutton";          Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\AI.panel\RevitChatLocal.pushbutton";     Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\Labeling.panel\SmartTag.pushbutton";     Files = @("script.py") },
    @{ Src = "HD.tab\WIP.panel\CommonFeature.pushbutton";     Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\WIP.panel\CheckCode.pushbutton";         Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\General.panel\Reload.pushbutton";        Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\General.panel\Extension.pushbutton";     Files = @("script.py", "icon.png") },
    @{ Src = "HD.tab\General.panel\Setting.pushbutton";       Files = @("script.py", "icon.png") }
)
foreach ($pb in $pushbuttons) {
    foreach ($file in $pb.Files) {
        $srcPath = Join-Path $DevExtensionDir "$($pb.Src)\$file"
        $destPath = Join-Path $ExtensionDir "$($pb.Src)\$file"
        if (Test-Path $srcPath) { Copy-Item $srcPath $destPath -Force }
    }
}

# VERSION.txt
@"
HD Extension v$Version
Build Date: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Revit Support: 2025, 2026
Framework: .NET 8.0
"@ | Out-File "$ExtensionDir\VERSION.txt" -Encoding UTF8

# README.md
@"
# HD Extension for Revit

## Installation

### Option A: Automatic (recommended)
1. Extract the ZIP file
2. Double-click ``install.bat``
3. Reload pyRevit or restart Revit

### Option B: Manual
1. Copy ``HD.extension`` folder to your pyRevit extensions folder:
   - Default: ``%APPDATA%\pyRevit-Master\extensions\``
   - Or custom location configured in pyRevit settings
2. Reload pyRevit or restart Revit

## Requirements

- **Revit 2025** or 2026
- **pyRevit 4.8+** (with IronPython or CPython 3)

## AI Chatbot (RevitChatLocal) - Additional Setup

The AI chatbot uses **Ollama** (free, local AI - no API key needed).

### 1. Install Ollama
- Download from https://ollama.com and install
- After install, Ollama runs automatically at ``http://localhost:11434``

### 2. Download AI Model
Open a terminal and run:
``````
ollama pull qwen2.5:7b
``````

### 3. Use in Revit
- Open Revit > **HD tab** > **AI panel** > **RevitChatLocal**
- Type your question in English or Vietnamese

### Recommended Models

| Model | Size | Speed | Quality |
|-------|------|-------|---------|
| qwen2.5:7b | ~4 GB | Fast | Good (default) |
| qwen3:8b | ~5 GB | Fast | Better |
| qwen2.5:14b | ~9 GB | Medium | Best |

## Tools Included

- **RevitChatLocal** (AI): Query, modify, export via natural language (Ollama)
- **RevitChat** (AI): Same features, uses OpenAI API (requires API key)
- **SmartTag** (Labeling): Auto tag, preview/confirm/undo, export training data
- **CommonFeature**: Element information, isolate, section box
- **CheckCode**: Code checking utilities

---
(c) $(Get-Date -Format "yyyy") - All rights reserved
"@ | Out-File "$ExtensionDir\README.md" -Encoding UTF8

# install.bat
$InstallBat = Join-Path $RootDir "install.bat"
if (Test-Path $InstallBat) { Copy-Item $InstallBat $ReleaseDir -Force }

Write-Host "   Done." -ForegroundColor Green

# ── Summary ──────────────────────────────────────────────────────────────
$sw.Stop()
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  BUILD COMPLETE ($("{0:N1}" -f $sw.Elapsed.TotalSeconds)s)" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Release folder: $ReleaseDir" -ForegroundColor White
Write-Host ""

Write-Host "Files included:" -ForegroundColor Yellow
Get-ChildItem $ReleaseDir -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Replace("$ReleaseDir\", "")
    $size = "{0:N0} KB" -f ($_.Length / 1KB)
    Write-Host "   $relativePath ($size)" -ForegroundColor Gray
}

$totalSize = (Get-ChildItem $ReleaseDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host "Total size: $("{0:N0} KB" -f ($totalSize / 1KB))" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  .\package-release.ps1 -Version $Version" -ForegroundColor Gray
Write-Host "  .\deploy.ps1 -SharedPath '\\server\share\HD-Extension' -Version $Version" -ForegroundColor Gray
Write-Host "  .\github-release.ps1 -Version $Version" -ForegroundColor Gray
