using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class QuerySkill : IRevitSkill
    {
        public string Name => "Query";
        public string Description => "Query and search elements in the Revit model";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_elements", "count_elements", "get_element_parameters", "search_elements"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                functionName: "get_elements",
                functionDescription: "Get a list of elements from the Revit model filtered by category, type name, and/or level. Returns element ID, category, family, type, and level for each match.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Revit category name (e.g. 'Walls', 'Doors', 'Windows'). Use get_categories first if unsure." },
                        "type_name": { "type": "string", "description": "Family type name to filter by (partial match)" },
                        "level": { "type": "string", "description": "Level name to filter by" },
                        "limit": { "type": "integer", "description": "Max elements to return. Default 100.", "default": 100 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool(
                functionName: "count_elements",
                functionDescription: "Count elements in the Revit model grouped by type, filtered by category and/or level.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Revit category name" },
                        "level": { "type": "string", "description": "Level name filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool(
                functionName: "get_element_parameters",
                functionDescription: "Get all parameters and values for a specific element by ID.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id": { "type": "integer", "description": "The Revit element ID" }
                    },
                    "required": ["element_id"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                functionName: "search_elements",
                functionDescription: "Search for elements where a specific parameter matches a given value.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "param_name": { "type": "string", "description": "Parameter name to search" },
                        "param_value": { "type": "string", "description": "Value to match (case-insensitive partial match)" },
                        "category": { "type": "string", "description": "Optional category filter" }
                    },
                    "required": ["param_name", "param_value"]
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
                "get_elements" => GetElements(doc, args),
                "count_elements" => CountElements(doc, args),
                "get_element_parameters" => GetElementParameters(doc, args),
                "search_elements" => SearchElements(doc, args),
                _ => JsonError($"QuerySkill: unknown tool '{functionName}'")
            };
        }

        private string GetElements(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var typeName = GetArg<string>(args, "type_name");
            var level = GetArg<string>(args, "level");
            var limit = GetArg<int>(args, "limit", 100);
            if (limit <= 0) limit = 100;

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;
            var results = new List<object>();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !elem.Category.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    continue;

                string elemLevel = null;
                if (!string.IsNullOrEmpty(level))
                {
                    elemLevel = GetElementLevel(doc, elem);
                    if (!elemLevel.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
                }

                string elemType = null;
                if (!string.IsNullOrEmpty(typeName))
                {
                    elemType = GetElementTypeName(doc, elem);
                    if (!elemType.Contains(typeName, StringComparison.OrdinalIgnoreCase)) continue;
                }

                results.Add(new
                {
                    id = elem.Id.Value,
                    category = elem.Category.Name,
                    family = GetFamilyName(doc, elem),
                    type = elemType ?? GetElementTypeName(doc, elem),
                    level = elemLevel ?? GetElementLevel(doc, elem)
                });

                if (results.Count >= limit) break;
            }

            return JsonSerializer.Serialize(new { count = results.Count, elements = results }, JsonOpts);
        }

        private string CountElements(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var level = GetArg<string>(args, "level");

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !elem.Category.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(level) &&
                    !GetElementLevel(doc, elem).Equals(level, StringComparison.OrdinalIgnoreCase))
                    continue;

                var tn = GetElementTypeName(doc, elem);
                typeCounts.TryGetValue(tn, out int c);
                typeCounts[tn] = c + 1;
                total++;
            }

            return JsonSerializer.Serialize(new
            {
                total,
                by_type = typeCounts.OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            }, JsonOpts);
        }

        private string GetElementParameters(Document doc, Dictionary<string, object> args)
        {
            var elementId = GetArg<long>(args, "element_id");
            var elem = doc.GetElement(new ElementId(elementId));
            if (elem == null) return JsonError($"Element {elementId} not found.");

            var parameters = new Dictionary<string, string>();

            foreach (Parameter param in elem.Parameters)
            {
                if (param.Definition == null) continue;
                var name = param.Definition.Name;
                if (string.IsNullOrEmpty(name) || name.StartsWith("INVALID")) continue;
                parameters[name] = GetParameterValueAsString(doc, param);
            }

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    foreach (Parameter param in elemType.Parameters)
                    {
                        if (param.Definition == null) continue;
                        var name = param.Definition.Name;
                        if (string.IsNullOrEmpty(name) || name.StartsWith("INVALID")) continue;
                        var key = $"[Type] {name}";
                        if (!parameters.ContainsKey(key))
                            parameters[key] = GetParameterValueAsString(doc, param);
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                element_id = elementId,
                category = elem.Category?.Name ?? "-",
                family = GetFamilyName(doc, elem),
                type = GetElementTypeName(doc, elem),
                parameters = parameters.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)
            }, JsonOpts);
        }

        private string SearchElements(Document doc, Dictionary<string, object> args)
        {
            var paramName = GetArg<string>(args, "param_name");
            var paramValue = GetArg<string>(args, "param_value");
            var category = GetArg<string>(args, "category");

            if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(paramValue))
                return JsonError("param_name and param_value are required.");

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;
            var results = new List<object>();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !elem.Category.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    continue;

                var param = elem.LookupParameter(paramName);
                if (param == null) continue;

                var val = GetParameterValueAsString(doc, param);
                if (val.Contains(paramValue, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category.Name,
                        family = GetFamilyName(doc, elem),
                        type = GetElementTypeName(doc, elem),
                        matched_value = val
                    });
                }

                if (results.Count >= 200) break;
            }

            return JsonSerializer.Serialize(new { count = results.Count, elements = results }, JsonOpts);
        }
    }
}
