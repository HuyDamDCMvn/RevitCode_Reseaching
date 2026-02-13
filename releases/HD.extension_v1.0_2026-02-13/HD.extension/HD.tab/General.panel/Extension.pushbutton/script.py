# -*- coding: utf-8 -*-
"""Open pyRevit Extension Manager."""
from pyrevit import script
from pyrevit.extensions import extensionmgr

__title__ = "Extension"
__doc__ = "Open pyRevit Extension Manager to manage installed extensions."

# Open extension manager
try:
    from pyrevit import forms
    from pyrevit.userconfig import user_config
    import os
    
    # Get extension directory
    ext_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
    
    # Show info about current extension
    forms.alert(
        "HD Extension\n\n"
        "Location: {}\n\n"
        "Use pyRevit settings to manage extensions.".format(ext_dir),
        title="Extension Info"
    )
except Exception as ex:
    from pyrevit import forms
    forms.alert("Error: {}".format(ex), title="Extension Error")
