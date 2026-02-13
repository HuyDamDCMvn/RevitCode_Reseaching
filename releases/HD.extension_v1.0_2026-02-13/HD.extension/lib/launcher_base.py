# -*- coding: utf-8 -*-
"""
Shared launcher for pyRevit thin launcher pattern.
All pushbutton scripts should use this instead of duplicating code.
"""
from __future__ import print_function
from pyrevit import HOST_APP, script, forms, EXEC_PARAMS
import clr
import os
import sys

# Version → Framework mapping
VERSION_MAP = {
    2025: "net8",
    2026: "net8",
}


def get_extension_root():
    """Get extension root using pyRevit's command path."""
    cmd_path = EXEC_PARAMS.command_path
    if cmd_path:
        path = cmd_path
        for _ in range(3):
            path = os.path.dirname(path)
        return path
    
    path = os.path.dirname(__file__)
    for _ in range(2):
        path = os.path.dirname(path)
    return path


def show_error(title, message):
    """Display error dialog and exit."""
    forms.alert(message, title=title, exitscript=True)


def launch_dll(dll_name, namespace, method, dependencies=None, uiapp=None):
    """
    Load and execute a DLL entry point.
    
    Args:
        dll_name: Name of the DLL file (e.g., "CommonFeature.dll")
        namespace: Namespace containing Entry class (e.g., "CommonFeature")
        method: Entry method name (e.g., "ShowTool", "Run")
        dependencies: Optional list of dependency DLL names to load first
        uiapp: UIApplication instance (defaults to __revit__)
    """
    if uiapp is None:
        uiapp = __revit__
    
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
            "Only Revit 2025+ is supported.".format(revit_version, supported)
        )
        return False
    
    # 3. Build paths
    ext_root = get_extension_root()
    lib_folder = os.path.join(ext_root, "lib", framework)
    dll_path = os.path.join(lib_folder, dll_name)
    
    if not os.path.exists(dll_path):
        show_error(
            "DLL Not Found",
            "Cannot find required DLL.\n\n"
            "Revit version: {}\n"
            "Framework: {}\n"
            "Expected path:\n{}\n\n"
            "Build the project first.".format(revit_version, framework, dll_path)
        )
        return False
    
    # 4. Add lib folder to path
    if lib_folder not in sys.path:
        sys.path.insert(0, lib_folder)
    
    # 5. Load dependencies
    if dependencies:
        for dep_dll in dependencies:
            dep_path = os.path.join(lib_folder, dep_dll)
            if os.path.exists(dep_path):
                try:
                    clr.AddReferenceToFileAndPath(dep_path)
                except Exception:
                    pass
    
    # 6. Load HD.Core first (if exists)
    core_path = os.path.join(lib_folder, "HD.Core.dll")
    if os.path.exists(core_path):
        try:
            clr.AddReferenceToFileAndPath(core_path)
        except Exception:
            pass
    
    # 7. Load main assembly
    try:
        clr.AddReferenceToFileAndPath(dll_path)
    except Exception as ex:
        show_error(
            "Assembly Load Failed",
            "Failed to load DLL.\n\n"
            "Path: {}\n"
            "Error: {}".format(dll_path, ex)
        )
        return False
    
    # 8. Import and call entry point
    try:
        module = __import__(namespace)
        Entry = getattr(module, "Entry")
        entry_method = getattr(Entry, method)
        entry_method(uiapp)
        return True
    except AttributeError as ex:
        show_error(
            "Entry Method Not Found",
            "Cannot find {}.Entry.{}()\n\n"
            "Error: {}".format(namespace, method, ex)
        )
    except Exception as ex:
        import traceback
        show_error(
            "Execution Error",
            "Error running {}:\n\n"
            "{}: {}\n\n"
            "{}".format(namespace, type(ex).__name__, ex, traceback.format_exc())
        )
    
    return False
