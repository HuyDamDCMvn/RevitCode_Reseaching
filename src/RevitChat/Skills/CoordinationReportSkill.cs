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
    public class CoordinationReportSkill : BaseRevitSkill
    {
        protected override string SkillName => "CoordinationReport";
        protected override string SkillDescription => "Generate clash reports, compare element counts, check link status, scope box summary";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "generate_clash_report", "compare_element_counts",
            "get_link_coordination_status", "get_scope_box_summary",
            "compare_model_versions", "generate_qc_report"
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
                """)),

            ChatTool.CreateFunctionTool("compare_model_versions",
                "Snapshot current model state and compare with a previous snapshot to detect changes.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "action": { "type": "string", "enum": ["snapshot", "compare"], "description": "Take snapshot or compare with last" },
                        "snapshot_path": { "type": "string", "description": "Path to saved snapshot (for compare)" }
                    },
                    "required": ["action"]
                }
                """)),

            ChatTool.CreateFunctionTool("generate_qc_report",
                "Generate a QC checklist report with pass/fail items. Export to HTML or CSV.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "export_format": { "type": "string", "enum": ["html", "csv", "json"], "description": "Export format (default: json)" },
                        "checklist": { "type": "array", "items": { "type": "string" }, "description": "Specific checks to run (default: all)" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "generate_clash_report" => GenerateClashReport(doc, args),
                "compare_element_counts" => CompareElementCounts(doc, args),
                "get_link_coordination_status" => GetLinkCoordinationStatus(doc),
                "get_scope_box_summary" => GetScopeBoxSummary(doc),
                "compare_model_versions" => CompareModelVersions(doc, args),
                "generate_qc_report" => GenerateQcReport(doc, args),
                _ => UnknownTool(functionName)
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

        private string CompareModelVersions(Document doc, Dictionary<string, object> args)
        {
            var action = GetArg(args, "action", "snapshot");
            var snapshotPath = GetArg<string>(args, "snapshot_path");

            if (action == "snapshot")
            {
                var snapshot = new Dictionary<string, object>();
                var allElems = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds();
                snapshot["element_count"] = allElems.Count;
                snapshot["timestamp"] = DateTime.Now.ToString("o");
                snapshot["document"] = doc.Title;

                var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
                var categories = elements.Where(e => e.Category != null).GroupBy(e => e.Category.Name)
                    .ToDictionary(g => g.Key, g => g.Count());
                snapshot["categories"] = categories;

                var path = string.IsNullOrEmpty(snapshotPath)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"model_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.json")
                    : snapshotPath;

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                path = Path.GetFullPath(path);
                var pathErr = ValidateOutputPath(path);
                if (pathErr != null) return JsonError(pathErr);
                File.WriteAllText(path, json);
                return JsonSerializer.Serialize(new { action = "snapshot", file_path = path, element_count = allElems.Count }, JsonOpts);
            }

            if (string.IsNullOrEmpty(snapshotPath) || !File.Exists(snapshotPath))
                return JsonError("snapshot_path required for compare action.");

            var prevJson = File.ReadAllText(snapshotPath);
            var prev = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prevJson);
            int prevCount = prev != null && prev.ContainsKey("element_count") ? prev["element_count"].GetInt32() : 0;
            int currentCount = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();

            return JsonSerializer.Serialize(new
            {
                action = "compare",
                previous_count = prevCount,
                current_count = currentCount,
                difference = currentCount - prevCount,
                snapshot_file = snapshotPath
            }, JsonOpts);
        }

        private string GenerateQcReport(Document doc, Dictionary<string, object> args)
        {
            var format = GetArg(args, "export_format", "json");

            var checks = new List<object>();

            int warnings = doc.GetWarnings().Count;
            checks.Add(new { check = "Model Warnings", status = warnings < 500 ? "PASS" : "FAIL", value = warnings, limit = 500 });

            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var familyInstances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
            var usedFamilyIds = new HashSet<ElementId>(familyInstances.Select(fi => fi.Symbol?.Family?.Id).Where(id => id != null && id != ElementId.InvalidElementId));
            int unusedFamilies = families.Count(f => !usedFamilyIds.Contains(f.Id));
            int totalFamilies = families.Count;
            double unusedPct = totalFamilies > 0 ? (double)unusedFamilies / totalFamilies * 100 : 0;
            checks.Add(new { check = "Unused Families", status = unusedPct < 30 ? "PASS" : "WARNING", value = $"{unusedFamilies} ({unusedPct:F0}%)", limit = "30%" });

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate).ToList();
            var unnamedViews = views.Count(v => v.Name.StartsWith("Copy of") || v.Name.Contains("{"));
            checks.Add(new { check = "View Naming", status = unnamedViews == 0 ? "PASS" : "WARNING", value = $"{unnamedViews} poorly named views" });

            int passCount = 0;
            foreach (var c in checks)
            {
                var status = ((dynamic)c).status;
                if (status == "PASS") passCount++;
            }
            int score = checks.Count > 0 ? passCount * 100 / checks.Count : 0;

            if (format == "html")
            {
                var html = "<html><body><h1>QC Report</h1><table border='1'><tr><th>Check</th><th>Status</th><th>Value</th></tr>";
                foreach (dynamic c in checks)
                    html += $"<tr><td>{c.check}</td><td>{c.status}</td><td>{c.value}</td></tr>";
                html += $"</table><p>Score: {score}/100</p></body></html>";
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"QC_Report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
                var qcPathErr = ValidateOutputPath(path);
                if (qcPathErr != null) return JsonError(qcPathErr);
                File.WriteAllText(path, html);
                return JsonSerializer.Serialize(new { format = "html", file_path = path, score }, JsonOpts);
            }

            return JsonSerializer.Serialize(new { score, check_count = checks.Count, checks }, JsonOpts);
        }
    }
}
