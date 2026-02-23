using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class MepSpaceSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepSpace";
        protected override string SkillDescription => "MEP spaces, HVAC zones, airflow analysis";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_mep_spaces", "get_hvac_zones", "check_space_airflow",
            "get_unoccupied_spaces", "get_elements_in_space"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_mep_spaces",
                "List MEP Spaces with area, volume, loads, airflow. Filter by level, area range (sqm), volume range (m³/CBM).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "min_area_sqm": { "type": "number", "description": "Min area in m² to include" },
                        "max_area_sqm": { "type": "number", "description": "Max area in m² to include" },
                        "min_volume_m3": { "type": "number", "description": "Min volume in m³ (CBM) to include" },
                        "max_volume_m3": { "type": "number", "description": "Max volume in m³ (CBM) to include" },
                        "limit": { "type": "integer", "description": "Max results. Default 200.", "default": 200 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_hvac_zones",
                "List all HVAC zones with their assigned spaces and airflow data.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_space_airflow",
                "Compare design vs actual supply/return airflow for each MEP space. Flags imbalances.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" },
                        "tolerance_pct": { "type": "number", "description": "Tolerance % for flagging imbalance. Default 10.", "default": 10 }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_unoccupied_spaces",
                "Find MEP spaces that have no mechanical equipment or no duct/pipe serving them.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_elements_in_space",
                "List and count all elements (equipment, sensors, devices, fixtures) physically inside a specific MEP Space. Filter by space number/name and optionally by category.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "space_number": { "type": "string", "description": "Space number to search in" },
                        "space_id": { "type": "integer", "description": "Space ElementId (alternative to space_number)" },
                        "category": { "type": "string", "description": "Optional category filter (e.g. 'Mechanical Equipment', 'Fire Alarm Devices', 'Sprinklers', 'Lighting Fixtures')" },
                        "limit": { "type": "integer", "description": "Max elements to return. Default 200.", "default": 200 }
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
                "get_mep_spaces" => GetMepSpaces(doc, args),
                "get_hvac_zones" => GetHvacZones(doc, args),
                "check_space_airflow" => CheckSpaceAirflow(doc, args),
                "get_unoccupied_spaces" => GetUnoccupiedSpaces(doc, args),
                "get_elements_in_space" => GetElementsInSpace(doc, args),
                _ => JsonError($"MepSpaceSkill: unknown tool '{functionName}'")
            };
        }

        private List<Space> CollectSpaces(Document doc, string levelFilter)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Space>()
                .Where(s => s.Area > 0)
                .Where(s =>
                {
                    if (string.IsNullOrEmpty(levelFilter)) return true;
                    return s.Level?.Name?.Equals(levelFilter, StringComparison.OrdinalIgnoreCase) == true;
                })
                .ToList();
        }

        private string GetMepSpaces(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            double minAreaSqm = GetArg<double>(args, "min_area_sqm", double.NaN);
            double maxAreaSqm = GetArg<double>(args, "max_area_sqm", double.NaN);
            double minVolM3 = GetArg<double>(args, "min_volume_m3", double.NaN);
            double maxVolM3 = GetArg<double>(args, "max_volume_m3", double.NaN);
            int limit = GetArg(args, "limit", 200);

            var spaces = CollectSpaces(doc, levelFilter);

            var projected = spaces.Select(s => new
            {
                space = s,
                area_sqm = Math.Round(s.Area * 0.092903, 2),
                volume_m3 = Math.Round(s.Volume * 0.0283168, 2)
            });

            if (!double.IsNaN(minAreaSqm))
                projected = projected.Where(x => x.area_sqm >= minAreaSqm);
            if (!double.IsNaN(maxAreaSqm))
                projected = projected.Where(x => x.area_sqm <= maxAreaSqm);
            if (!double.IsNaN(minVolM3))
                projected = projected.Where(x => x.volume_m3 >= minVolM3);
            if (!double.IsNaN(maxVolM3))
                projected = projected.Where(x => x.volume_m3 <= maxVolM3);

            var filtered = projected.ToList();

            var results = filtered.Take(limit).Select(x =>
            {
                var s = x.space;
                return new
                {
                    id = s.Id.Value,
                    name = s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                    number = s.Number ?? "-",
                    level = s.Level?.Name ?? "-",
                    area_sqm = x.area_sqm,
                    volume_m3 = x.volume_m3,
                    condition_type = s.LookupParameter("Condition Type")?.AsValueString() ?? "-",
                    design_heating_load = s.LookupParameter("Design Heating Load")?.AsValueString() ?? "-",
                    design_cooling_load = s.LookupParameter("Design Cooling Load")?.AsValueString() ?? "-",
                    design_supply_airflow = s.LookupParameter("Specified Supply Airflow")?.AsValueString()
                        ?? s.LookupParameter("Design Supply Airflow")?.AsValueString() ?? "-",
                    actual_supply_airflow = s.LookupParameter("Actual Supply Airflow")?.AsValueString() ?? "-",
                    design_return_airflow = s.LookupParameter("Specified Return Airflow")?.AsValueString()
                        ?? s.LookupParameter("Design Return Airflow")?.AsValueString() ?? "-",
                    actual_return_airflow = s.LookupParameter("Actual Return Airflow")?.AsValueString() ?? "-"
                };
            })
            .OrderBy(s => s.level).ThenBy(s => s.number).ToList();

            bool hasFilter = !double.IsNaN(minAreaSqm) || !double.IsNaN(maxAreaSqm)
                          || !double.IsNaN(minVolM3) || !double.IsNaN(maxVolM3);

            return JsonSerializer.Serialize(new
            {
                total_matching = filtered.Count,
                returned = results.Count,
                filter_applied = hasFilter,
                spaces = results
            }, JsonOpts);
        }

        private string GetHvacZones(Document doc, Dictionary<string, object> args)
        {
            var zones = new FilteredElementCollector(doc)
                .OfClass(typeof(Zone))
                .Cast<Zone>()
                .ToList();

            var results = zones.Select(z =>
            {
                var spaceList = new List<string>();
                try
                {
                    foreach (Space s in z.Spaces)
                        spaceList.Add($"{s.Number}: {s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-"}");
                }
                catch { }

                return new
                {
                    id = z.Id.Value,
                    name = z.Name ?? "-",
                    space_count = spaceList.Count,
                    spaces = spaceList,
                    area_sqm = Math.Round(z.Area * 0.092903, 2),
                    volume_m3 = Math.Round(z.Volume * 0.0283168, 2),
                    design_supply_airflow = z.LookupParameter("Specified Supply Airflow")?.AsValueString()
                        ?? z.LookupParameter("Design Supply Airflow")?.AsValueString() ?? "-",
                    actual_supply_airflow = z.LookupParameter("Actual Supply Airflow")?.AsValueString() ?? "-"
                };
            }).ToList();

            return JsonSerializer.Serialize(new { count = results.Count, zones = results }, JsonOpts);
        }

        private string CheckSpaceAirflow(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            var tolerancePct = GetArg<double>(args, "tolerance_pct", 10);
            var spaces = CollectSpaces(doc, levelFilter);

            var issues = new List<object>();
            int checkedCount = 0;

            foreach (var s in spaces)
            {
                var designSupply = GetParamDouble(s, "Specified Supply Airflow")
                    ?? GetParamDouble(s, "Design Supply Airflow") ?? 0;
                var actualSupply = GetParamDouble(s, "Actual Supply Airflow") ?? 0;
                var designReturn = GetParamDouble(s, "Specified Return Airflow")
                    ?? GetParamDouble(s, "Design Return Airflow") ?? 0;
                var actualReturn = GetParamDouble(s, "Actual Return Airflow") ?? 0;

                if (designSupply <= 0 && designReturn <= 0) continue;
                checkedCount++;

                var supplyDiff = designSupply > 0 ? Math.Abs(actualSupply - designSupply) / designSupply * 100 : 0;
                var returnDiff = designReturn > 0 ? Math.Abs(actualReturn - designReturn) / designReturn * 100 : 0;

                if (supplyDiff > tolerancePct || returnDiff > tolerancePct)
                {
                    issues.Add(new
                    {
                        id = s.Id.Value,
                        name = s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                        number = s.Number ?? "-",
                        level = s.Level?.Name ?? "-",
                        supply_design = Math.Round(designSupply, 2),
                        supply_actual = Math.Round(actualSupply, 2),
                        supply_diff_pct = Math.Round(supplyDiff, 1),
                        return_design = Math.Round(designReturn, 2),
                        return_actual = Math.Round(actualReturn, 2),
                        return_diff_pct = Math.Round(returnDiff, 1)
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                spaces_checked = checkedCount,
                issues_found = issues.Count,
                tolerance_pct = tolerancePct,
                issues
            }, JsonOpts);
        }

        private string GetUnoccupiedSpaces(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            var spaces = CollectSpaces(doc, levelFilter);

            var equipmentInSpaces = new HashSet<long>();
            var mechEquipment = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var eq in mechEquipment)
            {
                var spaceParam = eq.LookupParameter("Space")
                    ?? eq.get_Parameter(BuiltInParameter.ROOM_NAME);
                if (spaceParam != null && spaceParam.StorageType == StorageType.ElementId
                    && spaceParam.AsElementId() != ElementId.InvalidElementId)
                    equipmentInSpaces.Add(spaceParam.AsElementId().Value);

                if (eq is FamilyInstance fi && fi.Space != null)
                    equipmentInSpaces.Add(fi.Space.Id.Value);
            }

            var results = new List<object>();
            foreach (var s in spaces)
            {
                var hasEquipment = equipmentInSpaces.Contains(s.Id.Value);
                var actualSupply = GetParamDouble(s, "Actual Supply Airflow") ?? 0;
                var actualReturn = GetParamDouble(s, "Actual Return Airflow") ?? 0;
                var hasAirflow = actualSupply > 0 || actualReturn > 0;

                if (!hasEquipment && !hasAirflow)
                {
                    results.Add(new
                    {
                        id = s.Id.Value,
                        name = s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                        number = s.Number ?? "-",
                        level = s.Level?.Name ?? "-",
                        area_sqm = Math.Round(s.Area * 0.092903, 2),
                        has_equipment = false,
                        has_airflow = false
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                total_spaces = spaces.Count,
                unoccupied_count = results.Count,
                unoccupied_spaces = results
            }, JsonOpts);
        }

        private string GetElementsInSpace(Document doc, Dictionary<string, object> args)
        {
            var spaceNumber = GetArg<string>(args, "space_number");
            var spaceId = GetArg<long>(args, "space_id");
            var categoryFilter = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 200);

            Space targetSpace = null;

            if (spaceId > 0)
            {
                targetSpace = doc.GetElement(new ElementId(spaceId)) as Space;
            }

            if (targetSpace == null && !string.IsNullOrEmpty(spaceNumber))
            {
                targetSpace = new FilteredElementCollector(doc)
                    .OfClass(typeof(SpatialElement))
                    .OfType<Space>()
                    .FirstOrDefault(s => string.Equals(s.Number, spaceNumber, StringComparison.OrdinalIgnoreCase));
            }

            if (targetSpace == null)
                return JsonError($"Space not found. number='{spaceNumber}', id={spaceId}");

            var spaceIdVal = targetSpace.Id.Value;
            var spaceName = targetSpace.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-";

            BuiltInCategory? bicFilter = null;
            if (!string.IsNullOrEmpty(categoryFilter))
                bicFilter = ResolveCategoryFilter(doc, categoryFilter);

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            if (bicFilter.HasValue)
                collector = collector.OfCategory(bicFilter.Value);

            var results = new List<object>();
            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in collector)
            {
                if (!(elem is FamilyInstance fi)) continue;

                Space elemSpace = null;
                try { elemSpace = fi.Space; } catch { }

                if (elemSpace == null || elemSpace.Id.Value != spaceIdVal) continue;

                if (!string.IsNullOrEmpty(categoryFilter) && !bicFilter.HasValue)
                {
                    var catName = elem.Category?.Name ?? "";
                    if (!catName.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var cat = elem.Category?.Name ?? "-";
                categoryCounts.TryGetValue(cat, out int c);
                categoryCounts[cat] = c + 1;

                if (results.Count < limit)
                {
                    results.Add(new
                    {
                        id = elem.Id.Value,
                        category = cat,
                        family = GetFamilyName(doc, elem),
                        type = GetElementTypeName(doc, elem),
                        level = GetElementLevel(doc, elem)
                    });
                }
            }

            var summary = categoryCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { category = kv.Key, count = kv.Value })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                space_id = spaceIdVal,
                space_number = targetSpace.Number ?? "-",
                space_name = spaceName,
                space_level = targetSpace.Level?.Name ?? "-",
                total_elements = categoryCounts.Values.Sum(),
                by_category = summary,
                elements = results
            }, JsonOpts);
        }

        private static double? GetParamDouble(Element elem, string paramName)
        {
            var p = elem.LookupParameter(paramName);
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
            return p.AsDouble();
        }
    }
}
