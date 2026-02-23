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
    public class CoordinationReportSkill : BaseRevitSkill
    {
        protected override string SkillName => "CoordinationReport";
        protected override string SkillDescription => "Generate clash reports, compare element counts, check link status, scope box summary";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "generate_clash_report", "compare_element_counts",
            "get_link_coordination_status", "get_scope_box_summary"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("generate_clash_report",
                "Generate a comprehensive clash report between multiple category pairs for coordination review.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "pairs": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "category_a": { "type": "string" },
                                    "category_b": { "type": "string" }
                                }
                            },
                            "description": "Category pairs to check. If empty, uses common MEP vs Structure pairs."
                        },
                        "max_per_pair": { "type": "integer", "description": "Max clashes per pair (default 20)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("compare_element_counts",
                "Compare element counts by category between the host model and linked models, or get a snapshot of current counts.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "categories": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "Category names to compare. If empty, uses top categories."
                        }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_link_coordination_status",
                "Get detailed status of all linked models: loaded state, file path, shared coordinates, transform.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_scope_box_summary",
                "List all scope boxes and the views that use them.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
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
                "generate_clash_report" => GenerateClashReport(doc, args),
                "compare_element_counts" => CompareElementCounts(doc, args),
                "get_link_coordination_status" => GetLinkCoordinationStatus(doc),
                "get_scope_box_summary" => GetScopeBoxSummary(doc),
                _ => JsonError($"CoordinationReportSkill: unknown tool '{functionName}'")
            };
        }

        private string GenerateClashReport(Document doc, Dictionary<string, object> args)
        {
            int maxPerPair = GetArg(args, "max_per_pair", 20);

            var defaultPairs = new (string a, string b)[]
            {
                ("Ducts", "Structural Framing"),
                ("Pipes", "Structural Framing"),
                ("Ducts", "Pipes"),
                ("Cable Trays", "Ducts"),
                ("Conduits", "Pipes"),
                ("Pipes", "Walls"),
                ("Ducts", "Walls"),
            };

            var customPairs = GetCustomPairs(args);
            var pairs = customPairs.Count > 0 ? customPairs : defaultPairs.Select(p => (p.a, p.b)).ToList();

            var report = new List<object>();

            foreach (var (catA, catB) in pairs)
            {
                var bicA = ResolveCategoryFilter(doc, catA);
                var bicB = ResolveCategoryFilter(doc, catB);
                if (!bicA.HasValue || !bicB.HasValue) continue;

                var elemsA = new FilteredElementCollector(doc).OfCategory(bicA.Value).WhereElementIsNotElementType().ToList();
                int countA = elemsA.Count;
                int countB = new FilteredElementCollector(doc).OfCategory(bicB.Value).WhereElementIsNotElementType().GetElementCount();
                if (countA == 0 || countB == 0) continue;

                var clashes = new List<object>();

                foreach (var a in elemsA)
                {
                    if (clashes.Count >= maxPerPair) break;
                    BoundingBoxXYZ bb;
                    try { bb = a.get_BoundingBox(null); } catch { continue; }
                    if (bb == null) continue;

                    var intersecting = new FilteredElementCollector(doc)
                        .OfCategory(bicB.Value)
                        .WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max)))
                        .ToList();

                    foreach (var b in intersecting)
                    {
                        if (clashes.Count >= maxPerPair) break;
                        clashes.Add(new
                        {
                            a_id = a.Id.Value, a_name = a.Name,
                            b_id = b.Id.Value, b_name = b.Name
                        });
                    }
                }

                report.Add(new
                {
                    category_a = catA, count_a = countA,
                    category_b = catB, count_b = countB,
                    clashes_found = clashes.Count,
                    clashes
                });
            }

            int totalClashes = report.Sum(r => ((dynamic)r).clashes_found);
            return JsonSerializer.Serialize(new
            {
                pairs_checked = report.Count,
                total_clashes = totalClashes,
                report
            }, JsonOpts);
        }

        private List<(string a, string b)> GetCustomPairs(Dictionary<string, object> args)
        {
            var result = new List<(string, string)>();
            if (args == null || !args.TryGetValue("pairs", out var val)) return result;
            if (val is not JsonElement je || je.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in je.EnumerateArray())
            {
                var a = item.TryGetProperty("category_a", out var pa) ? pa.GetString() : null;
                var b = item.TryGetProperty("category_b", out var pb) ? pb.GetString() : null;
                if (a != null && b != null) result.Add((a, b));
            }
            return result;
        }

        private string CompareElementCounts(Document doc, Dictionary<string, object> args)
        {
            var catNames = GetArgStringArray(args, "categories");

            if (catNames == null || catNames.Count == 0)
            {
                catNames = new List<string>
                {
                    "Walls", "Floors", "Roofs", "Doors", "Windows",
                    "Structural Columns", "Structural Framing",
                    "Ducts", "Pipes", "Cable Trays", "Conduits",
                    "Mechanical Equipment", "Electrical Equipment",
                    "Plumbing Fixtures", "Rooms"
                };
            }

            var hostCounts = new Dictionary<string, int>();
            foreach (var cn in catNames)
            {
                var bic = ResolveCategoryFilter(doc, cn);
                if (!bic.HasValue) continue;
                int count = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().GetElementCount();
                if (count > 0) hostCounts[cn] = count;
            }

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var linkCounts = new List<object>();
            foreach (var link in links)
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                var counts = new Dictionary<string, int>();
                foreach (var cn in catNames)
                {
                    var bic = ResolveCategoryFilter(linkDoc, cn);
                    if (!bic.HasValue) continue;
                    int count = new FilteredElementCollector(linkDoc).OfCategory(bic.Value).WhereElementIsNotElementType().GetElementCount();
                    if (count > 0) counts[cn] = count;
                }

                linkCounts.Add(new { link_name = linkDoc.Title, counts });
            }

            return JsonSerializer.Serialize(new
            {
                host_model = doc.Title,
                host_counts = hostCounts,
                linked_models = linkCounts
            }, JsonOpts);
        }

        private string GetLinkCoordinationStatus(Document doc)
        {
            var linkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var items = linkInstances.Select(li =>
            {
                var linkDoc = li.GetLinkDocument();
                var transform = li.GetTotalTransform();
                var origin = transform.Origin;

                string status = linkDoc != null ? "Loaded" : "Not Loaded";
                string path = "-";

                try
                {
                    var lt = doc.GetElement(li.GetTypeId()) as RevitLinkType;
                    if (lt != null)
                    {
                        var extRef = ExternalFileUtils.GetExternalFileReference(doc, lt.Id);
                        if (extRef != null)
                        {
                            path = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
                            status = extRef.GetLinkedFileStatus().ToString();
                        }
                    }
                }
                catch { }

                bool isOrigin = Math.Abs(origin.X) < 0.001 && Math.Abs(origin.Y) < 0.001 && Math.Abs(origin.Z) < 0.001;

                return new
                {
                    instance_id = li.Id.Value,
                    name = li.Name,
                    status,
                    file_path = path,
                    is_at_origin = isOrigin,
                    origin_x = Math.Round(origin.X, 4),
                    origin_y = Math.Round(origin.Y, 4),
                    origin_z = Math.Round(origin.Z, 4),
                    element_count = linkDoc != null
                        ? new FilteredElementCollector(linkDoc).WhereElementIsNotElementType().GetElementCount()
                        : 0
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                link_count = items.Count,
                all_loaded = items.All(i => ((dynamic)i).status == "Loaded"),
                links = items
            }, JsonOpts);
        }

        private string GetScopeBoxSummary(Document doc)
        {
            var scopeBoxes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType()
                .ToList();

            if (scopeBoxes.Count == 0)
                return JsonSerializer.Serialize(new { message = "No scope boxes in this model.", scope_boxes = Array.Empty<object>() }, JsonOpts);

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            var items = scopeBoxes.Select(sb =>
            {
                var associatedViews = views
                    .Where(v =>
                    {
                        try
                        {
                            var scopeParam = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                            return scopeParam?.AsElementId() == sb.Id;
                        }
                        catch { return false; }
                    })
                    .Select(v => new { id = v.Id.Value, name = v.Name, view_type = v.ViewType.ToString() })
                    .ToList();

                return new
                {
                    id = sb.Id.Value,
                    name = sb.Name,
                    associated_view_count = associatedViews.Count,
                    views = associatedViews.Take(10).ToList()
                };
            }).ToList();

            return JsonSerializer.Serialize(new { scope_box_count = items.Count, scope_boxes = items }, JsonOpts);
        }
    }
}
