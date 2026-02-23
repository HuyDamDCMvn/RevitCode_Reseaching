using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class ModifySkill : BaseRevitSkill
    {
        protected override string SkillName => "Modify";
        protected override string SkillDescription => "Modify, copy, move, mirror, rename, duplicate elements and views in Revit";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "set_parameter_value", "delete_elements", "select_elements",
            "rename_elements", "copy_elements", "move_elements",
            "mirror_elements", "duplicate_views", "duplicate_sheets",
            "apply_parameter_formula", "transfer_parameters", "batch_update_parameters"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("set_parameter_value",
                "Set a parameter value on one or more elements. IMPORTANT: Always confirm with the user before calling this.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to modify" },
                        "param_name": { "type": "string", "description": "Parameter name to set" },
                        "value": { "type": "string", "description": "New value (as string)" },
                        "dry_run": { "type": "boolean", "description": "Preview changes only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "param_name", "value"]
                }
                """)),

            ChatTool.CreateFunctionTool("delete_elements",
                "Delete elements from the model. DANGEROUS: Always confirm with the user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to delete" },
                        "dry_run": { "type": "boolean", "description": "Preview deletions only (no transaction). Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("select_elements",
                "Select elements in the Revit view so the user can see them highlighted.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to select" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("rename_elements",
                "Rename elements by setting their Mark or Name parameter. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to rename" },
                        "new_name": { "type": "string", "description": "New name/mark value" },
                        "param_name": { "type": "string", "description": "Parameter to set: 'Mark', 'Name', or any writable string param. Default: Mark" },
                        "dry_run": { "type": "boolean", "description": "Preview rename only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "new_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("copy_elements",
                "Copy elements to a new position by offset (X,Y,Z in feet). Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to copy" },
                        "offset_x": { "type": "number", "description": "X offset in feet (default 0)" },
                        "offset_y": { "type": "number", "description": "Y offset in feet (default 0)" },
                        "offset_z": { "type": "number", "description": "Z offset in feet (default 0)" },
                        "dry_run": { "type": "boolean", "description": "Preview copy only (no transaction). Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("move_elements",
                "Move elements by offset (X,Y,Z in feet). Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to move" },
                        "offset_x": { "type": "number", "description": "X offset in feet (default 0)" },
                        "offset_y": { "type": "number", "description": "Y offset in feet (default 0)" },
                        "offset_z": { "type": "number", "description": "Z offset in feet (default 0)" },
                        "dry_run": { "type": "boolean", "description": "Preview move only (no transaction). Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("mirror_elements",
                "Mirror (flip) elements across an axis defined by a point and direction. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to mirror" },
                        "axis": { "type": "string", "enum": ["x", "y"], "description": "Mirror axis: 'x' (mirror across YZ plane) or 'y' (mirror across XZ plane)" },
                        "origin_x": { "type": "number", "description": "X coordinate of axis origin in feet (default 0)" },
                        "origin_y": { "type": "number", "description": "Y coordinate of axis origin in feet (default 0)" },
                        "dry_run": { "type": "boolean", "description": "Preview mirror only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "axis"]
                }
                """)),

            ChatTool.CreateFunctionTool("duplicate_views",
                "Duplicate views (floor plans, sections, 3D views, etc). Creates independent copies.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "view_ids": { "type": "array", "items": { "type": "integer" }, "description": "View IDs to duplicate" },
                        "suffix": { "type": "string", "description": "Suffix to append to duplicated view names (default: ' - Copy')" },
                        "duplicate_option": { "type": "string", "enum": ["independent", "with_detailing", "as_dependent"], "description": "Duplication type (default: independent)" },
                        "dry_run": { "type": "boolean", "description": "Preview duplicate only (no transaction). Default false." }
                    },
                    "required": ["view_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("duplicate_sheets",
                "Duplicate sheets. Creates new sheets with the same titleblock but empty viewports.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "sheet_ids": { "type": "array", "items": { "type": "integer" }, "description": "Sheet IDs to duplicate" },
                        "new_numbers": { "type": "array", "items": { "type": "string" }, "description": "New sheet numbers (must match sheet_ids count)" },
                        "dry_run": { "type": "boolean", "description": "Preview duplicate only (no transaction). Default false." }
                    },
                    "required": ["sheet_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("apply_parameter_formula",
                "Apply a formula to modify parameter values on elements. Supports: +10, -5, *2, /1.5, {ROUND:2}, prefix_, _suffix, {UPPER}, {LOWER}, {TRIM}, {REPLACE:old:new}, {REGEX:pattern:replacement}, {SUB:start:length}. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to update" },
                        "param_name": { "type": "string", "description": "Parameter name" },
                        "formula": { "type": "string", "description": "Formula: +10, -5, *2, /1.5, {ROUND:2}, prefix_, _suffix, {UPPER}, {LOWER}, {REPLACE:old:new}" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["element_ids", "param_name", "formula"]
                }
                """)),

            ChatTool.CreateFunctionTool("transfer_parameters",
                "Copy parameter values from a source element to target elements. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "source_element_id": { "type": "integer", "description": "Element to copy values FROM" },
                        "target_element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Elements to copy values TO" },
                        "param_names": { "type": "array", "items": { "type": "string" }, "description": "Parameter names to copy" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["source_element_id", "target_element_ids", "param_names"]
                }
                """)),

            ChatTool.CreateFunctionTool("batch_update_parameters",
                "Update different parameter values on different elements in one transaction. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "updates": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "element_id": { "type": "integer" },
                                    "param_name": { "type": "string" },
                                    "value": { "type": "string" }
                                },
                                "required": ["element_id", "param_name", "value"]
                            },
                            "description": "List of per-element parameter updates"
                        },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["updates"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "set_parameter_value" => SetParameterValue(doc, args),
                "delete_elements" => DeleteElements(doc, args),
                "select_elements" => SelectElements(uidoc, args),
                "rename_elements" => RenameElements(doc, args),
                "copy_elements" => CopyElements(doc, args),
                "move_elements" => MoveElements(doc, args),
                "mirror_elements" => MirrorElements(doc, args),
                "duplicate_views" => DuplicateViews(doc, args),
                "duplicate_sheets" => DuplicateSheets(doc, args),
                "apply_parameter_formula" => ApplyParameterFormula(doc, args),
                "transfer_parameters" => TransferParameters(doc, args),
                "batch_update_parameters" => BatchUpdateParameters(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string SetParameterValue(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var paramName = GetArg<string>(args, "param_name");
            var value = GetArg<string>(args, "value");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(paramName)) return JsonError("param_name required.");

            if (dryRun)
            {
                int wouldChange = 0, unchanged = 0, previewFailed = 0;
                var previewErrors = new List<string>();
                var preview = new List<object>();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { previewFailed++; previewErrors.Add($"Element {id} not found"); continue; }

                    var param = elem.LookupParameter(paramName);
                    if (param == null) { previewFailed++; previewErrors.Add($"Parameter '{paramName}' not found on {id}"); continue; }
                    if (param.IsReadOnly) { previewFailed++; previewErrors.Add($"Parameter '{paramName}' is read-only on {id}"); continue; }

                    var current = GetParameterValueAsString(doc, param);
                    bool change = !string.Equals(current ?? "", value ?? "", StringComparison.OrdinalIgnoreCase);
                    if (change) wouldChange++; else unchanged++;
                    if (preview.Count < 20)
                        preview.Add(new { id, from = current, to = value, would_change = change });
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_change = wouldChange,
                    unchanged,
                    failed = previewFailed,
                    preview,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0, failed = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Set Parameter"))
            {
                trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found"); continue; }

                    var param = elem.LookupParameter(paramName);
                    if (param == null) { failed++; errors.Add($"Parameter '{paramName}' not found on {id}"); continue; }
                    if (param.IsReadOnly) { failed++; errors.Add($"Parameter '{paramName}' is read-only on {id}"); continue; }

                    if (SetParamValue(param, value))
                        success++;
                    else { failed++; errors.Add($"Failed to set '{paramName}' on {id}"); }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { success, failed, errors = errors.Take(10) }, JsonOpts);
        }

        private string DeleteElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            bool dryRun = GetArg(args, "dry_run", false);

            if (dryRun)
            {
                var previewErrors = new List<string>();
                var items = new List<object>();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { previewErrors.Add($"Element {id} not found"); continue; }
                    items.Add(new { id, category = elem.Category?.Name ?? "-", name = elem.Name ?? "-" });
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    deletable = items.Count,
                    missing = ids.Count - items.Count,
                    elements = items.Take(50),
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int deleted = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Delete Elements"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    try
                    {
                        var elemId = new ElementId(id);
                        if (doc.GetElement(elemId) == null) { errors.Add($"Element {id} not found"); continue; }
                        doc.Delete(elemId);
                        deleted++;
                    }
                    catch (Exception ex) { errors.Add($"Cannot delete {id}: {ex.Message}"); }
                }
                if (deleted > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { deleted, errors = errors.Take(10) }, JsonOpts);
        }

        private string SelectElements(UIDocument uidoc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var doc = uidoc.Document;
            var elemIds = ids
                .Select(id => new ElementId(id))
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            if (elemIds.Count == 0) return JsonError("No valid elements found for the given IDs.");

            uidoc.Selection.SetElementIds(elemIds);

            return JsonSerializer.Serialize(new
            {
                requested = ids.Count,
                selected = elemIds.Count,
                message = $"Selected {elemIds.Count} of {ids.Count} element(s) in the active view."
            }, JsonOpts);
        }

        private string RenameElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var newName = GetArg<string>(args, "new_name");
            var paramName = GetArg(args, "param_name", "Mark");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(newName)) return JsonError("new_name required.");

            if (dryRun)
            {
                int wouldChange = 0, unchanged = 0, previewFailed = 0;
                var previewErrors = new List<string>();
                var preview = new List<object>();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { previewFailed++; previewErrors.Add($"Element {id} not found"); continue; }

                    var param = elem.LookupParameter(paramName);
                    if (param == null || param.IsReadOnly)
                    {
                        param = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    }
                    if (param == null || param.IsReadOnly)
                    {
                        previewFailed++; previewErrors.Add($"No writable '{paramName}' on {id}"); continue;
                    }

                    var current = GetParameterValueAsString(doc, param);
                    bool change = !string.Equals(current ?? "", newName ?? "", StringComparison.OrdinalIgnoreCase);
                    if (change) wouldChange++; else unchanged++;
                    if (preview.Count < 20)
                        preview.Add(new { id, from = current, to = newName, would_change = change });
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    param_name = paramName,
                    new_name = newName,
                    would_change = wouldChange,
                    unchanged,
                    failed = previewFailed,
                    preview,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0, failed = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Rename Elements"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found"); continue; }

                    var param = elem.LookupParameter(paramName);
                    if (param == null || param.IsReadOnly)
                    {
                        param = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    }
                    if (param == null || param.IsReadOnly)
                    {
                        failed++; errors.Add($"No writable '{paramName}' on {id}"); continue;
                    }

                    if (SetParamValue(param, newName)) success++;
                    else { failed++; errors.Add($"Failed to rename {id}"); }
                }
                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { success, failed, param_name = paramName, new_name = newName, errors = errors.Take(10) }, JsonOpts);
        }

        private string CopyElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            double ox = GetArg(args, "offset_x", 0.0);
            double oy = GetArg(args, "offset_y", 0.0);
            double oz = GetArg(args, "offset_z", 0.0);
            var translation = new XYZ(ox, oy, oz);
            bool dryRun = GetArg(args, "dry_run", false);

            var elemIds = ids.Select(id => new ElementId(id)).Where(id => doc.GetElement(id) != null).ToList();
            if (elemIds.Count == 0) return JsonError("No valid elements found.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_copy = elemIds.Count,
                    offset = new { x = ox, y = oy, z = oz }
                }, JsonOpts);
            }

            ICollection<ElementId> copied;
            using (var trans = new Transaction(doc, "AI: Copy Elements"))
            {
                trans.Start();
                try
                {
                    copied = ElementTransformUtils.CopyElements(doc, elemIds, translation);
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    return JsonError($"CopyElements failed: {ex.Message}");
                }
            }

            return JsonSerializer.Serialize(new
            {
                copied = copied.Count,
                new_ids = copied.Select(id => id.Value).ToList(),
                offset = new { x = ox, y = oy, z = oz },
                message = $"Copied {copied.Count} element(s)."
            }, JsonOpts);
        }

        private string MoveElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            double ox = GetArg(args, "offset_x", 0.0);
            double oy = GetArg(args, "offset_y", 0.0);
            double oz = GetArg(args, "offset_z", 0.0);
            var translation = new XYZ(ox, oy, oz);
            bool dryRun = GetArg(args, "dry_run", false);

            if (translation.IsZeroLength()) return JsonError("Offset is zero — nothing to move.");

            var elemIds = ids.Select(id => new ElementId(id)).Where(id => doc.GetElement(id) != null).ToList();
            if (elemIds.Count == 0) return JsonError("No valid elements found.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_move = elemIds.Count,
                    offset = new { x = ox, y = oy, z = oz }
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Move Elements"))
            {
                trans.Start();
                try
                {
                    ElementTransformUtils.MoveElements(doc, elemIds, translation);
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    return JsonError($"MoveElements failed: {ex.Message}");
                }
            }

            return JsonSerializer.Serialize(new
            {
                moved = elemIds.Count,
                offset = new { x = ox, y = oy, z = oz },
                message = $"Moved {elemIds.Count} element(s)."
            }, JsonOpts);
        }

        private string MirrorElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var axis = GetArg(args, "axis", "x");
            double originX = GetArg(args, "origin_x", 0.0);
            double originY = GetArg(args, "origin_y", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            var origin = new XYZ(originX, originY, 0);
            XYZ direction = axis == "y" ? XYZ.BasisX : XYZ.BasisY;
            var plane = Plane.CreateByNormalAndOrigin(direction, origin);

            var elemIds = ids.Select(id => new ElementId(id)).Where(id => doc.GetElement(id) != null).ToList();
            if (elemIds.Count == 0) return JsonError("No valid elements found.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_mirror = elemIds.Count,
                    axis,
                    origin = new { x = originX, y = originY }
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Mirror Elements"))
            {
                trans.Start();
                try
                {
                    ElementTransformUtils.MirrorElements(doc, elemIds, plane, true);
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    return JsonError($"MirrorElements failed: {ex.Message}");
                }
            }

            return JsonSerializer.Serialize(new
            {
                mirrored = elemIds.Count,
                axis,
                origin = new { x = originX, y = originY },
                message = $"Mirrored {elemIds.Count} element(s) across {axis}-axis."
            }, JsonOpts);
        }

        private string DuplicateViews(Document doc, Dictionary<string, object> args)
        {
            var viewIds = GetArgLongArray(args, "view_ids");
            if (viewIds == null || viewIds.Count == 0) return JsonError("view_ids required.");

            var suffix = GetArg(args, "suffix", " - Copy");
            var optStr = GetArg(args, "duplicate_option", "independent");
            bool dryRun = GetArg(args, "dry_run", false);

            var dupOpt = optStr switch
            {
                "with_detailing" => ViewDuplicateOption.WithDetailing,
                "as_dependent" => ViewDuplicateOption.AsDependent,
                _ => ViewDuplicateOption.Duplicate
            };

            if (dryRun)
            {
                int duplicatable = 0;
                var previewResults = new List<object>();
                var previewErrors = new List<string>();

                foreach (var id in viewIds)
                {
                    var view = doc.GetElement(new ElementId(id)) as View;
                    if (view == null) { previewErrors.Add($"Element {id} is not a view"); continue; }
                    if (!view.CanViewBeDuplicated(dupOpt)) { previewErrors.Add($"View '{view.Name}' cannot be duplicated with option '{optStr}'"); continue; }

                    duplicatable++;
                    if (previewResults.Count < 50)
                    {
                        previewResults.Add(new
                        {
                            original = view.Name,
                            new_name = view.Name + suffix
                        });
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    duplicatable,
                    views = previewResults,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0;
            var results = new List<object>();
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Duplicate Views"))
            {
                trans.Start();
                foreach (var id in viewIds)
                {
                    var view = doc.GetElement(new ElementId(id)) as View;
                    if (view == null) { errors.Add($"Element {id} is not a view"); continue; }
                    if (!view.CanViewBeDuplicated(dupOpt)) { errors.Add($"View '{view.Name}' cannot be duplicated with option '{optStr}'"); continue; }

                    try
                    {
                        var newId = view.Duplicate(dupOpt);
                        var newView = doc.GetElement(newId) as View;
                        if (newView != null)
                        {
                            try { newView.Name = view.Name + suffix; } catch { }
                            results.Add(new { original = view.Name, new_id = newId.Value, new_name = newView.Name });
                        }
                        success++;
                    }
                    catch (Exception ex) { errors.Add($"Failed to duplicate '{view.Name}': {ex.Message}"); }
                }
                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { duplicated = success, views = results, errors = errors.Take(10) }, JsonOpts);
        }

        private string DuplicateSheets(Document doc, Dictionary<string, object> args)
        {
            var sheetIds = GetArgLongArray(args, "sheet_ids");
            if (sheetIds == null || sheetIds.Count == 0) return JsonError("sheet_ids required.");

            var newNumbers = GetArgStringArray(args, "new_numbers");
            bool dryRun = GetArg(args, "dry_run", false);

            if (dryRun)
            {
                int duplicatable = 0;
                var previewResults = new List<object>();
                var previewErrors = new List<string>();

                for (int i = 0; i < sheetIds.Count; i++)
                {
                    var sheet = doc.GetElement(new ElementId(sheetIds[i])) as ViewSheet;
                    if (sheet == null) { previewErrors.Add($"Element {sheetIds[i]} is not a sheet"); continue; }

                    var number = (newNumbers != null && i < newNumbers.Count) ? newNumbers[i] : sheet.SheetNumber + "-COPY";
                    previewResults.Add(new
                    {
                        original_number = sheet.SheetNumber,
                        new_number = number,
                        name = sheet.Name
                    });
                    duplicatable++;
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    duplicatable,
                    sheets = previewResults,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0;
            var results = new List<object>();
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Duplicate Sheets"))
            {
                trans.Start();
                for (int i = 0; i < sheetIds.Count; i++)
                {
                    var sheet = doc.GetElement(new ElementId(sheetIds[i])) as ViewSheet;
                    if (sheet == null) { errors.Add($"Element {sheetIds[i]} is not a sheet"); continue; }

                    try
                    {
                        var titleBlockId = ElementId.InvalidElementId;
                        var tbCollector = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType();
                        var tb = tbCollector.FirstOrDefault() as FamilyInstance;
                        if (tb != null) titleBlockId = tb.GetTypeId();

                        var newSheet = ViewSheet.Create(doc, titleBlockId);
                        var number = (newNumbers != null && i < newNumbers.Count) ? newNumbers[i] : sheet.SheetNumber + "-COPY";
                        try { newSheet.SheetNumber = number; } catch { }
                        try { newSheet.Name = sheet.Name; } catch { }

                        results.Add(new
                        {
                            original_number = sheet.SheetNumber,
                            new_id = newSheet.Id.Value,
                            new_number = newSheet.SheetNumber,
                            new_name = newSheet.Name
                        });
                        success++;
                    }
                    catch (Exception ex) { errors.Add($"Failed to duplicate sheet '{sheet.SheetNumber}': {ex.Message}"); }
                }
                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { duplicated = success, sheets = results, errors = errors.Take(10) }, JsonOpts);
        }

        #region apply_parameter_formula

        private string ApplyParameterFormula(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var paramName = GetArg<string>(args, "param_name");
            var formula = GetArg<string>(args, "formula");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(paramName)) return JsonError("param_name required.");
            if (string.IsNullOrEmpty(formula)) return JsonError("formula required.");

            if (dryRun)
            {
                var preview = new List<object>();
                int wouldChange = 0;
                foreach (var id in ids.Take(30))
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) continue;
                    var param = elem.LookupParameter(paramName);
                    if (param == null || param.IsReadOnly) continue;
                    var current = GetParameterValueAsString(doc, param);
                    var storageType = param.StorageType == StorageType.Double ? "Double"
                        : param.StorageType == StorageType.Integer ? "Integer" : "String";
                    var newVal = ApplyFormula(formula, current, storageType);
                    if (newVal != current) wouldChange++;
                    preview.Add(new { id, from = current, to = newVal });
                }
                return JsonSerializer.Serialize(new { dry_run = true, formula, would_change = wouldChange, preview }, JsonOpts);
            }

            int success = 0;
            var errors = new List<string>();
            using (var trans = new Transaction(doc, "AI: Apply Formula"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found"); continue; }
                    var param = elem.LookupParameter(paramName);
                    if (param == null) { errors.Add($"Param '{paramName}' not found on {id}"); continue; }
                    if (param.IsReadOnly) { errors.Add($"Param '{paramName}' read-only on {id}"); continue; }
                    var current = GetParameterValueAsString(doc, param);
                    var storageType = param.StorageType == StorageType.Double ? "Double"
                        : param.StorageType == StorageType.Integer ? "Integer" : "String";
                    var newVal = ApplyFormula(formula, current, storageType);
                    if (SetParamValue(param, newVal)) success++;
                    else errors.Add($"Failed on {id}");
                }
                if (success > 0) trans.Commit(); else trans.RollBack();
            }
            return JsonSerializer.Serialize(new { success, formula, errors = errors.Take(10) }, JsonOpts);
        }

        private static string ApplyFormula(string formula, string currentValue, string storageType)
        {
            if (string.IsNullOrEmpty(formula)) return currentValue;
            formula = formula.Trim();
            bool isNumeric = storageType is "Double" or "Integer";

            if (isNumeric && double.TryParse(currentValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double numValue))
            {
                if (Regex.IsMatch(formula, @"^\+\d+(\.\d+)?$"))
                    return FormatNum(numValue + double.Parse(formula[1..], CultureInfo.InvariantCulture), storageType);
                if (Regex.IsMatch(formula, @"^-\d+(\.\d+)?$"))
                    return FormatNum(numValue - double.Parse(formula[1..], CultureInfo.InvariantCulture), storageType);
                if (Regex.IsMatch(formula, @"^\*\d+(\.\d+)?$"))
                    return FormatNum(numValue * double.Parse(formula[1..], CultureInfo.InvariantCulture), storageType);
                if (Regex.IsMatch(formula, @"^/\d+(\.\d+)?$"))
                {
                    double d = double.Parse(formula[1..], CultureInfo.InvariantCulture);
                    return d == 0 ? currentValue : FormatNum(numValue / d, storageType);
                }
                var roundMatch = Regex.Match(formula, @"^\{ROUND:(\d+)\}$", RegexOptions.IgnoreCase);
                if (roundMatch.Success)
                    return Math.Round(numValue, int.Parse(roundMatch.Groups[1].Value)).ToString(CultureInfo.InvariantCulture);
            }

            string strValue = currentValue ?? "";
            if (formula.EndsWith("_") && !formula.StartsWith("_")) return formula.TrimEnd('_') + strValue;
            if (formula.StartsWith("_") && !formula.EndsWith("_")) return strValue + formula.TrimStart('_');
            if (formula.Equals("{UPPER}", StringComparison.OrdinalIgnoreCase)) return strValue.ToUpperInvariant();
            if (formula.Equals("{LOWER}", StringComparison.OrdinalIgnoreCase)) return strValue.ToLowerInvariant();
            if (formula.Equals("{TRIM}", StringComparison.OrdinalIgnoreCase)) return strValue.Trim();

            var replaceMatch = Regex.Match(formula, @"^\{REPLACE:(.+?):(.+?)\}$", RegexOptions.IgnoreCase);
            if (replaceMatch.Success) return strValue.Replace(replaceMatch.Groups[1].Value, replaceMatch.Groups[2].Value);

            var subMatch = Regex.Match(formula, @"^\{SUB:(\d+):(\d+)\}$", RegexOptions.IgnoreCase);
            if (subMatch.Success)
            {
                int start = int.Parse(subMatch.Groups[1].Value), len = int.Parse(subMatch.Groups[2].Value);
                return start < strValue.Length ? strValue.Substring(start, Math.Min(len, strValue.Length - start)) : "";
            }

            return formula;
        }

        private static string FormatNum(double val, string storageType)
            => storageType == "Integer" ? ((int)val).ToString() : val.ToString(CultureInfo.InvariantCulture);

        #endregion

        #region transfer_parameters

        private string TransferParameters(Document doc, Dictionary<string, object> args)
        {
            long sourceId = GetArg<long>(args, "source_element_id");
            var targetIds = GetArgLongArray(args, "target_element_ids");
            var paramNames = GetArgStringArray(args, "param_names");
            bool dryRun = GetArg(args, "dry_run", false);

            if (sourceId <= 0) return JsonError("source_element_id required.");
            if (targetIds == null || targetIds.Count == 0) return JsonError("target_element_ids required.");
            if (paramNames == null || paramNames.Count == 0) return JsonError("param_names required.");

            var source = doc.GetElement(new ElementId(sourceId));
            if (source == null) return JsonError($"Source element {sourceId} not found.");

            var sourceValues = new Dictionary<string, string>();
            foreach (var pn in paramNames)
            {
                var p = source.LookupParameter(pn);
                sourceValues[pn] = p != null ? GetParameterValueAsString(doc, p) : null;
            }

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    source_id = sourceId,
                    target_count = targetIds.Count,
                    source_values = sourceValues
                }, JsonOpts);
            }

            int success = 0, skipped = 0;
            var errors = new List<string>();
            using (var trans = new Transaction(doc, "AI: Transfer Parameters"))
            {
                trans.Start();
                foreach (var tid in targetIds)
                {
                    var target = doc.GetElement(new ElementId(tid));
                    if (target == null) { errors.Add($"Target {tid} not found"); continue; }
                    int paramSuccess = 0;
                    foreach (var pn in paramNames)
                    {
                        if (sourceValues[pn] == null) continue;
                        var tp = target.LookupParameter(pn);
                        if (tp == null || tp.IsReadOnly) continue;
                        if (SetParamValue(tp, sourceValues[pn])) paramSuccess++;
                    }
                    if (paramSuccess > 0) success++; else skipped++;
                }
                if (success > 0) trans.Commit(); else trans.RollBack();
            }
            return JsonSerializer.Serialize(new { updated = success, skipped, errors = errors.Take(10) }, JsonOpts);
        }

        #endregion

        #region batch_update_parameters

        private string BatchUpdateParameters(Document doc, Dictionary<string, object> args)
        {
            bool dryRun = GetArg(args, "dry_run", false);
            if (!args.TryGetValue("updates", out var updatesObj))
                return JsonError("updates array required.");

            var updates = new List<(long id, string param, string value)>();
            if (updatesObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in je.EnumerateArray())
                {
                    long id = item.TryGetProperty("element_id", out var idProp) ? idProp.GetInt64() : 0;
                    string pn = item.TryGetProperty("param_name", out var pnProp) ? pnProp.GetString() : null;
                    string val = item.TryGetProperty("value", out var valProp) ? valProp.GetString() : null;
                    if (id > 0 && pn != null) updates.Add((id, pn, val ?? ""));
                }
            }

            if (updates.Count == 0) return JsonError("No valid updates provided.");

            if (dryRun)
            {
                var preview = updates.Take(30).Select(u =>
                {
                    var elem = doc.GetElement(new ElementId(u.id));
                    var param = elem?.LookupParameter(u.param);
                    return new { u.id, param_name = u.param, new_value = u.value,
                        current = param != null ? GetParameterValueAsString(doc, param) : "(not found)" };
                }).ToList();
                return JsonSerializer.Serialize(new { dry_run = true, update_count = updates.Count, preview }, JsonOpts);
            }

            int success = 0;
            var errors = new List<string>();
            using (var trans = new Transaction(doc, "AI: Batch Update Parameters"))
            {
                trans.Start();
                foreach (var (id, paramName, value) in updates)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found"); continue; }
                    var param = elem.LookupParameter(paramName);
                    if (param == null || param.IsReadOnly) { errors.Add($"Param '{paramName}' not writable on {id}"); continue; }
                    if (SetParamValue(param, value)) success++;
                    else errors.Add($"Failed on {id}.{paramName}");
                }
                if (success > 0) trans.Commit(); else trans.RollBack();
            }
            return JsonSerializer.Serialize(new { success, total = updates.Count, errors = errors.Take(10) }, JsonOpts);
        }

        #endregion

        private static bool SetParamValue(Parameter param, string value)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value ?? "");
                        return true;
                    case StorageType.Integer:
                        if (param.Definition?.GetDataType() == SpecTypeId.Boolean.YesNo)
                        {
                            var lower = value?.ToLower() ?? "";
                            param.Set(lower is "yes" or "1" or "true" ? 1 : 0);
                            return true;
                        }
                        if (int.TryParse(value, out int iv)) { param.Set(iv); return true; }
                        return false;
                    case StorageType.Double:
                        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dv))
                        { param.Set(dv); return true; }
                        return param.SetValueString(value);
                    case StorageType.ElementId:
                        if (long.TryParse(value, out long lid)) { param.Set(new ElementId(lid)); return true; }
                        return false;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }
    }
}
