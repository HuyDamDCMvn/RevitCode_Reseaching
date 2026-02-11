#Requires -Version 5.1
<#
.SYNOPSIS
    Verify pyRevit extension package is complete and ready for deployment.

.DESCRIPTION
    Runs verification checklist against a packaged pyRevit extension:
    - Checks lib/net48 and lib/net8 folders exist and contain DLLs
    - Verifies main DLL exists in both folders
    - Checks for common missing dependencies
    - Validates launcher scripts exist

.PARAMETER ExtensionPath
    Path to the pyRevit extension root (e.g., YourExt.extension).

.PARAMETER MainDllName
    Name of the main DLL (e.g., "RevitToolkit.dll").

.EXAMPLE
    .\verify-package.ps1 -ExtensionPath "deploy\MyExt.extension" -MainDllName "MyProject.dll"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExtensionPath,

    [Parameter(Mandatory = $true)]
    [string]$MainDllName
)

$ErrorActionPreference = "Stop"

# Results tracking
$checks = @()

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Details = "")
    $script:checks += [PSCustomObject]@{
        Name    = $Name
        Passed  = $Passed
        Details = $Details
    }
}

Write-Host "=== pyRevit Extension Verification ===" -ForegroundColor Cyan
Write-Host "Extension: $ExtensionPath" -ForegroundColor White
Write-Host "Main DLL: $MainDllName`n" -ForegroundColor White

# 1. Check extension path exists
$extExists = Test-Path $ExtensionPath
Add-Check -Name "Extension path exists" -Passed $extExists -Details $ExtensionPath

if (-not $extExists) {
    Write-Host "FATAL: Extension path not found. Aborting." -ForegroundColor Red
    exit 1
}

# 2. Check lib folders
$libPath = Join-Path $ExtensionPath "lib"
$net48Path = Join-Path $libPath "net48"
$net8Path = Join-Path $libPath "net8"

Add-Check -Name "lib/net48 folder exists" -Passed (Test-Path $net48Path) -Details $net48Path
Add-Check -Name "lib/net8 folder exists" -Passed (Test-Path $net8Path) -Details $net8Path

# 3. Check main DLL in both folders
$net48MainDll = Join-Path $net48Path $MainDllName
$net8MainDll = Join-Path $net8Path $MainDllName

Add-Check -Name "Main DLL in net48" -Passed (Test-Path $net48MainDll) -Details $net48MainDll
Add-Check -Name "Main DLL in net8" -Passed (Test-Path $net8MainDll) -Details $net8MainDll

# 4. Count DLLs in each folder
$net48Dlls = @(Get-ChildItem "$net48Path\*.dll" -ErrorAction SilentlyContinue)
$net8Dlls = @(Get-ChildItem "$net8Path\*.dll" -ErrorAction SilentlyContinue)

Add-Check -Name "net48 has DLLs" -Passed ($net48Dlls.Count -gt 0) -Details "$($net48Dlls.Count) DLLs found"
Add-Check -Name "net8 has DLLs" -Passed ($net8Dlls.Count -gt 0) -Details "$($net8Dlls.Count) DLLs found"

# 5. Check for Revit API DLLs (should NOT be present)
$net48HasRevitApi = Test-Path (Join-Path $net48Path "RevitAPI.dll")
$net8HasRevitApi = Test-Path (Join-Path $net8Path "RevitAPI.dll")

Add-Check -Name "net48 excludes RevitAPI.dll" -Passed (-not $net48HasRevitApi) -Details "Should not ship Revit API"
Add-Check -Name "net8 excludes RevitAPI.dll" -Passed (-not $net8HasRevitApi) -Details "Should not ship Revit API"

# 6. Check for launcher scripts
$tabFolders = Get-ChildItem "$ExtensionPath\*.tab" -Directory -ErrorAction SilentlyContinue
$launcherScripts = @()

foreach ($tab in $tabFolders) {
    $scripts = Get-ChildItem "$($tab.FullName)\*.panel\*.pushbutton\script.py" -Recurse -ErrorAction SilentlyContinue
    $launcherScripts += $scripts
}

Add-Check -Name "Launcher scripts exist" -Passed ($launcherScripts.Count -gt 0) -Details "$($launcherScripts.Count) script.py files found"

# 7. Check launcher scripts reference correct DLL
$launcherIssues = @()
foreach ($script in $launcherScripts) {
    $content = Get-Content $script.FullName -Raw
    if ($content -notmatch [regex]::Escape($MainDllName.Replace(".dll", ""))) {
        $launcherIssues += $script.FullName
    }
}

Add-Check -Name "Launchers reference main DLL" -Passed ($launcherIssues.Count -eq 0) -Details "$(($launcherScripts.Count - $launcherIssues.Count))/$($launcherScripts.Count) correct"

# 8. Compare dependency sets
$net48DllNames = $net48Dlls | ForEach-Object { $_.Name } | Sort-Object
$net8DllNames = $net8Dlls | ForEach-Object { $_.Name } | Sort-Object

$dllDiff = Compare-Object $net48DllNames $net8DllNames -PassThru -ErrorAction SilentlyContinue
$depsMatch = ($null -eq $dllDiff -or $dllDiff.Count -eq 0)

Add-Check -Name "Dependency sets match" -Passed $depsMatch -Details $(if ($depsMatch) { "Identical" } else { "Differences found" })

# Print results
Write-Host "`n=== Verification Results ===" -ForegroundColor Cyan

$passed = 0
$failed = 0

foreach ($check in $checks) {
    $status = if ($check.Passed) { "[PASS]"; $passed++ } else { "[FAIL]"; $failed++ }
    $color = if ($check.Passed) { "Green" } else { "Red" }
    
    Write-Host "$status $($check.Name)" -ForegroundColor $color
    if ($check.Details -and -not $check.Passed) {
        Write-Host "       $($check.Details)" -ForegroundColor Gray
    }
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })

# List contents
Write-Host "`n=== Package Contents ===" -ForegroundColor Cyan

Write-Host "`nnet48/ (Revit 2023-2024):" -ForegroundColor White
$net48Dlls | ForEach-Object { Write-Host "  $($_.Name)" }

Write-Host "`nnet8/ (Revit 2025-2026):" -ForegroundColor White
$net8Dlls | ForEach-Object { Write-Host "  $($_.Name)" }

if ($launcherScripts.Count -gt 0) {
    Write-Host "`nLauncher scripts:" -ForegroundColor White
    $launcherScripts | ForEach-Object { 
        $relativePath = $_.FullName.Replace($ExtensionPath, "").TrimStart("\")
        Write-Host "  $relativePath" 
    }
}

# Exit code
if ($failed -gt 0) {
    Write-Host "`nVerification FAILED. Fix issues before deployment." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "`nVerification PASSED. Ready for deployment." -ForegroundColor Green
    exit 0
}
