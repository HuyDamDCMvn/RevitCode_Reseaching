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
    public class ScheduleSkill : BaseRevitSkill
    {
        protected override string SkillName => "Schedule";
        protected override string SkillDescription => "Create schedules from categories with selected fields";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "create_schedule", "get_schedule_fields"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("create_schedule",
                "Create a schedule view for a category with selected fields. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name (e.g. 'Ducts', 'Pipes')" },
                        "schedule_name": { "type": "string", "description": "Optional schedule name. Default: '<Category> Schedule'." },
                        "field_names": { "type": "array", "items": { "type": "string" }, "description": "Field names to add (e.g. 'System Name', 'Type', 'Level')" },
                        "if_exists": { "type": "string", "enum": ["error", "append"], "description": "If name exists, error or append suffix. Default: error." },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["category", "field_names"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_schedule_fields",
                "List available schedule fields for a category (useful before creating a schedule).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name (e.g. 'Ducts', 'Pipes')" }
                    },
                    "required": ["category"]
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
                "create_schedule" => CreateSchedule(doc, args),
                "get_schedule_fields" => GetScheduleFields(doc, args),
                _ => JsonError($"ScheduleSkill: unknown tool '{functionName}'")
            };
        }

        private string CreateSchedule(Document doc, Dictionary<string, object> args)
        {
            var categoryName = GetArg<string>(args, "category");
            var scheduleName = GetArg<string>(args, "schedule_name");
            var ifExists = GetArg(args, "if_exists", "error")?.ToLower() ?? "error";
            var fieldNames = GetArgStringArray(args, "field_names");
            bool dryRun = GetArg(args, "dry_run", false);

            if (string.IsNullOrWhiteSpace(categoryName)) return JsonError("category required.");
            if (fieldNames == null || fieldNames.Count == 0) return JsonError("field_names required.");

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (!bic.HasValue) return JsonError($"Category '{categoryName}' not found.");

            var cat = Category.GetCategory(doc, bic.Value);
            if (cat == null) return JsonError($"Category '{categoryName}' is not schedulable.");

            var baseName = string.IsNullOrWhiteSpace(scheduleName)
                ? $"{cat.Name} Schedule"
                : scheduleName.Trim();

            var existing = FindScheduleByName(doc, baseName);
            if (existing != null)
            {
                if (ifExists == "append")
                    baseName = GetUniqueScheduleName(doc, baseName);
                else
                    return JsonError($"Schedule '{baseName}' already exists.");
            }

            var addedFields = new List<string>();
            var missingFields = new List<string>();

            using (var trans = new Transaction(doc, "AI: Create Schedule"))
            {
                trans.Start();
                ViewSchedule schedule;
                try
                {
                    schedule = ViewSchedule.CreateSchedule(doc, cat.Id);
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return JsonError($"Failed to create schedule for category '{cat.Name}': {ex.Message}");
                }

                schedule.Name = baseName;

                var def = schedule.Definition;
                var available = def.GetSchedulableFields().ToList();

                foreach (var name in fieldNames.Where(n => !string.IsNullOrWhiteSpace(n)))
                {
                    var field = FindSchedulableField(doc, available, name);
                    if (field == null)
                    {
                        missingFields.Add(name);
                        continue;
                    }

                    def.AddField(field);
                    addedFields.Add(name);
                }

                if (def.GetFieldCount() == 0)
                {
                    trans.RollBack();
                    return JsonSerializer.Serialize(new
                    {
                        error = "No valid fields were added.",
                        missing_fields = missingFields.Distinct().Take(20)
                    }, JsonOpts);
                }

                if (dryRun)
                {
                    trans.RollBack();
                    return JsonSerializer.Serialize(new
                    {
                        dry_run = true,
                        would_create = true,
                        schedule_name = baseName,
                        added_fields = addedFields,
                        missing_fields = missingFields.Distinct().Take(20)
                    }, JsonOpts);
                }

                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    created = true,
                    schedule_name = schedule.Name,
                    schedule_id = schedule.Id.Value,
                    added_fields = addedFields,
                    missing_fields = missingFields.Distinct().Take(20)
                }, JsonOpts);
            }
        }

        private string GetScheduleFields(Document doc, Dictionary<string, object> args)
        {
            var categoryName = GetArg<string>(args, "category");
            if (string.IsNullOrWhiteSpace(categoryName)) return JsonError("category required.");

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (!bic.HasValue) return JsonError($"Category '{categoryName}' not found.");

            var cat = Category.GetCategory(doc, bic.Value);
            if (cat == null) return JsonError($"Category '{categoryName}' is not schedulable.");

            var fields = new List<string>();

            using (var trans = new Transaction(doc, "AI: Preview Schedule Fields"))
            {
                try
                {
                    trans.Start();
                    var schedule = ViewSchedule.CreateSchedule(doc, cat.Id);
                    var def = schedule.Definition;
                    fields = def.GetSchedulableFields()
                        .Select(f => f.GetName(doc))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n)
                        .ToList();
                    trans.RollBack();
                }
                catch (Exception ex)
                {
                    try { trans.RollBack(); } catch { }
                    return JsonError($"Failed to collect schedule fields: {ex.Message}");
                }
            }

            return JsonSerializer.Serialize(new
            {
                category = cat.Name,
                field_count = fields.Count,
                fields
            }, JsonOpts);
        }

        private static ViewSchedule FindScheduleByName(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetUniqueScheduleName(Document doc, string baseName)
        {
            var existing = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Select(s => s.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName)) return baseName;

            for (int i = 2; i < 1000; i++)
            {
                var candidate = $"{baseName} ({i})";
                if (!existing.Contains(candidate)) return candidate;
            }

            return $"{baseName} ({DateTime.Now:HHmmss})";
        }

        private static SchedulableField FindSchedulableField(Document doc, List<SchedulableField> fields, string name)
        {
            var trimmed = name.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;

            var normalized = NormalizeFieldName(trimmed);
            return fields.FirstOrDefault(f =>
            {
                var fn = f.GetName(doc);
                if (string.Equals(fn, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
                return NormalizeFieldName(fn) == normalized;
            });
        }

        private static string NormalizeFieldName(string name)
        {
            var chars = name.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }
    }
}
