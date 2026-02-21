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
    public class MepValidationSkill : IRevitSkill
    {
        public string Name => "MepValidation";
        public string Description => "Validate MEP model: disconnected elements, missing parameters, warnings";

        private static readonly HashSet<string> HandledTools = new()
        {
            "check_disconnected_elements", "check_missing_parameters",
            "check_elevation_conflicts", "check_oversized_elements", "get_warnings_mep"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("check_disconnected_elements",
                "Find MEP elements (ducts, pipes, conduits, cable trays) that have disconnected connectors (open ends not connected to anything).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Filter: 'duct','pipe','conduit','cable_tray', or 'all'. Default 'all'." },
                        "level": { "type": "string", "description": "Optional level filter" },
                        "limit": { "type": "integer", "description": "Max results. Default 100.", "default": 100 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_missing_parameters",
                "Find MEP elements with empty or missing required parameters (system name, size, flow, etc.).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Revit category to check (e.g. 'Ducts', 'Pipes', 'Mechanical Equipment')" },
                        "param_names": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "Parameter names to check. If not specified, checks common MEP params."
                        },
                        "level": { "type": "string", "description": "Optional level filter" },
                        "limit": { "type": "integer", "description": "Max results. Default 100.", "default": 100 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_elevation_conflicts",
                "Find MEP elements at unusual elevations: too close to floor/ceiling or crossing levels unexpectedly.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "'duct','pipe', or 'all'. Default 'all'." },
                        "min_height_m": { "type": "number", "description": "Minimum acceptable height from floor (meters). Default 2.4.", "default": 2.4 },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_oversized_elements",
                "Find ducts or pipes that exceed specified maximum dimensions.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "'duct' or 'pipe'. Default 'duct'." },
                        "max_width_mm": { "type": "number", "description": "Max duct width in mm. Default 1500.", "default": 1500 },
                        "max_diameter_mm": { "type": "number", "description": "Max pipe diameter in mm. Default 300.", "default": 300 },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_warnings_mep",
                "Get Revit model warnings related to MEP elements (disconnected, overlapping, etc.).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "limit": { "type": "integer", "description": "Max warnings to return. Default 50.", "default": 50 }
                    },
                    "required": []
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var doc = app.ActiveUIDocument.Document;
            return functionName switch
            {
                "check_disconnected_elements" => CheckDisconnected(doc, args),
                "check_missing_parameters" => CheckMissingParams(doc, args),
                "check_elevation_conflicts" => CheckElevation(doc, args),
                "check_oversized_elements" => CheckOversized(doc, args),
                "get_warnings_mep" => GetMepWarnings(doc, args),
                _ => JsonError($"MepValidationSkill: unknown tool '{functionName}'")
            };
        }

        private static readonly Dictionary<string, BuiltInCategory> CurveCategories = new()
        {
            ["duct"] = BuiltInCategory.OST_DuctCurves,
            ["pipe"] = BuiltInCategory.OST_PipeCurves,
            ["conduit"] = BuiltInCategory.OST_Conduit,
            ["cable_tray"] = BuiltInCategory.OST_CableTray
        };

        private static readonly BuiltInCategory[] MepCategories =
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_Sprinklers
        };

        private string CheckDisconnected(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category")?.ToLower() ?? "all";
            var levelFilter = GetArg<string>(args, "level");
            var limit = GetArg<int>(args, "limit", 100);

            var cats = catFilter == "all"
                ? CurveCategories.Values.ToList()
                : CurveCategories.TryGetValue(catFilter, out var c)
                    ? new List<BuiltInCategory> { c }
                    : CurveCategories.Values.ToList();

            var results = new List<object>();

            foreach (var cat in cats)
            {
                if (results.Count >= limit) break;

                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    if (results.Count >= limit) break;

                    if (!string.IsNullOrEmpty(levelFilter) &&
                        !GetElementLevel(doc, elem).Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (elem is MEPCurve mepCurve)
                    {
                        var cm = mepCurve.ConnectorManager;
                        if (cm == null) continue;

                        int openEnds = 0;
                        foreach (Connector conn in cm.Connectors)
                        {
                            if (conn.ConnectorType == ConnectorType.End && !conn.IsConnected)
                                openEnds++;
                        }

                        if (openEnds > 0)
                        {
                            results.Add(new
                            {
                                id = elem.Id.Value,
                                category = elem.Category?.Name ?? "-",
                                type = GetElementTypeName(doc, elem),
                                size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                                level = GetElementLevel(doc, elem),
                                open_ends = openEnds,
                                system = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-"
                            });
                        }
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                disconnected_count = results.Count,
                elements = results
            }, JsonOpts);
        }

        private string CheckMissingParams(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var paramNames = GetArgStringArray(args, "param_names");
            var levelFilter = GetArg<string>(args, "level");
            var limit = GetArg<int>(args, "limit", 100);

            if (paramNames == null || paramNames.Count == 0)
                paramNames = new List<string> { "System Name", "System Classification", "Size", "Comments" };

            var collector = BuildCollector(doc, category);
            var results = new List<object>();

            foreach (var elem in collector)
            {
                if (results.Count >= limit) break;
                if (elem.Category == null) continue;

                if (!string.IsNullOrEmpty(levelFilter) &&
                    !GetElementLevel(doc, elem).Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isMep = MepCategories.Any(mc => elem.Category.Id.Value == (long)mc);
                if (!string.IsNullOrEmpty(category) || isMep)
                {
                    var missing = new List<string>();
                    foreach (var pn in paramNames)
                    {
                        var p = elem.LookupParameter(pn);
                        if (p == null) continue;
                        if (!p.HasValue || string.IsNullOrWhiteSpace(GetParameterValueAsString(doc, p).Replace("-", "")))
                            missing.Add(pn);
                    }

                    if (missing.Count > 0)
                    {
                        results.Add(new
                        {
                            id = elem.Id.Value,
                            category = elem.Category.Name,
                            type = GetElementTypeName(doc, elem),
                            level = GetElementLevel(doc, elem),
                            missing_parameters = missing
                        });
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                checked_parameters = paramNames,
                issues_found = results.Count,
                elements = results
            }, JsonOpts);
        }

        private string CheckElevation(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category")?.ToLower() ?? "all";
            var minHeightM = GetArg<double>(args, "min_height_m", 2.4);
            var levelFilter = GetArg<string>(args, "level");
            var minHeightFt = minHeightM / 0.3048;

            var cats = new List<BuiltInCategory>();
            if (catFilter is "all" or "duct") cats.Add(BuiltInCategory.OST_DuctCurves);
            if (catFilter is "all" or "pipe") cats.Add(BuiltInCategory.OST_PipeCurves);

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToDictionary(l => l.Id, l => l);

            var results = new List<object>();

            foreach (var cat in cats)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    var elemLevelName = GetElementLevel(doc, elem);
                    if (!string.IsNullOrEmpty(levelFilter) &&
                        !elemLevelName.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (elem.Location is LocationCurve lc)
                    {
                        var midZ = (lc.Curve.GetEndPoint(0).Z + lc.Curve.GetEndPoint(1).Z) / 2;

                        Level refLevel = null;
                        var levelParam = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                                      ?? elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                        if (levelParam != null && levelParam.StorageType == StorageType.ElementId)
                        {
                            var lvlId = levelParam.AsElementId();
                            levels.TryGetValue(lvlId, out refLevel);
                        }

                        if (refLevel != null)
                        {
                            var heightAboveLevel = midZ - refLevel.Elevation;
                            if (heightAboveLevel < minHeightFt && heightAboveLevel >= 0)
                            {
                                results.Add(new
                                {
                                    id = elem.Id.Value,
                                    category = elem.Category?.Name ?? "-",
                                    type = GetElementTypeName(doc, elem),
                                    size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                                    level = refLevel.Name,
                                    height_above_level_m = Math.Round(heightAboveLevel * 0.3048, 3),
                                    min_required_m = minHeightM,
                                    issue = "Below minimum height"
                                });
                            }
                        }
                    }

                    if (results.Count >= 200) break;
                }
            }

            return JsonSerializer.Serialize(new
            {
                min_height_m = minHeightM,
                issues_found = results.Count,
                elements = results
            }, JsonOpts);
        }

        private string CheckOversized(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category")?.ToLower() ?? "duct";
            var maxWidthMm = GetArg<double>(args, "max_width_mm", 1500);
            var maxDiamMm = GetArg<double>(args, "max_diameter_mm", 300);
            var levelFilter = GetArg<string>(args, "level");

            var maxWidthFt = maxWidthMm / 304.8;
            var maxDiamFt = maxDiamMm / 304.8;

            var cat = catFilter == "pipe" ? BuiltInCategory.OST_PipeCurves : BuiltInCategory.OST_DuctCurves;
            var elems = new FilteredElementCollector(doc)
                .OfCategory(cat).WhereElementIsNotElementType().ToList();

            var results = new List<object>();

            foreach (var elem in elems)
            {
                if (!string.IsNullOrEmpty(levelFilter) &&
                    !GetElementLevel(doc, elem).Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool oversized = false;
                string reason = "";

                if (catFilter == "pipe")
                {
                    var diam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                    if (diam > maxDiamFt)
                    {
                        oversized = true;
                        reason = $"Diameter {Math.Round(diam * 304.8, 0)}mm > {maxDiamMm}mm";
                    }
                }
                else
                {
                    var width = elem.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
                    var height = elem.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0;
                    var diam = elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;

                    if (width > maxWidthFt)
                    {
                        oversized = true;
                        reason = $"Width {Math.Round(width * 304.8, 0)}mm > {maxWidthMm}mm";
                    }
                    else if (height > maxWidthFt)
                    {
                        oversized = true;
                        reason = $"Height {Math.Round(height * 304.8, 0)}mm > {maxWidthMm}mm";
                    }
                    else if (diam > maxWidthFt)
                    {
                        oversized = true;
                        reason = $"Diameter {Math.Round(diam * 304.8, 0)}mm > {maxWidthMm}mm";
                    }
                }

                if (oversized)
                {
                    results.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        type = GetElementTypeName(doc, elem),
                        size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                        level = GetElementLevel(doc, elem),
                        reason
                    });
                }

                if (results.Count >= 100) break;
            }

            return JsonSerializer.Serialize(new
            {
                threshold = catFilter == "pipe"
                    ? $"max_diameter={maxDiamMm}mm"
                    : $"max_width={maxWidthMm}mm",
                oversized_count = results.Count,
                elements = results
            }, JsonOpts);
        }

        private string GetMepWarnings(Document doc, Dictionary<string, object> args)
        {
            var limit = GetArg<int>(args, "limit", 50);
            var warnings = doc.GetWarnings();

            var mepCategoryIds = new HashSet<long>(
                MepCategories.Select(c => (long)c));

            var results = new List<object>();

            foreach (var warning in warnings)
            {
                if (results.Count >= limit) break;

                var failingIds = warning.GetFailingElements();
                bool isMep = false;

                foreach (var eid in failingIds)
                {
                    var elem = doc.GetElement(eid);
                    if (elem?.Category != null && mepCategoryIds.Contains(elem.Category.Id.Value))
                    {
                        isMep = true;
                        break;
                    }
                }

                if (isMep)
                {
                    results.Add(new
                    {
                        description = warning.GetDescriptionText(),
                        severity = warning.GetSeverity().ToString(),
                        element_ids = failingIds.Select(eid => eid.Value).ToList()
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                total_model_warnings = warnings.Count,
                mep_warnings_shown = results.Count,
                warnings = results
            }, JsonOpts);
        }
    }
}
