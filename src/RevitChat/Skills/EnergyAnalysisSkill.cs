using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class EnergyAnalysisSkill : BaseRevitSkill
    {
        protected override string SkillName => "EnergyAnalysis";
        protected override string SkillDescription => "Energy analysis, building schedules, and gbXML export";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_building_schedules", "set_operating_schedule", "export_gbxml", "get_space_energy_data"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_building_schedules",
                "List building operating schedules.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("set_operating_schedule",
                "Create or modify a building operating schedule.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "Schedule name" },
                        "hours_start": { "type": "integer", "description": "Operating start hour (0-23)" },
                        "hours_end": { "type": "integer", "description": "Operating end hour (0-23)" }
                    },
                    "required": ["name"]
                }
                """)),

            ChatTool.CreateFunctionTool("export_gbxml",
                "Export model to gbXML for energy analysis.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "file_path": { "type": "string", "description": "Output path. Default: Desktop." }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_space_energy_data",
                "Get energy-related data for spaces/rooms.",
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

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_building_schedules" => GetBuildingSchedules(doc),
                "set_operating_schedule" => SetOperatingSchedule(doc, args),
                "export_gbxml" => ExportGbxml(doc, args),
                "get_space_energy_data" => GetSpaceEnergyData(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetBuildingSchedules(Document doc)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTitleblockRevisionSchedule)
                .Take(50)
                .Select(s => new { id = s.Id.Value, name = s.Name })
                .ToList();

            return JsonSerializer.Serialize(new { count = schedules.Count, schedules }, JsonOpts);
        }

        private string SetOperatingSchedule(Document doc, Dictionary<string, object> args)
        {
            var name = GetArg<string>(args, "name");
            if (string.IsNullOrEmpty(name)) return JsonError("name required.");
            return JsonSerializer.Serialize(new
            {
                message = $"Operating schedule '{name}' noted. Revit Energy Analysis settings should be configured in the Energy Settings dialog.",
                note = "Programmatic schedule creation requires the Energy Analysis API which depends on the energy model being active."
            }, JsonOpts);
        }

        private string ExportGbxml(Document doc, Dictionary<string, object> args)
        {
            var filePath = GetArg<string>(args, "file_path");
            if (string.IsNullOrEmpty(filePath))
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Revit_Energy_{DateTime.Now:yyyyMMdd_HHmmss}.xml");

            try
            {
                var options = new GBXMLExportOptions();
                doc.Export(Path.GetDirectoryName(filePath), Path.GetFileNameWithoutExtension(filePath), options);
                return JsonSerializer.Serialize(new { file_path = filePath, message = "gbXML export completed." }, JsonOpts);
            }
            catch (Exception ex) { return JsonError($"gbXML export failed: {ex.Message}"); }
        }

        private string GetSpaceEnergyData(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            var spaces = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType().ToList();

            if (!string.IsNullOrEmpty(levelFilter))
                spaces = spaces.Where(s => GetElementLevel(doc, s).Equals(levelFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            var results = spaces.Take(100).Select(s =>
            {
                var area = s.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0;
                var volume = s.get_Parameter(BuiltInParameter.ROOM_VOLUME)?.AsDouble() ?? 0;
                return new
                {
                    id = s.Id.Value, name = s.Name,
                    level = GetElementLevel(doc, s),
                    area_sqft = Math.Round(area, 1),
                    volume_cuft = Math.Round(volume, 1)
                };
            }).ToList();

            return JsonSerializer.Serialize(new { space_count = results.Count, spaces = results }, JsonOpts);
        }
    }
}
