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
    public class MepSystemAnalysisSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepSystemAnalysis";
        protected override string SkillDescription => "Analyze MEP systems: ducts, pipes, conduits, cable trays";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_mep_systems", "get_system_elements",
            "get_duct_summary", "get_pipe_summary",
            "get_conduit_summary", "get_cable_tray_summary",
            "calculate_system_totals",
            "get_critical_path", "analyze_pressure_loss", "get_flow_distribution", "get_mep_elevation_table"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_mep_systems",
                "List all MEP systems in the model (duct systems, piping systems). Returns system name, classification, element count, total length.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_type": { "type": "string", "description": "Filter by type: 'duct', 'pipe', or 'all'. Default 'all'." }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_system_elements",
                "Get all elements belonging to a specific MEP system by system name.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "Exact system name to query" },
                        "limit": { "type": "integer", "description": "Max elements to return. Default 100.", "default": 100 }
                    },
                    "required": ["system_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_duct_summary",
                "Summarize all ducts: total length, grouped by size, level, and system. Includes insulation info.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "system_name": { "type": "string", "description": "Optional system name filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_pipe_summary",
                "Summarize all pipes: total length, grouped by diameter, level, and system. Includes insulation info.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "system_name": { "type": "string", "description": "Optional system name filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_conduit_summary",
                "Summarize all conduits: total length, grouped by size and level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_cable_tray_summary",
                "Summarize all cable trays: total length, grouped by size and level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("calculate_system_totals",
                "Calculate per-system totals: total length, element count broken down by type (curves, fittings, accessories, equipment, terminals). Can filter by MEP category.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "System name (exact or partial match). If not provided, shows all systems." },
                        "level": { "type": "string", "description": "Optional level filter" },
                        "category": { "type": "string", "enum": ["all", "duct", "pipe", "conduit", "cable_tray"], "description": "MEP category filter. Default: all" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_critical_path",
                "Get the critical path of a pipe/duct system with pressure drop and flow data.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "MEP system name" },
                        "include_segments": { "type": "boolean", "description": "Include per-segment details. Default false." }
                    },
                    "required": ["system_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("analyze_pressure_loss",
                "Per-segment pressure loss analysis for a pipe/duct system.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "MEP system name" },
                        "sort_by": { "type": "string", "enum": ["pressure_drop", "length", "cumulative"], "description": "Sort order. Default: pressure_drop" }
                    },
                    "required": ["system_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_flow_distribution",
                "Get flow rate distribution for all branches in a pipe/duct network.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "MEP system name" },
                        "unit": { "type": "string", "enum": ["L/s", "CFM", "m3/h"], "description": "Flow unit. Default: L/s" }
                    },
                    "required": ["system_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_mep_elevation_table",
                "Generate elevation summary table for MEP elements by system and level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "categories": { "type": "array", "items": { "type": "string" }, "description": "Categories to include (Pipes, Ducts, etc). Default: all MEP." },
                        "group_by": { "type": "string", "enum": ["system", "level", "both"], "description": "Grouping. Default: both" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_mep_systems" => GetMepSystems(doc, args),
                "get_system_elements" => GetSystemElements(doc, args),
                "get_duct_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_DuctCurves, "Ducts"),
                "get_pipe_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_PipeCurves, "Pipes"),
                "get_conduit_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_Conduit, "Conduits"),
                "get_cable_tray_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_CableTray, "Cable Trays"),
                "calculate_system_totals" => CalculateSystemTotals(doc, args),
                "get_critical_path" => GetCriticalPath(doc, args),
                "analyze_pressure_loss" => AnalyzePressureLoss(doc, args),
                "get_flow_distribution" => GetFlowDistribution(doc, args),
                "get_mep_elevation_table" => GetMepElevationTable(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetMepSystems(Document doc, Dictionary<string, object> args)
        {
            var typeFilter = GetArg<string>(args, "system_type")?.ToLower() ?? "all";

            var categories = new List<BuiltInCategory>();
            if (typeFilter is "all" or "duct")
            {
                categories.Add(BuiltInCategory.OST_DuctCurves);
                categories.Add(BuiltInCategory.OST_FlexDuctCurves);
            }
            if (typeFilter is "all" or "pipe")
            {
                categories.Add(BuiltInCategory.OST_PipeCurves);
                categories.Add(BuiltInCategory.OST_FlexPipeCurves);
            }

            var systemMap = new Dictionary<string, SystemInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var cat in categories)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (var elem in elems)
                {
                    var sysName = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "(Unassigned)";
                    var sysClass = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "-";
                    var length = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;

                    if (!systemMap.TryGetValue(sysName, out var info))
                    {
                        info = new SystemInfo { Name = sysName, Classification = sysClass, Category = elem.Category?.Name ?? "-" };
                        systemMap[sysName] = info;
                    }
                    info.ElementCount++;
                    info.TotalLengthFt += length;
                }
            }

            var results = systemMap.Values
                .OrderBy(s => s.Classification)
                .ThenBy(s => s.Name)
                .Select(s => new
                {
                    name = s.Name,
                    classification = s.Classification,
                    category = s.Category,
                    element_count = s.ElementCount,
                    total_length_m = Math.Round(s.TotalLengthFt * 0.3048, 2)
                })
                .ToList();

            return JsonSerializer.Serialize(new { system_count = results.Count, systems = results }, JsonOpts);
        }

        private string GetSystemElements(Document doc, Dictionary<string, object> args)
        {
            var systemName = GetArg<string>(args, "system_name");
            var limit = GetArg<int>(args, "limit", 100);
            if (string.IsNullOrEmpty(systemName)) return JsonError("system_name is required.");

            var mepCategories = new[]
            {
                BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexPipeCurves,
                BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_MechanicalEquipment
            };

            var results = new List<object>();
            foreach (var cat in mepCategories)
            {
                if (results.Count >= limit) break;

                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (var elem in elems)
                {
                    var sn = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                    var sc = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                    if (!MatchesSystem(sn, sc, systemName))
                        continue;

                    results.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        family = GetFamilyName(doc, elem),
                        type = GetElementTypeName(doc, elem),
                        size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                        level = GetElementLevel(doc, elem),
                        system = sn
                    });

                    if (results.Count >= limit) break;
                }
            }

            return JsonSerializer.Serialize(new { system_name = systemName, count = results.Count, elements = results }, JsonOpts);
        }

        private string GetMepCurveSummary(Document doc, Dictionary<string, object> args,
            BuiltInCategory category, string label)
        {
            var levelFilter = GetArg<string>(args, "level");
            var systemFilter = GetArg<string>(args, "system_name");

            var elems = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();

            double totalLengthFt = 0;
            int totalCount = 0;
            var bySize = new Dictionary<string, SizeGroup>(StringComparer.OrdinalIgnoreCase);
            var byLevel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elems)
            {
                var elemLevel = GetElementLevel(doc, elem);
                if (!string.IsNullOrEmpty(levelFilter) &&
                    !elemLevel.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sysName = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                var sysClass = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                if (!MatchesSystem(sysName, sysClass, systemFilter))
                    continue;

                var length = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
                var size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "Unknown";
                var insType = elem.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_TYPE)?.AsValueString() ?? "";
                var insThick = elem.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS)?.AsValueString() ?? "";

                totalLengthFt += length;
                totalCount++;

                if (!bySize.TryGetValue(size, out var sg))
                {
                    sg = new SizeGroup { Size = size };
                    bySize[size] = sg;
                }
                sg.Count++;
                sg.TotalLengthFt += length;
                if (!string.IsNullOrEmpty(insType)) sg.HasInsulation = true;

                byLevel.TryGetValue(elemLevel, out double lvlLen);
                byLevel[elemLevel] = lvlLen + length;
            }

            var sizeResults = bySize.Values
                .OrderByDescending(s => s.TotalLengthFt)
                .Select(s => new
                {
                    size = s.Size,
                    count = s.Count,
                    total_length_m = Math.Round(s.TotalLengthFt * 0.3048, 2),
                    has_insulation = s.HasInsulation
                }).ToList();

            var levelResults = byLevel
                .OrderBy(kv => kv.Key)
                .Select(kv => new
                {
                    level = kv.Key,
                    total_length_m = Math.Round(kv.Value * 0.3048, 2)
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                category = label,
                total_count = totalCount,
                total_length_m = Math.Round(totalLengthFt * 0.3048, 2),
                by_size = sizeResults,
                by_level = levelResults
            }, JsonOpts);
        }

        private string CalculateSystemTotals(Document doc, Dictionary<string, object> args)
        {
            var systemFilter = GetArg<string>(args, "system_name");
            var levelFilter = GetArg<string>(args, "level");
            var categoryFilter = GetArg<string>(args, "category")?.ToLower() ?? "all";

            var allCategories = new Dictionary<BuiltInCategory, string>
            {
                [BuiltInCategory.OST_DuctCurves] = "curves",
                [BuiltInCategory.OST_PipeCurves] = "curves",
                [BuiltInCategory.OST_FlexDuctCurves] = "flex_curves",
                [BuiltInCategory.OST_FlexPipeCurves] = "flex_curves",
                [BuiltInCategory.OST_DuctFitting] = "fittings",
                [BuiltInCategory.OST_PipeFitting] = "fittings",
                [BuiltInCategory.OST_DuctAccessory] = "accessories",
                [BuiltInCategory.OST_PipeAccessory] = "accessories",
                [BuiltInCategory.OST_MechanicalEquipment] = "equipment",
                [BuiltInCategory.OST_PlumbingFixtures] = "fixtures",
                [BuiltInCategory.OST_DuctTerminal] = "terminals",
                [BuiltInCategory.OST_Sprinklers] = "sprinklers",
                [BuiltInCategory.OST_Conduit] = "curves",
                [BuiltInCategory.OST_CableTray] = "curves"
            };

            var mepCategories = categoryFilter switch
            {
                "duct" => allCategories.Where(kv =>
                    kv.Key is BuiltInCategory.OST_DuctCurves or BuiltInCategory.OST_FlexDuctCurves
                        or BuiltInCategory.OST_DuctFitting or BuiltInCategory.OST_DuctAccessory
                        or BuiltInCategory.OST_DuctTerminal).ToDictionary(kv => kv.Key, kv => kv.Value),
                "pipe" => allCategories.Where(kv =>
                    kv.Key is BuiltInCategory.OST_PipeCurves or BuiltInCategory.OST_FlexPipeCurves
                        or BuiltInCategory.OST_PipeFitting or BuiltInCategory.OST_PipeAccessory
                        or BuiltInCategory.OST_PlumbingFixtures or BuiltInCategory.OST_Sprinklers).ToDictionary(kv => kv.Key, kv => kv.Value),
                "conduit" => allCategories.Where(kv =>
                    kv.Key is BuiltInCategory.OST_Conduit).ToDictionary(kv => kv.Key, kv => kv.Value),
                "cable_tray" => allCategories.Where(kv =>
                    kv.Key is BuiltInCategory.OST_CableTray).ToDictionary(kv => kv.Key, kv => kv.Value),
                _ => allCategories
            };

            var systemTotals = new Dictionary<string, SystemTotals>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in mepCategories)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(kvp.Key).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    if (!string.IsNullOrEmpty(levelFilter) &&
                        !GetElementLevel(doc, elem).Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sysName = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "(Unassigned)";
                    var sysClass = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                    if (!MatchesSystem(sysName, sysClass, systemFilter))
                        continue;

                    if (!systemTotals.TryGetValue(sysName, out var totals))
                    {
                        totals = new SystemTotals { Name = sysName };
                        systemTotals[sysName] = totals;
                    }

                    totals.TotalElements++;
                    var length = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
                    totals.TotalLengthFt += length;

                    switch (kvp.Value)
                    {
                        case "curves": case "flex_curves": totals.Curves++; break;
                        case "fittings": totals.Fittings++; break;
                        case "accessories": totals.Accessories++; break;
                        case "equipment": totals.Equipment++; break;
                        case "fixtures": totals.Fixtures++; break;
                        case "terminals": totals.Terminals++; break;
                        case "sprinklers": totals.Sprinklers++; break;
                    }
                }
            }

            var results = systemTotals.Values
                .OrderByDescending(s => s.TotalElements)
                .Select(s => new
                {
                    system = s.Name,
                    total_elements = s.TotalElements,
                    total_length_m = Math.Round(s.TotalLengthFt * 0.3048, 2),
                    curves = s.Curves,
                    fittings = s.Fittings,
                    accessories = s.Accessories,
                    equipment = s.Equipment,
                    fixtures = s.Fixtures,
                    terminals = s.Terminals,
                    sprinklers = s.Sprinklers
                }).ToList();

            return JsonSerializer.Serialize(new { system_count = results.Count, systems = results }, JsonOpts);
        }

        private string GetCriticalPath(Document doc, Dictionary<string, object> args)
        {
            var systemName = GetArg<string>(args, "system_name");
            bool includeSegments = GetArg(args, "include_segments", false);
            if (string.IsNullOrEmpty(systemName)) return JsonError("system_name required.");

            var normSys = NormalizeArg(systemName);
            var systems = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Mechanical.MechanicalSystem))
                .Cast<Autodesk.Revit.DB.Mechanical.MechanicalSystem>()
                .Where(s => MatchesSystem(s.Name, s.SystemType.ToString(), normSys))
                .ToList();

            if (systems.Count == 0)
            {
                var pipeSystems = new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystem))
                    .Cast<Autodesk.Revit.DB.Plumbing.PipingSystem>()
                    .Where(s => MatchesSystem(s.Name, s.SystemType.ToString(), normSys))
                    .ToList();

                if (pipeSystems.Count == 0) return JsonError($"System '{systemName}' not found.");

                var ps = pipeSystems.First();
                var elements = ps.PipingNetwork.Cast<Element>().ToList();
                double totalLength = 0;
                var segments = new List<object>();
                foreach (var elem in elements)
                {
                    var lenParam = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    double len = lenParam?.AsDouble() ?? 0;
                    totalLength += len;
                    var pressParam = elem.LookupParameter("Pressure Drop") ?? elem.get_Parameter(BuiltInParameter.RBS_PIPE_PRESSUREDROP_PARAM);
                    double press = pressParam?.AsDouble() ?? 0;
                    if (includeSegments && segments.Count < 50)
                        segments.Add(new { id = elem.Id.Value, name = elem.Name, length_ft = Math.Round(len, 2), pressure_drop = Math.Round(press, 4) });
                }

                return JsonSerializer.Serialize(new
                {
                    system_name = ps.Name,
                    system_type = "Piping",
                    element_count = elements.Count,
                    total_length_ft = Math.Round(totalLength, 2),
                    segments = includeSegments ? segments : null
                }, JsonOpts);
            }

            var mechSys = systems.First();
            var ductNetwork = mechSys.DuctNetwork.Cast<Element>().ToList();
            double ductTotalLength = 0;
            var ductSegments = new List<object>();
            foreach (var elem in ductNetwork)
            {
                var lenParam = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                double len = lenParam?.AsDouble() ?? 0;
                ductTotalLength += len;
                if (includeSegments && ductSegments.Count < 50)
                    ductSegments.Add(new { id = elem.Id.Value, name = elem.Name, length_ft = Math.Round(len, 2) });
            }

            return JsonSerializer.Serialize(new
            {
                system_name = mechSys.Name,
                system_type = "Mechanical",
                element_count = ductNetwork.Count,
                total_length_ft = Math.Round(ductTotalLength, 2),
                segments = includeSegments ? ductSegments : null
            }, JsonOpts);
        }

        private string AnalyzePressureLoss(Document doc, Dictionary<string, object> args)
        {
            var systemName = GetArg<string>(args, "system_name");
            var sortBy = GetArg(args, "sort_by", "pressure_drop");
            if (string.IsNullOrEmpty(systemName)) return JsonError("system_name required.");

            var mepCategoryIds = new List<ElementId>
            {
                Category.GetCategory(doc, BuiltInCategory.OST_DuctCurves).Id,
                Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves).Id,
                Category.GetCategory(doc, BuiltInCategory.OST_DuctFitting).Id,
                Category.GetCategory(doc, BuiltInCategory.OST_PipeFitting).Id,
                Category.GetCategory(doc, BuiltInCategory.OST_MechanicalEquipment).Id
            };
            var mepCatFilter = new ElementMulticategoryFilter(mepCategoryIds);
            var mepElements = new FilteredElementCollector(doc)
                .WherePasses(mepCatFilter)
                .WhereElementIsNotElementType()
                .Where(e =>
                {
                    var sn = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                    var sc = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                    return MatchesSystem(sn, sc, systemName);
                })
                .ToList();

            if (mepElements.Count == 0) return JsonError($"No elements found in system '{systemName}'.");

            var segments = new List<(long id, string name, double length, double pressureDrop)>();
            double cumulative = 0;
            foreach (var elem in mepElements)
            {
                var lenParam = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                double len = lenParam?.AsDouble() ?? 0;
                var pdParam = elem.LookupParameter("Pressure Drop")
                    ?? elem.get_Parameter(BuiltInParameter.RBS_PIPE_PRESSUREDROP_PARAM)
                    ?? elem.get_Parameter(BuiltInParameter.RBS_DUCT_PRESSURE_DROP);
                double pd = pdParam?.AsDouble() ?? 0;
                segments.Add((elem.Id.Value, elem.Name, len, pd));
            }

            var sorted = sortBy switch
            {
                "length" => segments.OrderByDescending(s => s.length).ToList(),
                "cumulative" => segments,
                _ => segments.OrderByDescending(s => s.pressureDrop).ToList()
            };

            cumulative = 0;
            var results = sorted.Take(50).Select(s =>
            {
                cumulative += s.pressureDrop;
                return new { id = s.id, name = s.name, length_ft = Math.Round(s.length, 2),
                    pressure_drop = Math.Round(s.pressureDrop, 4), cumulative = Math.Round(cumulative, 4) };
            }).ToList();

            var maxPd = segments.Any() ? segments.Max(s => s.pressureDrop) : 0;
            var maxElem = segments.FirstOrDefault(s => s.pressureDrop == maxPd);

            return JsonSerializer.Serialize(new
            {
                system_name = systemName,
                element_count = segments.Count,
                total_pressure_drop = Math.Round(segments.Sum(s => s.pressureDrop), 4),
                highest_drop = maxElem.id > 0 ? new { id = maxElem.id, name = maxElem.name, pressure_drop = Math.Round(maxPd, 4) } : null,
                segments = results
            }, JsonOpts);
        }

        private string GetFlowDistribution(Document doc, Dictionary<string, object> args)
        {
            var systemName = GetArg<string>(args, "system_name");
            var unit = GetArg(args, "unit", "L/s");
            if (string.IsNullOrEmpty(systemName)) return JsonError("system_name required.");

            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e =>
                {
                    var sn = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                    var sc = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                    return MatchesSystem(sn, sc, systemName);
                })
                .ToList();

            if (elements.Count == 0) return JsonError($"No elements in system '{systemName}'.");

            var branches = new Dictionary<string, (int count, double totalFlow, double minFlow, double maxFlow)>();
            foreach (var elem in elements)
            {
                var flowParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)
                             ?? elem.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM);
                double flow = flowParam?.AsDouble() ?? 0;

                double converted = unit switch
                {
                    "CFM" => flow,
                    "m3/h" => flow * 1.69901,
                    _ => flow * 0.47195
                };

                var sizeParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                             ?? elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                string size = sizeParam?.AsValueString() ?? "-";

                if (!branches.ContainsKey(size))
                    branches[size] = (0, 0, double.MaxValue, 0);
                var (c, tf, mn, mx) = branches[size];
                branches[size] = (c + 1, tf + converted, Math.Min(mn, converted), Math.Max(mx, converted));
            }

            var result = branches.Select(kvp => new
            {
                size = kvp.Key,
                element_count = kvp.Value.count,
                avg_flow = kvp.Value.count > 0 ? Math.Round(kvp.Value.totalFlow / kvp.Value.count, 2) : 0,
                min_flow = Math.Round(kvp.Value.minFlow == double.MaxValue ? 0 : kvp.Value.minFlow, 2),
                max_flow = Math.Round(kvp.Value.maxFlow, 2),
                unit
            }).OrderByDescending(x => x.avg_flow).ToList();

            return JsonSerializer.Serialize(new { system_name = systemName, branch_count = result.Count, branches = result }, JsonOpts);
        }

        private string GetMepElevationTable(Document doc, Dictionary<string, object> args)
        {
            var categories = GetArgStringArray(args, "categories");
            var groupBy = GetArg(args, "group_by", "both");

            var bicList = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit
            };

            if (categories != null && categories.Count > 0)
            {
                bicList.Clear();
                foreach (var cat in categories)
                {
                    var bic = ResolveCategoryFilter(doc, cat);
                    if (bic.HasValue) bicList.Add(bic.Value);
                }
            }

            var entries = new List<(string system, string level, double elevation, int count)>();

            foreach (var bic in bicList)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(bic).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    var sysParam = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                    string system = sysParam?.AsString() ?? "-";
                    string level = GetElementLevel(doc, elem);

                    var bb = elem.get_BoundingBox(null);
                    double elev = bb != null ? (bb.Min.Z + bb.Max.Z) / 2 : 0;
                    entries.Add((system, level, elev, 1));
                }
            }

            object result;
            if (groupBy == "system")
            {
                result = entries.GroupBy(e => e.system).Select(g => new
                {
                    system = g.Key, count = g.Count(),
                    min_elev_ft = Math.Round(g.Min(e => e.elevation), 2),
                    max_elev_ft = Math.Round(g.Max(e => e.elevation), 2),
                    avg_elev_ft = Math.Round(g.Average(e => e.elevation), 2)
                }).OrderBy(x => x.system).ToList();
            }
            else if (groupBy == "level")
            {
                result = entries.GroupBy(e => e.level).Select(g => new
                {
                    level = g.Key, count = g.Count(),
                    min_elev_ft = Math.Round(g.Min(e => e.elevation), 2),
                    max_elev_ft = Math.Round(g.Max(e => e.elevation), 2)
                }).OrderBy(x => x.level).ToList();
            }
            else
            {
                result = entries.GroupBy(e => (e.system, e.level)).Select(g => new
                {
                    system = g.Key.system, level = g.Key.level, count = g.Count(),
                    min_elev_ft = Math.Round(g.Min(e => e.elevation), 2),
                    max_elev_ft = Math.Round(g.Max(e => e.elevation), 2),
                    avg_elev_ft = Math.Round(g.Average(e => e.elevation), 2)
                }).OrderBy(x => x.system).ThenBy(x => x.level).ToList();
            }

            return JsonSerializer.Serialize(new { total_elements = entries.Count, group_by = groupBy, table = result }, JsonOpts);
        }

        private class SystemTotals
        {
            public string Name;
            public int TotalElements;
            public double TotalLengthFt;
            public int Curves, Fittings, Accessories, Equipment, Fixtures, Terminals, Sprinklers;
        }

        private class SystemInfo
        {
            public string Name;
            public string Classification;
            public string Category;
            public int ElementCount;
            public double TotalLengthFt;
        }

        private class SizeGroup
        {
            public string Size;
            public int Count;
            public double TotalLengthFt;
            public bool HasInsulation;
        }
    }
}
