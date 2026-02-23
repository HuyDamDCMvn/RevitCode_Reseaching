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
    public class GridLevelSkill : BaseRevitSkill
    {
        protected override string SkillName => "GridLevel";
        protected override string SkillDescription => "Manage grids and levels: list, create, rename, delete, duplicate with offset, check alignment/consistency";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_grids", "check_grid_alignment", "get_levels_detailed",
            "check_level_consistency", "find_off_axis_elements",
            "create_level", "duplicate_levels_offset", "rename_level", "delete_levels",
            "create_grid", "toggle_grid_bubbles", "set_grid_mode"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
                "Get detailed level info: elevation (in feet and mm), story height, associated views count.",
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
                """)),

            ChatTool.CreateFunctionTool("create_level",
                "Create a new level at a specific elevation. Elevation can be in feet or millimeters.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "Name for the new level" },
                        "elevation": { "type": "number", "description": "Elevation value" },
                        "unit": { "type": "string", "enum": ["ft", "mm"], "description": "Elevation unit: 'ft' (feet) or 'mm' (millimeters). Default: mm" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["name", "elevation"]
                }
                """)),

            ChatTool.CreateFunctionTool("duplicate_levels_offset",
                "Duplicate ALL existing levels with an elevation offset and a name suffix/prefix. Perfect for creating a parallel set of levels (e.g. structural vs architectural offsets).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "offset_mm": { "type": "number", "description": "Elevation offset in millimeters (positive = up, negative = down). e.g. 500 means +500mm above each existing level." },
                        "suffix": { "type": "string", "description": "Suffix to append to each new level name (e.g. '_add', '_structural')" },
                        "prefix": { "type": "string", "description": "Optional prefix to prepend to each new level name" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["offset_mm"]
                }
                """)),

            ChatTool.CreateFunctionTool("rename_level",
                "Rename an existing level.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_id": { "type": "integer", "description": "Level element ID" },
                        "new_name": { "type": "string", "description": "New name for the level" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["level_id", "new_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("delete_levels",
                "Delete levels from the model. DANGEROUS: elements hosted on these levels may lose their host. Confirm with the user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_ids": { "type": "array", "items": { "type": "integer" }, "description": "Level IDs to delete" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["level_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("create_grid",
                "Create a new linear grid line. Coordinates in feet.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "Grid name (e.g. 'A', '1', 'G1')" },
                        "start_x": { "type": "number", "description": "Start X coordinate in feet" },
                        "start_y": { "type": "number", "description": "Start Y coordinate in feet" },
                        "end_x": { "type": "number", "description": "End X coordinate in feet" },
                        "end_y": { "type": "number", "description": "End Y coordinate in feet" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["name", "start_x", "start_y", "end_x", "end_y"]
                }
                """)),

            ChatTool.CreateFunctionTool("toggle_grid_bubbles",
                "Show or hide grid bubbles (head/tail) in the current view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "grid_ids": { "type": "array", "items": { "type": "integer" }, "description": "Grid IDs. If empty, applies to all grids." },
                        "end": { "type": "string", "enum": ["start", "end", "both"], "description": "Which end. Default: both" },
                        "action": { "type": "string", "enum": ["show", "hide", "toggle"], "description": "Action. Default: toggle" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("set_grid_mode",
                "Switch grids between 2D (view-specific) and 3D (model extent) display mode in the current view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "grid_ids": { "type": "array", "items": { "type": "integer" }, "description": "Grid IDs. If empty, applies to all grids." },
                        "mode": { "type": "string", "enum": ["2d", "3d"], "description": "Display mode. 2d=view-specific, 3d=model extent" }
                    },
                    "required": ["mode"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_grids" => GetGrids(doc),
                "check_grid_alignment" => CheckGridAlignment(doc, args),
                "get_levels_detailed" => GetLevelsDetailed(doc),
                "check_level_consistency" => CheckLevelConsistency(doc, args),
                "find_off_axis_elements" => FindOffAxisElements(doc, args),
                "create_level" => CreateLevel(doc, args),
                "duplicate_levels_offset" => DuplicateLevelsOffset(doc, args),
                "rename_level" => RenameLevel(doc, args),
                "delete_levels" => DeleteLevels(doc, args),
                "create_grid" => CreateGrid(doc, args),
                "toggle_grid_bubbles" => ToggleGridBubbles(doc, args),
                "set_grid_mode" => SetGridMode(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private const double MmToFeet = 1.0 / 304.8;

        #region Query

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
                    elevation_mm = Math.Round(lvl.Elevation / MmToFeet, 1),
                    story_height_ft = storyHeight,
                    story_height_mm = Math.Round(storyHeight / MmToFeet, 1),
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

        #endregion

        #region Create / Modify

        private string CreateLevel(Document doc, Dictionary<string, object> args)
        {
            var name = GetArg<string>(args, "name");
            double elevation = GetArg(args, "elevation", 0.0);
            var unit = GetArg(args, "unit", "mm");
            bool dryRun = GetArg(args, "dry_run", false);

            if (string.IsNullOrWhiteSpace(name)) return JsonError("name required.");

            double elevFt = unit.Equals("mm", StringComparison.OrdinalIgnoreCase)
                ? elevation * MmToFeet
                : elevation;

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_create = true,
                    name,
                    elevation_ft = Math.Round(elevFt, 4),
                    elevation_mm = Math.Round(elevation, 1)
                }, JsonOpts);
            }

            Level newLevel;
            using (var trans = new Transaction(doc, "AI: Create Level"))
            {
                trans.Start();
                newLevel = Level.Create(doc, elevFt);
                try { newLevel.Name = name; }
                catch (Exception ex) { return JsonError($"Level created but rename failed: {ex.Message}"); }
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                id = newLevel.Id.Value,
                name = newLevel.Name,
                elevation_ft = Math.Round(elevFt, 4),
                elevation_mm = Math.Round(elevation, 1),
                message = $"Created level '{newLevel.Name}' at elevation {Math.Round(elevation, 1)} {unit}."
            }, JsonOpts);
        }

        private string DuplicateLevelsOffset(Document doc, Dictionary<string, object> args)
        {
            double offsetMm = GetArg(args, "offset_mm", 0.0);
            var suffix = GetArg(args, "suffix", "");
            var prefix = GetArg(args, "prefix", "");
            bool dryRun = GetArg(args, "dry_run", false);

            if (offsetMm == 0 && string.IsNullOrEmpty(suffix) && string.IsNullOrEmpty(prefix))
                return JsonError("At least offset_mm or suffix/prefix must be provided.");

            double offsetFt = offsetMm * MmToFeet;

            var existingLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (existingLevels.Count == 0) return JsonError("No existing levels found in the model.");

            var created = new List<object>();
            var errors = new List<string>();

            if (dryRun)
            {
                foreach (var lvl in existingLevels)
                {
                    double newElev = lvl.Elevation + offsetFt;
                    string newName = $"{prefix}{lvl.Name}{suffix}";
                    created.Add(new
                    {
                        name = newName,
                        elevation_ft = Math.Round(newElev, 4),
                        elevation_mm = Math.Round(newElev / MmToFeet, 1),
                        based_on = lvl.Name
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_create = created.Count,
                    offset_mm = offsetMm,
                    suffix,
                    prefix,
                    levels = created.Take(50)
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Duplicate Levels with Offset"))
            {
                trans.Start();

                foreach (var lvl in existingLevels)
                {
                    double newElev = lvl.Elevation + offsetFt;
                    string newName = $"{prefix}{lvl.Name}{suffix}";

                    try
                    {
                        var newLevel = Level.Create(doc, newElev);
                        try { newLevel.Name = newName; }
                        catch
                        {
                            newName = $"{newName}_{newLevel.Id.Value}";
                            try { newLevel.Name = newName; } catch { }
                        }

                        created.Add(new
                        {
                            id = newLevel.Id.Value,
                            name = newLevel.Name,
                            elevation_ft = Math.Round(newElev, 4),
                            elevation_mm = Math.Round(newElev / MmToFeet, 1),
                            based_on = lvl.Name
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to create level from '{lvl.Name}': {ex.Message}");
                    }
                }

                if (created.Count > 0)
                    trans.Commit();
                else
                    trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                created_count = created.Count,
                offset_mm = offsetMm,
                suffix,
                prefix,
                levels = created,
                errors = errors.Count > 0 ? errors : null,
                message = $"Created {created.Count} new level(s) with {offsetMm:+0;-0}mm offset{(string.IsNullOrEmpty(suffix) ? "" : $" and suffix '{suffix}'")}."
            }, JsonOpts);
        }

        private string RenameLevel(Document doc, Dictionary<string, object> args)
        {
            long levelId = GetArg(args, "level_id", 0L);
            var newName = GetArg<string>(args, "new_name");
            bool dryRun = GetArg(args, "dry_run", false);

            if (levelId == 0) return JsonError("level_id required.");
            if (string.IsNullOrWhiteSpace(newName)) return JsonError("new_name required.");

            var level = doc.GetElement(new ElementId(levelId)) as Level;
            if (level == null) return JsonError($"Element {levelId} is not a level or doesn't exist.");

            string oldName = level.Name;

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    id = levelId,
                    old_name = oldName,
                    new_name = newName,
                    would_rename = true
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Rename Level"))
            {
                trans.Start();
                try
                {
                    level.Name = newName;
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return JsonError($"Failed to rename level: {ex.Message}");
                }
            }

            return JsonSerializer.Serialize(new
            {
                id = levelId,
                old_name = oldName,
                new_name = level.Name,
                message = $"Renamed level '{oldName}' → '{level.Name}'."
            }, JsonOpts);
        }

        private string DeleteLevels(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "level_ids");
            bool dryRun = GetArg(args, "dry_run", false);
            if (ids == null || ids.Count == 0) return JsonError("level_ids required.");

            int deleted = 0;
            var errors = new List<string>();

            if (dryRun)
            {
                int deletable = 0;
                foreach (var id in ids)
                {
                    var level = doc.GetElement(new ElementId(id)) as Level;
                    if (level == null) { errors.Add($"Element {id} is not a level"); continue; }
                    deletable++;
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    deletable,
                    errors = errors.Take(10)
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Delete Levels"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elemId = new ElementId(id);
                    var level = doc.GetElement(elemId) as Level;
                    if (level == null) { errors.Add($"Element {id} is not a level"); continue; }

                    try
                    {
                        doc.Delete(elemId);
                        deleted++;
                    }
                    catch (Exception ex) { errors.Add($"Cannot delete '{level.Name}': {ex.Message}"); }
                }
                if (deleted > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                deleted,
                errors = errors.Count > 0 ? errors : null,
                message = $"Deleted {deleted} level(s)."
            }, JsonOpts);
        }

        private string CreateGrid(Document doc, Dictionary<string, object> args)
        {
            var name = GetArg<string>(args, "name");
            double x1 = GetArg(args, "start_x", 0.0);
            double y1 = GetArg(args, "start_y", 0.0);
            double x2 = GetArg(args, "end_x", 0.0);
            double y2 = GetArg(args, "end_y", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            if (string.IsNullOrWhiteSpace(name)) return JsonError("name required.");

            var start = new XYZ(x1, y1, 0);
            var end = new XYZ(x2, y2, 0);

            if (start.DistanceTo(end) < 0.01)
                return JsonError("Start and end points are too close. Grid line must have a minimum length.");

            var line = Line.CreateBound(start, end);

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_create = true,
                    name,
                    start = new { x = x1, y = y1 },
                    end_point = new { x = x2, y = y2 },
                    length_ft = Math.Round(line.Length, 4)
                }, JsonOpts);
            }

            Grid grid;
            using (var trans = new Transaction(doc, "AI: Create Grid"))
            {
                trans.Start();
                grid = Grid.Create(doc, line);
                try { grid.Name = name; } catch { }
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                id = grid.Id.Value,
                name = grid.Name,
                start = new { x = x1, y = y1 },
                end_point = new { x = x2, y = y2 },
                length_ft = Math.Round(line.Length, 4),
                message = $"Created grid '{grid.Name}'."
            }, JsonOpts);
        }

        #endregion

        #region Grid Bubbles & Mode

        private string ToggleGridBubbles(Document doc, Dictionary<string, object> args)
        {
            var view = doc.ActiveView;
            if (view == null) return JsonError("No active view.");
            var gridIds = GetArgLongArray(args, "grid_ids");
            var end = (GetArg(args, "end", "both") ?? "both").ToLower();
            var action = (GetArg(args, "action", "toggle") ?? "toggle").ToLower();

            var grids = ResolveGridElements(doc, gridIds);
            if (grids.Count == 0) return JsonError("No grids found.");

            int changed = 0;
            using (var trans = new Transaction(doc, "AI: Toggle Grid Bubbles"))
            {
                trans.Start();
                foreach (var grid in grids)
                {
                    var ends = new List<DatumEnds>();
                    if (end is "start" or "both") ends.Add(DatumEnds.End0);
                    if (end is "end" or "both") ends.Add(DatumEnds.End1);

                    foreach (var e in ends)
                    {
                        bool visible = grid.IsBubbleVisibleInView(e, view);
                        bool shouldShow = action switch
                        {
                            "show" => true,
                            "hide" => false,
                            _ => !visible
                        };

                        if (shouldShow && !visible) { grid.ShowBubbleInView(e, view); changed++; }
                        else if (!shouldShow && visible) { grid.HideBubbleInView(e, view); changed++; }
                    }
                }
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                grids_affected = grids.Count,
                bubbles_changed = changed,
                action,
                end
            }, JsonOpts);
        }

        private string SetGridMode(Document doc, Dictionary<string, object> args)
        {
            var view = doc.ActiveView;
            if (view == null) return JsonError("No active view.");
            var gridIds = GetArgLongArray(args, "grid_ids");
            var mode = (GetArg(args, "mode", "2d") ?? "2d").ToLower();
            var targetType = mode == "3d" ? DatumExtentType.Model : DatumExtentType.ViewSpecific;

            var grids = ResolveGridElements(doc, gridIds);
            if (grids.Count == 0) return JsonError("No grids found.");

            int changed = 0;
            using (var trans = new Transaction(doc, "AI: Set Grid Mode"))
            {
                trans.Start();
                foreach (var grid in grids)
                {
                    try
                    {
                        if (grid.GetDatumExtentTypeInView(DatumEnds.End0, view) != targetType)
                        {
                            grid.SetDatumExtentType(DatumEnds.End0, view, targetType);
                            grid.SetDatumExtentType(DatumEnds.End1, view, targetType);
                            changed++;
                        }
                    }
                    catch { }
                }
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                grids_affected = grids.Count,
                grids_changed = changed,
                mode = mode == "3d" ? "3D (Model)" : "2D (View-Specific)"
            }, JsonOpts);
        }

        private static List<Grid> ResolveGridElements(Document doc, List<long> gridIds)
        {
            if (gridIds != null && gridIds.Count > 0)
                return gridIds
                    .Select(id => doc.GetElement(new ElementId(id)) as Grid)
                    .Where(g => g != null)
                    .ToList();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .ToList();
        }

        #endregion
    }
}
