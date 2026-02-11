# pyRevit Thin Launcher Examples

## Example 1: Basic Tool Launcher

Tool that shows a modeless WPF window.

**Configuration:**
- DLL: `TicketAssistant.dll`
- Namespace: `DCM.TicketAssistant`
- Entry: `ShowTicketAssistant`

**script.py:**

```python
# -*- coding: utf-8 -*-
"""TicketAssistant launcher."""
from pyrevit import HOST_APP, script, forms
import clr
import os

DLL_NAME = "TicketAssistant.dll"
NAMESPACE = "DCM.TicketAssistant"
ENTRY_METHOD = "ShowTicketAssistant"
VERSION_MAP = {2023: "net48", 2024: "net48", 2025: "net8", 2026: "net8"}
DEPTH = 4

def get_extension_root():
    path = os.path.dirname(__file__)
    for _ in range(DEPTH):
        path = os.path.dirname(path)
    return path

def main():
    v = HOST_APP.version
    fw = VERSION_MAP.get(v)
    
    if not fw:
        forms.alert(f"Unsupported Revit {v}", title="Error", exitscript=True)
    
    dll = os.path.join(get_extension_root(), "lib", fw, DLL_NAME)
    
    if not os.path.exists(dll):
        forms.alert(
            f"DLL not found.\n\nRevit: {v}\nPath: {dll}",
            title="Error",
            exitscript=True
        )
    
    clr.AddReferenceToFileAndPath(dll)
    exec(f"from {NAMESPACE} import Entry; Entry.{ENTRY_METHOD}(__revit__)")

if __name__ == "__main__" or True:
    main()
```

---

## Example 2: Multi-Tool Extension

Extension with multiple tools sharing the same DLL.

**Folder structure:**

```
HD.extension/
├── lib/
│   ├── net48/
│   │   └── HDTools.dll
│   └── net8/
│       └── HDTools.dll
├── HD.tab/
│   └── Tools.panel/
│       ├── RoomAnalyzer.pushbutton/
│       │   ├── script.py        # Calls Entry.ShowRoomAnalyzer
│       │   └── icon.png
│       ├── WallScheduler.pushbutton/
│       │   ├── script.py        # Calls Entry.ShowWallScheduler
│       │   └── icon.png
│       └── Export.pushbutton/
│           ├── script.py        # Calls Entry.RunExport
│           └── icon.png
```

**Shared base for all launchers:**

```python
# -*- coding: utf-8 -*-
from pyrevit import HOST_APP, script, forms
import clr
import os

DLL_NAME = "HDTools.dll"
NAMESPACE = "HD.Tools"
ENTRY_METHOD = "ShowRoomAnalyzer"  # ← Only this differs per tool
VERSION_MAP = {2023: "net48", 2024: "net48", 2025: "net8", 2026: "net8"}
DEPTH = 4

# ... rest identical
```

---

## Example 3: Nested Panel Structure

When script.py is deeper in the folder hierarchy.

**Folder structure (DEPTH = 5):**

```
HD.extension/
├── lib/
│   └── ...
├── HD.tab/
│   └── Analysis.panel/
│       └── Advanced.stack/          # Extra level!
│           └── DeepTool.pushbutton/
│               └── script.py        # DEPTH = 5
```

**script.py:**

```python
# -*- coding: utf-8 -*-
from pyrevit import HOST_APP, script, forms
import clr
import os

DLL_NAME = "HDTools.dll"
NAMESPACE = "HD.Tools"
ENTRY_METHOD = "ShowDeepTool"
VERSION_MAP = {2023: "net48", 2024: "net48", 2025: "net8", 2026: "net8"}
DEPTH = 5  # ← Adjusted for extra stack folder level

# ... rest identical
```

---

## Example 4: Debug-friendly Launcher

Enhanced version with additional diagnostics for development.

```python
# -*- coding: utf-8 -*-
"""Debug-friendly launcher with verbose output."""
from pyrevit import HOST_APP, script, forms
import clr
import os
import sys

DLL_NAME = "DevTool.dll"
NAMESPACE = "DCM.DevTool"
ENTRY_METHOD = "ShowTool"
VERSION_MAP = {2023: "net48", 2024: "net48", 2025: "net8", 2026: "net8"}
DEPTH = 4
DEBUG = True  # Set False for production

def log(msg):
    if DEBUG:
        print(f"[Launcher] {msg}")

def get_extension_root():
    path = os.path.dirname(__file__)
    log(f"Script location: {path}")
    for i in range(DEPTH):
        path = os.path.dirname(path)
        log(f"  Level {i+1}: {path}")
    return path

def main():
    log(f"Python version: {sys.version}")
    log(f"IronPython: {'IronPython' in sys.version}")
    
    v = HOST_APP.version
    log(f"Revit version: {v}")
    
    fw = VERSION_MAP.get(v)
    if not fw:
        forms.alert(f"Unsupported Revit {v}", title="Error", exitscript=True)
    log(f"Framework: {fw}")
    
    ext_root = get_extension_root()
    log(f"Extension root: {ext_root}")
    
    lib_path = os.path.join(ext_root, "lib")
    log(f"Lib folder exists: {os.path.exists(lib_path)}")
    if os.path.exists(lib_path):
        log(f"Lib contents: {os.listdir(lib_path)}")
    
    dll = os.path.join(ext_root, "lib", fw, DLL_NAME)
    log(f"DLL path: {dll}")
    log(f"DLL exists: {os.path.exists(dll)}")
    
    if not os.path.exists(dll):
        fw_folder = os.path.join(ext_root, "lib", fw)
        contents = os.listdir(fw_folder) if os.path.exists(fw_folder) else "FOLDER MISSING"
        forms.alert(
            f"DLL not found.\n\n"
            f"Revit: {v}\n"
            f"Framework: {fw}\n"
            f"Path: {dll}\n\n"
            f"Folder contents: {contents}",
            title="Error",
            exitscript=True
        )
    
    log("Loading assembly...")
    clr.AddReferenceToFileAndPath(dll)
    log("Assembly loaded successfully")
    
    log(f"Calling {NAMESPACE}.Entry.{ENTRY_METHOD}")
    exec(f"from {NAMESPACE} import Entry; Entry.{ENTRY_METHOD}(__revit__)")
    log("Entry method completed")

if __name__ == "__main__" or True:
    main()
```

---

## Example 5: CPython Launcher (NumPy dependency)

When your DLL requires CPython-only packages.

```python
#! python3
# -*- coding: utf-8 -*-
"""CPython launcher for DLL with NumPy dependency."""
from pyrevit import HOST_APP, script, forms
import clr
import os

# Note: CPython required because DLL uses NumPy interop
DLL_NAME = "DataAnalyzer.dll"
NAMESPACE = "DCM.DataAnalyzer"
ENTRY_METHOD = "ShowAnalyzer"
VERSION_MAP = {2023: "net48", 2024: "net48", 2025: "net8", 2026: "net8"}
DEPTH = 4

# ... rest identical to standard template
```

---

## Example 6: Error Message Patterns

### Good Error Messages

```python
# Missing DLL
forms.alert(
    f"Cannot find required DLL.\n\n"
    f"Revit version: {v}\n"
    f"Framework: {fw}\n"
    f"Expected path:\n{dll_path}\n\n"
    "Troubleshooting:\n"
    "1. Build project: dotnet build -c Release\n"
    f"2. Copy output to lib/{fw}/ folder\n"
    "3. Verify DLL_NAME matches actual filename",
    title="DLL Not Found"
)

# Load failure
forms.alert(
    f"Failed to load assembly.\n\n"
    f"DLL: {dll_path}\n"
    f"Error: {ex}\n\n"
    "Common causes:\n"
    f"1. Missing dependency in lib/{fw}/ folder\n"
    "2. DLL built for wrong framework\n"
    "3. Corrupted DLL file\n\n"
    "Check Revit Output window for details.",
    title="Assembly Load Failed"
)

# Entry not found
forms.alert(
    f"Entry point not found.\n\n"
    f"Looking for: {NAMESPACE}.Entry.{ENTRY_METHOD}\n"
    f"Error: {ex}\n\n"
    "Ensure DLL defines:\n"
    f"namespace {NAMESPACE}\n"
    "{\n"
    "    public static class Entry\n"
    "    {\n"
    f"        public static void {ENTRY_METHOD}(UIApplication uiapp)\n"
    "    }\n"
    "}",
    title="Entry Method Not Found"
)
```

### Bad Error Messages (Avoid)

```python
# ❌ Too vague
forms.alert("Error loading DLL")

# ❌ No context
forms.alert("File not found")

# ❌ No actionable info
forms.alert("Something went wrong")

# ❌ Technical jargon without guidance
forms.alert(f"TypeLoadException: {ex}")
```
