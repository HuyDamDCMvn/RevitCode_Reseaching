# Build & Package Examples

## Example 1: Basic Tool Extension

### Project Structure

```
RevitToolkit/
  src/
    RevitToolkit/
      RevitToolkit.csproj
      Entry.cs
      Tools/
        WallAnalyzer.cs
  lib/
    Revit2024/
      RevitAPI.dll
      RevitAPIUI.dll
    Revit2025/
      RevitAPI.dll
      RevitAPIUI.dll
  deploy/
    RevitToolkit.extension/
      lib/
        net48/
        net8/
      DCM.tab/
        Analysis.panel/
          WallAnalyzer.pushbutton/
            script.py
            icon.png
```

### RevitToolkit.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
    <UseWPF>true</UseWPF>
    <LangVersion>latest</LangVersion>
    <AssemblyName>RevitToolkit</AssemblyName>
    <RootNamespace>DCM.RevitToolkit</RootNamespace>
  </PropertyGroup>
  
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
  
  <!-- Third-party dependencies -->
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
```

### Entry.cs

```csharp
using Autodesk.Revit.UI;

namespace DCM.RevitToolkit
{
    public static class Entry
    {
        public static void Run(UIApplication uiapp)
        {
            // Default entry point
            ShowWallAnalyzer(uiapp);
        }

        public static void ShowWallAnalyzer(UIApplication uiapp)
        {
            var tool = new Tools.WallAnalyzer();
            tool.Execute(uiapp);
        }

        public static void ShowTool(UIApplication uiapp)
        {
            ShowWallAnalyzer(uiapp);
        }
    }
}
```

### script.py (Thin Launcher)

```python
# pyRevit thin launcher for WallAnalyzer
from pyrevit import HOST_APP, script, forms
import clr
import os

revit_version = HOST_APP.version

if revit_version in [2023, 2024]:
    framework = "net48"
elif revit_version in [2025, 2026]:
    framework = "net8"
else:
    forms.alert(f"Unsupported Revit version: {revit_version}", title="Version Error")
    script.exit()

ext_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
dll_path = os.path.join(ext_dir, "lib", framework, "RevitToolkit.dll")

if not os.path.exists(dll_path):
    forms.alert(
        f"DLL not found.\n\n"
        f"Revit version: {revit_version}\n"
        f"Framework: {framework}\n"
        f"Expected path: {dll_path}",
        title="Load Error"
    )
    script.exit()

clr.AddReferenceToFileAndPath(dll_path)

from DCM.RevitToolkit import Entry
Entry.ShowWallAnalyzer(__revit__)
```

---

## Example 2: Extension with NuGet Dependencies

When your DLL depends on NuGet packages, ALL dependencies must be copied to lib folders.

### Build Output (after dotnet build -c Release)

```
bin/Release/net48/
  RevitToolkit.dll
  Newtonsoft.Json.dll        # NuGet dependency
  System.Memory.dll          # Transitive dependency

bin/Release/net8.0-windows/
  RevitToolkit.dll
  Newtonsoft.Json.dll        # Same package, different build
```

### Package Script (PowerShell)

```powershell
$SrcDir = "src\RevitToolkit\bin\Release"
$ExtLib = "deploy\RevitToolkit.extension\lib"

# Ensure directories exist
New-Item -ItemType Directory -Path "$ExtLib\net48" -Force | Out-Null
New-Item -ItemType Directory -Path "$ExtLib\net8" -Force | Out-Null

# Copy ALL files (DLL + dependencies)
Get-ChildItem "$SrcDir\net48\*.dll" | Copy-Item -Destination "$ExtLib\net48\" -Force
Get-ChildItem "$SrcDir\net8.0-windows\*.dll" | Copy-Item -Destination "$ExtLib\net8\" -Force

# Exclude Revit API DLLs if accidentally included
Remove-Item "$ExtLib\net48\RevitAPI*.dll" -ErrorAction SilentlyContinue
Remove-Item "$ExtLib\net8\RevitAPI*.dll" -ErrorAction SilentlyContinue

Write-Host "Package complete. Verify lib folders:"
Get-ChildItem "$ExtLib\net48" | Select-Object Name
Get-ChildItem "$ExtLib\net8" | Select-Object Name
```

---

## Example 3: MSBuild Post-Build Auto-Package

Add to .csproj for automatic packaging after build:

```xml
<Target Name="PackageToPyRevit" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
  <PropertyGroup>
    <ExtLibPath>$(SolutionDir)deploy\RevitToolkit.extension\lib</ExtLibPath>
  </PropertyGroup>
  
  <!-- Copy net48 -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <Net48Files Include="$(OutputPath)*.dll" />
  </ItemGroup>
  <Copy SourceFiles="@(Net48Files)" 
        DestinationFolder="$(ExtLibPath)\net48" 
        Condition="'$(TargetFramework)' == 'net48'" />
  
  <!-- Copy net8 -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
    <Net8Files Include="$(OutputPath)*.dll" />
  </ItemGroup>
  <Copy SourceFiles="@(Net8Files)" 
        DestinationFolder="$(ExtLibPath)\net8" 
        Condition="'$(TargetFramework)' == 'net8.0-windows'" />
</Target>
```

---

## Example 4: CI/CD Build Script

### GitHub Actions Workflow

```yaml
name: Build Multi-version DLLs

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: windows-latest
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0.x'
    
    - name: Restore dependencies
      run: dotnet restore src/RevitToolkit/RevitToolkit.csproj
    
    - name: Build both targets
      run: dotnet build src/RevitToolkit/RevitToolkit.csproj -c Release
    
    - name: Package into pyRevit extension
      run: |
        $ExtLib = "deploy/RevitToolkit.extension/lib"
        New-Item -ItemType Directory -Path "$ExtLib/net48" -Force
        New-Item -ItemType Directory -Path "$ExtLib/net8" -Force
        Copy-Item "src/RevitToolkit/bin/Release/net48/*.dll" "$ExtLib/net48/" -Force
        Copy-Item "src/RevitToolkit/bin/Release/net8.0-windows/*.dll" "$ExtLib/net8/" -Force
      shell: pwsh
    
    - name: Upload extension artifact
      uses: actions/upload-artifact@v4
      with:
        name: RevitToolkit-extension
        path: deploy/RevitToolkit.extension/
```

---

## Verification Commands

### Check Build Outputs

```powershell
# Verify both frameworks built
Test-Path "bin\Release\net48\RevitToolkit.dll"       # Should be True
Test-Path "bin\Release\net8.0-windows\RevitToolkit.dll"  # Should be True

# List all outputs
Get-ChildItem "bin\Release\net48\*.dll" | Select Name
Get-ChildItem "bin\Release\net8.0-windows\*.dll" | Select Name
```

### Check Package Contents

```powershell
# Verify lib folders are self-contained
$net48 = Get-ChildItem "deploy\*.extension\lib\net48\*.dll" | Select -Expand Name
$net8 = Get-ChildItem "deploy\*.extension\lib\net8\*.dll" | Select -Expand Name

Write-Host "net48 DLLs: $($net48 -join ', ')"
Write-Host "net8 DLLs: $($net8 -join ', ')"

# Both should have same dependency set (except framework-specific)
```

### Verify Entry Point

```powershell
# Use ILSpy or ildasm to verify Entry class exists
# Or quick .NET reflection test:
Add-Type -Path "bin\Release\net48\RevitToolkit.dll"
[DCM.RevitToolkit.Entry].GetMethods() | Select Name
# Should show: Run, ShowTool, ShowWallAnalyzer, etc.
```
