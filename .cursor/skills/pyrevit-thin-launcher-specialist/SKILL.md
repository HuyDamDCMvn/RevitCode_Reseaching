---
name: pyrevit-thin-launcher-specialist
description: pyRevit 6.0.0 specialist for thin launcher scripts that load C# DLLs by Revit version. Use when creating pyRevit pushbutton scripts, packaging DLLs for Revit 2023-2026, generating version-aware launchers, or troubleshooting DLL loading issues.
---

# pyRevit 6.0.0 Thin Launcher Specialist

You are a pyRevit 6.0.0 specialist. Generate robust thin launcher scripts and packaging guidance for loading C# DLLs by Revit version.

## Hard Rules (Non-negotiable)

| Rule | Requirement |
|------|-------------|
| Launcher scope | Python script is thin launcher **only** — no business logic |
| Version mapping | 2023–2024 → `net48`, 2025–2026 → `net8` |
| Error messages | Must include: Revit version, attempted path, actionable fix |
| Engine default | IronPython unless numpy/pandas required |
| Path resolution | Always absolute paths via `os.path.join` |

## Four Responsibilities Only

The launcher does exactly these four things:

1. **Detect** Revit major version
2. **Select** DLL path (`net48` vs `net8`)
3. **Load** assembly with validation
4. **Call** DLL Entry method

Nothing else belongs in Python.

## Robust Launcher Template

```python
# -*- coding: utf-8 -*-
"""pyRevit thin launcher - version-aware DLL loading."""
from pyrevit import HOST_APP, script, forms
import clr
import os

# ══════════════════════════════════════════════════════════════════════════════
# CONFIGURATION - Edit these values
# ══════════════════════════════════════════════════════════════════════════════
DLL_NAME = "YourProject.dll"
NAMESPACE = "YourNamespace"
ENTRY_METHOD = "ShowTool"  # Run | ShowTool | ShowTicketAssistant

# Version → Framework mapping (Hard Rule)
VERSION_MAP = {
    2023: "net48",
    2024: "net48",
    2025: "net8",
    2026: "net8",
}

# Folder depth: script.py → extension root (default: 4)
# HD.extension / HD.tab / Panel.panel / Tool.pushbutton / script.py
DEPTH = 4


# ══════════════════════════════════════════════════════════════════════════════
# LAUNCHER LOGIC - Do not modify
# ══════════════════════════════════════════════════════════════════════════════
def get_extension_root():
    """Traverse up DEPTH folders to extension root containing lib/."""
    path = os.path.dirname(__file__)
    for _ in range(DEPTH):
        path = os.path.dirname(path)
    return path


def show_error(title, message):
    """Display error dialog and exit."""
    forms.alert(message, title=title, exitscript=True)


def main():
    # 1. Detect Revit version
    revit_version = HOST_APP.version
    
    # 2. Map to framework
    framework = VERSION_MAP.get(revit_version)
    if not framework:
        supported = sorted(VERSION_MAP.keys())
        show_error(
            "Unsupported Revit Version",
            f"Revit {revit_version} is not supported.\n\n"
            f"Supported versions: {supported}\n\n"
            "If a new Revit version was released, update VERSION_MAP in script.py"
        )
    
    # 3. Build and validate DLL path
    ext_root = get_extension_root()
    dll_path = os.path.join(ext_root, "lib", framework, DLL_NAME)
    
    if not os.path.exists(dll_path):
        show_error(
            "DLL Not Found",
            f"Cannot find required DLL.\n\n"
            f"Revit version: {revit_version}\n"
            f"Framework: {framework}\n"
            f"Expected path:\n{dll_path}\n\n"
            "Troubleshooting:\n"
            f"1. Build project and copy to lib/{framework}/\n"
            f"2. Check DLL_NAME matches actual filename: {DLL_NAME}\n"
            "3. Verify extension folder structure\n"
            f"4. Check lib folder exists: {os.path.join(ext_root, 'lib')}"
        )
    
    # 4. Load assembly
    try:
        clr.AddReferenceToFileAndPath(dll_path)
    except Exception as ex:
        show_error(
            "Assembly Load Failed",
            f"Failed to load DLL.\n\n"
            f"Path: {dll_path}\n"
            f"Error: {ex}\n\n"
            "Troubleshooting:\n"
            f"1. Copy ALL dependencies to lib/{framework}/ folder\n"
            f"2. Ensure DLL built for {framework} framework\n"
            "3. Check Revit Output window for detailed error\n"
            "4. Verify no version mismatch in dependencies"
        )
    
    # 5. Import and call entry point
    try:
        exec(f"from {NAMESPACE} import Entry")
        entry_class = eval("Entry")
        method = getattr(entry_class, ENTRY_METHOD)
        method(__revit__)
    except AttributeError as ex:
        show_error(
            "Entry Method Not Found",
            f"Cannot find entry method.\n\n"
            f"Namespace: {NAMESPACE}\n"
            f"Expected: Entry.{ENTRY_METHOD}(UIApplication)\n"
            f"Error: {ex}\n\n"
            "Ensure DLL exposes:\n"
            f"namespace {NAMESPACE}\n"
            "{\n"
            "    public static class Entry\n"
            "    {\n"
            f"        public static void {ENTRY_METHOD}(UIApplication uiapp) {{ }}\n"
            "    }\n"
            "}"
        )
    except Exception as ex:
        show_error(
            "Entry Call Failed",
            f"Error executing entry method.\n\n"
            f"Method: {NAMESPACE}.Entry.{ENTRY_METHOD}\n"
            f"Error type: {type(ex).__name__}\n"
            f"Error: {ex}\n\n"
            "Check Revit journal/Output window for stack trace."
        )


if __name__ == "__main__" or True:
    main()
```

## Minimal Launcher (< 35 lines)

For simpler deployments when error detail is less critical:

```python
# -*- coding: utf-8 -*-
from pyrevit import HOST_APP, script, forms
import clr, os

DLL_NAME = "YourProject.dll"
NAMESPACE = "YourNamespace"
ENTRY_METHOD = "ShowTool"

v = HOST_APP.version
fw = {2023: "net48", 2024: "net48", 2025: "net8", 2026: "net8"}.get(v)

if not fw:
    forms.alert(f"Unsupported Revit {v}", title="Error", exitscript=True)

ext = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
dll = os.path.join(ext, "lib", fw, DLL_NAME)

if not os.path.exists(dll):
    forms.alert(f"DLL not found\nRevit: {v}\nPath: {dll}", title="Error", exitscript=True)

clr.AddReferenceToFileAndPath(dll)
exec(f"from {NAMESPACE} import Entry; Entry.{ENTRY_METHOD}(__revit__)")
```

## Extension Packaging Layout

```
YourExt.extension/
├── lib/
│   ├── net48/                    # Revit 2023–2024 (self-contained)
│   │   ├── YourProject.dll
│   │   ├── Newtonsoft.Json.dll   # All dependencies
│   │   └── OtherDep.dll
│   └── net8/                     # Revit 2025–2026 (self-contained)
│       ├── YourProject.dll
│       ├── Newtonsoft.Json.dll
│       └── OtherDep.dll
├── YourTab.tab/
│   └── YourPanel.panel/
│       ├── Tool1.pushbutton/
│       │   ├── script.py         # Thin launcher
│       │   └── icon.png
│       └── Tool2.pushbutton/
│           ├── script.py
│           └── icon.png
└── extension.json                # Optional metadata
```

## Folder Depth Calculation

Count levels from `script.py` up to extension root:

```
HD.extension/           ← EXTENSION ROOT (contains lib/)
└── HD.tab/             ← level 4
    └── Panel.panel/    ← level 3
        └── Tool.pushbutton/  ← level 2
            └── script.py     ← level 1 (DEPTH = 4)
```

Adjust DEPTH if structure differs (e.g., no tab folder → DEPTH = 3).

## CPython Variant

Only use when DLL requires numpy, pandas, or other CPython-only packages:

```python
#! python3
# -*- coding: utf-8 -*-
"""CPython launcher - use ONLY when required."""
# ... rest identical to standard template ...
```

**First line must be `#! python3`** to select CPython engine.

## Packaging Commands

### PowerShell (Windows)

```powershell
$BuildDir = "src\YourProject\bin\Release"
$ExtLib = "deploy\YourExt.extension\lib"

# Create folders if needed
New-Item -ItemType Directory -Force -Path "$ExtLib\net48", "$ExtLib\net8" | Out-Null

# Clean and copy
Remove-Item "$ExtLib\net48\*", "$ExtLib\net8\*" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$BuildDir\net48\*" "$ExtLib\net48\" -Recurse -Force
Copy-Item "$BuildDir\net8.0-windows\*" "$ExtLib\net8\" -Recurse -Force

Write-Host "✓ Packaged to $ExtLib"
```

### Bash (Linux/macOS)

```bash
BUILD_DIR="src/YourProject/bin/Release"
EXT_LIB="deploy/YourExt.extension/lib"

mkdir -p "$EXT_LIB/net48" "$EXT_LIB/net8"
rm -rf "$EXT_LIB/net48/"* "$EXT_LIB/net8/"*
cp -r "$BUILD_DIR/net48/"* "$EXT_LIB/net48/"
cp -r "$BUILD_DIR/net8.0-windows/"* "$EXT_LIB/net8/"

echo "✓ Packaged to $EXT_LIB"
```

## Entry Method Contract

DLL must expose stable entry API:

```csharp
namespace YourNamespace
{
    public static class Entry
    {
        // Choose one or more based on tool needs
        public static void Run(UIApplication uiapp) { }
        public static void ShowTool(UIApplication uiapp) { }
        public static void ShowTicketAssistant(UIApplication uiapp) { }
    }
}
```

| Method | Use Case |
|--------|----------|
| `Run` | One-shot command execution |
| `ShowTool` | Modeless tool window |
| `ShowTicketAssistant` | Ticket/support assistant |

## Verification Checklist

```
Launcher Verification:
- [ ] DLL_NAME matches actual filename
- [ ] NAMESPACE matches C# namespace
- [ ] ENTRY_METHOD matches Entry class method
- [ ] DEPTH correctly calculates extension root
- [ ] VERSION_MAP covers 2023, 2024 → net48
- [ ] VERSION_MAP covers 2025, 2026 → net8

Package Verification:
- [ ] lib/net48/ contains DLL + ALL dependencies
- [ ] lib/net8/ contains DLL + ALL dependencies
- [ ] No .pdb files (unless debugging)
- [ ] No bin/Debug paths in production

Error Message Verification:
- [ ] Unknown version → shows supported list
- [ ] Missing DLL → shows full attempted path
- [ ] Load failure → shows dependency hint
- [ ] Missing entry → shows expected signature

Runtime Verification:
- [ ] Revit 2023 loads net48 → works
- [ ] Revit 2024 loads net48 → works
- [ ] Revit 2025 loads net8 → works
- [ ] Revit 2026 loads net8 → works
```

## Troubleshooting Guide

| Symptom | Cause | Fix |
|---------|-------|-----|
| "DLL Not Found" | Wrong DEPTH | Count folders: script.py → extension root |
| "DLL Not Found" | Wrong DLL_NAME | Match exact filename including .dll |
| "Assembly Load Failed" | Missing dependency | Copy ALL files from bin/Release/{fw}/ |
| "Assembly Load Failed" | Framework mismatch | Check version→framework mapping |
| "Entry Method Not Found" | Namespace mismatch | Match NAMESPACE to C# namespace exactly |
| "Entry Method Not Found" | Method missing | Add public static void Method(UIApplication) |
| TypeLoadException | API version conflict | Rebuild for correct Revit API version |
| Silent failure | Exception swallowed | Check Revit Output window / journal |

## Anti-Patterns

```python
# ❌ BAD: Business logic in Python
def calculate_something():
    # This belongs in C# DLL
    pass

# ❌ BAD: Hardcoded paths
dll_path = "C:\\Users\\dev\\MyProject.dll"

# ❌ BAD: No validation before load
clr.AddReferenceToFileAndPath(dll_path)

# ❌ BAD: Vague error messages
forms.alert("Error loading DLL")

# ✅ GOOD: Actionable error with context
forms.alert(
    f"DLL not found.\n"
    f"Revit: {revit_version}\n"
    f"Path: {dll_path}\n"
    "Copy DLL to lib/{framework}/ folder."
)
```

## Additional Resources

- For building multi-target DLLs, see [build-package-pyrevit-multiversion](../build-package-pyrevit-multiversion/SKILL.md)
- For Entry class scaffolding, see [scaffold-entry-contract](../scaffold-entry-contract/SKILL.md)
- For API compatibility across versions, see [api-diff-compat-shim](../api-diff-compat-shim/SKILL.md)
