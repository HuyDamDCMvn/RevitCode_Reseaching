#Requires -Version 5.1
<#
.SYNOPSIS
    Package multi-version DLLs into pyRevit extension structure.

.DESCRIPTION
    Copies net48 and net8.0-windows build outputs to pyRevit extension lib folders.
    Ensures each lib folder is self-contained with all dependencies.

.PARAMETER ProjectDir
    Path to the C# project directory containing bin/Release outputs.

.PARAMETER ExtensionPath
    Path to the pyRevit extension root (e.g., YourExt.extension).

.PARAMETER ExcludePatterns
    DLL name patterns to exclude (e.g., RevitAPI*, System.*)

.EXAMPLE
    .\package-to-pyrevit.ps1 -ProjectDir "src\MyProject" -ExtensionPath "deploy\MyExt.extension"

.EXAMPLE
    .\package-to-pyrevit.ps1 -ProjectDir "src\MyProject" -ExtensionPath "deploy\MyExt.extension" -ExcludePatterns @("RevitAPI*", "AdWindows*")
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,

    [Parameter(Mandatory = $true)]
    [string]$ExtensionPath,

    [Parameter(Mandatory = $false)]
    [string[]]$ExcludePatterns = @("RevitAPI*", "RevitAPIUI*", "AdWindows*", "UIFramework*")
)

$ErrorActionPreference = "Stop"

# Resolve paths
$ProjectDir = Resolve-Path $ProjectDir -ErrorAction Stop
$Net48Source = Join-Path $ProjectDir "bin\Release\net48"
$Net8Source = Join-Path $ProjectDir "bin\Release\net8.0-windows"

# Validate source directories exist
if (-not (Test-Path $Net48Source)) {
    Write-Error "net48 build output not found at: $Net48Source"
    Write-Error "Run 'dotnet build -c Release' first."
    exit 1
}

if (-not (Test-Path $Net8Source)) {
    Write-Error "net8.0-windows build output not found at: $Net8Source"
    Write-Error "Run 'dotnet build -c Release' first."
    exit 1
}

# Setup destination
$LibPath = Join-Path $ExtensionPath "lib"
$Net48Dest = Join-Path $LibPath "net48"
$Net8Dest = Join-Path $LibPath "net8"

# Create directories
New-Item -ItemType Directory -Path $Net48Dest -Force | Out-Null
New-Item -ItemType Directory -Path $Net8Dest -Force | Out-Null

# Clean existing files
Write-Host "Cleaning destination folders..." -ForegroundColor Cyan
Remove-Item "$Net48Dest\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$Net8Dest\*" -Recurse -Force -ErrorAction SilentlyContinue

# Copy function with exclusion
function Copy-DllsWithExclusions {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$Exclude
    )
    
    $files = Get-ChildItem "$Source\*.dll" -ErrorAction SilentlyContinue
    $copied = 0
    $skipped = 0
    
    foreach ($file in $files) {
        $shouldExclude = $false
        foreach ($pattern in $Exclude) {
            if ($file.Name -like $pattern) {
                $shouldExclude = $true
                break
            }
        }
        
        if ($shouldExclude) {
            Write-Verbose "Skipping: $($file.Name)"
            $skipped++
        }
        else {
            Copy-Item $file.FullName -Destination $Destination -Force
            $copied++
        }
    }
    
    return @{ Copied = $copied; Skipped = $skipped }
}

# Copy net48
Write-Host "`nPackaging net48 (Revit 2023-2024)..." -ForegroundColor Cyan
$net48Result = Copy-DllsWithExclusions -Source $Net48Source -Destination $Net48Dest -Exclude $ExcludePatterns
Write-Host "  Copied: $($net48Result.Copied) DLLs" -ForegroundColor Green
Write-Host "  Skipped: $($net48Result.Skipped) DLLs (excluded)" -ForegroundColor Yellow

# Copy net8
Write-Host "`nPackaging net8 (Revit 2025-2026)..." -ForegroundColor Cyan
$net8Result = Copy-DllsWithExclusions -Source $Net8Source -Destination $Net8Dest -Exclude $ExcludePatterns
Write-Host "  Copied: $($net8Result.Copied) DLLs" -ForegroundColor Green
Write-Host "  Skipped: $($net8Result.Skipped) DLLs (excluded)" -ForegroundColor Yellow

# Verification
Write-Host "`n--- Verification ---" -ForegroundColor Cyan

Write-Host "`nnet48 contents:" -ForegroundColor White
Get-ChildItem "$Net48Dest\*.dll" | ForEach-Object { Write-Host "  $_" }

Write-Host "`nnet8 contents:" -ForegroundColor White
Get-ChildItem "$Net8Dest\*.dll" | ForEach-Object { Write-Host "  $_" }

# Check for main DLL
$mainDllName = (Get-ChildItem "$ProjectDir\*.csproj" | Select-Object -First 1).BaseName + ".dll"
$net48HasMain = Test-Path (Join-Path $Net48Dest $mainDllName)
$net8HasMain = Test-Path (Join-Path $Net8Dest $mainDllName)

Write-Host "`n--- Status ---" -ForegroundColor Cyan
if ($net48HasMain -and $net8HasMain) {
    Write-Host "SUCCESS: Both lib folders contain $mainDllName" -ForegroundColor Green
}
else {
    if (-not $net48HasMain) {
        Write-Host "WARNING: net48 missing $mainDllName" -ForegroundColor Red
    }
    if (-not $net8HasMain) {
        Write-Host "WARNING: net8 missing $mainDllName" -ForegroundColor Red
    }
}

Write-Host "`nPackage location: $LibPath" -ForegroundColor Cyan
