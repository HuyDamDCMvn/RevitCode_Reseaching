# -*- coding: utf-8 -*-
"""CommonFeature - Modeless tool for element operations."""
import sys
import os

# Add lib folder to path for launcher_base
ext_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
lib_path = os.path.join(ext_root, "lib")
if lib_path not in sys.path:
    sys.path.insert(0, lib_path)

from launcher_base import launch_dll

launch_dll(
    dll_name="CommonFeature.dll",
    namespace="CommonFeature",
    method="ShowTool",
    dependencies=["CommunityToolkit.Mvvm.dll"]
)
