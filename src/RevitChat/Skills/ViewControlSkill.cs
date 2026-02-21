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
    public class ViewControlSkill : IRevitSkill
    {
        public string Name => "ViewControl";
        public string Description => "Hide, unhide, isolate, and reset visibility of elements and categories in the active view";

        private static readonly HashSet<string> HandledTools = new()
        {
            "hide_elements", "unhide_elements",
            "isolate_elements", "isolate_category",
            "hide_category", "unhide_category",
            "reset_view_isolation", "get_hidden_elements"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("hide_elements",
                "Hide specific elements in the active view. Elements remain in the model but become invisible.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to hide" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("unhide_elements",
                "Unhide (reveal) previously hidden elements in the active view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to unhide" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("isolate_elements",
                "Temporarily isolate elements in the active view — only these elements will be visible, everything else is hidden. Use reset_view_isolation to undo.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to isolate (show only these)" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("isolate_category",
                "Temporarily isolate an entire category in the active view — only elements of this category will be visible.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name (e.g. 'Walls', 'Ducts', 'Pipes')" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("hide_category",
                "Permanently hide a category in the active view (via Visibility/Graphics override).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name to hide" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("unhide_category",
                "Unhide a previously hidden category in the active view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name to unhide" }
                    },
                    "required": ["category"]
                }
                """)),

            ChatTool.CreateFunctionTool("reset_view_isolation",
                "Reset all temporary hide/isolate in the active view, restoring normal visibility. No parameters needed.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_hidden_elements",
                "Get a summary of what is currently hidden or isolated in the active view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Optional: filter by category name" }
                    },
                    "required": []
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;
            var view = doc.ActiveView;

            if (view == null) return JsonError("No active view.");

            return functionName switch
            {
                "hide_elements" => HideElements(doc, view, args),
                "unhide_elements" => UnhideElements(doc, view, args),
                "isolate_elements" => IsolateElements(doc, view, args),
                "isolate_category" => IsolateCategory(doc, view, args),
                "hide_category" => HideCategory(doc, view, args),
                "unhide_category" => UnhideCategory(doc, view, args),
                "reset_view_isolation" => ResetIsolation(doc, view),
                "get_hidden_elements" => GetHiddenElements(doc, view, args),
                _ => JsonError($"ViewControlSkill: unknown tool '{functionName}'")
            };
        }

        private string HideElements(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var elemIds = ResolveValidIds(doc, view, ids, out var notFound);
            if (elemIds.Count == 0) return JsonError("No valid, visible elements found for the given IDs.");

            var canHide = elemIds.Where(id => view.CanCategoryBeHidden(doc.GetElement(id).Category?.Id ?? ElementId.InvalidElementId)).ToList();
            if (canHide.Count == 0) return JsonError("None of the elements can be hidden in this view.");

            using (var trans = new Transaction(doc, "AI: Hide Elements"))
            {
                trans.Start();
                view.HideElements(canHide);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                hidden = canHide.Count,
                not_found = notFound.Count > 0 ? notFound.Take(10).ToList() : null,
                message = $"Hidden {canHide.Count} element(s) in '{view.Name}'."
            }, JsonOpts);
        }

        private string UnhideElements(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var elemIds = ids
                .Select(id => new ElementId(id))
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            if (elemIds.Count == 0) return JsonError("No valid elements found.");

            using (var trans = new Transaction(doc, "AI: Unhide Elements"))
            {
                trans.Start();
                view.UnhideElements(elemIds);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                unhidden = elemIds.Count,
                message = $"Unhidden {elemIds.Count} element(s) in '{view.Name}'."
            }, JsonOpts);
        }

        private string IsolateElements(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var elemIds = ids
                .Select(id => new ElementId(id))
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            if (elemIds.Count == 0) return JsonError("No valid elements found.");

            using (var trans = new Transaction(doc, "AI: Isolate Elements"))
            {
                trans.Start();
                view.IsolateElementsTemporary(elemIds);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                isolated = elemIds.Count,
                view_name = view.Name,
                message = $"Isolated {elemIds.Count} element(s) in '{view.Name}'. Use reset_view_isolation to restore."
            }, JsonOpts);
        }

        private string IsolateCategory(Document doc, View view, Dictionary<string, object> args)
        {
            var categoryName = GetArg<string>(args, "category");
            if (string.IsNullOrEmpty(categoryName)) return JsonError("category required.");

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (!bic.HasValue) return JsonError($"Category '{categoryName}' not found in the model.");

            var categoryId = new ElementId(bic.Value);

            var elemIds = new FilteredElementCollector(doc, view.Id)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToElementIds()
                .ToList();

            if (elemIds.Count == 0) return JsonError($"No elements of category '{categoryName}' found in the active view.");

            using (var trans = new Transaction(doc, "AI: Isolate Category"))
            {
                trans.Start();
                view.IsolateCategoriesTemporary(new List<ElementId> { categoryId });
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                category = categoryName,
                element_count = elemIds.Count,
                view_name = view.Name,
                message = $"Isolated category '{categoryName}' ({elemIds.Count} elements) in '{view.Name}'. Use reset_view_isolation to restore."
            }, JsonOpts);
        }

        private string HideCategory(Document doc, View view, Dictionary<string, object> args)
        {
            var categoryName = GetArg<string>(args, "category");
            if (string.IsNullOrEmpty(categoryName)) return JsonError("category required.");

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (!bic.HasValue) return JsonError($"Category '{categoryName}' not found.");

            var categoryId = new ElementId(bic.Value);
            if (!view.CanCategoryBeHidden(categoryId))
                return JsonError($"Category '{categoryName}' cannot be hidden in this view type.");

            using (var trans = new Transaction(doc, "AI: Hide Category"))
            {
                trans.Start();
                view.SetCategoryHidden(categoryId, true);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                category = categoryName,
                hidden = true,
                view_name = view.Name,
                message = $"Category '{categoryName}' is now hidden in '{view.Name}'."
            }, JsonOpts);
        }

        private string UnhideCategory(Document doc, View view, Dictionary<string, object> args)
        {
            var categoryName = GetArg<string>(args, "category");
            if (string.IsNullOrEmpty(categoryName)) return JsonError("category required.");

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (!bic.HasValue) return JsonError($"Category '{categoryName}' not found.");

            var categoryId = new ElementId(bic.Value);

            using (var trans = new Transaction(doc, "AI: Unhide Category"))
            {
                trans.Start();
                view.SetCategoryHidden(categoryId, false);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                category = categoryName,
                hidden = false,
                view_name = view.Name,
                message = $"Category '{categoryName}' is now visible in '{view.Name}'."
            }, JsonOpts);
        }

        private string ResetIsolation(Document doc, View view)
        {
            using (var trans = new Transaction(doc, "AI: Reset View Isolation"))
            {
                trans.Start();
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                view_name = view.Name,
                message = $"Temporary hide/isolate reset in '{view.Name}'. All elements are now visible."
            }, JsonOpts);
        }

        private string GetHiddenElements(Document doc, View view, Dictionary<string, object> args)
        {
            var categoryFilter = GetArg<string>(args, "category");

            var collector = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType();

            var bic = ResolveCategoryFilter(doc, categoryFilter);
            if (bic.HasValue)
                collector = collector.OfCategory(bic.Value);

            var hiddenByCategory = new Dictionary<string, int>();
            int totalHidden = 0;

            foreach (var elem in collector)
            {
                if (elem.IsHidden(view))
                {
                    totalHidden++;
                    var catName = elem.Category?.Name ?? "Unknown";
                    hiddenByCategory.TryGetValue(catName, out int count);
                    hiddenByCategory[catName] = count + 1;
                }
            }

            var hiddenCategories = new List<string>();
            foreach (Category cat in doc.Settings.Categories)
            {
                try
                {
                    if (view.CanCategoryBeHidden(cat.Id) && view.GetCategoryHidden(cat.Id))
                        hiddenCategories.Add(cat.Name);
                }
                catch { }
            }

            var isIsolated = view.IsTemporaryHideIsolateActive();

            return JsonSerializer.Serialize(new
            {
                view_name = view.Name,
                is_temp_isolated = isIsolated,
                hidden_element_count = totalHidden,
                hidden_by_category = hiddenByCategory.OrderByDescending(kv => kv.Value)
                    .Take(20)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                hidden_categories_via_vg = hiddenCategories.Take(30).ToList()
            }, JsonOpts);
        }

        private static List<ElementId> ResolveValidIds(Document doc, View view, List<long> ids, out List<long> notFound)
        {
            notFound = new List<long>();
            var valid = new List<ElementId>();
            foreach (var id in ids)
            {
                var elemId = new ElementId(id);
                var elem = doc.GetElement(elemId);
                if (elem == null)
                    notFound.Add(id);
                else
                    valid.Add(elemId);
            }
            return valid;
        }
    }
}
