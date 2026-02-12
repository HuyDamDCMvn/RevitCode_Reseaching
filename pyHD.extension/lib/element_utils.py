# -*- coding: utf-8 -*-
"""Element utilities for pyHD Extension."""
from __future__ import print_function

import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')

from Autodesk.Revit.DB import (
    FilteredElementCollector, CategoryType, BuiltInParameter,
    FamilyInstance, ElementId, WorksharingUtils, StorageType,
    Transaction, View3D, ViewFamily, ViewFamilyType, BoundingBoxXYZ, XYZ,
    DisplayStyle, ViewDetailLevel
)

# Try to import SpecTypeId (Revit 2022+)
HAS_SPEC_TYPE_ID = False
SpecTypeId = None
try:
    from Autodesk.Revit.DB import SpecTypeId as _SpecTypeId
    SpecTypeId = _SpecTypeId
    HAS_SPEC_TYPE_ID = True
except ImportError:
    pass


def get_element_info(doc, element):
    """
    Get element information dictionary.
    
    Args:
        doc: Revit Document
        element: Revit Element
        
    Returns:
        dict with element info or None if invalid
    """
    if element is None:
        return None
    
    elem_id = element.Id.IntegerValue
    
    # Get Family Name and Type
    family_name = "-"
    family_type = "-"
    
    if isinstance(element, FamilyInstance):
        symbol = element.Symbol
        if symbol:
            family_type = symbol.Name or "-"
            family = symbol.Family
            if family:
                family_name = family.Name or "-"
    else:
        # For system families
        type_id = element.GetTypeId()
        if type_id != ElementId.InvalidElementId:
            elem_type = doc.GetElement(type_id)
            if elem_type:
                family_type = elem_type.Name or "-"
                # Try to get family name from type
                family_param = elem_type.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM)
                if family_param and family_param.HasValue:
                    family_name = family_param.AsString() or "-"
                else:
                    family_name = type(elem_type).__name__
        else:
            family_name = type(element).__name__
            family_type = element.Name or "-"
    
    # Get Category
    category = element.Category.Name if element.Category else "-"
    
    # Get Workset
    workset = "-"
    if doc.IsWorkshared:
        workset_id = element.WorksetId
        if workset_id and workset_id.IntegerValue > 0:
            try:
                ws = doc.GetWorksetTable().GetWorkset(workset_id)
                workset = ws.Name if ws else "-"
            except Exception:
                pass
    
    # Get Created By / Edited By
    created_by = "-"
    edited_by = "-"
    
    try:
        edited_param = element.get_Parameter(BuiltInParameter.EDITED_BY)
        if edited_param and edited_param.HasValue:
            edited_by = edited_param.AsString() or "-"
        
        if doc.IsWorkshared:
            tooltip_info = WorksharingUtils.GetWorksharingTooltipInfo(doc, element.Id)
            if tooltip_info:
                if tooltip_info.Creator:
                    created_by = tooltip_info.Creator
                if tooltip_info.LastChangedBy:
                    edited_by = tooltip_info.LastChangedBy
    except Exception:
        pass
    
    return {
        'id': elem_id,
        'family_name': family_name,
        'family_type': family_type,
        'category': category,
        'workset': workset,
        'created_by': created_by,
        'edited_by': edited_by,
        'element': element,
        'parameters': {},
        'original_params': {},
        'modified_params': set(),
        'readonly_params': set(),
        'param_types': {}
    }


def collect_all_elements(doc):
    """
    Collect all model and annotation elements from document.
    
    Args:
        doc: Revit Document
        
    Returns:
        List of element info dictionaries
    """
    elements = []
    
    collector = FilteredElementCollector(doc).WhereElementIsNotElementType()
    
    for elem in collector:
        if elem.Category is None:
            continue
        
        cat_type = elem.Category.CategoryType
        if cat_type != CategoryType.Model and cat_type != CategoryType.Annotation:
            continue
        
        info = get_element_info(doc, elem)
        if info:
            elements.append(info)
    
    return elements


def _is_yes_no_param(param):
    """Check if parameter is Yes/No type."""
    if not HAS_SPEC_TYPE_ID or SpecTypeId is None:
        return False
    try:
        if param.Definition and param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo:
            return True
    except Exception:
        pass
    return False


def get_parameter_value_as_string(doc, param):
    """
    Get parameter value as display string.
    
    Args:
        doc: Revit Document
        param: Revit Parameter
        
    Returns:
        String representation of parameter value
    """
    if param is None or not param.HasValue:
        return "-"
    
    storage = param.StorageType
    
    if storage == StorageType.String:
        return param.AsString() or "-"
    
    elif storage == StorageType.Integer:
        # Check for Yes/No
        if _is_yes_no_param(param):
            return "Yes" if param.AsInteger() == 1 else "No"
        return str(param.AsInteger())
    
    elif storage == StorageType.Double:
        value_str = param.AsValueString()
        if value_str:
            return value_str
        return "{:.2f}".format(param.AsDouble())
    
    elif storage == StorageType.ElementId:
        elem_id = param.AsElementId()
        if elem_id == ElementId.InvalidElementId:
            return "-"
        # Try to get element name
        if doc:
            ref_elem = doc.GetElement(elem_id)
            if ref_elem and ref_elem.Name:
                return ref_elem.Name
        return str(elem_id.IntegerValue)
    
    return "-"


def get_parameter_data_type(param):
    """
    Get parameter data type string for validation.
    
    Args:
        param: Revit Parameter
        
    Returns:
        String: "String", "Integer", "Double", "YesNo", "ElementId"
    """
    if param is None:
        return "String"
    
    storage = param.StorageType
    
    if storage == StorageType.String:
        return "String"
    
    elif storage == StorageType.Integer:
        if _is_yes_no_param(param):
            return "YesNo"
        return "Integer"
    
    elif storage == StorageType.Double:
        return "Double"
    
    elif storage == StorageType.ElementId:
        return "ElementId"
    
    return "String"


def is_parameter_readonly(param):
    """
    Check if parameter is read-only.
    
    Args:
        param: Revit Parameter
        
    Returns:
        Boolean
    """
    if param is None:
        return True
    
    if param.IsReadOnly:
        return True
    
    # Check for BuiltInParameter
    try:
        internal_def = param.Definition
        if hasattr(internal_def, 'BuiltInParameter'):
            if internal_def.BuiltInParameter != BuiltInParameter.INVALID:
                return param.IsReadOnly
    except Exception:
        pass
    
    return False


def get_element_parameters(doc, element_ids):
    """
    Get all available parameters from elements.
    
    Args:
        doc: Revit Document
        element_ids: List of element IDs
        
    Returns:
        List of dicts with name, is_instance
    """
    param_dict = {}  # name -> is_instance
    
    for elem_id in element_ids:
        element = doc.GetElement(ElementId(elem_id))
        if element is None:
            continue
        
        # Instance parameters
        for param in element.Parameters:
            if param.Definition is None:
                continue
            name = param.Definition.Name
            if name and not name.startswith("INVALID"):
                if name not in param_dict:
                    param_dict[name] = True  # Instance
        
        # Type parameters
        type_id = element.GetTypeId()
        if type_id != ElementId.InvalidElementId:
            elem_type = doc.GetElement(type_id)
            if elem_type:
                for param in elem_type.Parameters:
                    if param.Definition is None:
                        continue
                    name = param.Definition.Name
                    if name and not name.startswith("INVALID"):
                        if name not in param_dict:
                            param_dict[name] = False  # Type
    
    return [{'name': k, 'is_instance': v} for k, v in sorted(param_dict.items())]


def get_parameter_values(doc, element_ids, param_names):
    """
    Get parameter values for elements.
    
    Args:
        doc: Revit Document
        element_ids: List of element IDs
        param_names: List of parameter names
        
    Returns:
        Dict: {elem_id: {param_name: {'value': str, 'readonly': bool, 'type': str}}}
    """
    result = {}
    
    for elem_id in element_ids:
        element = doc.GetElement(ElementId(elem_id))
        if element is None:
            continue
        
        result[elem_id] = {}
        
        for param_name in param_names:
            value = "-"
            readonly = True
            data_type = "String"
            
            # Try instance parameter
            param = element.LookupParameter(param_name)
            if param:
                value = get_parameter_value_as_string(doc, param)
                readonly = is_parameter_readonly(param)
                data_type = get_parameter_data_type(param)
            else:
                # Try type parameter
                type_id = element.GetTypeId()
                if type_id != ElementId.InvalidElementId:
                    elem_type = doc.GetElement(type_id)
                    if elem_type:
                        param = elem_type.LookupParameter(param_name)
                        if param:
                            value = get_parameter_value_as_string(doc, param)
                            readonly = is_parameter_readonly(param)
                            data_type = get_parameter_data_type(param)
            
            result[elem_id][param_name] = {
                'value': value,
                'readonly': readonly,
                'type': data_type
            }
    
    return result


def validate_parameter_value(data_type, value):
    """
    Validate a value against expected data type.
    
    Args:
        data_type: String type name
        value: String value to validate
        
    Returns:
        None if valid, error message string if invalid
    """
    if not value or value == "-":
        return None
    
    if data_type == "Integer":
        try:
            int(value)
        except Exception:
            return "Expected integer value (e.g., 1, 2, 100)"
    
    elif data_type == "Double":
        try:
            float(value.replace(',', '.'))
        except Exception:
            return "Expected numeric value (e.g., 1.5, 100)"
    
    elif data_type == "YesNo":
        valid = ["yes", "no", "true", "false", "1", "0"]
        if value.lower() not in valid:
            return "Expected Yes/No value"
    
    elif data_type == "ElementId":
        try:
            int(value)
        except Exception:
            return "Expected Element ID (numeric)"
    
    return None


def set_parameter_value(doc, elem_id, param_name, new_value, is_instance=True):
    """
    Set parameter value on element.
    
    Args:
        doc: Revit Document
        elem_id: Element ID (int)
        param_name: Parameter name
        new_value: New value string
        is_instance: True for instance, False for type parameter
        
    Returns:
        Tuple: (success, error_message)
    """
    element = doc.GetElement(ElementId(elem_id))
    if element is None:
        return (False, "Element not found")
    
    # Get parameter
    if is_instance:
        param = element.LookupParameter(param_name)
    else:
        type_id = element.GetTypeId()
        if type_id == ElementId.InvalidElementId:
            return (False, "Element has no type")
        elem_type = doc.GetElement(type_id)
        param = elem_type.LookupParameter(param_name) if elem_type else None
    
    if param is None:
        return (False, "Parameter not found")
    
    if param.IsReadOnly:
        return (False, "Parameter is read-only")
    
    try:
        storage = param.StorageType
        
        if storage == StorageType.String:
            val = "" if new_value == "-" else new_value
            param.Set(val)
            return (True, None)
        
        elif storage == StorageType.Integer:
            # Handle Yes/No
            if _is_yes_no_param(param):
                lower = new_value.lower() if new_value else ""
                if lower in ["yes", "true", "1"]:
                    param.Set(1)
                else:
                    param.Set(0)
                return (True, None)
            param.Set(int(new_value))
            return (True, None)
        
        elif storage == StorageType.Double:
            # Try SetValueString first
            if param.SetValueString(new_value):
                return (True, None)
            # Fall back to direct set
            param.Set(float(new_value.replace(',', '.')))
            return (True, None)
        
        elif storage == StorageType.ElementId:
            if not new_value or new_value == "-":
                param.Set(ElementId.InvalidElementId)
            else:
                param.Set(ElementId(int(new_value)))
            return (True, None)
        
    except Exception as ex:
        return (False, str(ex))
    
    return (False, "Unknown storage type")


def select_elements(uidoc, element_ids):
    """
    Select elements in Revit.
    
    Args:
        uidoc: UIDocument
        element_ids: List of element IDs (int)
    """
    from System.Collections.Generic import List
    ids = [ElementId(eid) for eid in element_ids]
    id_list = List[ElementId](ids)
    uidoc.Selection.SetElementIds(id_list)


def create_section_box(uidoc, element_ids):
    """
    Create section box for elements.
    
    Args:
        uidoc: UIDocument
        element_ids: List of element IDs (int)
        
    Returns:
        Tuple: (success, message)
    """
    doc = uidoc.Document
    
    # Calculate combined bounding box
    combined_min = None
    combined_max = None
    
    for elem_id in element_ids:
        elem = doc.GetElement(ElementId(elem_id))
        if elem is None:
            continue
        
        bb = elem.get_BoundingBox(None)
        if bb is None:
            continue
        
        if combined_min is None:
            combined_min = XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)
            combined_max = XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
        else:
            combined_min = XYZ(
                min(combined_min.X, bb.Min.X),
                min(combined_min.Y, bb.Min.Y),
                min(combined_min.Z, bb.Min.Z)
            )
            combined_max = XYZ(
                max(combined_max.X, bb.Max.X),
                max(combined_max.Y, bb.Max.Y),
                max(combined_max.Z, bb.Max.Z)
            )
    
    if combined_min is None:
        return (False, "Could not calculate bounding box")
    
    # Add padding
    offset = 1.0  # 1 foot
    combined_min = XYZ(combined_min.X - offset, combined_min.Y - offset, combined_min.Z - offset)
    combined_max = XYZ(combined_max.X + offset, combined_max.Y + offset, combined_max.Z + offset)
    
    section_box = BoundingBoxXYZ()
    section_box.Min = combined_min
    section_box.Max = combined_max
    
    trans = Transaction(doc)
    try:
        trans.Start("Create Section Box")
        
        view3d = None
        
        # Check if current view is 3D
        if isinstance(uidoc.ActiveView, View3D) and not uidoc.ActiveView.IsTemplate:
            view3d = uidoc.ActiveView
        else:
            # Create new 3D view
            view_types = FilteredElementCollector(doc).OfClass(ViewFamilyType).ToElements()
            
            view_type_3d = None
            for vt in view_types:
                if vt.ViewFamily == ViewFamily.ThreeDimensional:
                    view_type_3d = vt
                    break
            
            if view_type_3d is None:
                trans.RollBack()
                return (False, "Cannot find 3D view family type")
            
            # Generate unique name
            base_name = "Element Information View"
            view_name = base_name
            counter = 1
            
            existing_views = FilteredElementCollector(doc).OfClass(View3D).ToElements()
            existing_names = set(v.Name for v in existing_views)
            
            while view_name in existing_names:
                view_name = "{} {}".format(base_name, counter)
                counter += 1
            
            view3d = View3D.CreateIsometric(doc, view_type_3d.Id)
            view3d.Name = view_name
            view3d.DisplayStyle = DisplayStyle.Shading
            view3d.DetailLevel = ViewDetailLevel.Fine
        
        # Set section box
        view3d.SetSectionBox(section_box)
        view3d.IsSectionBoxActive = True
        
        trans.Commit()
        
        # Activate view
        uidoc.ActiveView = view3d
        
        return (True, "Section box created in '{}'".format(view3d.Name))
    except Exception as ex:
        if trans.HasStarted():
            trans.RollBack()
        return (False, "Transaction error: {}".format(str(ex)))
