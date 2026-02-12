# -*- coding: utf-8 -*-
"""Open pyRevit Extension Manager."""
from pyrevit import script, forms
import os

__title__ = "Extension"
__doc__ = "Show information about pyHD Extension."

# Get extension directory
ext_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))

# Show info about current extension
forms.alert(
    "pyHD Extension (Pure Python)\n\n"
    "Version: 1.0.0\n"
    "Author: HD Team\n\n"
    "Location:\n{}\n\n"
    "This extension is written in pure Python\n"
    "without external C# DLL dependencies.".format(ext_dir),
    title="Extension Info"
)
