using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class NamingAuditSkill : BaseRevitSkill
    {
        protected override string SkillName => "NamingAudit";
        protected override string SkillDescription => "Audit naming conventions for views, sheets, levels, families, and worksets";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "audit_view_names", "audit_sheet_numbers", "audit_level_names",
            "audit_family_names", "audit_workset_names"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("audit_view_names",
                "Audit view names against a regex naming convention pattern. Returns compliant and non-compliant views.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Regex pattern for valid view names (e.g. '^[A-Z]{2}-.*-L\\d+$'). If omitted, lists all view names for review." },
                        "view_type": { "type": "string", "description": "Optional: filter by ViewType (FloorPlan, CeilingPlan, Section, Elevation, ThreeD)" },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("audit_sheet_numbers",
                "Audit sheet numbers against a naming convention pattern.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Regex pattern for valid sheet numbers (e.g. '^[A-Z]\\d{3}$'). If omitted, lists all sheet numbers." },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("audit_level_names",
                "Audit level names against a naming convention pattern.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Regex pattern for valid level names (e.g. '^Level \\d+$'). If omitted, lists all level names." }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("audit_family_names",
                "Audit loaded family names against a naming convention pattern.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Regex pattern for valid family names. If omitted, lists all family names." },
                        "category": { "type": "string", "description": "Optional: filter by category name" },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("audit_workset_names",
                "Audit user workset names against a naming convention pattern.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "pattern": { "type": "string", "description": "Regex pattern for valid workset names. If omitted, lists all workset names." }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "audit_view_names" => AuditViewNames(doc, args),
                "audit_sheet_numbers" => AuditSheetNumbers(doc, args),
                "audit_level_names" => AuditLevelNames(doc, args),
                "audit_family_names" => AuditFamilyNames(doc, args),
                "audit_workset_names" => AuditWorksetNames(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string AuditViewNames(Document doc, Dictionary<string, object> args)
        {
            var pattern = GetArg<string>(args, "pattern");
            var viewTypeStr = GetArg<string>(args, "view_type");
            int limit = GetArg(args, "limit", 100);

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            if (!string.IsNullOrEmpty(viewTypeStr) && Enum.TryParse<ViewType>(viewTypeStr, true, out var vt))
                views = views.Where(v => v.ViewType == vt).ToList();

            return AuditNames(views, v => v.Name, v => new
            {
                id = v.Id.Value,
                name = v.Name,
                view_type = v.ViewType.ToString()
            }, pattern, limit);
        }

        private string AuditSheetNumbers(Document doc, Dictionary<string, object> args)
        {
            var pattern = GetArg<string>(args, "pattern");
            int limit = GetArg(args, "limit", 100);

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .ToList();

            return AuditNames(sheets, s => s.SheetNumber, s => new
            {
                id = s.Id.Value,
                number = s.SheetNumber,
                name = s.Name
            }, pattern, limit);
        }

        private string AuditLevelNames(Document doc, Dictionary<string, object> args)
        {
            var pattern = GetArg<string>(args, "pattern");

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            return AuditNames(levels, l => l.Name, l => new
            {
                id = l.Id.Value,
                name = l.Name,
                elevation_ft = Math.Round(l.Elevation, 4)
            }, pattern, 200);
        }

        private string AuditFamilyNames(Document doc, Dictionary<string, object> args)
        {
            var pattern = GetArg<string>(args, "pattern");
            var catFilter = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 100);

            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => !f.IsInPlace)
                .ToList();

            if (!string.IsNullOrEmpty(catFilter))
                families = families.Where(f =>
                    f.FamilyCategory?.Name?.IndexOf(catFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            return AuditNames(families, f => f.Name, f => new
            {
                id = f.Id.Value,
                name = f.Name,
                category = f.FamilyCategory?.Name ?? "-"
            }, pattern, limit);
        }

        private string AuditWorksetNames(Document doc, Dictionary<string, object> args)
        {
            var pattern = GetArg<string>(args, "pattern");

            if (!doc.IsWorkshared)
                return JsonSerializer.Serialize(new { workshared = false, message = "Document is not workshared." }, JsonOpts);

            var worksets = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToList();

            return AuditNames(worksets, w => w.Name, w => new
            {
                id = w.Id.IntegerValue,
                name = w.Name,
                is_open = w.IsOpen
            }, pattern, 100);
        }

        private string AuditNames<T>(List<T> items, Func<T, string> nameSelector,
            Func<T, object> itemProjector, string pattern, int limit)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                var allItems = items.Take(limit).Select(itemProjector).ToList();
                return JsonSerializer.Serialize(new
                {
                    total = items.Count,
                    pattern = (string)null,
                    message = "No pattern provided. Listing all names for review.",
                    items = allItems
                }, JsonOpts);
            }

            Regex regex;
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch (Exception ex) { return JsonError($"Invalid regex pattern: {ex.Message}"); }

            var compliant = new List<object>();
            var nonCompliant = new List<object>();

            foreach (var item in items)
            {
                var name = nameSelector(item);
                var proj = itemProjector(item);

                if (regex.IsMatch(name ?? ""))
                    compliant.Add(proj);
                else
                    nonCompliant.Add(proj);
            }

            return JsonSerializer.Serialize(new
            {
                total = items.Count,
                pattern,
                compliant_count = compliant.Count,
                non_compliant_count = nonCompliant.Count,
                compliance_rate = items.Count > 0 ? $"{compliant.Count * 100 / items.Count}%" : "N/A",
                non_compliant = nonCompliant.Take(limit).ToList(),
                compliant = compliant.Take(20).ToList()
            }, JsonOpts);
        }
    }
}
