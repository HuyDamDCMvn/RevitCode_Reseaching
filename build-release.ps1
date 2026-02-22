# ============================================================================
# HD Extension - Build & Package Release Script
# ============================================================================
# Builds ALL projects (including AI Chatbot) and creates a distributable
# package WITHOUT source code - only compiled DLLs and launcher scripts.
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

# ── Step 1: Clean ──────────────────────────────────────────────────────────
if ($Clean) {
    Write-Host "[1/6] Cleaning release folder..." -ForegroundColor Yellow
    if (Test-Path $ReleaseDir) {
        Remove-Item $ReleaseDir -Recurse -Force
    }
}

# ── Step 2: Create directory structure ─────────────────────────────────────
Write-Host "[2/6] Creating release structure..." -ForegroundColor Yellow
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

# ── Step 3: Build all projects ─────────────────────────────────────────────
Write-Host "[3/6] Building projects..." -ForegroundColor Yellow

$projects = @(
    @{ Name = "HD.Core";        Path = "$SrcDir\HD.Core\HD.Core.csproj" },
    @{ Name = "CommonFeature";  Path = "$SrcDir\CommonFeature\CommonFeature.csproj" },
    @{ Name = "CheckCode";      Path = "$SrcDir\CheckCode\CheckCode.csproj" },
    @{ Name = "SmartTag";       Path = "$SrcDir\SmartTag\SmartTag.csproj" },
    @{ Name = "RevitChat";      Path = "$SrcDir\RevitChat\RevitChat.csproj" },
    @{ Name = "RevitChatLocal"; Path = "$SrcDir\RevitChatLocal\RevitChatLocal.csproj" }
)

foreach ($proj in $projects) {
    Write-Host "   Building $($proj.Name)..." -ForegroundColor Gray
    dotnet build $proj.Path -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$($proj.Name) build failed" }
}
Write-Host "   Done." -ForegroundColor Green

# ── Step 4: Copy DLLs (NO PDB, NO source code) ───────────────────────────
Write-Host "[4/6] Copying DLLs..." -ForegroundColor Yellow

$dlls = @(
    # Core
    "HD.Core.dll",
    "CommonFeature.dll",
    "CheckCode.dll",
    "SmartTag.dll",
    # Shared
    "CommunityToolkit.Mvvm.dll",
    # AI Chatbot
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
        Write-Host "   Copied: $dll" -ForegroundColor Gray
        $copied++
    } else {
        Write-Host "   WARNING: $dll not found in lib/net8" -ForegroundColor Yellow
    }
}
Write-Host "   $copied DLLs copied." -ForegroundColor Green

# SmartTag Data folder (Rules, Patterns, Training)
$smartTagDataSrc = Join-Path $SrcDir "SmartTag\Data"
$smartTagDataDest = Join-Path $LibNet8Dir "Data"
if (Test-Path $smartTagDataSrc) {
    if (Test-Path $smartTagDataDest) { Remove-Item $smartTagDataDest -Recurse -Force }
    Copy-Item $smartTagDataSrc $smartTagDataDest -Recurse -Force
    Write-Host "   Copied: SmartTag Data (Rules, Patterns, Training)" -ForegroundColor Gray
}

# Default Ollama config (no secrets)
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
Write-Host "   Created: default ollama_config.json" -ForegroundColor Gray

Write-Host "   Done." -ForegroundColor Green

# ── Step 5: Copy launcher scripts ─────────────────────────────────────────
Write-Host "[5/6] Copying launcher scripts..." -ForegroundColor Yellow

# launcher_base.py
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
        if (Test-Path $srcPath) {
            Copy-Item $srcPath $destPath -Force
        }
    }
}
Write-Host "   Done." -ForegroundColor Green

# ── Step 6: Create version file, README, install.bat ──────────────────────
Write-Host "[6/6] Creating release info..." -ForegroundColor Yellow

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
1. Copy the entire ``HD.extension`` folder to your pyRevit extensions folder:
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
- The chat window opens - type your question in English or Vietnamese
- Example: "How many walls in the model?" or "dem so luong tuong"

### Recommended Models

| Model | Size | Speed | Quality |
|-------|------|-------|---------|
| qwen2.5:7b | ~4 GB | Fast | Good (default) |
| qwen3:8b | ~5 GB | Fast | Better |
| qwen2.5:14b | ~9 GB | Medium | Best |

### Settings
Click the **Settings** button in the chat window to:
- Change Ollama endpoint URL (default: localhost:11434)
- Switch AI model
- Enable/disable skill packs (MEP, Modeler, BIM Coordinator, etc.)

## Tools Included

- **RevitChatLocal** (AI): Query, modify, export Revit data via natural language (Ollama)
- **RevitChat** (AI): Same features, uses OpenAI API (requires API key)
- **SmartTag** (Labeling): Auto tag, preview/confirm/undo, export training data
- **CommonFeature**: Element information, isolate, section box
- **CheckCode**: Code checking utilities

## Support

Contact: [Your contact info]
Version: $Version

---
(c) $(Get-Date -Format "yyyy") - All rights reserved
"@ | Out-File "$ExtensionDir\README.md" -Encoding UTF8

# install.bat
$InstallBat = Join-Path $RootDir "install.bat"
if (Test-Path $InstallBat) {
    Copy-Item $InstallBat $ReleaseDir -Force
    Write-Host "   Copied: install.bat" -ForegroundColor Gray
}
Write-Host "   Done." -ForegroundColor Green

# ── Summary ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  BUILD COMPLETE" -ForegroundColor Green
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
