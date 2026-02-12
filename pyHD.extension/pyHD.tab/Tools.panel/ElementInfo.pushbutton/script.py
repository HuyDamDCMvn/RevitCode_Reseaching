# -*- coding: utf-8 -*-
# type: ignore
"""Element Information Tool - Pure Python Implementation.

Shows all elements in a DataGrid with filtering, sorting, parameter editing,
and export capabilities. Matches HD.extension functionality.
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
clr.AddReference('PresentationFramework')
clr.AddReference('WindowsBase')

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
from System.Windows.Controls import (
    DataGridTextColumn, DataGridLength, ContextMenu, MenuItem, Separator,
    CheckBox, TextBlock, Button, StackPanel, Orientation
)
from System.Windows.Data import Binding
from System.Windows.Media import SolidColorBrush, Color
from System.Windows.Input import Key

__title__ = "Element\nInfo"
__doc__ = "View and edit element information with filtering and export capabilities."


class FilterItem(object):
    """Filter item for checkbox list."""
    def __init__(self, value, is_selected=True):
        self.value = value
        self.is_selected = is_selected


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
    
    def get_value(self, column_name):
        """Get value by column name for filtering."""
        if column_name == "id":
            return str(self.id)
        elif column_name == "family_name":
            return self.family_name
        elif column_name == "family_type":
            return self.family_type
        elif column_name == "category":
            return self.category
        elif column_name == "workset":
            return self.workset
        elif column_name == "created_by":
            return self.created_by
        elif column_name == "edited_by":
            return self.edited_by
        else:
            return self.get_param(column_name)


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
        self._current_filter_column = None
        self._filter_items = []  # List of FilterItem for popup
        self._filter_checkboxes = []  # List of CheckBox controls
        
        # Modified cell brush
        self._modified_brush = SolidColorBrush(Color.FromRgb(200, 230, 201))
        
        # Setup events
        self.CloseButton.Click += self.close_click
        self.RefreshButton.Click += self.refresh_click
        self.ClearFilterButton.Click += self.clear_filter_click
        self.ExportButton.Click += self.export_click
        self.AddParamButton.Click += self.add_param_click
        self.UpdateButton.Click += self.update_click
        self.ElementsGrid.MouseRightButtonUp += self.grid_right_click
        self.ElementsGrid.CellEditEnding += self.cell_edit_ending
        self.ElementsGrid.BeginningEdit += self.beginning_edit
        
        # Filter popup events
        self.PopupSearchBox.TextChanged += self.popup_search_changed
        self.SelectAllBtn.Click += self.select_all_click
        self.ClearAllBtn.Click += self.clear_all_click
        self.ApplyFilterBtn.Click += self.apply_filter_click
        
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
            self._column_filters = {}
            
            self.update_grid()
            self.CountText.Text = "{} elements".format(len(self._all_elements))
            self.StatusText.Text = "Loaded {} elements".format(len(self._all_elements))
        except Exception as ex:
            self.StatusText.Text = "Error: {}".format(str(ex))
    
    def apply_filters(self):
        """Apply all column filters to get filtered elements."""
        if not self._column_filters:
            self._filtered_elements = list(self._all_elements)
            return
        
        result = []
        for elem in self._all_elements:
            match = True
            for col_name, selected_values in self._column_filters.items():
                if selected_values:  # Only filter if there are selected values
                    elem_value = elem.get_value(col_name)
                    if elem_value not in selected_values:
                        match = False
                        break
            if match:
                result.append(elem)
        
        self._filtered_elements = result
    
    def update_grid(self):
        """Update DataGrid with filtered elements."""
        self.ElementsGrid.ItemsSource = None
        self.ElementsGrid.ItemsSource = self._filtered_elements
        self.CountText.Text = "{} / {} elements".format(
            len(self._filtered_elements), len(self._all_elements))
    
    def setup_columns(self):
        """Setup default columns with filter buttons."""
        self.ElementsGrid.Columns.Clear()
        
        # Fixed columns with filter buttons
        columns = [
            ("ID", "id", 80),
            ("Family Name", "family_name", 150),
            ("Family Type", "family_type", 150),
            ("Category", "category", 120),
            ("Workset", "workset", 100),
            ("Created By", "created_by", 100),
            ("Edited By", "edited_by", 100),
        ]
        
        for header, binding_path, width in columns:
            col = DataGridTextColumn()
            col.Width = DataGridLength(width)
            col.IsReadOnly = True
            col.Binding = Binding(binding_path)
            
            # Create header with filter button
            header_panel = StackPanel()
            header_panel.Orientation = Orientation.Horizontal
            
            header_text = TextBlock()
            header_text.Text = header
            header_text.VerticalAlignment = 1  # Center
            header_text.Margin = System.Windows.Thickness(0, 0, 8, 0)
            header_panel.Children.Add(header_text)
            
            filter_btn = Button()
            filter_btn.Content = "▼"
            filter_btn.FontSize = 10
            filter_btn.Padding = System.Windows.Thickness(4, 2, 4, 2)
            filter_btn.Background = SolidColorBrush(Color.FromRgb(224, 224, 224))
            filter_btn.BorderThickness = System.Windows.Thickness(0)
            filter_btn.Tag = binding_path
            filter_btn.ToolTip = "Filter"
            filter_btn.Click += self.filter_button_click
            header_panel.Children.Add(filter_btn)
            
            col.Header = header_panel
            self.ElementsGrid.Columns.Add(col)
    
    def add_parameter_column(self, param_name, is_instance):
        """Add a parameter column to the grid with filter button."""
        if param_name in self._param_columns:
            return
        
        self._param_columns.append(param_name)
        self._param_is_instance[param_name] = is_instance
        
        # Create column
        col = DataGridTextColumn()
        col.Width = DataGridLength(140)
        col.IsReadOnly = False
        
        # Create header with filter button
        header_text = "{} ({})".format(param_name, "I" if is_instance else "T")
        
        header_panel = StackPanel()
        header_panel.Orientation = Orientation.Horizontal
        
        txt = TextBlock()
        txt.Text = header_text
        txt.VerticalAlignment = 1
        txt.Margin = System.Windows.Thickness(0, 0, 8, 0)
        header_panel.Children.Add(txt)
        
        filter_btn = Button()
        filter_btn.Content = "▼"
        filter_btn.FontSize = 10
        filter_btn.Padding = System.Windows.Thickness(4, 2, 4, 2)
        filter_btn.Background = SolidColorBrush(Color.FromRgb(224, 224, 224))
        filter_btn.BorderThickness = System.Windows.Thickness(0)
        filter_btn.Tag = param_name
        filter_btn.ToolTip = "Filter"
        filter_btn.Click += self.filter_button_click
        header_panel.Children.Add(filter_btn)
        
        col.Header = header_panel
        self.ElementsGrid.Columns.Add(col)
    
    def filter_button_click(self, sender, args):
        """Show filter popup for column."""
        btn = sender
        column_name = str(btn.Tag)
        self._current_filter_column = column_name
        
        # Get unique values for this column from ALL elements
        unique_values = set()
        for elem in self._all_elements:
            val = elem.get_value(column_name)
            if val:
                unique_values.add(val)
        
        # Sort values
        sorted_values = sorted(unique_values)
        
        # Get currently selected values (if filter exists)
        current_selected = self._column_filters.get(column_name, set())
        if not current_selected:
            # Default: all selected
            current_selected = unique_values
        
        # Create filter items
        self._filter_items = []
        for val in sorted_values:
            item = FilterItem(val, val in current_selected)
            self._filter_items.append(item)
        
        # Populate popup
        self.populate_filter_popup()
        
        # Show popup
        self.FilterPopup.PlacementTarget = btn
        self.FilterPopup.IsOpen = True
        self.PopupSearchBox.Text = ""
        self.PopupSearchBox.Focus()
    
    def populate_filter_popup(self, search_text=""):
        """Populate filter popup with checkboxes."""
        self.FilterItemsPanel.Children.Clear()
        self._filter_checkboxes = []
        
        search_lower = search_text.lower() if search_text else ""
        
        for item in self._filter_items:
            if search_lower and search_lower not in item.value.lower():
                continue
            
            cb = CheckBox()
            cb.Content = item.value
            cb.IsChecked = item.is_selected
            cb.Tag = item
            cb.Margin = System.Windows.Thickness(0, 3, 0, 3)
            cb.Checked += self.filter_checkbox_changed
            cb.Unchecked += self.filter_checkbox_changed
            
            self.FilterItemsPanel.Children.Add(cb)
            self._filter_checkboxes.append(cb)
    
    def filter_checkbox_changed(self, sender, args):
        """Update filter item when checkbox changes."""
        cb = sender
        item = cb.Tag
        item.is_selected = cb.IsChecked
    
    def popup_search_changed(self, sender, args):
        """Filter popup items by search text."""
        search_text = self.PopupSearchBox.Text
        self.populate_filter_popup(search_text)
    
    def select_all_click(self, sender, args):
        """Select all filter items."""
        for item in self._filter_items:
            item.is_selected = True
        for cb in self._filter_checkboxes:
            cb.IsChecked = True
    
    def clear_all_click(self, sender, args):
        """Clear all filter items."""
        for item in self._filter_items:
            item.is_selected = False
        for cb in self._filter_checkboxes:
            cb.IsChecked = False
    
    def apply_filter_click(self, sender, args):
        """Apply filter and close popup."""
        if self._current_filter_column:
            # Get selected values
            selected_values = set()
            for item in self._filter_items:
                if item.is_selected:
                    selected_values.add(item.value)
            
            if selected_values:
                self._column_filters[self._current_filter_column] = selected_values
            else:
                # No items selected = show nothing (or could show all)
                self._column_filters[self._current_filter_column] = set()
        
        self.FilterPopup.IsOpen = False
        
        # Apply all filters
        self.apply_filters()
        self.update_grid()
        
        filter_count = sum(1 for v in self._column_filters.values() if v)
        if filter_count > 0:
            self.StatusText.Text = "Filter applied ({} column(s))".format(filter_count)
        else:
            self.StatusText.Text = "Ready"
    
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
        self.update_grid()
        self.StatusText.Text = "Filters cleared"
    
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
                
                self.StatusText.Text = "Exported {} rows to: {}".format(
                    len(self._filtered_elements), os.path.basename(file_path))
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
            title="Select Parameters to Add",
            button_name="Add Columns",
            multiselect=True
        )
        
        if not selected_options:
            return
        
        # Parse selections and add columns
        added_count = 0
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
            
            if name in self._param_columns:
                continue
            
            # Get values for all elements
            all_ids = [e.id for e in self._all_elements]
            values = get_parameter_values(self.doc, all_ids, [name])
            
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
            added_count += 1
        
        self.update_grid()
        self.StatusText.Text = "Added {} parameter column(s)".format(added_count)
    
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
            "Update {} parameter value(s) to Revit model?".format(len(updates)),
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
        
        # Get header text
        header = col.Header
        if header is None:
            args.Cancel = True
            return
        
        # Try to get text from header
        header_text = ""
        try:
            if hasattr(header, 'Children'):
                for child in header.Children:
                    if hasattr(child, 'Text'):
                        header_text = child.Text
                        break
            elif hasattr(header, 'Text'):
                header_text = header.Text
            else:
                header_text = str(header)
        except Exception:
            args.Cancel = True
            return
        
        # Only allow editing parameter columns
        if " (I)" not in header_text and " (T)" not in header_text:
            args.Cancel = True
            return
        
        # Extract param name
        if " (I)" in header_text:
            param_name = header_text.replace(" (I)", "")
        else:
            param_name = header_text.replace(" (T)", "")
        
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
        
        # Get header text
        header = col.Header
        header_text = ""
        try:
            if hasattr(header, 'Children'):
                for child in header.Children:
                    if hasattr(child, 'Text'):
                        header_text = child.Text
                        break
            elif hasattr(header, 'Text'):
                header_text = header.Text
            else:
                header_text = str(header)
        except Exception:
            return
        
        # Only process parameter columns
        if " (I)" not in header_text and " (T)" not in header_text:
            return
        
        if " (I)" in header_text:
            param_name = header_text.replace(" (I)", "")
        else:
            param_name = header_text.replace(" (T)", "")
        
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
            self.StatusText.Text = "Copied {} ID(s) to clipboard".format(len(elem_ids))
        except Exception as ex:
            self.StatusText.Text = "Copy failed: {}".format(str(ex))
    
    def select_in_revit(self, elem_ids):
        """Select elements in Revit."""
        try:
            select_elements(self.uidoc, elem_ids)
            self.StatusText.Text = "Selected {} element(s) in Revit".format(len(elem_ids))
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


# Need to import System for Thickness
import System


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
