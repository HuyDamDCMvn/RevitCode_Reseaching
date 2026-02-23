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
    public class MepValidationSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepValidation";
        protected override string SkillDescription => "Validate MEP model: disconnected elements, missing parameters, warnings";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "check_disconnected_elements", "check_missing_parameters",
            "check_elevation_conflicts", "check_oversized_elements", "get_warnings_mep",
            "check_pipe_slope", "check_slope_continuity", "get_penetration_schedule",
            "check_fire_dampers", "check_insulation_coverage", "check_velocity",
            "check_noise_level", "check_access_panel"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
            ,
            ChatTool.CreateFunctionTool("check_pipe_slope",
                "Check pipe slopes and flag pipes outside the allowed slope range (percent).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "Optional system name filter (partial match)" },
                        "level": { "type": "string", "description": "Optional level filter" },
                        "min_slope_pct": { "type": "number", "description": "Minimum slope percentage. Default 0.1.", "default": 0.1 },
                        "max_slope_pct": { "type": "number", "description": "Maximum slope percentage. Optional." },
                        "limit": { "type": "integer", "description": "Max issues to return. Default 100.", "default": 100 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_slope_continuity",
                "Trace connected pipes and check if slope direction is consistent (no reversals/sags). Start from a pipe or check all runs in a system.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id": { "type": "integer", "description": "Starting pipe element ID. If not provided, checks all systems." },
                        "system_name": { "type": "string", "description": "Optional system name filter" },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_penetration_schedule",
                "Find MEP elements (ducts, pipes) that cross level boundaries (span between floors). Useful for coordination and fire rating.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "'duct','pipe','conduit', or 'all'. Default 'all'." },
                        "limit": { "type": "integer", "description": "Max results. Default 200.", "default": 200 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_fire_dampers",
                "List fire dampers in the model and check their connection status. Reports dampers with/without connected ducts.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "limit": { "type": "integer", "description": "Max results. Default 200.", "default": 200 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_insulation_coverage",
                "Find pipes/ducts that should have insulation but don't. Reports uninsulated elements by system.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "enum": ["Pipes", "Ducts", "all"], "description": "Category to check (default: all)" },
                        "system_name": { "type": "string", "description": "Filter by system name (optional)" },
                        "level": { "type": "string", "description": "Filter by level name (optional)" },
                        "limit": { "type": "integer", "description": "Max results (default 200)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_velocity",
                "Check flow velocity in pipes/ducts. Flag elements exceeding the specified maximum velocity.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "enum": ["Pipes", "Ducts"], "description": "Category to check" },
                        "max_velocity_ms": { "type": "number", "description": "Max velocity in m/s (default: 2.5 for pipes, 8 for ducts)" },
                        "system_name": { "type": "string", "description": "Filter by system name (optional)" },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("check_noise_level",
                "Estimate noise level from velocity and duct/pipe size using standard formulas.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "enum": ["Pipes", "Ducts"], "description": "Category" },
                        "system_name": { "type": "string", "description": "Optional system name filter" },
                        "max_db": { "type": "number", "description": "Max acceptable noise in dB (default 45)" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("check_access_panel",
                "Check if valves/dampers are at accessible heights from floor.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "min_height_mm": { "type": "number", "description": "Min accessible height in mm (default 600)" },
                        "max_height_mm": { "type": "number", "description": "Max accessible height in mm (default 2000)" },
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Optional specific elements" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "check_disconnected_elements" => CheckDisconnected(doc, args),
                "check_missing_parameters" => CheckMissingParams(doc, args),
                "check_elevation_conflicts" => CheckElevation(doc, args),
                "check_oversized_elements" => CheckOversized(doc, args),
                "get_warnings_mep" => GetMepWarnings(doc, args),
                "check_pipe_slope" => CheckPipeSlope(doc, args),
                "check_slope_continuity" => CheckSlopeContinuity(doc, args),
                "get_penetration_schedule" => GetPenetrationSchedule(doc, args),
                "check_fire_dampers" => CheckFireDampers(doc, args),
                "check_insulation_coverage" => CheckInsulationCoverage(uidoc, doc, args),
                "check_velocity" => CheckVelocity(doc, args),
                "check_noise_level" => CheckNoiseLevel(doc, args),
                "check_access_panel" => CheckAccessPanel(doc, args),
                _ => UnknownTool(functionName)
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
                    else if (diam > maxDiamFt)
                    {
                        oversized = true;
                        reason = $"Diameter {Math.Round(diam * 304.8, 0)}mm > {maxDiamMm}mm";
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
                    : $"max_width={maxWidthMm}mm, max_round_diameter={maxDiamMm}mm",
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

        private string CheckPipeSlope(Document doc, Dictionary<string, object> args)
        {
            var systemFilter = GetArg<string>(args, "system_name");
            var levelFilter = GetArg<string>(args, "level");
            int limit = GetArg(args, "limit", 100);

            bool hasMin = args != null && args.ContainsKey("min_slope_pct");
            bool hasMax = args != null && args.ContainsKey("max_slope_pct");
            double minSlope = hasMin ? GetArg(args, "min_slope_pct", 0.1) : 0.1;
            double maxSlope = hasMax ? GetArg(args, "max_slope_pct", double.NaN) : double.NaN;

            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType();

            if (!string.IsNullOrEmpty(levelFilter))
            {
                var level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(levelFilter, StringComparison.OrdinalIgnoreCase));
                if (level == null) return JsonError($"Level '{levelFilter}' not found.");
                collector = collector.WherePasses(new ElementLevelFilter(level.Id));
            }

            int checkedCount = 0;
            var issues = new List<object>();

            foreach (var elem in collector)
            {
                var sys = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                var sysC = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                if (!MatchesSystem(sys, sysC, systemFilter))
                    continue;

                var slopeInfo = GetPipeSlope(elem);
                if (slopeInfo.source == "vertical" || slopeInfo.source == "unknown")
                    continue;

                checkedCount++;
                var slopePct = slopeInfo.slopeRatio * 100.0;
                var absPct = Math.Abs(slopePct);

                bool belowMin = absPct < minSlope;
                bool aboveMax = !double.IsNaN(maxSlope) && absPct > maxSlope;

                if (belowMin || aboveMax)
                {
                    issues.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        type = GetElementTypeName(doc, elem),
                        size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                        level = GetElementLevel(doc, elem),
                        system = sys,
                        slope_pct = Math.Round(slopePct, 3),
                        slope_source = slopeInfo.source,
                        issue = belowMin ? "below_min" : "above_max"
                    });
                }

                if (issues.Count >= limit) break;
            }

            return JsonSerializer.Serialize(new
            {
                checked_pipes = checkedCount,
                min_slope_pct = minSlope,
                max_slope_pct = double.IsNaN(maxSlope) ? (double?)null : maxSlope,
                issues_found = issues.Count,
                issues
            }, JsonOpts);
        }

        private string CheckSlopeContinuity(Document doc, Dictionary<string, object> args)
        {
            var startId = GetArg<long>(args, "element_id");
            var systemFilter = GetArg<string>(args, "system_name");
            var levelFilter = GetArg<string>(args, "level");

            var pipes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType().ToList();

            if (!string.IsNullOrEmpty(levelFilter))
                pipes = pipes.Where(p => GetElementLevel(doc, p).Equals(levelFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(systemFilter))
                pipes = pipes.Where(p => MatchesSystem(
                    p.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "",
                    p.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "",
                    systemFilter)).ToList();

            if (startId > 0)
            {
                var startElem = doc.GetElement(new ElementId(startId));
                if (startElem == null) return JsonError($"Element {startId} not found.");
                pipes = pipes.Where(p => p.Id.Value == startId ||
                    IsInSameSystem(doc, startElem, p, pipes)).ToList();
            }

            var issues = new List<object>();
            int totalChecked = 0;

            foreach (var pipe in pipes)
            {
                totalChecked++;
                if (!(pipe.Location is LocationCurve lc)) continue;
                var start = lc.Curve.GetEndPoint(0);
                var end = lc.Curve.GetEndPoint(1);
                var dz = end.Z - start.Z;
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var horizontal = Math.Sqrt(dx * dx + dy * dy);
                if (horizontal < 1e-6) continue;

                if (pipe is not MEPCurve mepCurve) continue;
                var cm = mepCurve.ConnectorManager;
                if (cm == null) continue;

                foreach (Connector conn in cm.Connectors)
                {
                    if (!conn.IsConnected) continue;
                    foreach (Connector refConn in conn.AllRefs)
                    {
                        var neighbor = refConn?.Owner as Element;
                        if (neighbor == null || neighbor.Id == pipe.Id) continue;
                        if (neighbor.Document != doc) continue;
                        if (!(neighbor.Location is LocationCurve nlc)) continue;

                        var nStart = nlc.Curve.GetEndPoint(0);
                        var nEnd = nlc.Curve.GetEndPoint(1);
                        var nDz = nEnd.Z - nStart.Z;
                        var nHoriz = Math.Sqrt(Math.Pow(nEnd.X - nStart.X, 2) + Math.Pow(nEnd.Y - nStart.Y, 2));
                        if (nHoriz < 1e-6) continue;

                        bool currentDown = dz < -1e-6;
                        bool neighborDown = nDz < -1e-6;
                        bool currentFlat = Math.Abs(dz) < 1e-6;
                        bool neighborFlat = Math.Abs(nDz) < 1e-6;

                        if (!currentFlat && !neighborFlat && currentDown != neighborDown)
                        {
                            issues.Add(new
                            {
                                pipe_id = pipe.Id.Value,
                                neighbor_id = neighbor.Id.Value,
                                pipe_slope_pct = Math.Round((dz / horizontal) * 100, 3),
                                neighbor_slope_pct = Math.Round((nDz / nHoriz) * 100, 3),
                                issue = "slope_reversal",
                                system = pipe.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-",
                                level = GetElementLevel(doc, pipe)
                            });
                            break;
                        }
                    }
                }
                if (issues.Count >= 200) break;
            }

            return JsonSerializer.Serialize(new
            {
                pipes_checked = totalChecked,
                reversals_found = issues.Count,
                issues
            }, JsonOpts);
        }

        private static bool IsInSameSystem(Document doc, Element start, Element candidate, List<Element> allPipes)
        {
            var sys1 = start.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
            var sys2 = candidate.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
            return !string.IsNullOrEmpty(sys1) && sys1.Equals(sys2, StringComparison.OrdinalIgnoreCase);
        }

        private string GetPenetrationSchedule(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category")?.ToLower() ?? "all";
            var limit = GetArg<int>(args, "limit", 200);

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            if (levels.Count < 2)
                return JsonSerializer.Serialize(new { note = "Need at least 2 levels for penetration check.", penetrations = new List<object>() }, JsonOpts);

            var cats = new List<BuiltInCategory>();
            if (catFilter is "all" or "duct") cats.Add(BuiltInCategory.OST_DuctCurves);
            if (catFilter is "all" or "pipe") cats.Add(BuiltInCategory.OST_PipeCurves);
            if (catFilter is "all" or "conduit") cats.Add(BuiltInCategory.OST_Conduit);

            var results = new List<object>();

            foreach (var cat in cats)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    if (results.Count >= limit) break;
                    if (!(elem.Location is LocationCurve lc)) continue;

                    var p0 = lc.Curve.GetEndPoint(0);
                    var p1 = lc.Curve.GetEndPoint(1);
                    double minZ = Math.Min(p0.Z, p1.Z);
                    double maxZ = Math.Max(p0.Z, p1.Z);

                    var crossedLevels = levels
                        .Where(l => l.Elevation > minZ + 0.01 && l.Elevation < maxZ - 0.01)
                        .Select(l => l.Name).ToList();

                    if (crossedLevels.Count > 0)
                    {
                        results.Add(new
                        {
                            id = elem.Id.Value,
                            category = elem.Category?.Name ?? "-",
                            type = GetElementTypeName(doc, elem),
                            size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                            system = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-",
                            level = GetElementLevel(doc, elem),
                            bottom_elevation_m = Math.Round(minZ * 0.3048, 3),
                            top_elevation_m = Math.Round(maxZ * 0.3048, 3),
                            levels_crossed = crossedLevels
                        });
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                penetration_count = results.Count,
                elements = results
            }, JsonOpts);
        }

        private string CheckFireDampers(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            var limit = GetArg<int>(args, "limit", 200);

            var accessories = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DuctAccessory)
                .WhereElementIsNotElementType().ToList();

            var dampers = new List<object>();
            int connectedCount = 0, disconnectedCount = 0;

            foreach (var elem in accessories)
            {
                if (dampers.Count >= limit) break;

                var familyName = GetFamilyName(doc, elem).ToLower();
                var typeName = GetElementTypeName(doc, elem).ToLower();
                bool isDamper = familyName.Contains("fire") || familyName.Contains("damper") ||
                                typeName.Contains("fire") || typeName.Contains("damper") ||
                                familyName.Contains("brandschutz");
                if (!isDamper) continue;

                if (!string.IsNullOrEmpty(levelFilter) &&
                    !GetElementLevel(doc, elem).Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var connectedDucts = new List<object>();
                if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
                {
                    foreach (Connector conn in fi.MEPModel.ConnectorManager.Connectors)
                    {
                        if (!conn.IsConnected) continue;
                        foreach (Connector refConn in conn.AllRefs)
                        {
                            var owner = refConn?.Owner as Element;
                            if (owner == null || owner.Id == elem.Id) continue;
                            if (owner.Document != doc) continue;
                            connectedDucts.Add(new
                            {
                                id = owner.Id.Value,
                                category = owner.Category?.Name ?? "-",
                                size = owner.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-"
                            });
                        }
                    }
                }

                bool isConnected = connectedDucts.Count > 0;
                if (isConnected) connectedCount++; else disconnectedCount++;

                dampers.Add(new
                {
                    id = elem.Id.Value,
                    family = GetFamilyName(doc, elem),
                    type = GetElementTypeName(doc, elem),
                    level = GetElementLevel(doc, elem),
                    system = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-",
                    is_connected = isConnected,
                    connected_ducts = connectedDucts
                });
            }

            return JsonSerializer.Serialize(new
            {
                total_fire_dampers = dampers.Count,
                connected = connectedCount,
                disconnected = disconnectedCount,
                dampers
            }, JsonOpts);
        }

        private string CheckInsulationCoverage(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            var category = (GetArg<string>(args, "category") ?? "all").ToLower();
            var systemFilter = GetArg<string>(args, "system_name");
            var levelFilter = GetArg<string>(args, "level");
            int limit = GetArg(args, "limit", 200);

            var categories = new List<BuiltInCategory>();
            if (category is "all" or "pipes") categories.Add(BuiltInCategory.OST_PipeCurves);
            if (category is "all" or "ducts") categories.Add(BuiltInCategory.OST_DuctCurves);

            var uninsulated = new List<object>();
            int totalChecked = 0, insulated = 0;

            foreach (var bic in categories)
            {
                var collector = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType();

                foreach (var elem in collector)
                {
                    totalChecked++;

                    if (!string.IsNullOrEmpty(systemFilter))
                    {
                        var sysParam = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                        if (sysParam == null || !(sysParam.AsString()?.Contains(systemFilter, StringComparison.OrdinalIgnoreCase) == true))
                            continue;
                    }

                    if (!string.IsNullOrEmpty(levelFilter))
                    {
                        var lvlParam = elem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)
                            ?? elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                        if (lvlParam != null)
                        {
                            var lvl = doc.GetElement(lvlParam.AsElementId());
                            if (lvl != null && !lvl.Name.Contains(levelFilter, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                    }

                    ICollection<ElementId> insIds = null;
                    try { insIds = InsulationLiningBase.GetInsulationIds(doc, elem.Id); } catch { }
                    if (insIds != null && insIds.Count > 0)
                    {
                        insulated++;
                        continue;
                    }

                    if (uninsulated.Count >= limit) continue;

                    var sysName = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-";
                    var size = GetMepSizeString(elem);

                    uninsulated.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        system_name = sysName,
                        size = size ?? "-",
                        name = elem.Name
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                total_checked = totalChecked,
                insulated_count = insulated,
                uninsulated_count = uninsulated.Count,
                coverage_pct = totalChecked > 0 ? Math.Round((double)insulated / totalChecked * 100, 1) : 0,
                uninsulated
            }, JsonOpts);
        }

        private string CheckVelocity(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category")?.ToLower();
            double maxVelocityMs = GetArg(args, "max_velocity_ms", 0);
            var systemFilter = GetArg<string>(args, "system_name");
            int limit = GetArg(args, "limit", 100);

            if (string.IsNullOrEmpty(category) || (category != "pipes" && category != "ducts"))
                return JsonError("category must be 'Pipes' or 'Ducts'.");

            if (maxVelocityMs <= 0) maxVelocityMs = category == "pipes" ? 2.5 : 8.0;

            var bic = category == "pipes" ? BuiltInCategory.OST_PipeCurves : BuiltInCategory.OST_DuctCurves;
            var collector = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType();

            var issues = new List<object>();

            foreach (var elem in collector)
            {
                if (!string.IsNullOrEmpty(systemFilter))
                {
                    var sys = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                    if (!sys.Contains(systemFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }

                double velocityMs = 0;
                if (category == "pipes")
                {
                    var velParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_VELOCITY_PARAM);
                    if (velParam != null && velParam.HasValue)
                    {
                        try { velocityMs = velParam.AsDouble() * 0.3048; } catch { }
                    }
                    else
                    {
                        var flowParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM);
                        var diamParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (flowParam != null && flowParam.HasValue && diamParam != null && diamParam.HasValue)
                        {
                            try
                            {
                                var flow = flowParam.AsDouble();
                                var diamFt = diamParam.AsDouble();
                                if (diamFt > 1e-6)
                                {
                                    var areaFt2 = Math.PI * (diamFt / 2) * (diamFt / 2);
                                    var velFtS = flow > 0 ? flow / areaFt2 : 0;
                                    velocityMs = velFtS * 0.3048;
                                }
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    var flowParam = elem.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM);
                    if (flowParam != null && flowParam.HasValue)
                    {
                        try
                        {
                            var flow = flowParam.AsDouble();
                            var w = elem.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
                            var h = elem.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0;
                            var d = elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                            double areaFt2 = 0;
                            if (d > 1e-6) areaFt2 = Math.PI * (d / 2) * (d / 2);
                            else if (w > 1e-6 && h > 1e-6) areaFt2 = w * h;
                            if (areaFt2 > 1e-6) velocityMs = (flow / 60.0 / areaFt2) * 0.3048;
                        }
                        catch { }
                    }
                }

                if (velocityMs > maxVelocityMs)
                {
                    issues.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        type = GetElementTypeName(doc, elem),
                        system = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-",
                        size = GetMepSizeString(elem) ?? "-",
                        velocity_ms = Math.Round(velocityMs, 2),
                        max_velocity_ms = maxVelocityMs
                    });
                    if (issues.Count >= limit) break;
                }
            }

            return JsonSerializer.Serialize(new
            {
                category = category == "pipes" ? "Pipes" : "Ducts",
                max_velocity_ms = maxVelocityMs,
                issues_found = issues.Count,
                issues
            }, JsonOpts);
        }

        private string CheckNoiseLevel(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var systemName = GetArg<string>(args, "system_name");
            double maxDb = GetArg(args, "max_db", 45.0);

            BuiltInCategory bic = category == "Pipes" ? BuiltInCategory.OST_PipeCurves : BuiltInCategory.OST_DuctCurves;
            var elements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();

            var results = new List<object>();
            int overLimit = 0;
            int totalChecked = 0;

            foreach (var elem in elements)
            {
                {
                    var sp = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                    var scl = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                    if (!MatchesSystem(sp, scl, systemName)) continue;
                }

                var flowParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)
                             ?? elem.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM);
                double flow = flowParam?.AsDouble() ?? 0;

                var diaParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                            ?? elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                double diameter = diaParam?.AsDouble() ?? 0;
                if (diameter <= 0) continue;

                totalChecked++;
                double area = Math.PI * Math.Pow(diameter / 2, 2);
                double velocity = area > 0
                    ? (category == "Ducts" ? flow / (60.0 * area) : flow / area)
                    : 0;
                double noiseLevelDb = 10 + 50 * Math.Log10(Math.Max(velocity, 0.1)) + 10 * Math.Log10(Math.Max(diameter * 304.8, 1));

                if (noiseLevelDb > maxDb) overLimit++;
                if (results.Count < 50)
                    results.Add(new { id = elem.Id.Value, name = elem.Name,
                        velocity_fps = Math.Round(velocity, 2), estimated_noise_db = Math.Round(noiseLevelDb, 1),
                        exceeds_limit = noiseLevelDb > maxDb });
            }

            return JsonSerializer.Serialize(new
            {
                category, max_db = maxDb, total_checked = totalChecked, over_limit = overLimit, elements = results
            }, JsonOpts);
        }

        private string CheckAccessPanel(Document doc, Dictionary<string, object> args)
        {
            double minH = GetArg(args, "min_height_mm", 600.0) / 304.8;
            double maxH = GetArg(args, "max_height_mm", 2000.0) / 304.8;
            var specificIds = GetArgLongArray(args, "element_ids");

            var elements = new List<Element>();
            if (specificIds != null && specificIds.Count > 0)
            {
                elements = specificIds.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null).ToList();
            }
            else
            {
                elements = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeAccessory)
                    .WhereElementIsNotElementType().ToList();
                elements.AddRange(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DuctAccessory)
                    .WhereElementIsNotElementType());
            }

            var accessible = new List<object>();
            var inaccessible = new List<object>();

            foreach (var elem in elements)
            {
                var bb = elem.get_BoundingBox(null);
                if (bb == null) continue;
                double centerZ = (bb.Min.Z + bb.Max.Z) / 2;
                bool ok = centerZ >= minH && centerZ <= maxH;
                var info = new { id = elem.Id.Value, name = elem.Name, category = elem.Category?.Name,
                    height_mm = Math.Round(centerZ * 304.8, 0), accessible = ok };
                if (ok) accessible.Add(info); else inaccessible.Add(info);
            }

            return JsonSerializer.Serialize(new
            {
                total = accessible.Count + inaccessible.Count,
                accessible_count = accessible.Count,
                inaccessible_count = inaccessible.Count,
                height_range_mm = new { min = minH * 304.8, max = maxH * 304.8 },
                inaccessible = inaccessible.Take(50)
            }, JsonOpts);
        }

        private static string GetMepSizeString(Element e)
        {
            var diam = e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (diam != null && diam.HasValue)
                return $"DN{Math.Round(diam.AsDouble() * 304.8)}";

            var w = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var h = e.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (w != null && h != null && w.HasValue && h.HasValue)
                return $"{Math.Round(w.AsDouble() * 304.8)}x{Math.Round(h.AsDouble() * 304.8)}";

            return null;
        }

        private static (double slopeRatio, string source) GetPipeSlope(Element elem)
        {
            var param = elem.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE);
            if (param != null && param.HasValue)
            {
                try
                {
                    return (param.AsDouble(), "parameter");
                }
                catch { }
            }

            if (elem.Location is LocationCurve lc)
            {
                var start = lc.Curve.GetEndPoint(0);
                var end = lc.Curve.GetEndPoint(1);
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var horizontal = Math.Sqrt(dx * dx + dy * dy);
                if (horizontal < 1e-6)
                    return (0, "vertical");
                if (horizontal > 1e-6)
                {
                    var slope = (end.Z - start.Z) / horizontal;
                    return (slope, "geometry");
                }
            }

            return (0, "unknown");
        }
    }
}
