# pyRevit Command Examples

## Example 1: Batch Rename Elements

```python
# -*- coding: utf-8 -*-
"""Batch rename selected elements with prefix/suffix."""
__title__ = "Batch\nRename"
__author__ = "Your Name"

from pyrevit import revit, DB
from pyrevit import script, forms

doc = revit.doc
logger = script.get_logger()
output = script.get_output()

def main():
    # Get selection
    selection = revit.get_selection()
    if not selection:
        forms.alert("Select elements to rename.", exitscript=True)
    
    # Get user input
    prefix = forms.ask_for_string(
        prompt="Enter prefix (leave empty to skip):",
        title="Prefix"
    ) or ""
    
    suffix = forms.ask_for_string(
        prompt="Enter suffix (leave empty to skip):",
        title="Suffix"
    ) or ""
    
    if not prefix and not suffix:
        forms.alert("No prefix or suffix provided.", exitscript=True)
    
    # Process
    renamed = 0
    with revit.Transaction("Batch Rename"):
        for elem in selection:
            mark_param = elem.get_Parameter(DB.BuiltInParameter.ALL_MODEL_MARK)
            if mark_param and not mark_param.IsReadOnly:
                old_value = mark_param.AsString() or ""
                new_value = "{}{}{}".format(prefix, old_value, suffix)
                mark_param.Set(new_value)
                renamed += 1
    
    output.print_md("Renamed **{}** elements".format(renamed))

if __name__ == "__main__":
    main()
```

## Example 2: Export Selection to CSV

```python
# -*- coding: utf-8 -*-
"""Export selected element data to CSV."""
__title__ = "Export\nto CSV"
__author__ = "Your Name"

import csv
from pyrevit import revit, DB
from pyrevit import script, forms

doc = revit.doc
logger = script.get_logger()

def main():
    selection = revit.get_selection()
    if not selection:
        forms.alert("Select elements to export.", exitscript=True)
    
    # Ask for file path
    filepath = forms.save_file(file_ext="csv")
    if not filepath:
        script.exit()
    
    # Collect data
    data = []
    for elem in selection:
        row = {
            "Id": elem.Id.IntegerValue,
            "Category": elem.Category.Name if elem.Category else "N/A",
            "Name": elem.Name if hasattr(elem, "Name") else "N/A",
        }
        # Add Mark parameter
        mark = elem.get_Parameter(DB.BuiltInParameter.ALL_MODEL_MARK)
        row["Mark"] = mark.AsString() if mark and mark.HasValue else ""
        data.append(row)
    
    # Write CSV
    with open(filepath, "wb") as f:
        writer = csv.DictWriter(f, fieldnames=data[0].keys())
        writer.writeheader()
        writer.writerows(data)
    
    forms.alert("Exported {} elements to:\n{}".format(len(data), filepath))

if __name__ == "__main__":
    main()
```

## Example 3: Filter by Parameter Value

```python
# -*- coding: utf-8 -*-
"""Select elements matching parameter criteria."""
__title__ = "Filter by\nParameter"
__author__ = "Your Name"

from pyrevit import revit, DB
from pyrevit import script, forms

doc = revit.doc
uidoc = revit.uidoc
logger = script.get_logger()
output = script.get_output()

def main():
    # Get all categories in model
    categories = doc.Settings.Categories
    cat_names = sorted([c.Name for c in categories if c.AllowsBoundParameters])
    
    # User selects category
    selected_cat = forms.SelectFromList.show(
        cat_names,
        title="Select Category",
        button_name="Select"
    )
    if not selected_cat:
        script.exit()
    
    # Get category
    category = None
    for cat in categories:
        if cat.Name == selected_cat:
            category = cat
            break
    
    # Collect elements
    collector = DB.FilteredElementCollector(doc)\
        .OfCategoryId(category.Id)\
        .WhereElementIsNotElementType()
    
    elements = list(collector)
    if not elements:
        forms.alert("No elements found in category.", exitscript=True)
    
    # Get parameter name
    param_name = forms.ask_for_string(
        prompt="Enter parameter name:",
        title="Parameter"
    )
    if not param_name:
        script.exit()
    
    # Get search value
    search_value = forms.ask_for_string(
        prompt="Enter value to match:",
        title="Value"
    )
    if search_value is None:
        script.exit()
    
    # Filter
    matching = []
    for elem in elements:
        param = elem.LookupParameter(param_name)
        if param and param.HasValue:
            value = param.AsString() or str(param.AsValueString())
            if search_value.lower() in value.lower():
                matching.append(elem.Id)
    
    if matching:
        uidoc.Selection.SetElementIds(
            List[DB.ElementId](matching)
        )
        output.print_md("Selected **{}** matching elements".format(len(matching)))
    else:
        forms.alert("No matching elements found.")

if __name__ == "__main__":
    from System.Collections.Generic import List
    main()
```

## Example 4: Custom WPF Dialog

**script.py:**
```python
# -*- coding: utf-8 -*-
"""Tool with custom WPF dialog."""
__title__ = "Custom\nDialog"
__author__ = "Your Name"

from pyrevit import revit, DB
from pyrevit import script, forms

doc = revit.doc
logger = script.get_logger()


class MyDialog(forms.WPFWindow):
    def __init__(self):
        forms.WPFWindow.__init__(self, "MyDialog.xaml")
        self.result = None
    
    def ok_click(self, sender, args):
        self.result = self.input_tb.Text
        self.Close()
    
    def cancel_click(self, sender, args):
        self.result = None
        self.Close()


def main():
    dialog = MyDialog()
    dialog.ShowDialog()
    
    if dialog.result:
        logger.info("User entered: {}".format(dialog.result))
        # Process with result
    else:
        script.exit()

if __name__ == "__main__":
    main()
```

**MyDialog.xaml:**
```xml
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="My Dialog" Height="150" Width="300"
        WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize">
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <TextBlock Grid.Row="0" Text="Enter value:" Margin="0,0,0,5"/>
        <TextBox x:Name="input_tb" Grid.Row="1" Margin="0,0,0,10"/>
        
        <StackPanel Grid.Row="3" Orientation="Horizontal" 
                    HorizontalAlignment="Right">
            <Button Content="OK" Width="75" Margin="0,0,10,0"
                    Click="ok_click"/>
            <Button Content="Cancel" Width="75" 
                    Click="cancel_click"/>
        </StackPanel>
    </Grid>
</Window>
```

## Example 5: Multi-threaded Collection (CPython)

```python
# -*- coding: utf-8 -*-
#! python3
"""Parallel processing example (CPython only)."""
__title__ = "Parallel\nProcess"
__author__ = "Your Name"

from concurrent.futures import ThreadPoolExecutor
from pyrevit import revit, DB
from pyrevit import script, forms

doc = revit.doc
logger = script.get_logger()
output = script.get_output()


def process_element(elem_id):
    """Process single element (read-only operations only)."""
    elem = doc.GetElement(elem_id)
    # Read-only analysis
    return {
        "id": elem_id.IntegerValue,
        "category": elem.Category.Name if elem.Category else None,
    }


def main():
    # Collect all walls
    walls = DB.FilteredElementCollector(doc)\
        .OfCategory(DB.BuiltInCategory.OST_Walls)\
        .WhereElementIsNotElementType()\
        .ToElementIds()
    
    wall_ids = list(walls)
    if not wall_ids:
        forms.alert("No walls found.", exitscript=True)
    
    # Parallel processing (read-only)
    results = []
    with ThreadPoolExecutor(max_workers=4) as executor:
        results = list(executor.map(process_element, wall_ids))
    
    output.print_md("Processed **{}** walls".format(len(results)))
    
    # Note: All model modifications must still be on main thread
    # with revit.Transaction("..."):
    #     ...

if __name__ == "__main__":
    main()
```

## bundle.yaml Templates

### Standard Command
```yaml
title:
  en_us: Button Title
tooltip:
  en_us: Description of what this command does.
author: Your Name
```

### Command Requiring Selection
```yaml
title:
  en_us: Process Selection
tooltip:
  en_us: Process the currently selected elements.
author: Your Name
context: selection
```

### Command Without Document
```yaml
title:
  en_us: Settings
tooltip:
  en_us: Open extension settings dialog.
author: Your Name
context: zero-doc
```

### CPython Command
```yaml
title:
  en_us: Python 3 Tool
tooltip:
  en_us: This tool uses Python 3 features.
author: Your Name
engine:
  type: CPython
  version: 3.8
```

### Highlighted New Feature
```yaml
title:
  en_us: New Feature
tooltip:
  en_us: Brand new feature description.
author: Your Name
highlight: new
```
