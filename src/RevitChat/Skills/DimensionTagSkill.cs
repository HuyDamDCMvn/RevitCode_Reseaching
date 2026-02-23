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
    public class DimensionTagSkill : BaseRevitSkill
    {
        protected override string SkillName => "DimensionTag";
        protected override string SkillDescription => "Tag elements, find untagged elements, add text notes";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "tag_elements", "get_untagged_elements", "tag_all_in_view", "add_text_note"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("tag_elements",
                "Tag specific elements with IndependentTag in the active view. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to tag" },
                        "tag_type_id": { "type": "integer", "description": "Optional: tag FamilySymbol ID. If omitted, uses default tag." },
                        "add_leader": { "type": "boolean", "description": "Add leader line (default: false)" },
                        "tag_orientation": { "type": "string", "enum": ["horizontal", "vertical"], "description": "Tag orientation (default: horizontal)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_untagged_elements",
                "Find elements in the active view that do not have tags.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name to check (e.g. 'Doors', 'Windows', 'Walls')" },
                        "limit": { "type": "integer", "description": "Max results (default 100)" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("tag_all_in_view",
                "Tag all untagged elements of a given category in the active view. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name (e.g. 'Doors', 'Rooms', 'Windows')" },
                        "add_leader": { "type": "boolean", "description": "Add leader line (default: false)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("add_text_note",
                "Add a text note in the active view at the specified location. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "text": { "type": "string", "description": "Text content" },
                        "x": { "type": "number", "description": "X coordinate in feet" },
                        "y": { "type": "number", "description": "Y coordinate in feet" },
                        "text_type_id": { "type": "integer", "description": "Optional: TextNoteType ID" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["text", "x", "y"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            var view = doc.ActiveView;
            if (view == null) return JsonError("No active view.");

            return functionName switch
            {
                "tag_elements" => TagElements(doc, view, args),
                "get_untagged_elements" => GetUntaggedElements(doc, view, args),
                "tag_all_in_view" => TagAllInView(doc, view, args),
                "add_text_note" => AddTextNote(doc, view, args),
                _ => UnknownTool(functionName)
            };
        }

        private string TagElements(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            long tagTypeId = GetArg<long>(args, "tag_type_id");
            bool addLeader = GetArg(args, "add_leader", false);
            var orientStr = GetArg(args, "tag_orientation", "horizontal");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var orient = orientStr == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;

            if (dryRun)
            {
                int taggable = 0, failed = 0;
                var previewErrors = new List<string>();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; previewErrors.Add($"Element {id} not found."); continue; }

                    var loc = elem.Location;
                    if (loc is LocationPoint || loc is LocationCurve)
                        taggable++;
                    else
                    {
                        failed++;
                        previewErrors.Add($"Element {id}: no location.");
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_tag = taggable,
                    failed,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Tag Elements"))
            {
                trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found."); continue; }

                    var loc = elem.Location;
                    XYZ point;
                    if (loc is LocationPoint lp) point = lp.Point;
                    else if (loc is LocationCurve lc) point = lc.Curve.Evaluate(0.5, true);
                    else { errors.Add($"Element {id}: no location."); continue; }

                    try
                    {
                        var refElem = new Reference(elem);
                        var tag = IndependentTag.Create(doc, view.Id, refElem, addLeader, TagMode.TM_ADDBY_CATEGORY, orient, point);

                        if (tagTypeId > 0)
                            tag.ChangeTypeId(new ElementId(tagTypeId));

                        success++;
                    }
                    catch (Exception ex) { errors.Add($"Element {id}: {ex.Message}"); }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { success, errors = errors.Take(10) }, JsonOpts);
        }

        private static HashSet<long> CollectTaggedElementIds(Document doc, View view)
        {
            var taggedIds = new HashSet<long>();
            var tags = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>();

            foreach (var tag in tags)
            {
                try
                {
                    foreach (var e in tag.GetTaggedLocalElements())
                        taggedIds.Add(e.Id.Value);
                }
                catch { }
            }
            return taggedIds;
        }

        private string GetUntaggedElements(Document doc, View view, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 100);

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc, view.Id)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var taggedIds = CollectTaggedElementIds(doc, view);

            var untagged = elements
                .Where(e => !taggedIds.Contains(e.Id.Value))
                .Take(limit)
                .Select(e => new
                {
                    id = e.Id.Value,
                    name = e.Name,
                    type = (doc.GetElement(e.GetTypeId()) as ElementType)?.Name ?? "-"
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                category = catName,
                total_in_view = elements.Count,
                untagged_count = untagged.Count,
                untagged = untagged
            }, JsonOpts);
        }

        private string TagAllInView(Document doc, View view, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            bool addLeader = GetArg(args, "add_leader", false);
            bool dryRun = GetArg(args, "dry_run", false);

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc, view.Id)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var taggedIds = CollectTaggedElementIds(doc, view);
            var untaggedElements = elements.Where(e => !taggedIds.Contains(e.Id.Value)).ToList();
            int success = 0;

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    category = catName,
                    total_in_view = elements.Count,
                    already_tagged = taggedIds.Count,
                    would_tag = untaggedElements.Count
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, $"AI: Tag All {catName}"))
            {
                trans.Start();

                foreach (var elem in untaggedElements)
                {
                    var loc = elem.Location;
                    XYZ point;
                    if (loc is LocationPoint lp) point = lp.Point;
                    else if (loc is LocationCurve lc) point = lc.Curve.Evaluate(0.5, true);
                    else continue;

                    try
                    {
                        IndependentTag.Create(doc, view.Id, new Reference(elem), addLeader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
                        success++;
                    }
                    catch { }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                category = catName,
                total_in_view = elements.Count,
                already_tagged = taggedIds.Count,
                newly_tagged = success
            }, JsonOpts);
        }

        private string AddTextNote(Document doc, View view, Dictionary<string, object> args)
        {
            var text = GetArg<string>(args, "text");
            double x = GetArg(args, "x", 0.0);
            double y = GetArg(args, "y", 0.0);
            long textTypeId = GetArg<long>(args, "text_type_id");
            bool dryRun = GetArg(args, "dry_run", false);

            if (string.IsNullOrEmpty(text)) return JsonError("text is required.");

            ElementId typeId;
            if (textTypeId > 0)
            {
                typeId = new ElementId(textTypeId);
            }
            else
            {
                var defaultType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .FirstOrDefault();
                typeId = defaultType?.Id ?? ElementId.InvalidElementId;
            }

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_create = true,
                    text,
                    location = new { x, y },
                    text_type_id = typeId.Value
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Add Text Note"))
            {
                trans.Start();
                var point = new XYZ(x, y, 0);
                var note = TextNote.Create(doc, view.Id, point, text, typeId);
                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    created = true,
                    text_note_id = note.Id.Value,
                    text,
                    location = new { x, y }
                }, JsonOpts);
            }
        }
    }
}
