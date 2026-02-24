# -*- coding: utf-8 -*-
"""RevitChatLocal - Local AI Chatbot using Ollama (free, no API key)."""
import sys
import os

ext_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
lib_path = os.path.join(ext_root, "lib")
if lib_path not in sys.path:
    sys.path.insert(0, lib_path)

from launcher_base import launch_dll

launch_dll(
    dll_name="RevitChatLocal.dll",
    namespace="RevitChatLocal",
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
        "Microsoft.ML.OnnxRuntime.dll",
    ]
)
