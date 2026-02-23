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
    public class RevitLinkSkill : BaseRevitSkill
    {
        protected override string SkillName => "RevitLink";
        protected override string SkillDescription => "Query Revit linked models: list links, get/count/search elements in linked documents";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_linked_models", "get_linked_elements", "count_linked_elements",
            "get_linked_element_parameters", "search_linked_elements", "get_link_types"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_linked_models",
                "List all Revit Link instances in the current model with name, path, loaded status, and position info.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_linked_elements",
                "Get elements from a linked Revit model by category and optional type filter.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "link_name": { "type": "string", "description": "Name or partial name of the linked model" },
                        "category": { "type": "string", "description": "Category name (e.g. 'Walls', 'Ducts', 'Pipes')" },
                        "type_name": { "type": "string", "description": "Optional: filter by type name (partial match)" },
                        "level": { "type": "string", "description": "Optional: filter by level name" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": ["link_name", "category"]
                }
                """)),

            ChatTool.CreateFunctionTool("count_linked_elements",
                "Count elements in a linked Revit model by category, optionally grouped by type or level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "link_name": { "type": "string", "description": "Name or partial name of the linked model" },
                        "category": { "type": "string", "description": "Category name" },
                        "group_by": { "type": "string", "enum": ["type", "level", "none"], "description": "Group results by type or level (default: none)" }
                    },
                    "required": ["link_name", "category"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_linked_element_parameters",
                "Get all parameters of a specific element in a linked model.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "link_name": { "type": "string", "description": "Name or partial name of the linked model" },
                        "element_id": { "type": "integer", "description": "Element ID in the linked model" }
                    },
                    "required": ["link_name", "element_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("search_linked_elements",
                "Search elements in a linked model by parameter value.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "link_name": { "type": "string", "description": "Name or partial name of the linked model" },
                        "category": { "type": "string", "description": "Category name to search in" },
                        "param_name": { "type": "string", "description": "Parameter name to search" },
                        "param_value": { "type": "string", "description": "Value to match (partial, case-insensitive)" },
                        "limit": { "type": "integer", "description": "Max results (default 30)" }
                    },
                    "required": ["link_name", "param_name", "param_value"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_link_types",
                "List all RevitLinkType definitions showing path type, nested status, and whether each is loaded.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """))
        };

        public override string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "get_linked_models" => GetLinkedModels(doc),
                "get_linked_elements" => GetLinkedElements(doc, args),
                "count_linked_elements" => CountLinkedElements(doc, args),
                "get_linked_element_parameters" => GetLinkedElementParameters(doc, args),
                "search_linked_elements" => SearchLinkedElements(doc, args),
                "get_link_types" => GetLinkTypes(doc),
                _ => JsonError($"RevitLinkSkill: unknown tool '{functionName}'")
            };
        }

        private RevitLinkInstance FindLink(Document doc, string linkName)
        {
            if (string.IsNullOrEmpty(linkName)) return null;

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            return links.FirstOrDefault(l => l.Name != null && l.Name.IndexOf(linkName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string GetLinkedModels(Document doc)
        {
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            if (links.Count == 0)
                return JsonSerializer.Serialize(new { count = 0, message = "No Revit links found in the model." }, JsonOpts);

            var results = new List<object>();
            foreach (var link in links)
            {
                var linkDoc = link.GetLinkDocument();
                var transform = link.GetTotalTransform();
                var origin = transform.Origin;

                results.Add(new
                {
                    id = link.Id.Value,
                    name = link.Name,
                    is_loaded = linkDoc != null,
                    path = GetLinkPath(doc, link),
                    origin_x = Math.Round(origin.X, 2),
                    origin_y = Math.Round(origin.Y, 2),
                    origin_z = Math.Round(origin.Z, 2),
                    element_count = linkDoc != null
                        ? new FilteredElementCollector(linkDoc).WhereElementIsNotElementType().GetElementCount()
                        : 0
                });
            }

            return JsonSerializer.Serialize(new { count = results.Count, links = results }, JsonOpts);
        }

        private string GetLinkedElements(Document doc, Dictionary<string, object> args)
        {
            var linkName = GetArg<string>(args, "link_name");
            var category = GetArg<string>(args, "category");
            var typeName = GetArg<string>(args, "type_name");
            var level = GetArg<string>(args, "level");
            int limit = GetArg(args, "limit", 50);

            var link = FindLink(doc, linkName);
            if (link == null) return JsonError($"Link '{linkName}' not found.");

            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) return JsonError($"Link '{linkName}' is not loaded.");

            var bic = ResolveCategoryFilter(linkDoc, category);
            FilteredElementCollector collector;
            if (bic.HasValue)
                collector = new FilteredElementCollector(linkDoc).OfCategory(bic.Value).WhereElementIsNotElementType();
            else
                collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();

            var elements = collector.ToList();

            if (!string.IsNullOrEmpty(category) && !bic.HasValue)
            {
                elements = elements.Where(e =>
                    e.Category?.Name?.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (!string.IsNullOrEmpty(typeName))
            {
                elements = elements.Where(e =>
                    GetElementTypeName(linkDoc, e).IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (!string.IsNullOrEmpty(level))
            {
                elements = elements.Where(e =>
                    GetElementLevel(linkDoc, e).IndexOf(level, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            int total = elements.Count;
            var items = elements.Take(limit).Select(e => new
            {
                id = e.Id.Value,
                category = e.Category?.Name ?? "-",
                family = GetFamilyName(linkDoc, e),
                type = GetElementTypeName(linkDoc, e),
                level = GetElementLevel(linkDoc, e)
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                link = link.Name,
                total,
                returned = items.Count,
                elements = items
            }, JsonOpts);
        }

        private string CountLinkedElements(Document doc, Dictionary<string, object> args)
        {
            var linkName = GetArg<string>(args, "link_name");
            var category = GetArg<string>(args, "category");
            var groupBy = GetArg(args, "group_by", "none");

            var link = FindLink(doc, linkName);
            if (link == null) return JsonError($"Link '{linkName}' not found.");

            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) return JsonError($"Link '{linkName}' is not loaded.");

            var bic = ResolveCategoryFilter(linkDoc, category);
            FilteredElementCollector collector;
            if (bic.HasValue)
                collector = new FilteredElementCollector(linkDoc).OfCategory(bic.Value).WhereElementIsNotElementType();
            else
                collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();

            var elements = collector.ToList();

            if (!string.IsNullOrEmpty(category) && !bic.HasValue)
            {
                elements = elements.Where(e =>
                    e.Category?.Name?.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (groupBy == "type")
            {
                var groups = elements
                    .GroupBy(e => GetElementTypeName(linkDoc, e))
                    .OrderByDescending(g => g.Count())
                    .Take(30)
                    .ToDictionary(g => g.Key, g => g.Count());

                return JsonSerializer.Serialize(new { link = link.Name, category, total = elements.Count, by_type = groups }, JsonOpts);
            }

            if (groupBy == "level")
            {
                var groups = elements
                    .GroupBy(e => GetElementLevel(linkDoc, e))
                    .OrderByDescending(g => g.Count())
                    .Take(30)
                    .ToDictionary(g => g.Key, g => g.Count());

                return JsonSerializer.Serialize(new { link = link.Name, category, total = elements.Count, by_level = groups }, JsonOpts);
            }

            return JsonSerializer.Serialize(new { link = link.Name, category, total = elements.Count }, JsonOpts);
        }

        private string GetLinkedElementParameters(Document doc, Dictionary<string, object> args)
        {
            var linkName = GetArg<string>(args, "link_name");
            long elementId = GetArg<long>(args, "element_id");

            var link = FindLink(doc, linkName);
            if (link == null) return JsonError($"Link '{linkName}' not found.");

            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) return JsonError($"Link '{linkName}' is not loaded.");

            var elem = linkDoc.GetElement(new ElementId(elementId));
            if (elem == null) return JsonError($"Element {elementId} not found in linked model.");

            var paramDict = new Dictionary<string, string>();
            foreach (Parameter p in elem.Parameters)
            {
                if (p?.Definition == null) continue;
                var val = GetParameterValueAsString(linkDoc, p);
                if (val != "-") paramDict[p.Definition.Name] = val;
            }

            return JsonSerializer.Serialize(new
            {
                link = link.Name,
                element_id = elementId,
                category = elem.Category?.Name ?? "-",
                family = GetFamilyName(linkDoc, elem),
                type = GetElementTypeName(linkDoc, elem),
                level = GetElementLevel(linkDoc, elem),
                parameter_count = paramDict.Count,
                parameters = paramDict
            }, JsonOpts);
        }

        private string SearchLinkedElements(Document doc, Dictionary<string, object> args)
        {
            var linkName = GetArg<string>(args, "link_name");
            var category = GetArg<string>(args, "category");
            var paramName = GetArg<string>(args, "param_name");
            var paramValue = GetArg<string>(args, "param_value");
            int limit = GetArg(args, "limit", 30);

            if (string.IsNullOrEmpty(paramName)) return JsonError("param_name required.");
            if (string.IsNullOrEmpty(paramValue)) return JsonError("param_value required.");

            var link = FindLink(doc, linkName);
            if (link == null) return JsonError($"Link '{linkName}' not found.");

            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) return JsonError($"Link '{linkName}' is not loaded.");

            var bic = ResolveCategoryFilter(linkDoc, category);
            FilteredElementCollector collector;
            if (bic.HasValue)
                collector = new FilteredElementCollector(linkDoc).OfCategory(bic.Value).WhereElementIsNotElementType();
            else
                collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();

            var matches = new List<object>();
            foreach (var elem in collector)
            {
                if (matches.Count >= limit) break;

                var p = elem.LookupParameter(paramName);
                if (p == null) continue;

                var val = GetParameterValueAsString(linkDoc, p);
                if (val.IndexOf(paramValue, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matches.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        type = GetElementTypeName(linkDoc, elem),
                        level = GetElementLevel(linkDoc, elem),
                        matched_value = val
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                link = link.Name,
                param_name = paramName,
                search_value = paramValue,
                found = matches.Count,
                elements = matches
            }, JsonOpts);
        }

        private string GetLinkTypes(Document doc)
        {
            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            if (linkTypes.Count == 0)
                return JsonSerializer.Serialize(new { count = 0, message = "No Revit link types found." }, JsonOpts);

            var results = linkTypes.Select(lt =>
            {
                ExternalFileReference extRef = null;
                try { extRef = ExternalFileUtils.GetExternalFileReference(doc, lt.Id); } catch { }

                return new
                {
                    id = lt.Id.Value,
                    name = lt.Name,
                    is_loaded = RevitLinkType.IsLoaded(doc, lt.Id),
                    status = extRef?.GetLinkedFileStatus().ToString() ?? "Unknown",
                    path_type = extRef?.PathType.ToString() ?? "Unknown",
                    path = extRef != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath()) : "-",
                    is_nested = lt.IsNestedLink
                };
            }).ToList();

            return JsonSerializer.Serialize(new { count = results.Count, link_types = results }, JsonOpts);
        }

        private static string GetLinkPath(Document doc, RevitLinkInstance link)
        {
            try
            {
                var typeId = link.GetTypeId();
                if (typeId == ElementId.InvalidElementId) return "-";
                var extRef = ExternalFileUtils.GetExternalFileReference(doc, typeId);
                if (extRef == null) return "-";
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
            }
            catch { return "-"; }
        }
    }
}
