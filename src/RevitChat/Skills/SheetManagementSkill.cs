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
    public class SheetManagementSkill : IRevitSkill
    {
        public string Name => "SheetManagement";
        public string Description => "Create sheets, place views on sheets, manage viewports";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_sheets_summary", "create_sheet", "place_view_on_sheet",
            "get_sheet_viewports", "remove_viewport"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
                        "titleblock_type_id": { "type": "integer", "description": "Optional: title block FamilySymbol ID. If omitted, uses first available." }
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
                        "y": { "type": "number", "description": "Y position on sheet in feet (default: center)" }
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
                        "viewport_id": { "type": "integer", "description": "Viewport ElementId to remove" }
                    },
                    "required": ["viewport_id"]
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "get_sheets_summary" => GetSheetsSummary(doc, args),
                "create_sheet" => CreateSheet(doc, args),
                "place_view_on_sheet" => PlaceViewOnSheet(doc, args),
                "get_sheet_viewports" => GetSheetViewports(doc, args),
                "remove_viewport" => RemoveViewport(doc, args),
                _ => JsonError($"SheetManagementSkill: unknown tool '{functionName}'")
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

            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null) return JsonError($"Sheet {sheetId} not found.");

            var view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null) return JsonError($"View {viewId} not found.");

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                return JsonError("Cannot add this view to the sheet. It may already be placed on another sheet.");

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
            var vp = doc.GetElement(new ElementId(vpId)) as Viewport;
            if (vp == null) return JsonError($"Viewport {vpId} not found.");

            var viewName = (doc.GetElement(vp.ViewId) as View)?.Name ?? "-";

            using (var trans = new Transaction(doc, "AI: Remove Viewport"))
            {
                trans.Start();
                doc.Delete(vp.Id);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new { removed = true, viewport_id = vpId, view_name = viewName }, JsonOpts);
        }
    }
}
