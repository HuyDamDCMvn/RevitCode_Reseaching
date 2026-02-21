using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class ProjectInfoSkill : IRevitSkill
    {
        public string Name => "ProjectInfo";
        public string Description => "Project info, levels, categories, views, rooms, schedules";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_project_info", "get_levels", "get_categories",
            "get_current_view", "get_rooms", "get_schedule_data"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_project_info",
                "Get project-level information including name, number, address, client, and status.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("get_levels",
                "Get all levels in the project with names and elevations.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("get_categories",
                "Get all categories that have elements in the model, with element counts.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("get_current_view",
                "Get information about the currently active view.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("get_rooms",
                "Get all rooms with name, number, level, area, perimeter. Optionally filter by level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level name filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_schedule_data",
                "Get data from an existing schedule/quantity takeoff by name.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "schedule_name": { "type": "string", "description": "Name of the schedule view" }
                    },
                    "required": ["schedule_name"]
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc.Document;
            return functionName switch
            {
                "get_project_info" => GetProjectInfo(doc),
                "get_levels" => GetLevels(doc),
                "get_categories" => GetCategories(doc),
                "get_current_view" => GetCurrentView(uidoc),
                "get_rooms" => GetRooms(doc, args),
                "get_schedule_data" => GetScheduleData(doc, args),
                _ => JsonError($"ProjectInfoSkill: unknown tool '{functionName}'")
            };
        }

        private string GetProjectInfo(Document doc)
        {
            var pi = doc.ProjectInformation;
            return JsonSerializer.Serialize(new
            {
                name = pi?.Name ?? "-",
                number = pi?.Number ?? "-",
                address = pi?.Address ?? "-",
                client_name = pi?.ClientName ?? "-",
                status = pi?.Status ?? "-",
                file_path = doc.PathName ?? "-",
                is_workshared = doc.IsWorkshared
            }, JsonOpts);
        }

        private string GetLevels(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new
                {
                    id = l.Id.Value,
                    name = l.Name,
                    elevation_ft = Math.Round(l.Elevation, 4),
                    elevation_m = Math.Round(l.Elevation * 0.3048, 4)
                })
                .ToList();

            return JsonSerializer.Serialize(new { count = levels.Count, levels }, JsonOpts);
        }

        private string GetCategories(Document doc)
        {
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            var catCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                catCounts.TryGetValue(elem.Category.Name, out int c);
                catCounts[elem.Category.Name] = c + 1;
            }

            var sorted = catCounts.OrderByDescending(kv => kv.Value)
                .Select(kv => new { category = kv.Key, count = kv.Value }).ToList();

            return JsonSerializer.Serialize(new { total_categories = sorted.Count, categories = sorted }, JsonOpts);
        }

        private string GetCurrentView(UIDocument uidoc)
        {
            var view = uidoc.ActiveView;
            if (view == null) return JsonError("No active view.");

            return JsonSerializer.Serialize(new
            {
                id = view.Id.Value,
                name = view.Name,
                view_type = view.ViewType.ToString(),
                level = view.GenLevel?.Name ?? "-",
                scale = view.Scale
            }, JsonOpts);
        }

        private string GetRooms(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");

            var rooms = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            var results = rooms
                .Where(r =>
                {
                    if (string.IsNullOrEmpty(levelFilter)) return true;
                    return r.Level?.Name?.Equals(levelFilter, StringComparison.OrdinalIgnoreCase) == true;
                })
                .Select(r => new
                {
                    id = r.Id.Value,
                    name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                    number = r.Number ?? "-",
                    level = r.Level?.Name ?? "-",
                    area_sqft = Math.Round(r.Area, 2),
                    area_sqm = Math.Round(r.Area * 0.092903, 2),
                    perimeter_ft = Math.Round(r.Perimeter, 2)
                })
                .OrderBy(r => r.level).ThenBy(r => r.number).ToList();

            return JsonSerializer.Serialize(new { count = results.Count, rooms = results }, JsonOpts);
        }

        private string GetScheduleData(Document doc, Dictionary<string, object> args)
        {
            var scheduleName = GetArg<string>(args, "schedule_name");
            if (string.IsNullOrEmpty(scheduleName))
                return JsonError("schedule_name is required.");

            var schedule = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(s => s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase));

            if (schedule == null)
            {
                var available = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                    .Select(s => s.Name).OrderBy(n => n).Take(20).ToList();

                return JsonSerializer.Serialize(new
                {
                    error = $"Schedule '{scheduleName}' not found.",
                    available_schedules = available
                }, JsonOpts);
            }

            var tableData = schedule.GetTableData();
            var body = tableData.GetSectionData(SectionType.Body);
            int rows = body.NumberOfRows;
            int cols = body.NumberOfColumns;

            var headers = new List<string>();
            for (int c = 0; c < cols; c++)
                headers.Add(schedule.GetCellText(SectionType.Body, 0, c));

            var data = new List<Dictionary<string, string>>();
            for (int r = 1; r < rows; r++)
            {
                var row = new Dictionary<string, string>();
                for (int c = 0; c < cols; c++)
                    row[c < headers.Count ? headers[c] : $"Col{c}"] = schedule.GetCellText(SectionType.Body, r, c);
                data.Add(row);
            }

            return JsonSerializer.Serialize(new
            {
                schedule_name = schedule.Name,
                row_count = data.Count,
                columns = headers,
                data
            }, JsonOpts);
        }
    }
}
