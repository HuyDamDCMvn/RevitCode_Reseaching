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
    public class GridLevelSkill : IRevitSkill
    {
        public string Name => "GridLevel";
        public string Description => "Manage grids and levels: list, check alignment, consistency, find off-axis elements";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_grids", "check_grid_alignment", "get_levels_detailed",
            "check_level_consistency", "find_off_axis_elements"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_grids",
                "List all grids with their name, direction, start/end points, and length.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_grid_alignment",
                "Check for grids that are not perfectly orthogonal (slightly off-axis).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "tolerance_degrees": { "type": "number", "description": "Angle tolerance in degrees (default 0.1)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_levels_detailed",
                "Get detailed level info: elevation, story height, associated views count.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_level_consistency",
                "Check if levels in the current model match levels in linked models.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "tolerance_feet": { "type": "number", "description": "Elevation tolerance in feet (default 0.01)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("find_off_axis_elements",
                "Find elements not aligned with the nearest grid (slightly rotated or offset).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category to check (e.g. 'Walls', 'Columns', 'Structural Columns')" },
                        "tolerance_degrees": { "type": "number", "description": "Angle tolerance from grid in degrees (default 1.0)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": ["category"]
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
                "get_grids" => GetGrids(doc),
                "check_grid_alignment" => CheckGridAlignment(doc, args),
                "get_levels_detailed" => GetLevelsDetailed(doc),
                "check_level_consistency" => CheckLevelConsistency(doc, args),
                "find_off_axis_elements" => FindOffAxisElements(doc, args),
                _ => JsonError($"GridLevelSkill: unknown tool '{functionName}'")
            };
        }

        private string GetGrids(Document doc)
        {
            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .ToList();

            var items = grids.Select(g =>
            {
                var curve = g.Curve;
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);
                var dir = (end - start).Normalize();

                string orientation;
                if (Math.Abs(dir.X) > Math.Abs(dir.Y))
                    orientation = "Horizontal (X-axis)";
                else if (Math.Abs(dir.Y) > Math.Abs(dir.X))
                    orientation = "Vertical (Y-axis)";
                else
                    orientation = "Diagonal";

                return new
                {
                    id = g.Id.Value,
                    name = g.Name,
                    orientation,
                    length_ft = Math.Round(curve.Length, 4),
                    start_x = Math.Round(start.X, 4),
                    start_y = Math.Round(start.Y, 4),
                    end_x = Math.Round(end.X, 4),
                    end_y = Math.Round(end.Y, 4),
                    is_curved = !(curve is Line)
                };
            }).OrderBy(g => g.name).ToList();

            return JsonSerializer.Serialize(new { grid_count = items.Count, grids = items }, JsonOpts);
        }

        private string CheckGridAlignment(Document doc, Dictionary<string, object> args)
        {
            double tolDeg = GetArg(args, "tolerance_degrees", 0.1);
            double tolRad = tolDeg * Math.PI / 180.0;

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Where(g => g.Curve is Line)
                .ToList();

            var offAxis = new List<object>();

            foreach (var g in grids)
            {
                var line = g.Curve as Line;
                if (line == null) continue;

                var dir = line.Direction;
                double angleX = Math.Abs(Math.Atan2(dir.Y, dir.X));
                double angleToOrthogonal = Math.Min(
                    Math.Min(angleX, Math.Abs(angleX - Math.PI / 2)),
                    Math.Min(Math.Abs(angleX - Math.PI), Math.Abs(angleX - 3 * Math.PI / 2))
                );

                if (angleToOrthogonal > tolRad && angleToOrthogonal < Math.PI / 4)
                {
                    offAxis.Add(new
                    {
                        id = g.Id.Value,
                        name = g.Name,
                        deviation_degrees = Math.Round(angleToOrthogonal * 180.0 / Math.PI, 4)
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                total_grids = grids.Count,
                off_axis_count = offAxis.Count,
                tolerance_degrees = tolDeg,
                message = offAxis.Count > 0
                    ? "These grids are slightly off orthogonal alignment."
                    : "All grids are properly aligned.",
                off_axis_grids = offAxis
            }, JsonOpts);
        }

        private string GetLevelsDetailed(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.GenLevel != null)
                .ToList();

            var items = new List<object>();
            for (int i = 0; i < levels.Count; i++)
            {
                var lvl = levels[i];
                double storyHeight = i < levels.Count - 1
                    ? Math.Round(levels[i + 1].Elevation - lvl.Elevation, 4)
                    : 0;

                int viewCount = views.Count(v => v.GenLevel?.Id == lvl.Id);

                items.Add(new
                {
                    id = lvl.Id.Value,
                    name = lvl.Name,
                    elevation_ft = Math.Round(lvl.Elevation, 4),
                    story_height_ft = storyHeight,
                    associated_views = viewCount
                });
            }

            return JsonSerializer.Serialize(new { level_count = items.Count, levels = items }, JsonOpts);
        }

        private string CheckLevelConsistency(Document doc, Dictionary<string, object> args)
        {
            double tolerance = GetArg(args, "tolerance_feet", 0.01);

            var hostLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToDictionary(l => l.Name, l => l.Elevation);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            if (links.Count == 0)
                return JsonSerializer.Serialize(new { message = "No linked models found to compare.", host_levels = hostLevels.Count }, JsonOpts);

            var issues = new List<object>();

            foreach (var link in links)
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                var linkLevels = new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToList();

                foreach (var ll in linkLevels)
                {
                    if (hostLevels.TryGetValue(ll.Name, out double hostElev))
                    {
                        double diff = Math.Abs(hostElev - ll.Elevation);
                        if (diff > tolerance)
                        {
                            issues.Add(new
                            {
                                level_name = ll.Name,
                                host_elevation = Math.Round(hostElev, 4),
                                link_elevation = Math.Round(ll.Elevation, 4),
                                difference_ft = Math.Round(diff, 4),
                                link_name = linkDoc.Title
                            });
                        }
                    }
                    else
                    {
                        issues.Add(new
                        {
                            level_name = ll.Name,
                            host_elevation = (double?)null,
                            link_elevation = Math.Round(ll.Elevation, 4),
                            difference_ft = (double?)null,
                            link_name = linkDoc.Title,
                            note = "Level exists in link but not in host"
                        });
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                host_levels = hostLevels.Count,
                links_checked = links.Count,
                issue_count = issues.Count,
                message = issues.Count > 0 ? "Level inconsistencies found between host and linked models." : "All levels are consistent.",
                issues
            }, JsonOpts);
        }

        private string FindOffAxisElements(Document doc, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            double tolDeg = GetArg(args, "tolerance_degrees", 1.0);
            int limit = GetArg(args, "limit", 50);

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Where(g => g.Curve is Line)
                .ToList();

            var gridDirs = grids.Select(g => ((Line)g.Curve).Direction.Normalize()).ToList();

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            double tolRad = tolDeg * Math.PI / 180.0;
            var offAxis = new List<object>();

            foreach (var elem in elements)
            {
                if (offAxis.Count >= limit) break;

                if (elem.Location is not LocationCurve lc) continue;
                if (lc.Curve is not Line elemLine) continue;

                var elemDir = elemLine.Direction.Normalize();
                bool aligned = false;

                foreach (var gDir in gridDirs)
                {
                    double dot = Math.Abs(elemDir.DotProduct(gDir));
                    double angle = Math.Acos(Math.Min(dot, 1.0));
                    double minAngle = Math.Min(angle, Math.PI / 2 - angle);

                    if (minAngle <= tolRad) { aligned = true; break; }
                }

                if (!aligned)
                {
                    offAxis.Add(new
                    {
                        id = elem.Id.Value,
                        name = elem.Name,
                        category = elem.Category?.Name ?? "-",
                        level = GetElementLevel(doc, elem)
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                category = catName,
                total_checked = elements.Count,
                off_axis_count = offAxis.Count,
                tolerance_degrees = tolDeg,
                off_axis_elements = offAxis
            }, JsonOpts);
        }
    }
}
