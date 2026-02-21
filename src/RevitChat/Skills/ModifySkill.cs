using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class ModifySkill : IRevitSkill
    {
        public string Name => "Modify";
        public string Description => "Modify parameters, delete elements, select elements in Revit";

        private static readonly HashSet<string> HandledTools = new()
        {
            "set_parameter_value", "delete_elements", "select_elements"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("set_parameter_value",
                "Set a parameter value on one or more elements. IMPORTANT: Always confirm with the user before calling this.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to modify" },
                        "param_name": { "type": "string", "description": "Parameter name to set" },
                        "value": { "type": "string", "description": "New value (as string)" }
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
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to delete" }
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
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc.Document;
            return functionName switch
            {
                "set_parameter_value" => SetParameterValue(doc, args),
                "delete_elements" => DeleteElements(doc, args),
                "select_elements" => SelectElements(uidoc, args),
                _ => JsonError($"ModifySkill: unknown tool '{functionName}'")
            };
        }

        private string SetParameterValue(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var paramName = GetArg<string>(args, "param_name");
            var value = GetArg<string>(args, "value");

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(paramName)) return JsonError("param_name required.");

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

            var elemIds = ids.Select(id => new ElementId(id)).ToList();
            uidoc.Selection.SetElementIds(elemIds);

            return JsonSerializer.Serialize(new
            {
                selected = elemIds.Count,
                message = $"Selected {elemIds.Count} element(s) in the active view."
            }, JsonOpts);
        }

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
