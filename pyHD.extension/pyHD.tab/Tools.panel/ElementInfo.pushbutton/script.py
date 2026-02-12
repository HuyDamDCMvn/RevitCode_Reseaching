# -*- coding: utf-8 -*-
# type: ignore
"""Element Information Tool - Pure Python Implementation.

Shows all elements in a DataGrid with filtering, sorting, parameter editing,
and export capabilities.

Note: This module uses Revit API and WPF via IronPython/pythonnet.
The '# type: ignore' at module level suppresses Pylance warnings
for .NET types that the IDE cannot resolve.
"""
from __future__ import print_function
import os
import sys
import csv
import codecs

# pyRevit imports
from pyrevit import script, forms, HOST_APP, EXEC_PARAMS  # noqa: F401
from pyrevit import revit
from pyrevit.forms import WPFWindow

# Add lib folder to path
ext_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(__file__))))
lib_dir = os.path.join(ext_dir, "lib")
if lib_dir not in sys.path:
    sys.path.insert(0, lib_dir)

# Import element utilities
from element_utils import (
    collect_all_elements, get_element_parameters, get_parameter_values,
    validate_parameter_value, set_parameter_value, select_elements,
    create_section_box
)

# .NET imports
import clr  # noqa: F401
clr.AddReference('RevitAPI')
clr.AddReference('System.Windows.Forms')

# Try to add IronPython.Wpf reference
try:
    clr.AddReference('IronPython.Wpf')
    import wpf  # noqa: F401
except Exception:
    wpf = None

# Revit API
from Autodesk.Revit.DB import Transaction

# WPF imports
from System.Windows import MessageBox, MessageBoxButton, MessageBoxImage, Clipboard
from System.Windows.Controls import DataGridTextColumn, DataGridLength, ContextMenu, MenuItem, Separator
from System.Windows.Data import Binding
from System.Windows.Media import SolidColorBrush, Color
from System.Windows.Input import Key

__title__ = "Element\nInfo"
__doc__ = "View and edit element information with filtering and export capabilities."


class ElementData(object):
    """Wrapper class for element data to work with WPF DataGrid."""
    
    def __init__(self, info):
        self.info = info
        self.id = info['id']
        self.family_name = info['family_name']
        self.family_type = info['family_type']
        self.category = info['category']
        self.workset = info['workset']
        self.created_by = info['created_by']
        self.edited_by = info['edited_by']
        
        # Dynamic parameters
        self._parameters = {}
        self._original_params = {}
        self._modified_params = set()
        self._readonly_params = set()
        self._param_types = {}
    
    def get_param(self, name):
        return self._parameters.get(name, "-")
    
    def set_param(self, name, value):
        if name not in self._original_params:
            self._original_params[name] = self._parameters.get(name, "-")
        
        self._parameters[name] = value
        
        # Track modification
        if self._original_params.get(name) != value:
            self._modified_params.add(name)
        else:
            self._modified_params.discard(name)
    
    def is_modified(self, name):
        return name in self._modified_params
    
    def has_modifications(self):
        return len(self._modified_params) > 0
    
    def clear_modifications(self):
        self._original_params.clear()
        self._modified_params.clear()
    
    def revert_param(self, name):
        if name in self._original_params:
            self._parameters[name] = self._original_params[name]
            self._modified_params.discard(name)


class ElementInfoWindow(WPFWindow):
    """Main window for Element Information tool."""
    
    def __init__(self, xaml_file, uidoc):
        WPFWindow.__init__(self, xaml_file)
        
        self.uidoc = uidoc
        self.doc = uidoc.Document
        
        self._all_elements = []
        self._filtered_elements = []
        self._param_columns = []  # List of added parameter column names
        self._param_is_instance = {}  # param_name -> is_instance
        
        # Filter state
        self._column_filters = {}  # column_name -> set of selected values
        
        # Modified cell brush
        self._modified_brush = SolidColorBrush(Color.FromRgb(200, 230, 201))
        
        # Setup events
        self.CloseButton.Click += self.close_click
        self.RefreshButton.Click += self.refresh_click
        self.ClearFilterButton.Click += self.clear_filter_click
        self.ExportButton.Click += self.export_click
        self.AddParamButton.Click += self.add_param_click
        self.UpdateButton.Click += self.update_click
        self.SearchBox.TextChanged += self.search_changed
        self.ElementsGrid.MouseRightButtonUp += self.grid_right_click
        self.ElementsGrid.CellEditEnding += self.cell_edit_ending
        self.ElementsGrid.BeginningEdit += self.beginning_edit
        
        # Keyboard shortcut
        self.KeyDown += self.on_key_down
        
        # Load data
        self.load_data()
    
    def load_data(self):
        """Load all elements from document."""
        self.StatusText.Text = "Loading elements..."
        
        try:
            elements = collect_all_elements(self.doc)
            self._all_elements = [ElementData(e) for e in elements]
            self._filtered_elements = list(self._all_elements)
            
            self.update_grid()
            self.CountText.Text = "{} elements".format(len(self._all_elements))
            self.StatusText.Text = "Loaded {} elements".format(len(self._all_elements))
        except Exception as ex:
            self.StatusText.Text = "Error: {}".format(str(ex))
    
    def update_grid(self):
        """Update DataGrid with filtered elements."""
        # Simple list binding
        self.ElementsGrid.ItemsSource = None
        
        # Apply search filter
        search_text = self.SearchBox.Text.lower() if self.SearchBox.Text else ""
        
        if search_text:
            filtered = []
            for elem in self._filtered_elements:
                if (search_text in str(elem.id) or
                    search_text in elem.family_name.lower() or
                    search_text in elem.family_type.lower() or
                    search_text in elem.category.lower()):
                    filtered.append(elem)
            display_list = filtered
        else:
            display_list = self._filtered_elements
        
        self.ElementsGrid.ItemsSource = display_list
        self.CountText.Text = "{} / {} elements".format(
            len(display_list), len(self._all_elements))
    
    def setup_columns(self):
        """Setup default columns."""
        self.ElementsGrid.Columns.Clear()
        
        # Fixed columns
        columns = [
            ("Id", "id", 80),
            ("Family Name", "family_name", 150),
            ("Family Type", "family_type", 150),
            ("Category", "category", 120),
            ("Workset", "workset", 100),
            ("Created By", "created_by", 100),
            ("Edited By", "edited_by", 100),
        ]
        
        for header, binding_path, width in columns:
            col = DataGridTextColumn()
            col.Header = header
            col.Binding = Binding(binding_path)
            col.Width = DataGridLength(width)
            col.IsReadOnly = True
            self.ElementsGrid.Columns.Add(col)
    
    def add_parameter_column(self, param_name, is_instance):
        """Add a parameter column to the grid."""
        if param_name in self._param_columns:
            return
        
        self._param_columns.append(param_name)
        self._param_is_instance[param_name] = is_instance
        
        # Create column header
        header = "{} ({})".format(param_name, "I" if is_instance else "T")
        
        col = DataGridTextColumn()
        col.Header = header
        col.Width = DataGridLength(140)
        col.IsReadOnly = False
        
        self.ElementsGrid.Columns.Add(col)
    
    def close_click(self, sender, args):
        self.Close()
    
    def refresh_click(self, sender, args):
        self._param_columns = []
        self._param_is_instance = {}
        self._column_filters = {}
        self.setup_columns()
        self.load_data()
    
    def clear_filter_click(self, sender, args):
        self._column_filters = {}
        self._filtered_elements = list(self._all_elements)
        self.SearchBox.Text = ""
        self.update_grid()
        self.StatusText.Text = "Filters cleared"
    
    def search_changed(self, sender, args):
        self.update_grid()
    
    def export_click(self, sender, args):
        """Export to CSV."""
        try:
            from System.Windows.Forms import SaveFileDialog, DialogResult
            
            dialog = SaveFileDialog()
            dialog.Filter = "CSV files (*.csv)|*.csv"
            dialog.DefaultExt = "csv"
            dialog.FileName = "element_info.csv"
            
            if dialog.ShowDialog() == DialogResult.OK:
                file_path = dialog.FileName
                
                with codecs.open(file_path, 'w', 'utf-8-sig') as f:
                    writer = csv.writer(f)
                    
                    # Headers
                    headers = ["ID", "Family Name", "Family Type", "Category", 
                               "Workset", "Created By", "Edited By"]
                    headers.extend(self._param_columns)
                    writer.writerow(headers)
                    
                    # Data
                    for elem in self._filtered_elements:
                        row = [
                            elem.id,
                            elem.family_name,
                            elem.family_type,
                            elem.category,
                            elem.workset,
                            elem.created_by,
                            elem.edited_by
                        ]
                        for param in self._param_columns:
                            row.append(elem.get_param(param))
                        writer.writerow(row)
                
                self.StatusText.Text = "Exported to: {}".format(file_path)
        except Exception as ex:
            self.StatusText.Text = "Export error: {}".format(str(ex))
    
    def add_param_click(self, sender, args):
        """Show parameter selection dialog."""
        # Get selected element IDs
        selected = self.ElementsGrid.SelectedItems
        if not selected or len(list(selected)) == 0:
            # Use all elements if none selected
            elem_ids = [e.id for e in self._all_elements[:100]]  # Limit for performance
        else:
            elem_ids = [e.id for e in selected]
        
        if not elem_ids:
            forms.alert("No elements available.", title="Add Parameter")
            return
        
        # Get parameters
        params = get_element_parameters(self.doc, elem_ids)
        
        if not params:
            forms.alert("No parameters found.", title="Add Parameter")
            return
        
        # Show selection dialog
        param_options = ["{} ({})".format(p['name'], "Instance" if p['is_instance'] else "Type") 
                        for p in params]
        
        selected_options = forms.SelectFromList.show(
            param_options,
            title="Select Parameters",
            button_name="Add Columns",
            multiselect=True
        )
        
        if not selected_options:
            return
        
        # Parse selections and add columns
        for opt in selected_options:
            # Extract name (remove " (Instance)" or " (Type)" suffix)
            if opt.endswith(" (Instance)"):
                name = opt[:-11]
                is_instance = True
            elif opt.endswith(" (Type)"):
                name = opt[:-7]
                is_instance = False
            else:
                continue
            
            # Get values for all elements
            elem_ids = [e.id for e in self._all_elements]
            values = get_parameter_values(self.doc, elem_ids, [name])
            
            # Store values in elements
            for elem in self._all_elements:
                if elem.id in values and name in values[elem.id]:
                    info = values[elem.id][name]
                    elem._parameters[name] = info['value']
                    elem._original_params[name] = info['value']
                    if info['readonly']:
                        elem._readonly_params.add(name)
                    elem._param_types[name] = info['type']
            
            self.add_parameter_column(name, is_instance)
        
        self.update_grid()
        self.StatusText.Text = "Added {} parameter column(s)".format(len(selected_options))
    
    def update_click(self, sender, args):
        """Update modified parameter values to Revit."""
        # Collect modifications
        updates = []
        for elem in self._all_elements:
            for param_name in elem._modified_params:
                is_instance = self._param_is_instance.get(param_name, True)
                new_value = elem.get_param(param_name)
                updates.append({
                    'elem_id': elem.id,
                    'param_name': param_name,
                    'value': new_value,
                    'is_instance': is_instance
                })
        
        if not updates:
            forms.alert("No modifications to update.", title="Update Values")
            return
        
        # Confirm
        if not forms.alert(
            "Update {} parameter value(s)?".format(len(updates)),
            title="Confirm Update",
            yes=True, no=True
        ):
            return
        
        # Execute updates
        success_count = 0
        error_count = 0
        errors = []
        
        trans = Transaction(self.doc)
        try:
            trans.Start("Update Parameter Values")
            
            for update in updates:
                success, error = set_parameter_value(
                    self.doc,
                    update['elem_id'],
                    update['param_name'],
                    update['value'],
                    update['is_instance']
                )
                
                if success:
                    success_count += 1
                else:
                    error_count += 1
                    errors.append("ID {}: {}".format(update['elem_id'], error))
            
            trans.Commit()
        except Exception as ex:
            if trans.HasStarted():
                trans.RollBack()
            self.StatusText.Text = "Transaction error: {}".format(str(ex))
            return
        
        # Clear modifications on success
        if success_count > 0:
            for elem in self._all_elements:
                elem.clear_modifications()
        
        self.update_modified_count()
        
        msg = "Updated {} value(s)".format(success_count)
        if error_count > 0:
            msg += ", {} error(s)".format(error_count)
        self.StatusText.Text = msg
        
        if errors:
            forms.alert(
                "Some updates failed:\n\n" + "\n".join(errors[:10]),
                title="Update Errors"
            )
    
    def update_modified_count(self):
        """Update the modified count display."""
        count = sum(1 for e in self._all_elements if e.has_modifications())
        if count > 0:
            self.ModifiedText.Text = "{} modified".format(count)
            self.UpdateButton.IsEnabled = True
        else:
            self.ModifiedText.Text = ""
            self.UpdateButton.IsEnabled = False
    
    def beginning_edit(self, sender, args):
        """Handle cell edit beginning."""
        # Get column header
        col = args.Column
        if col is None:
            return
        
        header = str(col.Header) if col.Header else ""
        
        # Only allow editing parameter columns
        if not header.endswith(" (I)") and not header.endswith(" (T)"):
            args.Cancel = True
            return
        
        # Extract param name
        param_name = header[:-4]  # Remove " (I)" or " (T)"
        
        # Check if read-only
        row = args.Row
        if row and row.Item:
            elem = row.Item
            if param_name in elem._readonly_params:
                args.Cancel = True
                MessageBox.Show(
                    "Cannot edit parameter '{}'.\n\nThis parameter is read-only.".format(param_name),
                    "Read-Only Parameter",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                )
                return
    
    def cell_edit_ending(self, sender, args):
        """Handle cell edit ending."""
        if args.EditAction == 1:  # Cancel
            return
        
        col = args.Column
        row = args.Row
        
        if col is None or row is None or row.Item is None:
            return
        
        header = str(col.Header) if col.Header else ""
        
        # Only process parameter columns
        if not header.endswith(" (I)") and not header.endswith(" (T)"):
            return
        
        param_name = header[:-4]
        elem = row.Item
        
        # Get new value from editing element
        editing_elem = args.EditingElement
        if editing_elem:
            try:
                new_value = editing_elem.Text
                old_value = elem.get_param(param_name)
                
                if new_value != old_value:
                    # Validate
                    data_type = elem._param_types.get(param_name, "String")
                    error = validate_parameter_value(data_type, new_value)
                    
                    if error:
                        args.Cancel = True
                        MessageBox.Show(
                            "Invalid value for '{}'.\n\n{}".format(param_name, error),
                            "Invalid Input",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        )
                        editing_elem.Text = old_value
                        return
                    
                    # Store modification
                    elem.set_param(param_name, new_value)
                    self.update_modified_count()
                    self.StatusText.Text = "Modified: {} = '{}'".format(param_name, new_value)
            except Exception:
                pass
    
    def grid_right_click(self, sender, args):
        """Handle right-click context menu."""
        # Get selected items
        selected = list(self.ElementsGrid.SelectedItems)
        if not selected:
            return
        
        elem_ids = [e.id for e in selected]
        
        # Create context menu
        menu = ContextMenu()
        
        # Copy ID
        copy_item = MenuItem()
        copy_item.Header = "Copy ID(s)"
        copy_item.Click += lambda s, e: self.copy_ids(elem_ids)
        menu.Items.Add(copy_item)
        
        menu.Items.Add(Separator())
        
        # Select in Revit
        select_item = MenuItem()
        select_item.Header = "Select in Revit ({})".format(len(elem_ids))
        select_item.Click += lambda s, e: self.select_in_revit(elem_ids)
        menu.Items.Add(select_item)
        
        # Create Section Box
        section_item = MenuItem()
        section_item.Header = "Create Section Box ({})".format(len(elem_ids))
        section_item.Click += lambda s, e: self.create_section_box_action(elem_ids)
        menu.Items.Add(section_item)
        
        menu.IsOpen = True
    
    def copy_ids(self, elem_ids):
        """Copy element IDs to clipboard."""
        try:
            text = ", ".join(str(eid) for eid in elem_ids)
            Clipboard.SetText(text)
            self.StatusText.Text = "Copied {} ID(s)".format(len(elem_ids))
        except Exception as ex:
            self.StatusText.Text = "Copy failed: {}".format(str(ex))
    
    def select_in_revit(self, elem_ids):
        """Select elements in Revit."""
        try:
            select_elements(self.uidoc, elem_ids)
            self.StatusText.Text = "Selected {} element(s)".format(len(elem_ids))
        except Exception as ex:
            self.StatusText.Text = "Select failed: {}".format(str(ex))
    
    def create_section_box_action(self, elem_ids):
        """Create section box for elements."""
        try:
            success, msg = create_section_box(self.uidoc, elem_ids)
            self.StatusText.Text = msg
        except Exception as ex:
            self.StatusText.Text = "Section box failed: {}".format(str(ex))
    
    def on_key_down(self, sender, args):
        """Handle keyboard shortcuts."""
        if args.Key == Key.Escape:
            self.Close()


def main():
    """Main entry point."""
    uidoc = revit.uidoc
    if not uidoc:
        forms.alert("No active document.", title="Element Info")
        return
    
    # Get XAML file path
    script_dir = os.path.dirname(__file__)
    xaml_file = os.path.join(script_dir, "ElementInfoWindow.xaml")
    
    if not os.path.exists(xaml_file):
        forms.alert(
            "XAML file not found:\n{}".format(xaml_file),
            title="Element Info Error"
        )
        return
    
    # Show window
    window = ElementInfoWindow(xaml_file, uidoc)
    window.setup_columns()
    window.ShowDialog()


if __name__ == "__main__" or True:
    main()
