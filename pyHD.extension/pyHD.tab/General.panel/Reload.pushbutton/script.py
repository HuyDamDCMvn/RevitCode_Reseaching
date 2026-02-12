# -*- coding: utf-8 -*-
"""Reload pyRevit extension."""
from pyrevit import script
from pyrevit.loader import sessionmgr

__title__ = "Reload"
__doc__ = "Reload the current pyRevit extension to apply changes."

# Reload current extension
sessionmgr.reload_pyrevit()
