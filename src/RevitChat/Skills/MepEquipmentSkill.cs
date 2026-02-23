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
    public class MepEquipmentSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepEquipment";
        protected override string SkillDescription => "Query MEP equipment, fixtures, and fittings";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_mechanical_equipment", "get_plumbing_fixtures",
            "get_electrical_equipment", "get_fire_protection_equipment", "get_fittings"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_mechanical_equipment",
                "List mechanical equipment (FCU, AHU, chiller, pump, fan...) with family, type, system, level. Includes key parameters like airflow, capacity.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "limit": { "type": "integer", "description": "Max results. Default 100.", "default": 100 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_plumbing_fixtures",
                "List plumbing fixtures (lavabo, toilet, shower, sink...) with family, type, level, room.",
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

            ChatTool.CreateFunctionTool("get_electrical_equipment",
                "List electrical equipment (panels, transformers, switchboards) with capacity, circuit count, level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "limit": { "type": "integer", "description": "Max results. Default 100.", "default": 100 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_fire_protection_equipment",
                "List fire protection devices (sprinklers, fire alarm devices) with type, coverage, level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "device_type": { "type": "string", "description": "Filter: 'sprinkler' or 'alarm'. Default 'all'." }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_fittings",
                "Count MEP fittings grouped by category (duct/pipe/conduit/cable tray) and type.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "fitting_category": { "type": "string", "description": "Filter: 'duct', 'pipe', 'conduit', 'cable_tray', or 'all'. Default 'all'." },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_mechanical_equipment" => GetEquipmentByCategory(doc, args, BuiltInCategory.OST_MechanicalEquipment, "Mechanical Equipment"),
                "get_plumbing_fixtures" => GetEquipmentByCategory(doc, args, BuiltInCategory.OST_PlumbingFixtures, "Plumbing Fixtures"),
                "get_electrical_equipment" => GetEquipmentByCategory(doc, args, BuiltInCategory.OST_ElectricalEquipment, "Electrical Equipment"),
                "get_fire_protection_equipment" => GetFireProtection(doc, args),
                "get_fittings" => GetFittings(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetEquipmentByCategory(Document doc, Dictionary<string, object> args,
            BuiltInCategory category, string label)
        {
            var levelFilter = GetArg<string>(args, "level");
            var resolvedLevel = ResolveLevelName(doc, levelFilter);
            var limit = GetArg<int>(args, "limit", 100);

            var elems = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();

            var results = new List<object>();
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elems)
            {
                var elemLevel = GetElementLevel(doc, elem);
                if (resolvedLevel != null && !elemLevel.Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var typeName = GetElementTypeName(doc, elem);
                typeCounts.TryGetValue(typeName, out int tc);
                typeCounts[typeName] = tc + 1;

                if (results.Count < limit)
                {
                    var sysName = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-";

                    var keyParams = new Dictionary<string, string>();
                    foreach (var pName in MepKeyParams)
                    {
                        var p = elem.LookupParameter(pName);
                        if (p != null && p.HasValue)
                            keyParams[pName] = GetParameterValueAsString(doc, p);
                    }

                    results.Add(new
                    {
                        id = elem.Id.Value,
                        family = GetFamilyName(doc, elem),
                        type = typeName,
                        system = sysName,
                        level = elemLevel,
                        key_parameters = keyParams.Count > 0 ? keyParams : null
                    });
                }
            }

            var summary = typeCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { type = kv.Key, count = kv.Value })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                category = label,
                total_count = typeCounts.Values.Sum(),
                by_type = summary,
                elements = results
            }, JsonOpts);
        }

        private string GetFireProtection(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            var resolvedLevel = ResolveLevelName(doc, levelFilter);
            var deviceType = GetArg<string>(args, "device_type")?.ToLower() ?? "all";

            var categories = new List<BuiltInCategory>();
            if (deviceType is "all" or "sprinkler")
                categories.Add(BuiltInCategory.OST_Sprinklers);
            if (deviceType is "all" or "alarm")
                categories.Add(BuiltInCategory.OST_FireAlarmDevices);

            var results = new List<object>();
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var cat in categories)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (var elem in elems)
                {
                    var elemLevel = GetElementLevel(doc, elem);
                    if (resolvedLevel != null && !elemLevel.Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var typeName = $"{elem.Category?.Name}: {GetElementTypeName(doc, elem)}";
                    typeCounts.TryGetValue(typeName, out int tc);
                    typeCounts[typeName] = tc + 1;

                    if (results.Count < 200)
                    {
                        results.Add(new
                        {
                            id = elem.Id.Value,
                            category = elem.Category?.Name ?? "-",
                            family = GetFamilyName(doc, elem),
                            type = GetElementTypeName(doc, elem),
                            level = elemLevel
                        });
                    }
                }
            }

            var summary = typeCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { type = kv.Key, count = kv.Value })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                total_count = typeCounts.Values.Sum(),
                by_type = summary,
                elements = results
            }, JsonOpts);
        }

        private string GetFittings(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "fitting_category")?.ToLower() ?? "all";
            var levelFilter = GetArg<string>(args, "level");
            var resolvedLevel = ResolveLevelName(doc, levelFilter);

            var catMap = new Dictionary<string, BuiltInCategory>
            {
                ["duct"] = BuiltInCategory.OST_DuctFitting,
                ["pipe"] = BuiltInCategory.OST_PipeFitting,
                ["conduit"] = BuiltInCategory.OST_ConduitFitting,
                ["cable_tray"] = BuiltInCategory.OST_CableTrayFitting
            };

            var selectedCats = catFilter == "all"
                ? catMap.Values.ToList()
                : catMap.TryGetValue(catFilter, out var c) ? new List<BuiltInCategory> { c } : catMap.Values.ToList();

            var groupResults = new List<object>();

            foreach (var cat in selectedCats)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToList();

                var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var elem in elems)
                {
                    if (resolvedLevel != null && !GetElementLevel(doc, elem).Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tn = GetElementTypeName(doc, elem);
                    typeCounts.TryGetValue(tn, out int tc);
                    typeCounts[tn] = tc + 1;
                }

                var catName = cat switch
                {
                    BuiltInCategory.OST_DuctFitting => "Duct Fittings",
                    BuiltInCategory.OST_PipeFitting => "Pipe Fittings",
                    BuiltInCategory.OST_ConduitFitting => "Conduit Fittings",
                    BuiltInCategory.OST_CableTrayFitting => "Cable Tray Fittings",
                    _ => cat.ToString()
                };

                groupResults.Add(new
                {
                    category = catName,
                    total = typeCounts.Values.Sum(),
                    by_type = typeCounts.OrderByDescending(kv => kv.Value)
                        .Select(kv => new { type = kv.Key, count = kv.Value })
                });
            }

            return JsonSerializer.Serialize(new { fittings = groupResults }, JsonOpts);
        }

        private static readonly string[] MepKeyParams = new[]
        {
            "Air Flow", "Airflow", "Flow", "Cooling Capacity", "Heating Capacity",
            "Power", "Apparent Power", "Voltage", "Current",
            "Total Connected Load", "Number of Poles",
            "Motor Power", "Pressure Drop"
        };
    }
}
