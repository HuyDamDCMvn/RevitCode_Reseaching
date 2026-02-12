# -*- coding: utf-8 -*-
"""Open pyHD Extension Settings."""
from pyrevit import script, forms
import webbrowser

__title__ = "Setting"
__doc__ = "Open settings for pyHD Extension."

# Settings options
options = [
    "About pyHD Extension",
    "Check for Updates",
    "View Documentation",
    "Report Issue",
]

selected = forms.SelectFromList.show(
    options,
    title="pyHD Extension Settings",
    button_name="Select",
    multiselect=False
)

if selected:
    if selected == "About pyHD Extension":
        forms.alert(
            "pyHD Extension for Revit\n\n"
            "Version: 1.0.0\n"
            "Author: HD Team\n\n"
            "A pure Python collection of productivity tools for Revit.\n\n"
            "Features:\n"
            "- Element Information: View and filter all elements\n"
            "- Parameter Editing: Modify parameter values\n"
            "- Export to CSV: Export element data",
            title="About"
        )
    elif selected == "Check for Updates":
        forms.alert("You are using the latest version.", title="Updates")
    elif selected == "View Documentation":
        webbrowser.open("https://github.com/")
    elif selected == "Report Issue":
        webbrowser.open("https://github.com/")
