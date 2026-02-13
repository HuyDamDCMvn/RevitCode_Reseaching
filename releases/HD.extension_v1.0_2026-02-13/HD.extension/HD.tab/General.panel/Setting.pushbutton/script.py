# -*- coding: utf-8 -*-
"""Open HD Extension Settings."""
from pyrevit import script, forms

__title__ = "Setting"
__doc__ = "Open settings for HD Extension."

# Placeholder settings dialog
options = [
    "About HD Extension",
    "Check for Updates",
    "View Documentation",
    "Report Issue",
]

selected = forms.SelectFromList.show(
    options,
    title="HD Extension Settings",
    button_name="Select",
    multiselect=False
)

if selected:
    if selected == "About HD Extension":
        forms.alert(
            "HD Extension for Revit\n\n"
            "Version: 1.0.0\n"
            "Author: HD Team\n\n"
            "A collection of productivity tools for Revit.",
            title="About"
        )
    elif selected == "Check for Updates":
        forms.alert("You are using the latest version.", title="Updates")
    elif selected == "View Documentation":
        import webbrowser
        webbrowser.open("https://github.com/")  # Replace with actual docs URL
    elif selected == "Report Issue":
        import webbrowser
        webbrowser.open("https://github.com/")  # Replace with actual issues URL
