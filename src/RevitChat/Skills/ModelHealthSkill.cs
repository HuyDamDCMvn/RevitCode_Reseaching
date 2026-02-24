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
    public class ModelHealthSkill : BaseRevitSkill
    {
        protected override string SkillName => "ModelHealth";
        protected override string SkillDescription => "Check model health: warnings, statistics, imported CAD, in-place families, unused families";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_model_warnings", "get_warning_elements", "get_model_statistics",
            "find_imported_cad", "find_inplace_families", "find_unused_families",
            "audit_model_standards"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_model_warnings",
                "Get all warnings in the model grouped by description/severity.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "limit": { "type": "integer", "description": "Max warning groups to return (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_warning_elements",
                "Get elements causing a specific type of warning.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "warning_text": { "type": "string", "description": "Partial match of warning description" },
                        "limit": { "type": "integer", "description": "Max elements to return (default 50)" }
                    },
                    "required": ["warning_text"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_model_statistics",
                "Get comprehensive model statistics and element summary: counts by top categories (Walls, Ducts, Pipes, Equipment, etc.), family counts, view counts, link counts, warnings. Use this tool when the user asks to 'summarize elements', 'summary of model', 'what is in the model', or 'model overview'.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("find_imported_cad",
                "Find all imported/linked CAD files (DWG, DXF, DGN) in the model.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("find_inplace_families",
                "Find all In-Place families (Model In Place) -- a common anti-pattern that affects performance.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("find_unused_families",
                "Find loaded families that have no instances placed in the model.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("audit_model_standards",
                "Comprehensive model health audit: warnings count, unused families, imported CAD, in-place families, naming issues. Returns a health score 0-100.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "include": { "type": "array", "items": { "type": "string" }, "description": "Sections to include: warnings, families, cad, naming. Default: all." }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_model_warnings" => GetModelWarnings(doc, args),
                "get_warning_elements" => GetWarningElements(doc, args),
                "get_model_statistics" => GetModelStatistics(doc),
                "find_imported_cad" => FindImportedCad(doc),
                "find_inplace_families" => FindInPlaceFamilies(doc),
                "find_unused_families" => FindUnusedFamilies(doc, args),
                "audit_model_standards" => AuditModelStandards(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetModelWarnings(Document doc, Dictionary<string, object> args)
        {
            int limit = GetArg(args, "limit", 50);

            var warnings = doc.GetWarnings();
            var grouped = warnings
                .GroupBy(w => w.GetDescriptionText())
                .OrderByDescending(g => g.Count())
                .Take(limit)
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    sample_element_ids = g.First().GetFailingElements()
                        .Take(3).Select(id => id.Value).ToList()
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_warnings = warnings.Count,
                unique_types = grouped.Count,
                warnings = grouped
            }, JsonOpts);
        }

        private string GetWarningElements(Document doc, Dictionary<string, object> args)
        {
            var text = GetArg<string>(args, "warning_text");
            int limit = GetArg(args, "limit", 50);

            if (string.IsNullOrEmpty(text)) return JsonError("warning_text required.");

            var warnings = doc.GetWarnings()
                .Where(w => w.GetDescriptionText().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var elementIds = new HashSet<long>();
            var items = new List<object>();

            foreach (var w in warnings)
            {
                if (items.Count >= limit) break;
                foreach (var eid in w.GetFailingElements())
                {
                    if (!elementIds.Add(eid.Value)) continue;
                    if (items.Count >= limit) break;

                    var elem = doc.GetElement(eid);
                    items.Add(new
                    {
                        id = eid.Value,
                        name = elem?.Name ?? "-",
                        category = elem?.Category?.Name ?? "-",
                        warning = w.GetDescriptionText()
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                warning_filter = text,
                matching_warnings = warnings.Count,
                element_count = items.Count,
                elements = items
            }, JsonOpts);
        }

        private string GetModelStatistics(Document doc)
        {
            int totalElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();

            var topCategories = new[]
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves,
                BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_GenericModel
            };

            var byCat = topCategories
                .Select(bic =>
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat == null) return null;
                    int cnt = new FilteredElementCollector(doc)
                        .OfCategory(bic).WhereElementIsNotElementType().GetElementCount();
                    return cnt > 0 ? new { category = cat.Name, count = cnt } : null;
                })
                .Where(x => x != null)
                .OrderByDescending(x => x.count)
                .ToList();

            int familyCount = new FilteredElementCollector(doc)
                .OfClass(typeof(Family)).GetElementCount();
            int familySymbolCount = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).GetElementCount();

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>().ToList();
            int templateCount = allViews.Count(v => v.IsTemplate);
            int viewCount = allViews.Count - templateCount;

            int sheetCount = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).GetElementCount();
            int linkCount = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).GetElementCount();
            int groupCount = new FilteredElementCollector(doc)
                .OfClass(typeof(Group)).GetElementCount();
            int warningCount = doc.GetWarnings().Count;

            return JsonSerializer.Serialize(new
            {
                total_elements = totalElements,
                families_loaded = familyCount,
                family_types = familySymbolCount,
                views = viewCount,
                view_templates = templateCount,
                sheets = sheetCount,
                revit_links = linkCount,
                groups = groupCount,
                warnings = warningCount,
                top_categories = byCat
            }, JsonOpts);
        }

        private string FindImportedCad(Document doc)
        {
            var imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            var cadLinks = new FilteredElementCollector(doc)
                .OfClass(typeof(CADLinkType))
                .Cast<CADLinkType>()
                .ToList();

            int linkedCount = 0;
            var importItems = imports.Select(i =>
            {
                var isLinked = i.IsLinked;
                if (isLinked) linkedCount++;
                var typeId = i.GetTypeId();
                var typeName = doc.GetElement(typeId)?.Name ?? "-";
                var ownerView = i.OwnerViewId != ElementId.InvalidElementId
                    ? (doc.GetElement(i.OwnerViewId) as View)?.Name : "3D/Model";

                return new
                {
                    id = i.Id.Value,
                    type_name = typeName,
                    is_linked = isLinked,
                    mode = isLinked ? "linked" : "imported",
                    owner_view = ownerView ?? "-"
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_cad = importItems.Count,
                imported_count = importItems.Count - linkedCount,
                linked_count = linkedCount,
                cad_link_types = cadLinks.Count,
                items = importItems
            }, JsonOpts);
        }

        private string FindInPlaceFamilies(Document doc)
        {
            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => f.IsInPlace)
                .ToList();

            var inPlaceFamilyIds = new HashSet<long>(families.Select(f => f.Id.Value));

            var instanceCountByFamily = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Family != null && inPlaceFamilyIds.Contains(fi.Symbol.Family.Id.Value))
                .GroupBy(fi => fi.Symbol.Family.Id.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var items = families.Select(f => new
            {
                family_id = f.Id.Value,
                name = f.Name,
                category = f.FamilyCategory?.Name ?? "-",
                instance_count = instanceCountByFamily.TryGetValue(f.Id.Value, out var c) ? c : 0
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                inplace_family_count = items.Count,
                message = items.Count > 0
                    ? "In-Place families hurt model performance. Consider converting to loadable families."
                    : "No In-Place families found. Good!",
                families = items
            }, JsonOpts);
        }

        private string FindUnusedFamilies(Document doc, Dictionary<string, object> args)
        {
            int limit = GetArg(args, "limit", 100);

            var usedSymbolIds = new HashSet<long>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol != null)
                    .Select(fi => fi.Symbol.Id.Value));

            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => !f.IsInPlace)
                .ToList();

            var unused = new List<object>();

            foreach (var fam in families)
            {
                if (unused.Count >= limit) break;

                var symbolIds = fam.GetFamilySymbolIds();
                bool hasInstance = symbolIds.Any(sid => usedSymbolIds.Contains(sid.Value));

                if (!hasInstance)
                {
                    unused.Add(new
                    {
                        family_id = fam.Id.Value,
                        name = fam.Name,
                        category = fam.FamilyCategory?.Name ?? "-",
                        type_count = symbolIds.Count
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                total_families = families.Count,
                unused_count = unused.Count,
                message = unused.Count > 0 ? "These families can be purged to reduce model size." : "All families are in use.",
                unused_families = unused
            }, JsonOpts);
        }

        private string AuditModelStandards(Document doc, Dictionary<string, object> args)
        {
            var sections = GetArgStringArray(args, "include") ?? new List<string>();
            bool all = sections.Count == 0;

            int totalScore = 0;
            int checkCount = 0;
            var report = new Dictionary<string, object>();

            if (all || sections.Contains("warnings", StringComparer.OrdinalIgnoreCase))
            {
                var warnings = doc.GetWarnings();
                int warnCount = warnings?.Count ?? 0;
                int warnScore = warnCount == 0 ? 100 : warnCount < 10 ? 80 : warnCount < 50 ? 60 : warnCount < 200 ? 40 : 20;
                report["warnings"] = new { count = warnCount, score = warnScore, status = warnScore >= 60 ? "OK" : "NEEDS_ATTENTION" };
                totalScore += warnScore;
                checkCount++;
            }

            if (all || sections.Contains("families", StringComparer.OrdinalIgnoreCase))
            {
                var allFamilies = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
                int inPlace = allFamilies.Count(f => f.IsInPlace);
                int famScore = inPlace == 0 ? 100 : inPlace < 5 ? 70 : inPlace < 20 ? 40 : 20;
                report["families"] = new { total = allFamilies.Count, in_place = inPlace, score = famScore };
                totalScore += famScore;
                checkCount++;
            }

            if (all || sections.Contains("cad", StringComparer.OrdinalIgnoreCase))
            {
                int imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).GetElementCount();
                int cadScore = imports == 0 ? 100 : imports < 5 ? 70 : imports < 20 ? 40 : 20;
                report["imported_cad"] = new { count = imports, score = cadScore, status = cadScore >= 70 ? "OK" : "NEEDS_ATTENTION" };
                totalScore += cadScore;
                checkCount++;
            }

            if (all || sections.Contains("naming", StringComparer.OrdinalIgnoreCase))
            {
                var views = new FilteredElementCollector(doc).OfClass(typeof(View))
                    .Cast<View>().Where(v => !v.IsTemplate).ToList();
                int copyOfCount = views.Count(v => v.Name.StartsWith("Copy of") || v.Name.Contains("- Copy"));
                int namingScore = copyOfCount == 0 ? 100 : copyOfCount < 5 ? 70 : 40;
                report["naming"] = new { total_views = views.Count, copy_of_views = copyOfCount, score = namingScore };
                totalScore += namingScore;
                checkCount++;
            }

            int overallScore = checkCount > 0 ? totalScore / checkCount : 0;
            string grade = overallScore >= 90 ? "A" : overallScore >= 75 ? "B" : overallScore >= 60 ? "C" : overallScore >= 40 ? "D" : "F";

            return JsonSerializer.Serialize(new
            {
                overall_score = overallScore,
                grade,
                checks = report
            }, JsonOpts);
        }
    }
}
