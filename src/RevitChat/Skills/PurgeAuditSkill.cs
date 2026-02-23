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
    public class PurgeAuditSkill : BaseRevitSkill
    {
        protected override string SkillName => "PurgeAudit";
        protected override string SkillDescription => "Find purgeable elements, duplicate types, unresolved references, design options";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_purgeable_elements", "find_duplicate_types", "find_unresolved_references",
            "get_design_options", "audit_detail_levels"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_purgeable_elements",
                "Find element types (family symbols) with no instances -- candidates for purging.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Optional: filter by category" },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("find_duplicate_types",
                "Find family types with identical names but different IDs (potential duplicates from linked/imported sources).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Optional: filter by category" },
                        "limit": { "type": "integer", "description": "Max groups to return (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("find_unresolved_references",
                "Find broken or unresolved references: unloaded links, missing families, broken file paths.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_design_options",
                "List all design option sets and their options with element counts.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("audit_detail_levels",
                "Audit views for their detail level settings (Coarse/Medium/Fine).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "view_type": { "type": "string", "description": "Optional: filter by ViewType" }
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
                "get_purgeable_elements" => GetPurgeableElements(doc, args),
                "find_duplicate_types" => FindDuplicateTypes(doc, args),
                "find_unresolved_references" => FindUnresolvedReferences(doc),
                "get_design_options" => GetDesignOptions(doc),
                "audit_detail_levels" => AuditDetailLevels(doc, args),
                _ => JsonError($"PurgeAuditSkill: unknown tool '{functionName}'")
            };
        }

        private string GetPurgeableElements(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 100);

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol));

            if (!string.IsNullOrEmpty(catFilter))
            {
                var bic = ResolveCategoryFilter(doc, catFilter);
                if (bic.HasValue) collector = collector.OfCategory(bic.Value);
            }

            var symbols = collector.Cast<FamilySymbol>().ToList();

            var usedSymbolIds = new HashSet<long>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol != null)
                    .Select(fi => fi.Symbol.Id.Value));

            var purgeable = new List<object>();

            foreach (var sym in symbols)
            {
                if (purgeable.Count >= limit) break;

                if (!usedSymbolIds.Contains(sym.Id.Value))
                {
                    purgeable.Add(new
                    {
                        type_id = sym.Id.Value,
                        family = sym.Family?.Name ?? "-",
                        type = sym.Name,
                        category = sym.Category?.Name ?? "-"
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                total_types_checked = symbols.Count,
                purgeable_count = purgeable.Count,
                message = purgeable.Count > 0 ? "These types have 0 instances and can be purged." : "No purgeable types found.",
                purgeable_types = purgeable
            }, JsonOpts);
        }

        private string FindDuplicateTypes(Document doc, Dictionary<string, object> args)
        {
            var catFilter = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 50);

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsElementType();

            if (!string.IsNullOrEmpty(catFilter))
            {
                var bic = ResolveCategoryFilter(doc, catFilter);
                if (bic.HasValue) collector = collector.OfCategory(bic.Value);
            }

            var types = collector.ToList();

            var duplicates = types
                .GroupBy(t => new { Name = t.Name, Cat = t.Category?.Name ?? "-" })
                .Where(g => g.Count() > 1)
                .Take(limit)
                .Select(g => new
                {
                    name = g.Key.Name,
                    category = g.Key.Cat,
                    count = g.Count(),
                    type_ids = g.Select(t => t.Id.Value).ToList()
                })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                total_types = types.Count,
                duplicate_groups = duplicates.Count,
                message = duplicates.Count > 0 ? "These type names appear multiple times." : "No duplicate type names found.",
                duplicates
            }, JsonOpts);
        }

        private string FindUnresolvedReferences(Document doc)
        {
            var issues = new List<object>();

            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            foreach (var lt in linkTypes)
            {
                try
                {
                    var extRef = ExternalFileUtils.GetExternalFileReference(doc, lt.Id);
                    var status = extRef?.GetLinkedFileStatus();

                    if (status != LinkedFileStatus.Loaded)
                    {
                        issues.Add(new
                        {
                            type = "RevitLink",
                            name = lt.Name,
                            id = lt.Id.Value,
                            status = status?.ToString() ?? "Unknown"
                        });
                    }
                }
                catch { }
            }

            var cadTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(CADLinkType))
                .Cast<CADLinkType>()
                .ToList();

            foreach (var ct in cadTypes)
            {
                try
                {
                    var extRef = ExternalFileUtils.GetExternalFileReference(doc, ct.Id);
                    var status = extRef?.GetLinkedFileStatus();

                    if (status != LinkedFileStatus.Loaded)
                    {
                        issues.Add(new
                        {
                            type = "CADLink",
                            name = ct.Name,
                            id = ct.Id.Value,
                            status = status?.ToString() ?? "Unknown"
                        });
                    }
                }
                catch { }
            }

            return JsonSerializer.Serialize(new
            {
                revit_links_total = linkTypes.Count,
                cad_links_total = cadTypes.Count,
                issue_count = issues.Count,
                message = issues.Count > 0 ? "Unresolved or unloaded references found." : "All references are resolved.",
                issues
            }, JsonOpts);
        }

        private string GetDesignOptions(Document doc)
        {
            var designOptions = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .ToList();

            if (designOptions.Count == 0)
                return JsonSerializer.Serialize(new { message = "No design options in this model.", design_options = Array.Empty<object>() }, JsonOpts);

            var items = designOptions.Select(opt =>
            {
                var elemCount = new FilteredElementCollector(doc)
                    .WherePasses(new ElementDesignOptionFilter(opt.Id))
                    .GetElementCount();

                return new
                {
                    id = opt.Id.Value,
                    name = opt.Name,
                    is_primary = opt.IsPrimary,
                    element_count = elemCount
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                design_option_count = items.Count,
                design_options = items
            }, JsonOpts);
        }

        private string AuditDetailLevels(Document doc, Dictionary<string, object> args)
        {
            var viewTypeStr = GetArg<string>(args, "view_type");

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .ToList();

            if (!string.IsNullOrEmpty(viewTypeStr) && Enum.TryParse<ViewType>(viewTypeStr, true, out var vt))
                views = views.Where(v => v.ViewType == vt).ToList();

            var grouped = views
                .GroupBy(v => v.DetailLevel.ToString())
                .Select(g => new
                {
                    detail_level = g.Key,
                    count = g.Count(),
                    sample_views = g.Take(5).Select(v => new { id = v.Id.Value, name = v.Name, view_type = v.ViewType.ToString() }).ToList()
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_views = views.Count,
                by_detail_level = grouped
            }, JsonOpts);
        }
    }
}
