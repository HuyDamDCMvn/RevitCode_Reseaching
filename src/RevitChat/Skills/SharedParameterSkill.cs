using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class SharedParameterSkill : IRevitSkill
    {
        public string Name => "SharedParameter";
        public string Description => "Manage shared and project parameters, check bindings and missing values";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_shared_parameters", "get_project_parameters", "check_parameter_values",
            "add_project_parameter", "get_parameter_bindings"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_shared_parameters",
                "List all shared parameters currently used in the document.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter": { "type": "string", "description": "Optional: filter by parameter name (partial match)" },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_project_parameters",
                "List all project parameters with their category bindings.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter": { "type": "string", "description": "Optional: filter by parameter name (partial match)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_parameter_values",
                "Check elements for missing or empty values on a required parameter.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "parameter_name": { "type": "string", "description": "Parameter name to check" },
                        "category": { "type": "string", "description": "Category to check elements in" },
                        "limit": { "type": "integer", "description": "Max elements with missing values to return (default 50)" }
                    },
                    "required": ["parameter_name", "category"]
                }
                """)),

            ChatTool.CreateFunctionTool("add_project_parameter",
                "Add a new project parameter to specified categories. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "parameter_name": { "type": "string", "description": "Name for the new parameter" },
                        "group": { "type": "string", "enum": ["General", "Identity Data", "Constraints", "Dimensions", "Structural", "Mechanical", "Electrical", "Other"], "description": "Parameter group (default: General)" },
                        "categories": { "type": "array", "items": { "type": "string" }, "description": "Category names to bind parameter to" },
                        "is_instance": { "type": "boolean", "description": "Instance parameter (true) or Type parameter (false). Default: true" },
                        "data_type": { "type": "string", "enum": ["text", "integer", "number", "yes_no", "length", "area", "volume"], "description": "Parameter data type (default: text)" }
                    },
                    "required": ["parameter_name", "categories"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_parameter_bindings",
                "Get detailed binding info for a specific parameter: instance vs type, bound categories.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "parameter_name": { "type": "string", "description": "Parameter name to inspect" }
                    },
                    "required": ["parameter_name"]
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "get_shared_parameters" => GetSharedParameters(doc, args),
                "get_project_parameters" => GetProjectParameters(doc, args),
                "check_parameter_values" => CheckParameterValues(doc, args),
                "add_project_parameter" => AddProjectParameter(doc, args),
                "get_parameter_bindings" => GetParameterBindings(doc, args),
                _ => JsonError($"SharedParameterSkill: unknown tool '{functionName}'")
            };
        }

        private string GetSharedParameters(Document doc, Dictionary<string, object> args)
        {
            var filter = GetArg<string>(args, "filter");
            int limit = GetArg(args, "limit", 100);

            var bindingMap = doc.ParameterBindings;
            var iter = bindingMap.ForwardIterator();

            var items = new List<object>();
            while (iter.MoveNext())
            {
                var def = iter.Key;
                if (def == null) continue;

                if (!string.IsNullOrEmpty(filter) &&
                    def.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var binding = iter.Current as ElementBinding;
                var catNames = new List<string>();
                if (binding?.Categories != null)
                {
                    foreach (Category cat in binding.Categories)
                        catNames.Add(cat.Name);
                }

                var isInstance = binding is InstanceBinding;
                var isShared = def is ExternalDefinition;

                items.Add(new
                {
                    name = def.Name,
                    is_shared = isShared,
                    is_instance = isInstance,
                    binding_type = isInstance ? "Instance" : "Type",
                    categories = catNames,
                    parameter_group = def.GetGroupTypeId()?.TypeId ?? "-"
                });

                if (items.Count >= limit) break;
            }

            return JsonSerializer.Serialize(new { total = items.Count, parameters = items }, JsonOpts);
        }

        private string GetProjectParameters(Document doc, Dictionary<string, object> args)
        {
            var filter = GetArg<string>(args, "filter");

            var bindingMap = doc.ParameterBindings;
            var iter = bindingMap.ForwardIterator();

            var items = new List<object>();
            while (iter.MoveNext())
            {
                var def = iter.Key;
                if (def == null) continue;

                if (!string.IsNullOrEmpty(filter) &&
                    def.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var binding = iter.Current as ElementBinding;
                var catNames = new List<string>();
                if (binding?.Categories != null)
                {
                    foreach (Category cat in binding.Categories)
                        catNames.Add(cat.Name);
                }

                items.Add(new
                {
                    name = def.Name,
                    binding_type = binding is InstanceBinding ? "Instance" : "Type",
                    category_count = catNames.Count,
                    categories = catNames
                });
            }

            return JsonSerializer.Serialize(new { total = items.Count, project_parameters = items }, JsonOpts);
        }

        private string CheckParameterValues(Document doc, Dictionary<string, object> args)
        {
            var paramName = GetArg<string>(args, "parameter_name");
            var catName = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 50);

            if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(catName))
                return JsonError("parameter_name and category required.");

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var missing = new List<object>();
            int hasValue = 0;

            foreach (var elem in elements)
            {
                var param = elem.LookupParameter(paramName);
                if (param == null || !param.HasValue ||
                    (param.StorageType == StorageType.String && string.IsNullOrWhiteSpace(param.AsString())))
                {
                    if (missing.Count < limit)
                    {
                        missing.Add(new
                        {
                            id = elem.Id.Value,
                            name = elem.Name,
                            current_value = param != null ? GetParameterValueAsString(doc, param) : "parameter not found"
                        });
                    }
                }
                else
                {
                    hasValue++;
                }
            }

            return JsonSerializer.Serialize(new
            {
                category = catName,
                parameter = paramName,
                total_elements = elements.Count,
                with_value = hasValue,
                missing_count = elements.Count - hasValue,
                completion_rate = elements.Count > 0 ? $"{hasValue * 100 / elements.Count}%" : "N/A",
                missing_elements = missing
            }, JsonOpts);
        }

        private string AddProjectParameter(Document doc, Dictionary<string, object> args)
        {
            var paramName = GetArg<string>(args, "parameter_name");
            var categoryNames = GetArgStringArray(args, "categories");
            bool isInstance = GetArg(args, "is_instance", true);
            var groupStr = GetArg(args, "group", "General");
            var dataTypeStr = GetArg(args, "data_type", "text");

            if (string.IsNullOrEmpty(paramName))
                return JsonError("parameter_name required.");
            if (categoryNames == null || categoryNames.Count == 0)
                return JsonError("categories required.");

            var catSet = new CategorySet();
            foreach (var cn in categoryNames)
            {
                var bic = ResolveCategoryFilter(doc, cn);
                if (bic.HasValue)
                {
                    var cat = Category.GetCategory(doc, bic.Value);
                    if (cat != null) catSet.Insert(cat);
                }
            }

            if (catSet.IsEmpty) return JsonError("No valid categories resolved.");

            var specTypeId = dataTypeStr switch
            {
                "integer" => SpecTypeId.Int.Integer,
                "number" => SpecTypeId.Number,
                "yes_no" => SpecTypeId.Boolean.YesNo,
                "length" => SpecTypeId.Length,
                "area" => SpecTypeId.Area,
                "volume" => SpecTypeId.Volume,
                _ => SpecTypeId.String.Text
            };

            var groupTypeId = groupStr switch
            {
                "Identity Data" => GroupTypeId.IdentityData,
                "Constraints" => GroupTypeId.Constraints,
                "Dimensions" => GroupTypeId.Geometry,
                "Structural" => GroupTypeId.Structural,
                "Mechanical" => GroupTypeId.Mechanical,
                "Electrical" => GroupTypeId.Electrical,
                "Other" => GroupTypeId.General,
                _ => GroupTypeId.General
            };

            var revitApp = doc.Application;
            string originalFile = revitApp.SharedParametersFilename;
            string tempFile = Path.GetTempFileName();

            try
            {
                revitApp.SharedParametersFilename = tempFile;
                var defFile = revitApp.OpenSharedParameterFile();
                if (defFile == null) return JsonError("Cannot create shared parameter definition file.");

                var group = defFile.Groups.Create("AI_Parameters");
                var opts = new ExternalDefinitionCreationOptions(paramName, specTypeId) { UserModifiable = true };
                var definition = group.Definitions.Create(opts);

                using (var trans = new Transaction(doc, "AI: Add Project Parameter"))
                {
                    trans.Start();

                    ElementBinding binding = isInstance
                        ? (ElementBinding)revitApp.Create.NewInstanceBinding(catSet)
                        : revitApp.Create.NewTypeBinding(catSet);

                    bool success = doc.ParameterBindings.Insert(definition, binding, groupTypeId);

                    if (!success)
                    {
                        trans.RollBack();
                        return JsonError("Failed to add parameter. It may already exist.");
                    }

                    trans.Commit();
                }
            }
            finally
            {
                revitApp.SharedParametersFilename = originalFile;
                try { File.Delete(tempFile); } catch { }
            }

            return JsonSerializer.Serialize(new
            {
                added = true,
                name = paramName,
                binding = isInstance ? "Instance" : "Type",
                data_type = dataTypeStr,
                categories = categoryNames
            }, JsonOpts);
        }

        private string GetParameterBindings(Document doc, Dictionary<string, object> args)
        {
            var paramName = GetArg<string>(args, "parameter_name");
            if (string.IsNullOrEmpty(paramName)) return JsonError("parameter_name required.");

            var bindingMap = doc.ParameterBindings;
            var iter = bindingMap.ForwardIterator();

            while (iter.MoveNext())
            {
                var def = iter.Key;
                if (def?.Name?.Equals(paramName, StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                var binding = iter.Current as ElementBinding;
                var catNames = new List<string>();
                if (binding?.Categories != null)
                {
                    foreach (Category cat in binding.Categories)
                        catNames.Add(cat.Name);
                }

                return JsonSerializer.Serialize(new
                {
                    name = def.Name,
                    binding_type = binding is InstanceBinding ? "Instance" : "Type",
                    is_shared = def is ExternalDefinition,
                    group = def.GetGroupTypeId()?.TypeId ?? "-",
                    category_count = catNames.Count,
                    categories = catNames
                }, JsonOpts);
            }

            return JsonError($"Parameter '{paramName}' not found in project parameter bindings.");
        }
    }
}
