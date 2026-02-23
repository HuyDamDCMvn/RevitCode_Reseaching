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
    public class RevisionMarkupSkill : BaseRevitSkill
    {
        protected override string SkillName => "RevisionMarkup";
        protected override string SkillDescription => "Manage revisions, revision clouds, and revision schedules";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_revisions", "get_revision_clouds", "add_revision",
            "get_sheets_by_revision", "get_revision_schedule"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_revisions",
                "List all revisions in the document with date, description, and numbering.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_revision_clouds",
                "Find revision clouds, optionally filtered by revision, sheet, or view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "revision_id": { "type": "integer", "description": "Optional: filter by Revision ID" },
                        "sheet_id": { "type": "integer", "description": "Optional: filter by Sheet ID" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("add_revision",
                "Add a new revision to the document. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "date": { "type": "string", "description": "Revision date string (e.g. '2025-01-15')" },
                        "description": { "type": "string", "description": "Revision description" },
                        "issued_to": { "type": "string", "description": "Optional: issued to" },
                        "issued_by": { "type": "string", "description": "Optional: issued by" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["description"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_sheets_by_revision",
                "Find all sheets that have a specific revision.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "revision_id": { "type": "integer", "description": "Revision ID to search for" }
                    },
                    "required": ["revision_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_revision_schedule",
                "Get a comprehensive revision matrix: which sheets have which revisions.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "limit": { "type": "integer", "description": "Max sheets to include (default 50)" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_revisions" => GetRevisions(doc),
                "get_revision_clouds" => GetRevisionClouds(doc, args),
                "add_revision" => AddRevision(doc, args),
                "get_sheets_by_revision" => GetSheetsByRevision(doc, args),
                "get_revision_schedule" => GetRevisionSchedule(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetRevisions(Document doc)
        {
            var revisions = Revision.GetAllRevisionIds(doc)
                .Select(id => doc.GetElement(id) as Revision)
                .Where(r => r != null)
                .ToList();

            var items = revisions.Select((r, idx) => new
            {
                id = r.Id.Value,
                sequence = idx + 1,
                date = r.RevisionDate ?? "-",
                description = r.Description ?? "-",
                issued_to = r.IssuedTo ?? "-",
                issued_by = r.IssuedBy ?? "-",
                is_issued = r.Issued,
                revision_number = r.RevisionNumber
            }).ToList();

            return JsonSerializer.Serialize(new { revision_count = items.Count, revisions = items }, JsonOpts);
        }

        private string GetRevisionClouds(Document doc, Dictionary<string, object> args)
        {
            long revId = GetArg<long>(args, "revision_id");
            long sheetId = GetArg<long>(args, "sheet_id");
            int limit = GetArg(args, "limit", 50);

            var clouds = new FilteredElementCollector(doc)
                .OfClass(typeof(RevisionCloud))
                .Cast<RevisionCloud>()
                .ToList();

            if (revId > 0)
                clouds = clouds.Where(c => c.RevisionId.Value == revId).ToList();

            if (sheetId > 0)
            {
                var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
                if (sheet != null)
                {
                    var viewportIds = sheet.GetAllViewports();
                    var viewIdsOnSheet = new HashSet<long>();
                    viewIdsOnSheet.Add(sheetId);
                    foreach (var vpId in viewportIds)
                    {
                        var vp = doc.GetElement(vpId) as Viewport;
                        if (vp != null)
                            viewIdsOnSheet.Add(vp.ViewId.Value);
                    }
                    clouds = clouds.Where(c => viewIdsOnSheet.Contains(c.OwnerViewId.Value)).ToList();
                }
                else
                {
                    clouds = clouds.Where(c => c.OwnerViewId.Value == sheetId).ToList();
                }
            }

            var items = clouds.Take(limit).Select(c =>
            {
                var rev = doc.GetElement(c.RevisionId) as Revision;
                var ownerView = doc.GetElement(c.OwnerViewId) as View;

                return new
                {
                    cloud_id = c.Id.Value,
                    revision_id = c.RevisionId.Value,
                    revision_desc = rev?.Description ?? "-",
                    revision_date = rev?.RevisionDate ?? "-",
                    owner_view = ownerView?.Name ?? "-",
                    owner_view_type = ownerView?.ViewType.ToString() ?? "-"
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_clouds = clouds.Count,
                returned = items.Count,
                clouds = items
            }, JsonOpts);
        }

        private string AddRevision(Document doc, Dictionary<string, object> args)
        {
            var description = GetArg<string>(args, "description");
            var date = GetArg<string>(args, "date");
            var issuedTo = GetArg<string>(args, "issued_to");
            var issuedBy = GetArg<string>(args, "issued_by");
            bool dryRun = GetArg(args, "dry_run", false);

            if (string.IsNullOrEmpty(description))
                return JsonError("description required.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_create = true,
                    description,
                    date,
                    issued_to = issuedTo,
                    issued_by = issuedBy
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Add Revision"))
            {
                trans.Start();
                try
                {
                    var rev = Revision.Create(doc);
                    rev.Description = description;
                    if (!string.IsNullOrEmpty(date)) rev.RevisionDate = date;
                    if (!string.IsNullOrEmpty(issuedTo)) rev.IssuedTo = issuedTo;
                    if (!string.IsNullOrEmpty(issuedBy)) rev.IssuedBy = issuedBy;
                    trans.Commit();

                    return JsonSerializer.Serialize(new
                    {
                        created = true,
                        id = rev.Id.Value,
                        description = rev.Description,
                        date = rev.RevisionDate,
                        revision_number = rev.RevisionNumber
                    }, JsonOpts);
                }
                catch (Exception ex)
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    return JsonError($"AddRevision failed: {ex.Message}");
                }
            }
        }

        private string GetSheetsByRevision(Document doc, Dictionary<string, object> args)
        {
            long revId = GetArg<long>(args, "revision_id");
            if (revId <= 0) return JsonError("revision_id required.");

            var revElemId = new ElementId(revId);
            var rev = doc.GetElement(revElemId) as Revision;
            if (rev == null) return JsonError($"Revision {revId} not found.");

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s =>
                {
                    try { return s.GetAllRevisionIds().Contains(revElemId); }
                    catch { return false; }
                })
                .ToList();

            var items = sheets.Select(s => new
            {
                id = s.Id.Value,
                number = s.SheetNumber,
                name = s.Name
            }).OrderBy(s => s.number).ToList();

            return JsonSerializer.Serialize(new
            {
                revision = rev.Description,
                revision_date = rev.RevisionDate,
                sheet_count = items.Count,
                sheets = items
            }, JsonOpts);
        }

        private string GetRevisionSchedule(Document doc, Dictionary<string, object> args)
        {
            int limit = GetArg(args, "limit", 50);

            var revisionIds = Revision.GetAllRevisionIds(doc);
            var revisions = revisionIds
                .Select(id => doc.GetElement(id) as Revision)
                .Where(r => r != null)
                .ToList();

            var allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber)
                .ToList();
            var sheets = allSheets.Take(limit).ToList();

            var matrix = sheets.Select(s =>
            {
                var sheetRevIds = new HashSet<long>();
                try
                {
                    foreach (var rid in s.GetAllRevisionIds())
                        sheetRevIds.Add(rid.Value);
                }
                catch { }

                var revStatus = revisions.Select(r => new
                {
                    revision_id = r.Id.Value,
                    revision_desc = r.Description ?? "-",
                    has_revision = sheetRevIds.Contains(r.Id.Value)
                }).ToList();

                return new
                {
                    sheet_id = s.Id.Value,
                    sheet_number = s.SheetNumber,
                    sheet_name = s.Name,
                    revision_count = sheetRevIds.Count,
                    revisions = revStatus
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_revisions = revisions.Count,
                total_sheets = allSheets.Count,
                schedule = matrix
            }, JsonOpts);
        }
    }
}
