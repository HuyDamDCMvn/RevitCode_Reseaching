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
            "calculate_system_totals"
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
                "Calculate per-system totals: total length, element count broken down by type (curves, fittings, accessories, equipment, terminals).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "system_name": { "type": "string", "description": "System name (exact or partial match). If not provided, shows all systems." },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
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
                "get_mep_systems" => GetMepSystems(doc, args),
                "get_system_elements" => GetSystemElements(doc, args),
                "get_duct_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_DuctCurves, "Ducts"),
                "get_pipe_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_PipeCurves, "Pipes"),
                "get_conduit_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_Conduit, "Conduits"),
                "get_cable_tray_summary" => GetMepCurveSummary(doc, args, BuiltInCategory.OST_CableTray, "Cable Trays"),
                "calculate_system_totals" => CalculateSystemTotals(doc, args),
                _ => JsonError($"MepSystemAnalysisSkill: unknown tool '{functionName}'")
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
                    if (!sn.Equals(systemName, StringComparison.OrdinalIgnoreCase)) continue;

                    results.Add(new
                    {
                        id = elem.Id.Value,
                        category = elem.Category?.Name ?? "-",
                        family = GetFamilyName(doc, elem),
                        type = GetElementTypeName(doc, elem),
                        size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                        level = GetElementLevel(doc, elem)
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
                if (!string.IsNullOrEmpty(systemFilter) &&
                    !sysName.Equals(systemFilter, StringComparison.OrdinalIgnoreCase))
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

            var mepCategories = new Dictionary<BuiltInCategory, string>
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

                    if (!string.IsNullOrEmpty(systemFilter) &&
                        sysName.IndexOf(systemFilter, StringComparison.OrdinalIgnoreCase) < 0)
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
