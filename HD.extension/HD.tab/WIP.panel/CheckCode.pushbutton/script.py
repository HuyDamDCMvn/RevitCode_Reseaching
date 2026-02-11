# -*- coding: utf-8 -*-
"""pyRevit thin launcher - CheckCode DLL loader."""
from __future__ import print_function
from pyrevit import HOST_APP, script, forms, EXEC_PARAMS
import clr  # type: ignore # IronPython/.NET interop
import os
import sys

# ══════════════════════════════════════════════════════════════════════════════
# CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════
DLL_NAME = "CheckCode.dll"
NAMESPACE = "CheckCode"
ENTRY_METHOD = "Run"

# Version → Framework mapping
VERSION_MAP = {
    2023: "net48",
    2024: "net48",
    2025: "net8",
    2026: "net8",
}


# ══════════════════════════════════════════════════════════════════════════════
# LAUNCHER LOGIC
# ══════════════════════════════════════════════════════════════════════════════
def get_extension_root():
    """Get extension root using pyRevit's command path."""
    # Use pyRevit's EXEC_PARAMS to get the actual script location
    cmd_path = EXEC_PARAMS.command_path
    if cmd_path:
        # cmd_path points to the .pushbutton folder
        # Go up: pushbutton -> panel -> tab -> extension
        path = cmd_path
        for _ in range(3):
            path = os.path.dirname(path)
        return path
    
    # Fallback: traverse from __file__
    path = os.path.dirname(__file__)
    for _ in range(4):
        path = os.path.dirname(path)
    return path


def show_error(title, message):
    """Display error dialog and exit."""
    forms.alert(message, title=title, exitscript=True)


def main():
    # 1. Detect Revit version (ensure it's an integer)
    revit_version = int(HOST_APP.version)
    
    # 2. Map to framework
    framework = VERSION_MAP.get(revit_version)
    if not framework:
        supported = sorted(VERSION_MAP.keys())
        show_error(
            "Unsupported Revit Version",
            "Revit {} is not supported.\n\n"
            "Supported versions: {}\n\n"
            "Update VERSION_MAP in script.py if needed.".format(
                revit_version, supported
            )
        )
        return
    
    # 3. Build and validate DLL path
    ext_root = get_extension_root()
    lib_folder = os.path.join(ext_root, "lib", framework)
    dll_path = os.path.join(lib_folder, DLL_NAME)
    
    if not os.path.exists(dll_path):
        # Debug info
        cmd_path = EXEC_PARAMS.command_path if EXEC_PARAMS.command_path else "None"
        file_path = __file__
        show_error(
            "DLL Not Found",
            "Cannot find required DLL.\n\n"
            "Revit version: {}\n"
            "Framework: {}\n"
            "Expected path:\n{}\n\n"
            "Debug info:\n"
            "- command_path: {}\n"
            "- __file__: {}\n"
            "- ext_root: {}\n\n"
            "Build the project and copy DLL to lib/{}/".format(
                revit_version, framework, dll_path,
                cmd_path, file_path, ext_root, framework
            )
        )
        return
    
    # 4. Add lib folder to sys.path
    if lib_folder not in sys.path:
        sys.path.insert(0, lib_folder)
    
    # 5. Load assembly
    try:
        clr.AddReferenceToFileAndPath(dll_path)
    except Exception as ex:
        show_error(
            "Assembly Load Failed",
            "Failed to load DLL.\n\n"
            "Path: {}\n"
            "Error: {}\n\n"
            "Ensure DLL is built for {} framework.".format(
                dll_path, ex, framework
            )
        )
        return
    
    # 6. Import and call entry point
    try:
        from CheckCode import Entry  # type: ignore # loaded via clr
        method = getattr(Entry, ENTRY_METHOD)
        method(__revit__)  # type: ignore # pyRevit global
    except AttributeError as ex:
        show_error(
            "Entry Method Not Found",
            "Cannot find Entry.{}()\n\n"
            "Error: {}\n\n"
            "Ensure DLL has:\n"
            "  public static class Entry\n"
            "  {{\n"
            "      public static void {}(UIApplication uiapp)\n"
            "  }}".format(ENTRY_METHOD, ex, ENTRY_METHOD)
        )
    except Exception as ex:
        import traceback
        show_error(
            "Execution Error",
            "Error running CheckCode:\n\n"
            "{}: {}\n\n"
            "{}".format(type(ex).__name__, ex, traceback.format_exc())
        )


if __name__ == "__main__" or True:
    main()
