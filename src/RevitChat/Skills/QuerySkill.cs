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
    public class QuerySkill : BaseRevitSkill
    {
        protected override string SkillName => "Query";
        protected override string SkillDescription => "Query and search elements in the Revit model";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_elements", "count_elements", "get_element_parameters", "search_elements",
            "compare_element_parameters", "find_empty_parameters", "find_duplicate_values",
            "get_element_geometry", "find_elements_near", "get_wall_layers",
            "get_element_host", "get_element_connections"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
                        "limit": { "type": "integer", "description": "Max elements to return. Default 100.", "default": 100 },
                        "group_by": { "type": "string", "enum": ["level", "family", "type", "system"], "description": "Group results by field. Returns grouped counts and first few IDs per group." }
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
                functionDescription: "Search elements by parameter value with comparison operators. Supports numeric (greater/less) and string matching. Can group results by level and/or family.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "param_name": { "type": "string", "description": "Parameter name to search" },
                        "param_value": { "type": "string", "description": "Value to compare against" },
                        "category": { "type": "string", "description": "Optional category filter" },
                        "match_type": { "type": "string", "enum": ["contains", "equals", "greater", "less", "greater_equal", "less_equal"], "description": "Comparison operator. Default: contains" },
                        "group_by": { "type": "array", "items": { "type": "string", "enum": ["level", "family", "type"] }, "description": "Group results by these fields" },
                        "limit": { "type": "integer", "description": "Max elements to return. Default 200.", "default": 200 }
                    },
                    "required": ["param_name", "param_value"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                functionName: "compare_element_parameters",
                functionDescription: "Compare all parameters between two elements side-by-side. Returns matching, different, and unique parameters for each element.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id_1": { "type": "integer", "description": "First element ID" },
                        "element_id_2": { "type": "integer", "description": "Second element ID" }
                    },
                    "required": ["element_id_1", "element_id_2"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                functionName: "find_empty_parameters",
                functionDescription: "Find elements that have empty or missing values for specified parameters. Useful for QA checks.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to check" },
                        "param_names": { "type": "array", "items": { "type": "string" }, "description": "Parameter names to check for empty values" }
                    },
                    "required": ["element_ids", "param_names"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                functionName: "find_duplicate_values",
                functionDescription: "Find elements that share duplicate values for a parameter. Groups elements by their parameter value. Useful for QA duplicate detection.",
                functionParameters: BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to check" },
                        "param_name": { "type": "string", "description": "Parameter name to check for duplicates" }
                    },
                    "required": ["element_ids", "param_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_element_geometry",
                "Get geometry summary of elements: volume, area, length, bounding box dimensions in mm.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs" },
                        "limit": { "type": "integer", "description": "Max results (default 20)" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("find_elements_near",
                "Find elements within a specified radius from a reference element.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "reference_element_id": { "type": "integer", "description": "Center element ID" },
                        "radius_mm": { "type": "number", "description": "Search radius in mm" },
                        "category": { "type": "string", "description": "Filter by category (optional)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": ["reference_element_id", "radius_mm"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_wall_layers",
                "Get wall type compound structure: layers, materials, thickness.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Wall element IDs or wall type IDs" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_element_host",
                "Get the host element of hosted elements (e.g., door's wall, fixture's floor).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to check" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_element_connections",
                "Get all elements connected to given elements (joins, connectors, hosted).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id": { "type": "integer", "description": "Element ID to check connections for" },
                        "connection_type": { "type": "string", "enum": ["all", "connector", "join", "host"], "description": "Type of connections. Default: all" }
                    },
                    "required": ["element_id"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_elements" => GetElements(doc, args),
                "count_elements" => CountElements(doc, args),
                "get_element_parameters" => GetElementParameters(doc, args),
                "search_elements" => SearchElements(doc, args),
                "compare_element_parameters" => CompareElementParameters(doc, args),
                "find_empty_parameters" => FindEmptyParameters(doc, args),
                "find_duplicate_values" => FindDuplicateValues(doc, args),
                "get_element_geometry" => GetElementGeometry(doc, args),
                "find_elements_near" => FindElementsNear(doc, args),
                "get_wall_layers" => GetWallLayers(doc, args),
                "get_element_host" => GetElementHost(doc, args),
                "get_element_connections" => GetElementConnections(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetElements(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var typeName = GetArg<string>(args, "type_name");
            var level = GetArg<string>(args, "level");
            var limit = GetArg<int>(args, "limit", 100);
            var groupBy = GetArg<string>(args, "group_by");
            if (limit <= 0) limit = 100;

            var resolvedLevel = ResolveLevelName(doc, level);
            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;
            var results = new List<object>();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !MatchesCategoryName(elem.Category.Name, category))
                    continue;

                string elemLevel = null;
                if (resolvedLevel != null)
                {
                    elemLevel = GetElementLevel(doc, elem);
                    if (!elemLevel.Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase)) continue;
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
                    level = elemLevel ?? GetElementLevel(doc, elem),
                    system = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-"
                });

                if (results.Count >= limit) break;
            }

            if (!string.IsNullOrEmpty(groupBy) && ValidGetElementsGroupFields.Contains(groupBy, StringComparer.OrdinalIgnoreCase))
            {
                var groups = results
                    .GroupBy(r => GetGroupKey(r, groupBy))
                    .OrderByDescending(g => g.Count())
                    .Select(g => new
                    {
                        key = g.Key,
                        count = g.Count(),
                        sample_ids = g.Take(5).Select(x => (long)((dynamic)x).id).ToList()
                    })
                    .ToList();
                return JsonSerializer.Serialize(new { groups, total = results.Count }, JsonOpts);
            }

            var elements = results.Select(r =>
            {
                var d = (dynamic)r;
                return new { id = (long)d.id, category = (string)d.category, family = (string)d.family, type = (string)d.type, level = (string)d.level };
            }).ToList();
            return JsonSerializer.Serialize(new { count = elements.Count, elements }, JsonOpts);
        }

        private static readonly string[] ValidGetElementsGroupFields = { "level", "family", "type", "system" };

        private static string GetGroupKey(object r, string field)
        {
            var d = (dynamic)r;
            return field.ToLowerInvariant() switch
            {
                "level" => (string)d.level ?? "-",
                "family" => (string)d.family ?? "-",
                "type" => (string)d.type ?? "-",
                "system" => (string)d.system ?? "-",
                _ => "-"
            };
        }

        private string CountElements(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var level = GetArg<string>(args, "level");

            string resolvedLevel = ResolveLevelName(doc, level);
            bool levelFilterActive = !string.IsNullOrEmpty(level);
            bool levelResolved = resolvedLevel != null;

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !MatchesCategoryName(elem.Category.Name, category))
                    continue;

                if (levelResolved &&
                    !GetElementLevel(doc, elem).Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var tn = GetElementTypeName(doc, elem);
                typeCounts.TryGetValue(tn, out int c);
                typeCounts[tn] = c + 1;
                total++;
            }

            var result = new Dictionary<string, object>
            {
                ["total"] = total,
                ["by_type"] = typeCounts.OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            };

            if (levelFilterActive)
            {
                result["level_filter"] = level;
                result["level_resolved"] = resolvedLevel ?? "(no matching level found)";
                if (!levelResolved)
                {
                    var available = new FilteredElementCollector(doc).OfClass(typeof(Level))
                        .Cast<Level>().Select(l => l.Name).OrderBy(n => n).ToList();
                    result["available_levels"] = available;
                    result["warning"] = $"Level '{level}' not found. Showing unfiltered results. Available levels: {string.Join(", ", available)}";
                }
            }

            return JsonSerializer.Serialize(result, JsonOpts);
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
            var matchType = GetArg(args, "match_type", "contains");
            int limit = GetArg(args, "limit", 200);
            if (limit <= 0) limit = 200;

            var groupByRaw = args.ContainsKey("group_by") ? args["group_by"] : null;
            var groupBySet = ParseGroupBy(groupByRaw);

            if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(paramValue))
                return JsonError("param_name and param_value are required.");

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;

            var matched = new List<(long id, string cat, string family, string type, string level, string val)>();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !MatchesCategoryName(elem.Category.Name, category))
                    continue;

                var param = elem.LookupParameter(paramName);
                if (param == null) continue;

                var val = GetParameterValueAsString(doc, param);
                if (val == "-") continue;

                if (!MatchValue(val, paramValue, matchType)) continue;

                matched.Add((
                    elem.Id.Value,
                    elem.Category.Name,
                    GetFamilyName(doc, elem),
                    GetElementTypeName(doc, elem),
                    GetElementLevel(doc, elem),
                    val
                ));

                if (matched.Count >= limit) break;
            }

            if (groupBySet.Count > 0)
                return SerializeGrouped(matched, groupBySet, paramName, matchType, paramValue);

            var elements = matched.Select(m => new
            {
                id = m.id, category = m.cat, family = m.family,
                type = m.type, level = m.level, matched_value = m.val
            }).ToList();

            return JsonSerializer.Serialize(new { count = elements.Count, elements }, JsonOpts);
        }

        private string GetElementGeometry(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            int limit = GetArg(args, "limit", 20);
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var results = new List<object>();
            var opts = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };

            foreach (var id in ids.Take(limit))
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;

                double volume = 0, area = 0, length = 0;
                var bb = elem.get_BoundingBox(null);

                try
                {
                    var geom = elem.get_Geometry(opts);
                    if (geom != null)
                    {
                        foreach (var gObj in geom)
                        {
                            if (gObj is Solid solid && solid.Volume > 0)
                            {
                                volume += solid.Volume;
                                area += solid.SurfaceArea;
                            }
                            else if (gObj is GeometryInstance gi)
                            {
                                foreach (var inner in gi.GetInstanceGeometry())
                                {
                                    if (inner is Solid s2 && s2.Volume > 0)
                                    {
                                        volume += s2.Volume;
                                        area += s2.SurfaceArea;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                if (elem.Location is LocationCurve lc)
                    length = lc.Curve.Length;

                var info = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = elem.Name ?? "-",
                    ["category"] = elem.Category?.Name ?? "-"
                };

                if (volume > 0) info["volume_m3"] = Math.Round(volume * 0.0283168, 4);
                if (area > 0) info["area_m2"] = Math.Round(area * 0.092903, 4);
                if (length > 0) info["length_mm"] = Math.Round(length * 304.8, 1);

                if (bb != null)
                {
                    info["bbox_mm"] = new Dictionary<string, object>
                    {
                        ["width"] = Math.Round((bb.Max.X - bb.Min.X) * 304.8, 1),
                        ["depth"] = Math.Round((bb.Max.Y - bb.Min.Y) * 304.8, 1),
                        ["height"] = Math.Round((bb.Max.Z - bb.Min.Z) * 304.8, 1)
                    };
                }

                results.Add(info);
            }

            return JsonSerializer.Serialize(new { count = results.Count, elements = results }, JsonOpts);
        }

        private string FindElementsNear(Document doc, Dictionary<string, object> args)
        {
            long refId = GetArg(args, "reference_element_id", 0L);
            double radiusMm = GetArg(args, "radius_mm", 1000.0);
            var category = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 50);

            var refElem = doc.GetElement(new ElementId(refId));
            if (refElem == null) return JsonError("Reference element not found.");

            XYZ center;
            if (refElem.Location is LocationPoint lp) center = lp.Point;
            else if (refElem.Location is LocationCurve lc) center = lc.Curve.Evaluate(0.5, true);
            else
            {
                var bb = refElem.get_BoundingBox(null);
                if (bb == null) return JsonError("Cannot determine element location.");
                center = (bb.Min + bb.Max) / 2;
            }

            double radiusFt = radiusMm / 304.8;
            var outline = new Outline(
                new XYZ(center.X - radiusFt, center.Y - radiusFt, center.Z - radiusFt),
                new XYZ(center.X + radiusFt, center.Y + radiusFt, center.Z + radiusFt));

            var collector = new FilteredElementCollector(doc)
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .WhereElementIsNotElementType();

            var bic = ResolveCategoryFilter(doc, category);
            if (bic.HasValue) collector = collector.OfCategory(bic.Value);

            var list = new List<(long id, string category, string name, double distance_mm)>();
            foreach (var elem in collector)
            {
                if (elem.Id.Value == refId) continue;
                if (elem.Category == null) continue;

                XYZ elemCenter;
                if (elem.Location is LocationPoint ep) elemCenter = ep.Point;
                else if (elem.Location is LocationCurve ec) elemCenter = ec.Curve.Evaluate(0.5, true);
                else
                {
                    var ebb = elem.get_BoundingBox(null);
                    if (ebb == null) continue;
                    elemCenter = (ebb.Min + ebb.Max) / 2;
                }

                double dist = center.DistanceTo(elemCenter) * 304.8;
                if (dist > radiusMm) continue;

                list.Add((elem.Id.Value, elem.Category.Name, elem.Name ?? "-", dist));
                if (list.Count >= limit) break;
            }

            var results = list.OrderBy(x => x.distance_mm)
                .Select(x => new { id = x.id, category = x.category, name = x.name, distance_mm = Math.Round(x.distance_mm, 1) })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                reference_id = refId,
                radius_mm = radiusMm,
                found = results.Count,
                elements = results
            }, JsonOpts);
        }

        private string GetWallLayers(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var results = new List<object>();
            foreach (var id in ids.Take(20))
            {
                var elem = doc.GetElement(new ElementId(id));
                WallType wallType = elem as WallType ?? (elem as Wall)?.WallType;
                if (wallType == null) continue;

                var cs = wallType.GetCompoundStructure();
                if (cs == null)
                {
                    results.Add(new Dictionary<string, object> { ["wall_type"] = wallType.Name, ["layers"] = Array.Empty<object>(), ["note"] = "No compound structure" });
                    continue;
                }

                var layers = new List<object>();
                foreach (var layer in cs.GetLayers())
                {
                    var mat = doc.GetElement(layer.MaterialId);
                    layers.Add(new Dictionary<string, object>
                    {
                        ["function"] = layer.Function.ToString(),
                        ["material"] = mat?.Name ?? "(none)",
                        ["thickness_mm"] = Math.Round(layer.Width * 304.8, 1),
                        ["is_structural"] = layer.Function == MaterialFunctionAssignment.Structure
                    });
                }

                results.Add(new Dictionary<string, object>
                {
                    ["wall_type"] = wallType.Name,
                    ["total_thickness_mm"] = Math.Round(wallType.Width * 304.8, 1),
                    ["layer_count"] = layers.Count,
                    ["layers"] = layers
                });
            }

            return JsonSerializer.Serialize(new { walls = results }, JsonOpts);
        }

        private string GetElementHost(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var results = new List<object>();
            foreach (var id in ids.Take(50))
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) { results.Add(new { element_id = id, error = "not found" }); continue; }

                Element host = null;
                if (elem is FamilyInstance fi) host = fi.Host;
                if (host == null && elem is Opening opening)
                    host = opening.Host;

                if (host != null)
                {
                    results.Add(new
                    {
                        element_id = id,
                        host_id = host.Id.Value,
                        host_category = host.Category?.Name ?? "-",
                        host_name = host.Name ?? "-",
                        host_type = GetElementTypeName(doc, host)
                    });
                }
                else
                {
                    results.Add(new { element_id = id, host_id = (long?)null, message = "No host found (not a hosted element)" });
                }
            }

            return JsonSerializer.Serialize(new { count = results.Count, elements = results }, JsonOpts);
        }

        private string GetElementConnections(Document doc, Dictionary<string, object> args)
        {
            long elemId = GetArg<long>(args, "element_id");
            var connType = GetArg(args, "connection_type", "all");
            if (elemId <= 0) return JsonError("element_id required.");

            var elem = doc.GetElement(new ElementId(elemId));
            if (elem == null) return JsonError($"Element {elemId} not found.");

            var connections = new List<object>();

            if (connType is "all" or "host")
            {
                if (elem is FamilyInstance fi && fi.Host != null)
                    connections.Add(new { type = "host", id = fi.Host.Id.Value, category = fi.Host.Category?.Name, name = fi.Host.Name });

                var hosted = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(f => f.Host?.Id == elem.Id)
                    .Take(20);
                foreach (var h in hosted)
                    connections.Add(new { type = "hosted_by_this", id = h.Id.Value, category = h.Category?.Name, name = h.Name });
            }

            if (connType is "all" or "join")
            {
                if (elem is Wall wall && wall.Location is LocationCurve lc)
                {
                    for (int end = 0; end <= 1; end++)
                    {
                        try
                        {
                            var joined = lc.get_ElementsAtJoin(end);
                            if (joined != null)
                            {
                                foreach (Element jw in joined)
                                {
                                    if (jw.Id == wall.Id) continue;
                                    connections.Add(new { type = $"wall_join_end{end}", id = jw.Id.Value, name = jw.Name });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (connType is "all" or "connector")
            {
                try
                {
                    ConnectorManager cm = null;
                    if (elem is MEPCurve mep) cm = mep.ConnectorManager;
                    else if (elem is FamilyInstance fi2) cm = fi2.MEPModel?.ConnectorManager;

                    if (cm != null)
                    {
                        foreach (Connector c in cm.Connectors)
                        {
                            if (c.AllRefs == null) continue;
                            foreach (Connector other in c.AllRefs)
                            {
                                if (other.Owner.Id == elem.Id) continue;
                                connections.Add(new
                                {
                                    type = "mep_connector",
                                    id = other.Owner.Id.Value,
                                    category = other.Owner.Category?.Name,
                                    name = other.Owner.Name,
                                    connector_domain = c.Domain.ToString()
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            return JsonSerializer.Serialize(new
            {
                element_id = elemId,
                connection_type = connType,
                connection_count = connections.Count,
                connections
            }, JsonOpts);
        }

        #region compare_element_parameters

        private string CompareElementParameters(Document doc, Dictionary<string, object> args)
        {
            long id1 = GetArg<long>(args, "element_id_1");
            long id2 = GetArg<long>(args, "element_id_2");
            if (id1 <= 0 || id2 <= 0) return JsonError("Both element_id_1 and element_id_2 are required.");

            var elem1 = doc.GetElement(new ElementId(id1));
            var elem2 = doc.GetElement(new ElementId(id2));
            if (elem1 == null) return JsonError($"Element {id1} not found.");
            if (elem2 == null) return JsonError($"Element {id2} not found.");

            var params1 = GetAllParamValues(doc, elem1);
            var params2 = GetAllParamValues(doc, elem2);
            var allKeys = new HashSet<string>(params1.Keys, StringComparer.OrdinalIgnoreCase);
            allKeys.UnionWith(params2.Keys);

            var matching = new List<object>();
            var different = new List<object>();
            var onlyIn1 = new List<object>();
            var onlyIn2 = new List<object>();

            foreach (var key in allKeys.OrderBy(k => k))
            {
                bool has1 = params1.TryGetValue(key, out var v1);
                bool has2 = params2.TryGetValue(key, out var v2);
                if (has1 && has2)
                {
                    if (string.Equals(v1, v2, StringComparison.Ordinal))
                        matching.Add(new { parameter = key, value = v1 });
                    else
                        different.Add(new { parameter = key, element_1 = v1, element_2 = v2 });
                }
                else if (has1) onlyIn1.Add(new { parameter = key, value = v1 });
                else onlyIn2.Add(new { parameter = key, value = v2 });
            }

            return JsonSerializer.Serialize(new
            {
                element_1 = new { id = id1, category = elem1.Category?.Name, name = elem1.Name },
                element_2 = new { id = id2, category = elem2.Category?.Name, name = elem2.Name },
                matching_count = matching.Count,
                different_count = different.Count,
                matching = matching.Take(50),
                different = different.Take(50),
                only_in_element_1 = onlyIn1.Take(20),
                only_in_element_2 = onlyIn2.Take(20)
            }, JsonOpts);
        }

        private static Dictionary<string, string> GetAllParamValues(Document doc, Element elem)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter param in elem.Parameters)
            {
                if (param.Definition == null) continue;
                var name = param.Definition.Name;
                if (string.IsNullOrEmpty(name) || name.StartsWith("INVALID")) continue;
                result[name] = GetParameterValueAsString(doc, param);
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
                        if (!result.ContainsKey(key))
                            result[key] = GetParameterValueAsString(doc, param);
                    }
                }
            }
            return result;
        }

        #endregion

        #region find_empty_parameters

        private string FindEmptyParameters(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var paramNames = GetArgStringArray(args, "param_names");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (paramNames == null || paramNames.Count == 0) return JsonError("param_names required.");

            var emptyResults = new List<object>();
            foreach (var id in ids)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;
                var emptyParams = new List<string>();
                foreach (var pn in paramNames)
                {
                    var p = elem.LookupParameter(pn);
                    if (p == null || IsEmptyValue(GetParameterValueAsString(doc, p)))
                        emptyParams.Add(pn);
                }
                if (emptyParams.Count > 0)
                {
                    emptyResults.Add(new
                    {
                        id,
                        category = elem.Category?.Name ?? "-",
                        name = elem.Name ?? "-",
                        empty_params = emptyParams
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                checked_count = ids.Count,
                elements_with_empty = emptyResults.Count,
                results = emptyResults.Take(100)
            }, JsonOpts);
        }

        private static bool IsEmptyValue(string value)
            => string.IsNullOrWhiteSpace(value) || value == "-" || value == "0" || value == "<none>";

        #endregion

        #region find_duplicate_values

        private string FindDuplicateValues(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var paramName = GetArg<string>(args, "param_name");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(paramName)) return JsonError("param_name required.");

            var valueGroups = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            int noParam = 0;

            foreach (var id in ids)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;
                var p = elem.LookupParameter(paramName);
                if (p == null) { noParam++; continue; }
                var val = GetParameterValueAsString(doc, p);
                if (IsEmptyValue(val)) continue;
                if (!valueGroups.TryGetValue(val, out var list))
                {
                    list = new List<long>();
                    valueGroups[val] = list;
                }
                list.Add(id);
            }

            var duplicates = valueGroups
                .Where(kv => kv.Value.Count > 1)
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => new { value = kv.Key, count = kv.Value.Count, element_ids = kv.Value.Take(20) })
                .Take(50)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                checked_count = ids.Count,
                param_name = paramName,
                duplicate_groups = duplicates.Count,
                total_duplicated = duplicates.Sum(d => d.count),
                duplicates,
                elements_without_param = noParam
            }, JsonOpts);
        }

        #endregion

        private static readonly string[] ValidGroupFields = { "level", "family", "type" };

        private static bool MatchValue(string paramVal, string target, string matchType)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return matchType switch
            {
                "equals" => paramVal.Equals(target, StringComparison.OrdinalIgnoreCase),
                "greater" => double.TryParse(paramVal, System.Globalization.NumberStyles.Any, inv, out double pv1)
                          && double.TryParse(target, System.Globalization.NumberStyles.Any, inv, out double t1) && pv1 > t1,
                "less" => double.TryParse(paramVal, System.Globalization.NumberStyles.Any, inv, out double pv2)
                       && double.TryParse(target, System.Globalization.NumberStyles.Any, inv, out double t2) && pv2 < t2,
                "greater_equal" => double.TryParse(paramVal, System.Globalization.NumberStyles.Any, inv, out double pv3)
                                && double.TryParse(target, System.Globalization.NumberStyles.Any, inv, out double t3) && pv3 >= t3,
                "less_equal" => double.TryParse(paramVal, System.Globalization.NumberStyles.Any, inv, out double pv4)
                             && double.TryParse(target, System.Globalization.NumberStyles.Any, inv, out double t4) && pv4 <= t4,
                _ => paramVal.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0
            };
        }

        private static HashSet<string> ParseGroupBy(object raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (raw == null) return set;
            if (raw is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in je.EnumerateArray())
                    {
                        if (item.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s) && ValidGroupFields.Contains(s, StringComparer.OrdinalIgnoreCase))
                            set.Add(s);
                    }
                }
                else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = je.GetString();
                    if (!string.IsNullOrEmpty(s) && ValidGroupFields.Contains(s, StringComparer.OrdinalIgnoreCase))
                        set.Add(s);
                }
            }
            else if (raw is IEnumerable<object> list)
                foreach (var o in list)
                {
                    var s = o?.ToString();
                    if (!string.IsNullOrEmpty(s) && ValidGroupFields.Contains(s, StringComparer.OrdinalIgnoreCase))
                        set.Add(s);
                }
            else if (raw is string str && ValidGroupFields.Contains(str, StringComparer.OrdinalIgnoreCase))
                set.Add(str);
            return set;
        }

        private string SerializeGrouped(
            List<(long id, string cat, string family, string type, string level, string val)> items,
            HashSet<string> groupBy, string paramName, string matchType, string targetValue)
        {
            Func<(long id, string cat, string family, string type, string level, string val), string> keyFn =
                item =>
                {
                    var parts = new List<string>();
                    if (groupBy.Contains("level")) parts.Add(item.level);
                    if (groupBy.Contains("family")) parts.Add(item.family);
                    if (groupBy.Contains("type")) parts.Add(item.type);
                    return string.Join(" | ", parts);
                };

            var groups = items
                .GroupBy(keyFn)
                .OrderByDescending(g => g.Count())
                .Select(g =>
                {
                    var first = g.First();
                    var obj = new Dictionary<string, object>();
                    if (groupBy.Contains("level")) obj["level"] = first.level;
                    if (groupBy.Contains("family")) obj["family"] = first.family;
                    if (groupBy.Contains("type")) obj["type"] = first.type;
                    obj["count"] = g.Count();
                    obj["sample_ids"] = g.Take(5).Select(x => x.id).ToList();
                    return obj;
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_matched = items.Count,
                parameter = paramName,
                match_type = matchType,
                value = targetValue,
                group_count = groups.Count,
                groups
            }, JsonOpts);
        }
    }
}
