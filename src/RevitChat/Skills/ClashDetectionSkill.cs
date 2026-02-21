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
    public class ClashDetectionSkill : IRevitSkill
    {
        public string Name => "ClashDetection";
        public string Description => "Detect element intersections, clearance issues, and overlapping geometry";

        private static readonly HashSet<string> HandledTools = new()
        {
            "check_clashes", "check_clearance", "find_overlapping", "get_clash_summary"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("check_clashes",
                "Find geometric intersections between two categories (e.g. Ducts vs Structural Framing).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category_a": { "type": "string", "description": "First category name" },
                        "category_b": { "type": "string", "description": "Second category name" },
                        "limit": { "type": "integer", "description": "Max clashes to return (default 50)" }
                    },
                    "required": ["category_a", "category_b"]
                }
                """)),

            ChatTool.CreateFunctionTool("check_clearance",
                "Find elements of a category within a specified clearance distance from elements of another category.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category_a": { "type": "string", "description": "Source category" },
                        "category_b": { "type": "string", "description": "Nearby category to check" },
                        "min_distance_feet": { "type": "number", "description": "Minimum clearance distance in feet" },
                        "limit": { "type": "integer", "description": "Max violations to return (default 50)" }
                    },
                    "required": ["category_a", "category_b", "min_distance_feet"]
                }
                """)),

            ChatTool.CreateFunctionTool("find_overlapping",
                "Find elements in the same category that overlap or share the same location.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category to check for overlaps" },
                        "tolerance_feet": { "type": "number", "description": "Distance tolerance in feet (default 0.01)" },
                        "limit": { "type": "integer", "description": "Max pairs to return (default 50)" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_clash_summary",
                "Get a high-level summary of potential clashes across common category combinations.",
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
                "check_clashes" => CheckClashes(doc, args),
                "check_clearance" => CheckClearance(doc, args),
                "find_overlapping" => FindOverlapping(doc, args),
                "get_clash_summary" => GetClashSummary(doc),
                _ => JsonError($"ClashDetectionSkill: unknown tool '{functionName}'")
            };
        }

        private string CheckClashes(Document doc, Dictionary<string, object> args)
        {
            var catA = GetArg<string>(args, "category_a");
            var catB = GetArg<string>(args, "category_b");
            int limit = GetArg(args, "limit", 50);

            var bicA = ResolveCategoryFilter(doc, catA);
            var bicB = ResolveCategoryFilter(doc, catB);
            if (!bicA.HasValue) return JsonError($"Category '{catA}' not found.");
            if (!bicB.HasValue) return JsonError($"Category '{catB}' not found.");

            var elemsA = new FilteredElementCollector(doc)
                .OfCategory(bicA.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var clashes = new List<object>();

            foreach (var a in elemsA)
            {
                if (clashes.Count >= limit) break;

                BoundingBoxXYZ bbA;
                try { bbA = a.get_BoundingBox(null); } catch { continue; }
                if (bbA == null) continue;

                var outline = new Outline(bbA.Min, bbA.Max);
                var bbFilter = new BoundingBoxIntersectsFilter(outline);

                var intersecting = new FilteredElementCollector(doc)
                    .OfCategory(bicB.Value)
                    .WhereElementIsNotElementType()
                    .WherePasses(bbFilter)
                    .ToList();

                foreach (var b in intersecting)
                {
                    if (clashes.Count >= limit) break;
                    clashes.Add(new
                    {
                        element_a = new { id = a.Id.Value, name = a.Name, category = catA },
                        element_b = new { id = b.Id.Value, name = b.Name, category = catB }
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                category_a = catA,
                category_b = catB,
                clash_count = clashes.Count,
                clashes
            }, JsonOpts);
        }

        private string CheckClearance(Document doc, Dictionary<string, object> args)
        {
            var catA = GetArg<string>(args, "category_a");
            var catB = GetArg<string>(args, "category_b");
            double minDist = GetArg(args, "min_distance_feet", 1.0);
            int limit = GetArg(args, "limit", 50);

            var bicA = ResolveCategoryFilter(doc, catA);
            var bicB = ResolveCategoryFilter(doc, catB);
            if (!bicA.HasValue) return JsonError($"Category '{catA}' not found.");
            if (!bicB.HasValue) return JsonError($"Category '{catB}' not found.");

            var elemsA = new FilteredElementCollector(doc)
                .OfCategory(bicA.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var violations = new List<object>();

            foreach (var a in elemsA)
            {
                if (violations.Count >= limit) break;

                var locA = GetElementCenter(a);
                if (locA == null) continue;

                var bbA = a.get_BoundingBox(null);
                if (bbA == null) continue;

                var expandedMin = new XYZ(bbA.Min.X - minDist, bbA.Min.Y - minDist, bbA.Min.Z - minDist);
                var expandedMax = new XYZ(bbA.Max.X + minDist, bbA.Max.Y + minDist, bbA.Max.Z + minDist);
                var outline = new Outline(expandedMin, expandedMax);
                var bbFilter = new BoundingBoxIntersectsFilter(outline);

                var nearby = new FilteredElementCollector(doc)
                    .OfCategory(bicB.Value)
                    .WhereElementIsNotElementType()
                    .WherePasses(bbFilter)
                    .ToList();

                foreach (var b in nearby)
                {
                    if (violations.Count >= limit) break;
                    var locB = GetElementCenter(b);
                    if (locB == null) continue;

                    double dist = locA.DistanceTo(locB);
                    if (dist < minDist)
                    {
                        violations.Add(new
                        {
                            element_a = new { id = a.Id.Value, name = a.Name },
                            element_b = new { id = b.Id.Value, name = b.Name },
                            distance_feet = Math.Round(dist, 4),
                            required_clearance = minDist
                        });
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                category_a = catA,
                category_b = catB,
                min_clearance = minDist,
                violation_count = violations.Count,
                violations
            }, JsonOpts);
        }

        private string FindOverlapping(Document doc, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            double tolerance = GetArg(args, "tolerance_feet", 0.01);
            int limit = GetArg(args, "limit", 50);

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var overlaps = new List<object>();
            var processed = new HashSet<long>();

            for (int i = 0; i < elements.Count && overlaps.Count < limit; i++)
            {
                var a = elements[i];
                if (processed.Contains(a.Id.Value)) continue;

                var locA = GetElementCenter(a);
                if (locA == null) continue;

                for (int j = i + 1; j < elements.Count && overlaps.Count < limit; j++)
                {
                    var b = elements[j];
                    var locB = GetElementCenter(b);
                    if (locB == null) continue;

                    if (locA.DistanceTo(locB) <= tolerance)
                    {
                        overlaps.Add(new
                        {
                            element_a = new { id = a.Id.Value, name = a.Name },
                            element_b = new { id = b.Id.Value, name = b.Name },
                            distance_feet = Math.Round(locA.DistanceTo(locB), 6)
                        });
                        processed.Add(b.Id.Value);
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                category = catName,
                total_elements = elements.Count,
                overlap_pairs = overlaps.Count,
                overlaps
            }, JsonOpts);
        }

        private string GetClashSummary(Document doc)
        {
            var pairs = new[]
            {
                ("Ducts", BuiltInCategory.OST_DuctCurves, "Structural Framing", BuiltInCategory.OST_StructuralFraming),
                ("Pipes", BuiltInCategory.OST_PipeCurves, "Structural Framing", BuiltInCategory.OST_StructuralFraming),
                ("Ducts", BuiltInCategory.OST_DuctCurves, "Pipes", BuiltInCategory.OST_PipeCurves),
                ("Cable Trays", BuiltInCategory.OST_CableTray, "Ducts", BuiltInCategory.OST_DuctCurves),
                ("Pipes", BuiltInCategory.OST_PipeCurves, "Walls", BuiltInCategory.OST_Walls),
            };

            var results = new List<object>();

            foreach (var (nameA, bicA, nameB, bicB) in pairs)
            {
                int countA = new FilteredElementCollector(doc).OfCategory(bicA).WhereElementIsNotElementType().GetElementCount();
                int countB = new FilteredElementCollector(doc).OfCategory(bicB).WhereElementIsNotElementType().GetElementCount();

                if (countA == 0 || countB == 0) continue;

                int clashCount = 0;
                var elemsA = new FilteredElementCollector(doc).OfCategory(bicA).WhereElementIsNotElementType().ToList();

                foreach (var a in elemsA.Take(200))
                {
                    var bb = a.get_BoundingBox(null);
                    if (bb == null) continue;

                    var outline = new Outline(bb.Min, bb.Max);
                    var intersecting = new FilteredElementCollector(doc)
                        .OfCategory(bicB)
                        .WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(outline))
                        .GetElementCount();
                    clashCount += intersecting;
                }

                results.Add(new
                {
                    category_a = nameA,
                    count_a = countA,
                    category_b = nameB,
                    count_b = countB,
                    potential_clashes = clashCount
                });
            }

            return JsonSerializer.Serialize(new { combinations_checked = results.Count, summary = results }, JsonOpts);
        }

        private static XYZ GetElementCenter(Element elem)
        {
            if (elem.Location is LocationPoint lp) return lp.Point;
            if (elem.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);

            var bb = elem.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) / 2;

            return null;
        }
    }
}
