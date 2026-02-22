# -*- coding: utf-8 -*-
"""RevitChat - AI Chatbot for querying and modifying Revit models."""
import sys
import os

ext_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
lib_path = os.path.join(ext_root, "lib")
if lib_path not in sys.path:
    sys.path.insert(0, lib_path)

from launcher_base import launch_dll

launch_dll(
    dll_name="RevitChat.dll",
    namespace="RevitChat",
    method="ShowTool",
    dependencies=[
        "HD.Core.dll",
        "CommunityToolkit.Mvvm.dll",
        "OpenAI.dll",
        "System.ClientModel.dll",
        "System.Memory.Data.dll",
        "System.Net.ServerSentEvents.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
    ]
)
