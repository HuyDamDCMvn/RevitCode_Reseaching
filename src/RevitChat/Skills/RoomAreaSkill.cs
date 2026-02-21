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
    public class RoomAreaSkill : IRevitSkill
    {
        public string Name => "RoomArea";
        public string Description => "Query rooms, room boundaries, finishes, area schemes, and find unplaced/redundant rooms";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_rooms_detailed", "get_room_boundaries", "get_room_finishes",
            "get_area_schemes", "get_unplaced_rooms", "get_redundant_rooms"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_rooms_detailed",
                "List rooms with detailed info: area, volume, level, department, number, name, phase.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_name": { "type": "string", "description": "Optional: filter by level name (partial match)" },
                        "department": { "type": "string", "description": "Optional: filter by department (partial match)" },
                        "min_area_sqft": { "type": "number", "description": "Optional: minimum area in sq ft" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_room_boundaries",
                "Get the boundary segments of a specific room.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "room_id": { "type": "integer", "description": "Room ElementId" }
                    },
                    "required": ["room_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_room_finishes",
                "Get the finish parameters (wall, floor, ceiling) of rooms.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "room_ids": { "type": "array", "items": { "type": "integer" }, "description": "Room IDs (optional, defaults to all)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_area_schemes",
                "List all area schemes and their areas in the document.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_unplaced_rooms",
                "Find rooms that exist in the project but are not placed in any view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_redundant_rooms",
                "Find rooms that are redundant (not enclosed or with 0 area).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "get_rooms_detailed" => GetRoomsDetailed(doc, args),
                "get_room_boundaries" => GetRoomBoundaries(doc, args),
                "get_room_finishes" => GetRoomFinishes(doc, args),
                "get_area_schemes" => GetAreaSchemes(doc),
                "get_unplaced_rooms" => GetUnplacedRooms(doc),
                "get_redundant_rooms" => GetRedundantRooms(doc),
                _ => JsonError($"RoomAreaSkill: unknown tool '{functionName}'")
            };
        }

        private string GetRoomsDetailed(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level_name");
            var deptFilter = GetArg<string>(args, "department");
            double minArea = GetArg(args, "min_area_sqft", 0.0);
            int limit = GetArg(args, "limit", 50);

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Location != null && r.Area > 0)
                .ToList();

            if (!string.IsNullOrEmpty(levelFilter))
            {
                rooms = rooms.Where(r =>
                {
                    var lvl = r.Level;
                    return lvl != null && lvl.Name.IndexOf(levelFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
            }

            if (!string.IsNullOrEmpty(deptFilter))
            {
                rooms = rooms.Where(r =>
                {
                    var dept = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString();
                    return dept != null && dept.IndexOf(deptFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
            }

            if (minArea > 0)
                rooms = rooms.Where(r => r.Area >= minArea).ToList();

            var items = rooms.OrderBy(r => r.Level?.Elevation ?? 0).ThenBy(r => r.Number).Take(limit).Select(r => new
            {
                id = r.Id.Value,
                number = r.Number,
                name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                level = r.Level?.Name ?? "-",
                area_sqft = Math.Round(r.Area, 2),
                volume_cuft = Math.Round(r.Volume, 2),
                perimeter_ft = Math.Round(r.Perimeter, 2),
                department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "-",
                phase = doc.GetElement(r.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() ?? ElementId.InvalidElementId)?.Name ?? "-",
                upper_limit = r.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL)?.AsValueString() ?? "-",
                limit_offset = r.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET)?.AsValueString() ?? "-"
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                total = rooms.Count,
                returned = items.Count,
                total_area_sqft = Math.Round(rooms.Sum(r => r.Area), 2),
                rooms = items
            }, JsonOpts);
        }

        private string GetRoomBoundaries(Document doc, Dictionary<string, object> args)
        {
            long roomId = GetArg<long>(args, "room_id");
            var room = doc.GetElement(new ElementId(roomId)) as Room;
            if (room == null) return JsonError($"Room {roomId} not found.");

            var options = new SpatialElementBoundaryOptions();
            var segments = room.GetBoundarySegments(options);

            if (segments == null || segments.Count == 0)
                return JsonSerializer.Serialize(new { room_id = roomId, boundaries = Array.Empty<object>() }, JsonOpts);

            var loops = new List<object>();
            int loopIdx = 0;
            foreach (var loop in segments)
            {
                var segs = new List<object>();
                foreach (var seg in loop)
                {
                    try
                    {
                        var curve = seg.GetCurve();
                        var boundaryElem = doc.GetElement(seg.ElementId);
                        segs.Add(new
                        {
                            start_x = Math.Round(curve.GetEndPoint(0).X, 4),
                            start_y = Math.Round(curve.GetEndPoint(0).Y, 4),
                            end_x = Math.Round(curve.GetEndPoint(1).X, 4),
                            end_y = Math.Round(curve.GetEndPoint(1).Y, 4),
                            length_ft = Math.Round(curve.Length, 4),
                            boundary_element = boundaryElem?.Name ?? "-",
                            boundary_category = boundaryElem?.Category?.Name ?? "-"
                        });
                    }
                    catch { }
                }

                loops.Add(new { loop_index = loopIdx++, segment_count = segs.Count, segments = segs });
            }

            return JsonSerializer.Serialize(new
            {
                room_id = roomId,
                room_name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                room_number = room.Number,
                loop_count = loops.Count,
                boundaries = loops
            }, JsonOpts);
        }

        private string GetRoomFinishes(Document doc, Dictionary<string, object> args)
        {
            var roomIds = GetArgLongArray(args, "room_ids");
            int limit = GetArg(args, "limit", 50);

            IEnumerable<Room> rooms;
            if (roomIds != null && roomIds.Count > 0)
            {
                rooms = roomIds.Select(id => doc.GetElement(new ElementId(id)) as Room).Where(r => r != null);
            }
            else
            {
                rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Location != null && r.Area > 0);
            }

            var items = rooms.Take(limit).Select(r => new
            {
                id = r.Id.Value,
                number = r.Number,
                name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                wall_finish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString() ?? "-",
                floor_finish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString() ?? "-",
                ceiling_finish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString() ?? "-",
                base_finish = r.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.AsString() ?? "-"
            }).ToList();

            return JsonSerializer.Serialize(new { room_count = items.Count, finishes = items }, JsonOpts);
        }

        private string GetAreaSchemes(Document doc)
        {
            var schemes = new FilteredElementCollector(doc)
                .OfClass(typeof(AreaScheme))
                .Cast<AreaScheme>()
                .ToList();

            var results = new List<object>();
            foreach (var scheme in schemes)
            {
                var areas = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Areas)
                    .WhereElementIsNotElementType()
                    .Cast<Area>()
                    .Where(a => a.AreaScheme?.Id == scheme.Id && a.Area > 0)
                    .ToList();

                results.Add(new
                {
                    id = scheme.Id.Value,
                    name = scheme.Name,
                    area_count = areas.Count,
                    total_area_sqft = Math.Round(areas.Sum(a => a.Area), 2)
                });
            }

            return JsonSerializer.Serialize(new { scheme_count = results.Count, area_schemes = results }, JsonOpts);
        }

        private string GetUnplacedRooms(Document doc)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Location == null)
                .ToList();

            var items = rooms.Select(r => new
            {
                id = r.Id.Value,
                number = r.Number,
                name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                level = r.Level?.Name ?? "-"
            }).ToList();

            return JsonSerializer.Serialize(new { unplaced_count = items.Count, rooms = items }, JsonOpts);
        }

        private string GetRedundantRooms(Document doc)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Location != null && r.Area <= 0)
                .ToList();

            var items = rooms.Select(r => new
            {
                id = r.Id.Value,
                number = r.Number,
                name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "-",
                level = r.Level?.Name ?? "-",
                area = r.Area
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                redundant_count = items.Count,
                message = items.Count > 0 ? "These rooms are placed but not enclosed or have 0 area." : "No redundant rooms found.",
                rooms = items
            }, JsonOpts);
        }
    }
}
