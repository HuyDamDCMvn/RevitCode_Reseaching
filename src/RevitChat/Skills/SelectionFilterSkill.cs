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
    public class SelectionFilterSkill : IRevitSkill
    {
        public string Name => "SelectionFilter";
        public string Description => "Advanced selection: select by parameter value, bounding box, view, and summarize selection";

        private static readonly HashSet<string> HandledTools = new()
        {
            "select_by_parameter_value", "select_by_bounding_box",
            "select_elements_in_view", "get_selection_summary"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("select_by_parameter_value",
                "Select (highlight) elements matching a parameter value in Revit.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category to search" },
                        "parameter_name": { "type": "string", "description": "Parameter name to match" },
                        "value": { "type": "string", "description": "Value to match (partial match for strings)" },
                        "match_type": { "type": "string", "enum": ["equals", "contains", "starts_with", "ends_with", "greater", "less"], "description": "Match type (default: contains)" }
                    },
                    "required": ["category", "parameter_name", "value"]
                }
                """)),

            ChatTool.CreateFunctionTool("select_by_bounding_box",
                "Select elements within a specified 3D bounding box region.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "min_x": { "type": "number", "description": "Min X in feet" },
                        "min_y": { "type": "number", "description": "Min Y in feet" },
                        "min_z": { "type": "number", "description": "Min Z in feet" },
                        "max_x": { "type": "number", "description": "Max X in feet" },
                        "max_y": { "type": "number", "description": "Max Y in feet" },
                        "max_z": { "type": "number", "description": "Max Z in feet" },
                        "category": { "type": "string", "description": "Optional: filter by category" }
                    },
                    "required": ["min_x", "min_y", "min_z", "max_x", "max_y", "max_z"]
                }
                """)),

            ChatTool.CreateFunctionTool("select_elements_in_view",
                "Select all visible elements in the active view, optionally filtered by category.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Optional: filter by category" },
                        "max_select": { "type": "integer", "description": "Max elements to select (default 500)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_selection_summary",
                "Get a detailed summary of the currently selected elements in Revit.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
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
                "select_by_parameter_value" => SelectByParameterValue(uidoc, doc, args),
                "select_by_bounding_box" => SelectByBoundingBox(uidoc, doc, args),
                "select_elements_in_view" => SelectElementsInView(uidoc, doc, args),
                "get_selection_summary" => GetSelectionSummary(uidoc, doc),
                _ => JsonError($"SelectionFilterSkill: unknown tool '{functionName}'")
            };
        }

        private string SelectByParameterValue(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            var paramName = GetArg<string>(args, "parameter_name");
            var value = GetArg<string>(args, "value");
            var matchType = GetArg(args, "match_type", "contains");

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var matched = new List<Element>();
            foreach (var elem in elements)
            {
                var param = elem.LookupParameter(paramName);
                if (param == null) continue;

                string paramVal = GetParameterValueAsString(doc, param);
                if (paramVal == "-") continue;

                bool match = matchType switch
                {
                    "equals" => paramVal.Equals(value, StringComparison.OrdinalIgnoreCase),
                    "starts_with" => paramVal.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                    "ends_with" => paramVal.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                    "greater" => double.TryParse(paramVal, out double pv) && double.TryParse(value, out double v1) && pv > v1,
                    "less" => double.TryParse(paramVal, out double pv2) && double.TryParse(value, out double v2) && pv2 < v2,
                    _ => paramVal.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                };

                if (match) matched.Add(elem);
            }

            if (matched.Count > 0)
            {
                var ids = matched.Select(e => e.Id).ToList();
                uidoc.Selection.SetElementIds(ids);
            }

            return JsonSerializer.Serialize(new
            {
                category = catName,
                parameter = paramName,
                value,
                match_type = matchType,
                total_checked = elements.Count,
                selected_count = matched.Count,
                sample_ids = matched.Take(10).Select(e => e.Id.Value).ToList()
            }, JsonOpts);
        }

        private string SelectByBoundingBox(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            double minX = GetArg(args, "min_x", 0.0);
            double minY = GetArg(args, "min_y", 0.0);
            double minZ = GetArg(args, "min_z", 0.0);
            double maxX = GetArg(args, "max_x", 0.0);
            double maxY = GetArg(args, "max_y", 0.0);
            double maxZ = GetArg(args, "max_z", 0.0);
            var catFilter = GetArg<string>(args, "category");

            var outline = new Outline(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
            var bbFilter = new BoundingBoxIntersectsFilter(outline);

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(bbFilter);

            if (!string.IsNullOrEmpty(catFilter))
            {
                var bic = ResolveCategoryFilter(doc, catFilter);
                if (bic.HasValue) collector = collector.OfCategory(bic.Value);
            }

            var elements = collector.ToList();

            if (elements.Count > 0)
            {
                uidoc.Selection.SetElementIds(elements.Select(e => e.Id).ToList());
            }

            var byCat = elements
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category.Name)
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count)
                .Take(10).ToList();

            return JsonSerializer.Serialize(new
            {
                bounding_box = new { minX, minY, minZ, maxX, maxY, maxZ },
                selected_count = elements.Count,
                by_category = byCat
            }, JsonOpts);
        }

        private string SelectElementsInView(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category");
            int maxSelect = GetArg(args, "max_select", 500);

            var view = doc.ActiveView;
            if (view == null) return JsonError("No active view.");

            var collector = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType();

            if (!string.IsNullOrEmpty(catFilter))
            {
                var bic = ResolveCategoryFilter(doc, catFilter);
                if (bic.HasValue) collector = collector.OfCategory(bic.Value);
            }

            var elements = collector.ToList();
            var toSelect = elements.Take(maxSelect).ToList();

            if (toSelect.Count > 0)
            {
                uidoc.Selection.SetElementIds(toSelect.Select(e => e.Id).ToList());
            }

            return JsonSerializer.Serialize(new
            {
                view_name = view.Name,
                total_in_view = elements.Count,
                selected_count = toSelect.Count,
                capped = elements.Count > maxSelect
            }, JsonOpts);
        }

        private string GetSelectionSummary(UIDocument uidoc, Document doc)
        {
            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
                return JsonSerializer.Serialize(new { selected_count = 0, message = "No elements currently selected." }, JsonOpts);

            var elements = selectedIds.Select(id => doc.GetElement(id)).Where(e => e != null).ToList();

            var byCat = elements
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    category = g.Key,
                    count = g.Count(),
                    sample_ids = g.Take(3).Select(e => e.Id.Value).ToList()
                }).ToList();

            var byFamily = elements
                .GroupBy(e => GetFamilyName(doc, e))
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new { family = g.Key, count = g.Count() })
                .ToList();

            var byLevel = elements
                .GroupBy(e => GetElementLevel(doc, e))
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new { level = g.Key, count = g.Count() })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                selected_count = elements.Count,
                by_category = byCat,
                by_family = byFamily,
                by_level = byLevel
            }, JsonOpts);
        }
    }
}
