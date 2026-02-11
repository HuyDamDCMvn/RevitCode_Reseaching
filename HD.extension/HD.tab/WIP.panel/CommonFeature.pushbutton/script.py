# -*- coding: utf-8 -*-
"""pyRevit thin launcher - CommonFeature modeless tool."""
from __future__ import print_function
from pyrevit import HOST_APP, script, forms, EXEC_PARAMS
import clr  # type: ignore
import os
import sys

# ══════════════════════════════════════════════════════════════════════════════
# CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════
DLL_NAME = "CommonFeature.dll"
NAMESPACE = "CommonFeature"
ENTRY_METHOD = "ShowTool"  # Modeless window

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
    # 1. Detect Revit version
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
        show_error(
            "DLL Not Found",
            "Cannot find required DLL.\n\n"
            "Revit version: {}\n"
            "Framework: {}\n"
            "Expected path:\n{}\n\n"
            "Build the project and copy DLL to lib/{}/".format(
                revit_version, framework, dll_path, framework
            )
        )
        return
    
    # 4. Add lib folder to sys.path
    if lib_folder not in sys.path:
        sys.path.insert(0, lib_folder)
    
    # 5. Load Material Design dependencies first
    dependency_dlls = [
        "Microsoft.Xaml.Behaviors.dll",
        "MaterialDesignColors.dll",
        "MaterialDesignThemes.Wpf.dll",
    ]
    
    for dep_dll in dependency_dlls:
        dep_path = os.path.join(lib_folder, dep_dll)
        if os.path.exists(dep_path):
            try:
                clr.AddReferenceToFileAndPath(dep_path)
            except Exception:
                pass  # May already be loaded
    
    # 6. Load main assembly
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
    
    # 7. Import and call entry point
    try:
        from CommonFeature import Entry  # type: ignore
        method = getattr(Entry, ENTRY_METHOD)
        method(__revit__)  # type: ignore
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
            "Error running CommonFeature:\n\n"
            "{}: {}\n\n"
            "{}".format(type(ex).__name__, ex, traceback.format_exc())
        )


if __name__ == "__main__" or True:
    main()
