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
    public class SheetManagementSkill : BaseRevitSkill
    {
        protected override string SkillName => "SheetManagement";
        protected override string SkillDescription => "Create sheets, place views on sheets, manage viewports";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_sheets_summary", "create_sheet", "place_view_on_sheet",
            "get_sheet_viewports", "remove_viewport", "manage_sheet_collection"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_sheets_summary",
                "Get a summary of all sheets with their viewports and revision status.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter": { "type": "string", "description": "Optional: filter by sheet number or name (partial match)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("create_sheet",
                "Create a new sheet. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "sheet_number": { "type": "string", "description": "Sheet number (e.g. 'A101')" },
                        "sheet_name": { "type": "string", "description": "Sheet name/title" },
                        "titleblock_type_id": { "type": "integer", "description": "Optional: title block FamilySymbol ID. If omitted, uses first available." },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["sheet_number", "sheet_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("place_view_on_sheet",
                "Place a view onto a sheet at a specific position. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "sheet_id": { "type": "integer", "description": "Sheet ElementId" },
                        "view_id": { "type": "integer", "description": "View ElementId to place" },
                        "x": { "type": "number", "description": "X position on sheet in feet (default: center)" },
                        "y": { "type": "number", "description": "Y position on sheet in feet (default: center)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["sheet_id", "view_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_sheet_viewports",
                "List all viewports placed on a specific sheet.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "sheet_id": { "type": "integer", "description": "Sheet ElementId" }
                    },
                    "required": ["sheet_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("remove_viewport",
                "Remove a viewport from a sheet. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "viewport_id": { "type": "integer", "description": "Viewport ElementId to remove" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["viewport_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("manage_sheet_collection",
                "Create or manage sheet collections/sets.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "action": { "type": "string", "enum": ["list", "create"], "description": "List or create" },
                        "name": { "type": "string", "description": "Collection name (for create)" },
                        "sheet_ids": { "type": "array", "items": { "type": "integer" }, "description": "Sheet IDs (for create)" }
                    },
                    "required": ["action"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_sheets_summary" => GetSheetsSummary(doc, args),
                "create_sheet" => CreateSheet(doc, args),
                "place_view_on_sheet" => PlaceViewOnSheet(doc, args),
                "get_sheet_viewports" => GetSheetViewports(doc, args),
                "remove_viewport" => RemoveViewport(doc, args),
                "manage_sheet_collection" => ManageSheetCollection(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetSheetsSummary(Document doc, Dictionary<string, object> args)
        {
            var filter = GetArg<string>(args, "filter");
            int limit = GetArg(args, "limit", 50);

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .ToList();

            if (!string.IsNullOrEmpty(filter))
            {
                sheets = sheets.Where(s =>
                    (s.SheetNumber?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (s.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                ).ToList();
            }

            var items = sheets.OrderBy(s => s.SheetNumber).Take(limit).Select(s =>
            {
                var vpIds = s.GetAllViewports();
                var viewports = vpIds.Select(vpId =>
                {
                    var vp = doc.GetElement(vpId) as Viewport;
                    if (vp == null) return null;
                    var view = doc.GetElement(vp.ViewId) as View;
                    return new
                    {
                        viewport_id = vpId.Value,
                        view_id = vp.ViewId.Value,
                        view_name = view?.Name ?? "-",
                        view_type = view?.ViewType.ToString() ?? "-"
                    };
                }).Where(v => v != null).ToList();

                return new
                {
                    id = s.Id.Value,
                    number = s.SheetNumber,
                    name = s.Name,
                    viewport_count = viewports.Count,
                    viewports
                };
            }).ToList();

            return JsonSerializer.Serialize(new { total = sheets.Count, returned = items.Count, sheets = items }, JsonOpts);
        }

        private string CreateSheet(Document doc, Dictionary<string, object> args)
        {
            var number = GetArg<string>(args, "sheet_number");
            var name = GetArg<string>(args, "sheet_name");
            long tbTypeId = GetArg<long>(args, "titleblock_type_id");
            bool dryRun = GetArg(args, "dry_run", false);

            if (string.IsNullOrEmpty(number) || string.IsNullOrEmpty(name))
                return JsonError("sheet_number and sheet_name are required.");

            ElementId titleBlockId;
            if (tbTypeId > 0)
            {
                titleBlockId = new ElementId(tbTypeId);
            }
            else
            {
                var tb = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol))
                    .FirstOrDefault();
                if (tb == null)
                    return JsonError("No titleblock family found in the project. Load a titleblock family first, or provide a titleblock_type_id.");
                titleBlockId = tb.Id;
            }

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => s.SheetNumber != null && s.SheetNumber.Equals(number, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return JsonError($"Sheet number '{number}' already exists.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_create = true,
                    sheet_number = number,
                    sheet_name = name,
                    titleblock_type_id = titleBlockId.Value
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Create Sheet"))
            {
                trans.Start();
                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.SheetNumber = number;
                sheet.Name = name;
                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    created = true,
                    id = sheet.Id.Value,
                    number = sheet.SheetNumber,
                    name = sheet.Name
                }, JsonOpts);
            }
        }

        private string PlaceViewOnSheet(Document doc, Dictionary<string, object> args)
        {
            long sheetId = GetArg<long>(args, "sheet_id");
            long viewId = GetArg<long>(args, "view_id");
            double x = GetArg(args, "x", 1.0);
            double y = GetArg(args, "y", 1.0);
            bool dryRun = GetArg(args, "dry_run", false);

            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null) return JsonError($"Sheet {sheetId} not found.");

            var view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null) return JsonError($"View {viewId} not found.");

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                return JsonError("Cannot add this view to the sheet. It may already be placed on another sheet.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_place = true,
                    sheet = sheet.SheetNumber,
                    view_name = view.Name,
                    position = new { x, y }
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Place View on Sheet"))
            {
                trans.Start();
                var point = new XYZ(x, y, 0);
                var vp = Viewport.Create(doc, sheet.Id, view.Id, point);
                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    placed = true,
                    viewport_id = vp.Id.Value,
                    sheet = sheet.SheetNumber,
                    view_name = view.Name,
                    position = new { x, y }
                }, JsonOpts);
            }
        }

        private string GetSheetViewports(Document doc, Dictionary<string, object> args)
        {
            long sheetId = GetArg<long>(args, "sheet_id");
            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null) return JsonError($"Sheet {sheetId} not found.");

            var vpIds = sheet.GetAllViewports();
            var viewports = vpIds.Select(vpId =>
            {
                var vp = doc.GetElement(vpId) as Viewport;
                if (vp == null) return null;
                var view = doc.GetElement(vp.ViewId) as View;
                var center = vp.GetBoxCenter();
                return new
                {
                    viewport_id = vpId.Value,
                    view_id = vp.ViewId.Value,
                    view_name = view?.Name ?? "-",
                    view_type = view?.ViewType.ToString() ?? "-",
                    center_x = Math.Round(center.X, 4),
                    center_y = Math.Round(center.Y, 4)
                };
            }).Where(v => v != null).ToList();

            return JsonSerializer.Serialize(new
            {
                sheet_number = sheet.SheetNumber,
                sheet_name = sheet.Name,
                viewport_count = viewports.Count,
                viewports
            }, JsonOpts);
        }

        private string RemoveViewport(Document doc, Dictionary<string, object> args)
        {
            long vpId = GetArg<long>(args, "viewport_id");
            bool dryRun = GetArg(args, "dry_run", false);
            var vp = doc.GetElement(new ElementId(vpId)) as Viewport;
            if (vp == null) return JsonError($"Viewport {vpId} not found.");

            var viewName = (doc.GetElement(vp.ViewId) as View)?.Name ?? "-";

            if (dryRun)
            {
                return JsonSerializer.Serialize(new { dry_run = true, would_remove = true, viewport_id = vpId, view_name = viewName }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Remove Viewport"))
            {
                trans.Start();
                doc.Delete(vp.Id);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new { removed = true, viewport_id = vpId, view_name = viewName }, JsonOpts);
        }

        private string ManageSheetCollection(Document doc, Dictionary<string, object> args)
        {
            var action = GetArg<string>(args, "action", "list");

            if (action == "list")
            {
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Select(s => new { id = s.Id.Value, number = s.SheetNumber, name = s.Name })
                    .OrderBy(s => s.number).ToList();
                return JsonSerializer.Serialize(new { sheet_count = sheets.Count, sheets }, JsonOpts);
            }

            var name = GetArg<string>(args, "name");
            var sheetIds = GetArgLongArray(args, "sheet_ids");
            if (string.IsNullOrEmpty(name)) return JsonError("name required for create.");

            return JsonSerializer.Serialize(new
            {
                action = "create",
                message = $"Sheet collection '{name}' created conceptually. Revit doesn't have a native sheet collection API. " +
                          $"Use view filters or a custom parameter 'Sheet Set' to organize {sheetIds?.Count ?? 0} sheets.",
                sheet_count = sheetIds?.Count ?? 0
            }, JsonOpts);
        }
    }
}
