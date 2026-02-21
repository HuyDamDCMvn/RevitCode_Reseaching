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
    public class MepSpaceSkill : IRevitSkill
    {
        public string Name => "MepSpace";
        public string Description => "MEP spaces, HVAC zones, airflow analysis";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_mep_spaces", "get_hvac_zones", "check_space_airflow", "get_unoccupied_spaces"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_mep_spaces",
                "List all MEP Spaces with area, volume, heating/cooling loads, supply/return airflow. Optionally filter by level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" }
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
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var doc = app.ActiveUIDocument.Document;
            return functionName switch
            {
                "get_mep_spaces" => GetMepSpaces(doc, args),
                "get_hvac_zones" => GetHvacZones(doc, args),
                "check_space_airflow" => CheckSpaceAirflow(doc, args),
                "get_unoccupied_spaces" => GetUnoccupiedSpaces(doc, args),
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
            var spaces = CollectSpaces(doc, levelFilter);

            var results = spaces.Select(s => new
            {
                id = s.Id.Value,
                name = s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                number = s.Number ?? "-",
                level = s.Level?.Name ?? "-",
                area_sqm = Math.Round(s.Area * 0.092903, 2),
                volume_m3 = Math.Round(s.Volume * 0.0283168, 2),
                condition_type = s.LookupParameter("Condition Type")?.AsValueString() ?? "-",
                design_heating_load = s.LookupParameter("Design Heating Load")?.AsValueString() ?? "-",
                design_cooling_load = s.LookupParameter("Design Cooling Load")?.AsValueString() ?? "-",
                design_supply_airflow = s.LookupParameter("Specified Supply Airflow")?.AsValueString()
                    ?? s.LookupParameter("Design Supply Airflow")?.AsValueString() ?? "-",
                actual_supply_airflow = s.LookupParameter("Actual Supply Airflow")?.AsValueString() ?? "-",
                design_return_airflow = s.LookupParameter("Specified Return Airflow")?.AsValueString()
                    ?? s.LookupParameter("Design Return Airflow")?.AsValueString() ?? "-",
                actual_return_airflow = s.LookupParameter("Actual Return Airflow")?.AsValueString() ?? "-"
            })
            .OrderBy(s => s.level).ThenBy(s => s.number).ToList();

            return JsonSerializer.Serialize(new { count = results.Count, spaces = results }, JsonOpts);
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

        private static double? GetParamDouble(Element elem, string paramName)
        {
            var p = elem.LookupParameter(paramName);
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
            return p.AsDouble();
        }
    }
}
