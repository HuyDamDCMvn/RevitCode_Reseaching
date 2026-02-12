# HD Extension for Revit

pyRevit extension with C# DLL backend for Revit 2025+

## Project Structure

```
RevitCode/
├── src/                        # Source code (PRIVATE)
│   ├── HD.Core/               # Shared library
│   ├── CommonFeature/         # Main tool
│   └── CheckCode/             # Code checking tool
│
├── HD.extension/               # Development extension (with source)
│   ├── lib/
│   │   ├── net8/              # Compiled DLLs
│   │   └── launcher_base.py   # Shared launcher
│   └── HD.tab/                # pyRevit tabs and panels
│
├── release/                    # Clean release (no source, no PDB)
│   └── HD.extension/          # Ready to distribute
│
├── dist/                       # Distribution packages
│   └── HD.extension-vX.X.X-YYYYMMDD.zip
│
├── build-release.ps1           # Build and prepare release
└── package-release.ps1         # Create ZIP for distribution
```

## Development

### Prerequisites
- .NET SDK 8.0+
- Revit 2025 or 2026
- pyRevit 4.8+

### Build Commands

```powershell
# Build for development (with PDB)
dotnet build src/HD.Core/HD.Core.csproj -c Release
dotnet build src/CommonFeature/CommonFeature.csproj -c Release

# Build release (without PDB) and package
.\build-release.ps1 -Version "1.0.0" -Clean
.\package-release.ps1 -Version "1.0.0"

# Or build and package in one step
.\package-release.ps1 -Version "1.0.0" -Build
```

## Distribution

### For Users
1. Download the ZIP from `dist/` folder
2. Extract to: `%APPDATA%\pyRevit-Master\extensions\`
3. Reload pyRevit

### What's Included in Release
- Compiled DLLs only (no source code, no debug symbols)
- Python launcher scripts
- Icons
- README and VERSION info

### What's NOT Included
- C# source code (.cs files)
- Debug symbols (.pdb files)
- Development files

## Security

- Source code is in `src/` - **DO NOT distribute**
- Release folder contains only compiled binaries
- `.gitignore` excludes `release/` and `dist/` from version control

## Adding New Tools

1. Create new project in `src/NewTool/`
2. Reference `HD.Core` for shared functionality
3. Create pushbutton in `HD.extension/HD.tab/`
4. Use `launcher_base.py` for minimal script:

```python
from launcher_base import launch_dll
launch_dll(
    dll_name="NewTool.dll",
    namespace="NewTool",
    method="Run"
)
```

## License

Proprietary - All rights reserved
