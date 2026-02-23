using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class MepQuantityTakeoffSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepQuantityTakeoff";
        protected override string SkillDescription => "MEP quantity takeoff, insulation quantities, hanger estimates, BOQ export";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "mep_quantity_takeoff", "get_insulation_quantities",
            "get_hanger_quantities", "export_mep_boq"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("mep_quantity_takeoff",
                "Aggregate MEP quantities by category (duct/pipe/conduit/cable tray), grouped by size and level. Returns total length and count.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "categories": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "Categories to include: 'duct','pipe','conduit','cable_tray','flex_duct','flex_pipe'. Default: all."
                        },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_insulation_quantities",
                "Get insulation quantities for ducts and pipes: type, thickness, total length/area.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "insulation_for": { "type": "string", "description": "'duct', 'pipe', or 'all'. Default 'all'." },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_hanger_quantities",
                "Estimate hanger/support quantities for ducts, pipes, conduits, cable trays based on length and standard spacing.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "spacing_m": { "type": "number", "description": "Hanger spacing in meters. Default 3.0.", "default": 3.0 },
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("export_mep_boq",
                "Export MEP Bill of Quantities to CSV, grouped by category/size/level with total lengths.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "file_path": { "type": "string", "description": "Output CSV path. Default: Desktop." },
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
                "mep_quantity_takeoff" => MepQuantityTakeoff(doc, args),
                "get_insulation_quantities" => GetInsulationQuantities(doc, args),
                "get_hanger_quantities" => GetHangerQuantities(doc, args),
                "export_mep_boq" => ExportMepBoq(doc, args),
                _ => JsonError($"MepQuantityTakeoffSkill: unknown tool '{functionName}'")
            };
        }

        private static readonly Dictionary<string, BuiltInCategory> CatMap = new()
        {
            ["duct"] = BuiltInCategory.OST_DuctCurves,
            ["pipe"] = BuiltInCategory.OST_PipeCurves,
            ["conduit"] = BuiltInCategory.OST_Conduit,
            ["cable_tray"] = BuiltInCategory.OST_CableTray,
            ["flex_duct"] = BuiltInCategory.OST_FlexDuctCurves,
            ["flex_pipe"] = BuiltInCategory.OST_FlexPipeCurves
        };

        private List<MepLineItem> CollectMepLines(Document doc, Dictionary<string, object> args)
        {
            var cats = GetArgStringArray(args, "categories");
            var levelFilter = GetArg<string>(args, "level");

            var selectedCats = (cats != null && cats.Count > 0)
                ? cats.Where(c => CatMap.ContainsKey(c.ToLower())).Select(c => CatMap[c.ToLower()]).ToList()
                : CatMap.Values.ToList();

            if (selectedCats.Count == 0)
                return new List<MepLineItem>();

            var items = new List<MepLineItem>();

            foreach (var cat in selectedCats)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    var lvl = GetElementLevel(doc, elem);
                    if (!string.IsNullOrEmpty(levelFilter) &&
                        !lvl.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    items.Add(new MepLineItem
                    {
                        Id = elem.Id.Value,
                        Category = elem.Category?.Name ?? "-",
                        Size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                        LengthFt = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0,
                        Level = lvl,
                        SystemName = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-",
                        Family = GetFamilyName(doc, elem),
                        Type = GetElementTypeName(doc, elem)
                    });
                }
            }

            return items;
        }

        private string MepQuantityTakeoff(Document doc, Dictionary<string, object> args)
        {
            var items = CollectMepLines(doc, args);
            if (items.Count == 0)
            {
                var cats = GetArgStringArray(args, "categories");
                var hint = (cats != null && cats.Count > 0)
                    ? $"No MEP elements found for categories: {string.Join(", ", cats)}. Valid: duct, pipe, conduit, cable_tray, flex_duct, flex_pipe."
                    : "No MEP elements found in this model.";
                return JsonError(hint);
            }

            var groups = items
                .GroupBy(i => new { i.Category, i.Size, i.Level })
                .Select(g => new
                {
                    category = g.Key.Category,
                    size = g.Key.Size,
                    level = g.Key.Level,
                    count = g.Count(),
                    total_length_m = Math.Round(g.Sum(i => i.LengthFt) * 0.3048, 2)
                })
                .OrderBy(g => g.category).ThenBy(g => g.level).ThenByDescending(g => g.total_length_m)
                .ToList();

            var categorySummary = items
                .GroupBy(i => i.Category)
                .Select(g => new
                {
                    category = g.Key,
                    count = g.Count(),
                    total_length_m = Math.Round(g.Sum(i => i.LengthFt) * 0.3048, 2)
                })
                .OrderByDescending(g => g.total_length_m)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                total_elements = items.Count,
                total_length_m = Math.Round(items.Sum(i => i.LengthFt) * 0.3048, 2),
                by_category = categorySummary,
                details = groups
            }, JsonOpts);
        }

        private string GetInsulationQuantities(Document doc, Dictionary<string, object> args)
        {
            var insFor = GetArg<string>(args, "insulation_for")?.ToLower() ?? "all";
            var levelFilter = GetArg<string>(args, "level");

            var categories = new List<BuiltInCategory>();
            if (insFor is "all" or "duct") categories.Add(BuiltInCategory.OST_DuctInsulations);
            if (insFor is "all" or "pipe") categories.Add(BuiltInCategory.OST_PipeInsulations);

            var groups = new Dictionary<string, InsGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var cat in categories)
            {
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType().ToList();

                foreach (var elem in elems)
                {
                    var lvl = GetElementLevel(doc, elem);
                    if (!string.IsNullOrEmpty(levelFilter) &&
                        !lvl.Equals(levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var typeName = GetElementTypeName(doc, elem);
                    var thickness = elem.LookupParameter("Thickness")?.AsValueString() ?? "-";
                    var length = elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
                    var area = elem.LookupParameter("Area")?.AsDouble() ?? 0;

                    var key = $"{elem.Category?.Name}|{typeName}|{thickness}";
                    if (!groups.TryGetValue(key, out var g))
                    {
                        g = new InsGroup
                        {
                            Category = elem.Category?.Name ?? "-",
                            Type = typeName,
                            Thickness = thickness
                        };
                        groups[key] = g;
                    }
                    g.Count++;
                    g.TotalLengthFt += length;
                    g.TotalAreaSqFt += area;
                }
            }

            var results = groups.Values.Select(g => new
            {
                category = g.Category,
                type = g.Type,
                thickness = g.Thickness,
                count = g.Count,
                total_length_m = Math.Round(g.TotalLengthFt * 0.3048, 2),
                total_area_sqm = Math.Round(g.TotalAreaSqFt * 0.092903, 2)
            }).OrderBy(g => g.category).ThenByDescending(g => g.total_length_m).ToList();

            return JsonSerializer.Serialize(new
            {
                total_insulation_pieces = groups.Values.Sum(g => g.Count),
                insulation = results
            }, JsonOpts);
        }

        private string GetHangerQuantities(Document doc, Dictionary<string, object> args)
        {
            var spacingM = GetArg<double>(args, "spacing_m", 3.0);
            if (spacingM <= 0) spacingM = 3.0;
            var spacingFt = spacingM / 0.3048;

            var items = CollectMepLines(doc, args);

            var groups = items
                .GroupBy(i => new { i.Category, i.Size })
                .Select(g =>
                {
                    var totalFt = g.Sum(i => i.LengthFt);
                    var hangerCount = totalFt > 0 ? (int)Math.Ceiling(totalFt / spacingFt) + 1 : 0;
                    return new
                    {
                        category = g.Key.Category,
                        size = g.Key.Size,
                        segment_count = g.Count(),
                        total_length_m = Math.Round(totalFt * 0.3048, 2),
                        estimated_hangers = hangerCount
                    };
                })
                .OrderBy(g => g.category).ThenByDescending(g => g.estimated_hangers)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                spacing_m = spacingM,
                total_estimated_hangers = groups.Sum(g => g.estimated_hangers),
                details = groups
            }, JsonOpts);
        }

        private string ExportMepBoq(Document doc, Dictionary<string, object> args)
        {
            var filePath = GetArg<string>(args, "file_path");
            if (string.IsNullOrEmpty(filePath))
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"MEP_BOQ_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var items = CollectMepLines(doc, args);

            var sb = new StringBuilder();
            sb.AppendLine("Category,System,Size,Family,Type,Level,Length_m,ElementId");

            foreach (var item in items.OrderBy(i => i.Category).ThenBy(i => i.Level).ThenBy(i => i.Size))
            {
                sb.AppendLine(string.Join(",",
                    Esc(item.Category), Esc(item.SystemName), Esc(item.Size),
                    Esc(item.Family), Esc(item.Type), Esc(item.Level),
                    Math.Round(item.LengthFt * 0.3048, 3), item.Id));
            }

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to write BOQ file: {ex.Message}. Path: {filePath}");
            }

            var catSummary = items.GroupBy(i => i.Category)
                .Select(g => new { category = g.Key, count = g.Count(), length_m = Math.Round(g.Sum(i => i.LengthFt) * 0.3048, 2) })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                file_path = filePath,
                total_rows = items.Count,
                summary = catSummary
            }, JsonOpts);
        }

        private static string Esc(string v) => EscapeCsv(v);

        private class MepLineItem
        {
            public long Id;
            public string Category, Size, Level, SystemName, Family, Type;
            public double LengthFt;
        }

        private class InsGroup
        {
            public string Category, Type, Thickness;
            public int Count;
            public double TotalLengthFt, TotalAreaSqFt;
        }
    }
}
