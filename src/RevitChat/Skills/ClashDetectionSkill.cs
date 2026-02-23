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
    public class ClashDetectionSkill : BaseRevitSkill
    {
        protected override string SkillName => "ClashDetection";
        protected override string SkillDescription => "Detect element intersections, clearance issues, and overlapping geometry";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "check_clashes", "check_clearance", "find_overlapping", "get_clash_summary"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("check_clashes",
                "Find geometric intersections between two categories (e.g. Ducts vs Structural Framing). Supports system_pair filter.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category_a": { "type": "string", "description": "First category name" },
                        "category_b": { "type": "string", "description": "Second category name" },
                        "system_pair": { "type": "string", "description": "Filter by system names, e.g. 'Hot Water vs Cold Water'" },
                        "max_results": { "type": "integer", "description": "Max clashes to return (default 100)" },
                        "limit": { "type": "integer", "description": "Alias for max_results (default 50)" }
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

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "check_clashes" => CheckClashes(doc, args),
                "check_clearance" => CheckClearance(doc, args),
                "find_overlapping" => FindOverlapping(doc, args),
                "get_clash_summary" => GetClashSummary(doc),
                _ => UnknownTool(functionName)
            };
        }

        private string CheckClashes(Document doc, Dictionary<string, object> args)
        {
            var catA = GetArg<string>(args, "category_a");
            var catB = GetArg<string>(args, "category_b");
            var systemPair = GetArg<string>(args, "system_pair");
            int maxResults = GetArg(args, "max_results", 0);
            int limit = maxResults > 0 ? maxResults : GetArg(args, "limit", 100);

            var bicA = ResolveCategoryFilter(doc, catA);
            var bicB = ResolveCategoryFilter(doc, catB);
            if (!bicA.HasValue) return JsonError($"Category '{catA}' not found.");
            if (!bicB.HasValue) return JsonError($"Category '{catB}' not found.");

            string systemA = null, systemB = null;
            if (!string.IsNullOrEmpty(systemPair))
            {
                var parts = systemPair.Split(new[] { " vs ", " VS ", " Vs " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) { systemA = parts[0].Trim(); systemB = parts[1].Trim(); }
            }

            var elemsB = new FilteredElementCollector(doc)
                .OfCategory(bicB.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var bIndex = new List<(Element Elem, BoundingBoxXYZ BB)>();
            foreach (var b in elemsB)
            {
                if (systemB != null && !MatchesElemSystem(b, systemB)) continue;
                BoundingBoxXYZ bb;
                try { bb = b.get_BoundingBox(null); } catch { continue; }
                if (bb != null) bIndex.Add((b, bb));
            }

            var elemsA = new FilteredElementCollector(doc)
                .OfCategory(bicA.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var clashes = new List<object>();
            int totalEstimate = 0;
            var seen = new HashSet<(long, long)>();

            foreach (var a in elemsA)
            {
                if (clashes.Count >= limit) { totalEstimate = -1; break; }
                if (systemA != null && !MatchesElemSystem(a, systemA)) continue;

                BoundingBoxXYZ bbA;
                try { bbA = a.get_BoundingBox(null); } catch { continue; }
                if (bbA == null) continue;

                foreach (var (b, bbB) in bIndex)
                {
                    if (b.Id == a.Id) continue;
                    var pairKey = (Math.Min(a.Id.Value, b.Id.Value), Math.Max(a.Id.Value, b.Id.Value));
                    if (seen.Contains(pairKey)) continue;

                    if (bbA.Max.X < bbB.Min.X || bbA.Min.X > bbB.Max.X ||
                        bbA.Max.Y < bbB.Min.Y || bbA.Min.Y > bbB.Max.Y ||
                        bbA.Max.Z < bbB.Min.Z || bbA.Min.Z > bbB.Max.Z)
                        continue;

                    seen.Add(pairKey);
                    totalEstimate++;
                    if (clashes.Count < limit)
                    {
                        clashes.Add(new
                        {
                            element_a = new { id = a.Id.Value, name = a.Name, category = catA },
                            element_b = new { id = b.Id.Value, name = b.Name, category = catB }
                        });
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                category_a = catA,
                category_b = catB,
                system_pair = systemPair,
                clash_count = clashes.Count,
                estimated_total = totalEstimate >= 0 ? totalEstimate : clashes.Count,
                capped = totalEstimate < 0,
                clashes
            }, JsonOpts);
        }

        private static bool MatchesElemSystem(Element elem, string systemName)
        {
            var sn = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
            var sc = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
            if (string.IsNullOrEmpty(sn))
            {
                var altParam = elem.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                sn = altParam?.AsString() ?? altParam?.AsValueString() ?? "";
            }
            return MatchesSystem(sn, sc, systemName);
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

                BoundingBoxXYZ bbA;
                try { bbA = a.get_BoundingBox(null); } catch { continue; }
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
                    if (b.Id == a.Id) continue;
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
                var elemsA = new FilteredElementCollector(doc).OfCategory(bicA).WhereElementIsNotElementType().ToList();
                var elemsBWithBB = new List<(Element Elem, BoundingBoxXYZ BB)>();
                foreach (var b in new FilteredElementCollector(doc).OfCategory(bicB).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ bb;
                    try { bb = b.get_BoundingBox(null); } catch { continue; }
                    if (bb != null) elemsBWithBB.Add((b, bb));
                }

                int countA = elemsA.Count;
                int countB = elemsBWithBB.Count;
                if (countA == 0 || countB == 0) continue;

                var clashingBIds = new HashSet<long>();
                int elementsWithClash = 0;
                bool sameCat = bicA == bicB;

                foreach (var a in elemsA)
                {
                    BoundingBoxXYZ bbA;
                    try { bbA = a.get_BoundingBox(null); } catch { continue; }
                    if (bbA == null) continue;

                    bool hasClash = false;
                    foreach (var (b, bbB) in elemsBWithBB)
                    {
                        if (sameCat && b.Id == a.Id) continue;
                        if (bbA.Max.X < bbB.Min.X || bbA.Min.X > bbB.Max.X ||
                            bbA.Max.Y < bbB.Min.Y || bbA.Min.Y > bbB.Max.Y ||
                            bbA.Max.Z < bbB.Min.Z || bbA.Min.Z > bbB.Max.Z)
                            continue;
                        clashingBIds.Add(b.Id.Value);
                        hasClash = true;
                    }
                    if (hasClash) elementsWithClash++;
                }

                results.Add(new
                {
                    category_a = nameA,
                    count_a = countA,
                    category_b = nameB,
                    count_b = countB,
                    elements_a_with_clash = elementsWithClash,
                    unique_elements_b_clashing = clashingBIds.Count
                });
            }

            return JsonSerializer.Serialize(new { combinations_checked = results.Count, summary = results }, JsonOpts);
        }

    }
}
