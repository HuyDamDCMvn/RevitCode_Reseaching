---
name: build-package-pyrevit-multiversion
description: Build C# DLLs for multiple Revit versions (net48 + net8.0-windows) and package into pyRevit extension structure. Use when shipping a pyRevit extension that loads correct DLL per Revit version, building for Revit 2023-2026, or packaging DLLs into lib/net48 and lib/net8 folders.
---

# Multi-version Revit Build + Package into pyRevit

You are a multi-version Revit build engineer. Ship DLLs for Revit 2023–2026 via pyRevit.

## Hard Rules

| Revit Version | Target Framework | Output Folder |
|---------------|------------------|---------------|
| 2023–2024 | `net48` | `lib/net48/` |
| 2025–2026 | `net8.0-windows` | `lib/net8/` |

- **Always** produce BOTH targets
- pyRevit script is a thin launcher only (no business logic)
- Each lib folder must be self-contained (DLL + all dependencies)

## Build Layout

### Project Structure

```
src/
  YourProject/
    YourProject.csproj      # Multi-target csproj
    Entry.cs                # Stable entrypoint
    ...
lib/
  Revit2024/                # net48 API references
    RevitAPI.dll
    RevitAPIUI.dll
  Revit2025/                # net8 API references
    RevitAPI.dll
    RevitAPIUI.dll
```

### .csproj Multi-targeting

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
    <UseWPF>true</UseWPF>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  
  <!-- Revit API references for net48 (2023–2024) -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <Reference Include="RevitAPI">
      <HintPath>..\..\lib\Revit2024\RevitAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>..\..\lib\Revit2024\RevitAPIUI.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
  
  <!-- Revit API references for net8 (2025–2026) -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
    <Reference Include="RevitAPI">
      <HintPath>..\..\lib\Revit2025\RevitAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>..\..\lib\Revit2025\RevitAPIUI.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

### Build Command

```bash
dotnet build -c Release
# Outputs:
#   bin/Release/net48/YourProject.dll
#   bin/Release/net8.0-windows/YourProject.dll
```

## Packaging Steps

### Target pyRevit Layout

```
YourExt.extension/
  lib/
    net48/              # Self-contained for Revit 2023–2024
      YourProject.dll
      Dependency1.dll
      Dependency2.dll
    net8/               # Self-contained for Revit 2025–2026
      YourProject.dll
      Dependency1.dll
      Dependency2.dll
  YourTab.tab/
    YourPanel.panel/
      YourTool.pushbutton/
        script.py       # Thin launcher only
        icon.png
```

### Copy Script (PowerShell)

```powershell
$ProjectDir = "src\YourProject"
$ExtPath = "deploy\YourExt.extension\lib"

# Clean destination
Remove-Item "$ExtPath\net48\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$ExtPath\net8\*" -Recurse -Force -ErrorAction SilentlyContinue

# Copy net48 output (Revit 2023–2024)
Copy-Item "$ProjectDir\bin\Release\net48\*" "$ExtPath\net48\" -Recurse -Force

# Copy net8.0-windows output (Revit 2025–2026)
Copy-Item "$ProjectDir\bin\Release\net8.0-windows\*" "$ExtPath\net8\" -Recurse -Force

Write-Host "Packaged to $ExtPath"
```

### Copy Script (Bash)

```bash
PROJECT_DIR="src/YourProject"
EXT_PATH="deploy/YourExt.extension/lib"

# Clean destination
rm -rf "$EXT_PATH/net48/"* "$EXT_PATH/net8/"*

# Copy outputs
cp -r "$PROJECT_DIR/bin/Release/net48/"* "$EXT_PATH/net48/"
cp -r "$PROJECT_DIR/bin/Release/net8.0-windows/"* "$EXT_PATH/net8/"

echo "Packaged to $EXT_PATH"
```

## Thin Launcher Template

Create `script.py` in each pushbutton folder:

```python
# pyRevit thin launcher - NO business logic
from pyrevit import HOST_APP, script, forms
import clr
import os

# 1. Detect Revit version
revit_version = HOST_APP.version

# 2. Select framework folder
if revit_version in [2023, 2024]:
    framework = "net48"
elif revit_version in [2025, 2026]:
    framework = "net8"
else:
    forms.alert(f"Unsupported Revit version: {revit_version}", title="Version Error")
    script.exit()

# 3. Build DLL path
ext_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
dll_path = os.path.join(ext_dir, "lib", framework, "YourProject.dll")

# 4. Validate and load
if not os.path.exists(dll_path):
    forms.alert(
        f"DLL not found.\n\n"
        f"Revit version: {revit_version}\n"
        f"Framework: {framework}\n"
        f"Expected path: {dll_path}\n\n"
        "Ensure DLL was deployed to correct lib folder.",
        title="Load Error"
    )
    script.exit()

clr.AddReferenceToFileAndPath(dll_path)

# 5. Call entry point
from YourNamespace import Entry
Entry.ShowTool(__revit__)
```

## Verification Checklist

Copy and complete this checklist for each release:

```
Build Verification:
- [ ] dotnet build -c Release succeeds (no errors)
- [ ] bin/Release/net48/ contains DLL + all dependencies
- [ ] bin/Release/net8.0-windows/ contains DLL + all dependencies
- [ ] Both outputs have identical public API

Package Verification:
- [ ] lib/net48/ is self-contained (no missing deps)
- [ ] lib/net8/ is self-contained (no missing deps)
- [ ] No bin/Debug or bin/Release paths in production
- [ ] No .pdb files shipped (unless debugging)

Runtime Verification per Version:
- [ ] Revit 2023: loads net48 DLL → runs successfully
- [ ] Revit 2024: loads net48 DLL → runs successfully
- [ ] Revit 2025: loads net8 DLL → runs successfully
- [ ] Revit 2026: loads net8 DLL → runs successfully

Error Handling Verification:
- [ ] Missing DLL → shows path in error message
- [ ] Missing dependency → shows actionable error
- [ ] Unsupported Revit version → shows clear message
- [ ] Entry point mismatch → shows namespace hint
```

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| DLL not found | Wrong path traversal | Check `ext_dir` calculation (4 levels up from script.py) |
| Missing dependency | Deps not copied | Copy ALL files from bin/Release/targetfx/ |
| TypeLoadException | Wrong framework loaded | Verify version→framework mapping in launcher |
| Entry not found | Namespace mismatch | Match `from X import Entry` to DLL namespace |
| Method not found | API version mismatch | Check Revit API version in HintPath |

## Version-Specific API Handling

If API differs between versions, use conditional compilation:

```csharp
public static class VersionHelper
{
#if NET48
    // Revit 2023–2024 implementation
    public static void DoSomething() { /* net48 code */ }
#else
    // Revit 2025–2026 implementation
    public static void DoSomething() { /* net8 code */ }
#endif
}
```

Or use runtime detection with the api-diff-compat-shim skill for cleaner isolation.

## Output Summary

A successful build produces:

1. **Two DLL sets** in bin/Release/ (net48 + net8.0-windows)
2. **Self-contained lib folders** in pyRevit extension
3. **Thin launcher** (< 60 lines, version detection only)
4. **Verified on all 4 Revit versions** (2023, 2024, 2025, 2026)
