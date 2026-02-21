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
        public string Description => "Control element visibility, color overrides, transparency, zoom, and selection in the active view";

        private static readonly HashSet<string> HandledTools = new()
        {
            "hide_elements", "unhide_elements",
            "isolate_elements", "isolate_category",
            "hide_category", "unhide_category",
            "reset_view_isolation", "get_hidden_elements",
            "override_element_color", "override_category_color",
            "reset_element_overrides", "set_element_transparency",
            "zoom_to_elements", "get_current_selection",
            "isolate_by_level", "hide_by_level", "override_color_by_level",
            "isolate_by_filter", "override_color_by_filter"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        private static readonly Dictionary<string, (byte R, byte G, byte B)> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = (255, 0, 0),
            ["green"] = (0, 128, 0),
            ["blue"] = (0, 0, 255),
            ["yellow"] = (255, 255, 0),
            ["orange"] = (255, 165, 0),
            ["purple"] = (128, 0, 128),
            ["cyan"] = (0, 255, 255),
            ["magenta"] = (255, 0, 255),
            ["black"] = (0, 0, 0),
            ["white"] = (255, 255, 255),
            ["gray"] = (128, 128, 128),
            ["grey"] = (128, 128, 128),
            ["pink"] = (255, 192, 203),
            ["brown"] = (139, 69, 19),
            ["lime"] = (0, 255, 0),
            ["navy"] = (0, 0, 128),
            ["teal"] = (0, 128, 128),
            ["maroon"] = (128, 0, 0),
            ["olive"] = (128, 128, 0),
            ["coral"] = (255, 127, 80),
            ["gold"] = (255, 215, 0),
        };

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
                """)),

            ChatTool.CreateFunctionTool("override_element_color",
                "Override the display color of elements in the active view. Affects lines and surfaces. Supports color names (Red, Blue, Green, Yellow, Orange, Purple, Cyan, Magenta, Pink, etc.) or RGB values.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to color" },
                        "color": { "type": "string", "description": "Color name (Red, Blue, Green, Yellow, Orange, Purple, Cyan, Pink, etc.) or RGB hex (#FF0000)" }
                    },
                    "required": ["element_ids", "color"]
                }
                """)),

            ChatTool.CreateFunctionTool("override_category_color",
                "Override the display color of an entire category in the active view. Supports color names or RGB hex.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name (e.g. 'Walls', 'Ducts')" },
                        "color": { "type": "string", "description": "Color name (Red, Blue, Green, etc.) or RGB hex (#FF0000)" }
                    },
                    "required": ["category", "color"]
                }
                """)),

            ChatTool.CreateFunctionTool("reset_element_overrides",
                "Remove all graphics overrides (color, transparency, line style) from elements in the active view, restoring their default appearance.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to reset overrides for" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("set_element_transparency",
                "Set the transparency of elements in the active view (0 = opaque, 100 = fully transparent).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to set transparency" },
                        "transparency": { "type": "integer", "description": "Transparency value 0-100 (0=opaque, 100=transparent)" }
                    },
                    "required": ["element_ids", "transparency"]
                }
                """)),

            ChatTool.CreateFunctionTool("zoom_to_elements",
                "Zoom/pan the active view to show specific elements so the user can see them.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to zoom to" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_current_selection",
                "Get the elements currently selected by the user in Revit. Returns element IDs, categories, and names.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("isolate_by_level",
                "Isolate ALL elements on a specific level in the active view. Everything not on that level becomes hidden. Use reset_view_isolation to undo.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_name": { "type": "string", "description": "Level name (e.g. 'Level 1', 'Level 01', 'Ground Floor')" }
                    },
                    "required": ["level_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("hide_by_level",
                "Hide ALL elements on a specific level in the active view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_name": { "type": "string", "description": "Level name to hide elements for" }
                    },
                    "required": ["level_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("override_color_by_level",
                "Override the display color of ALL elements on a specific level in the active view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_name": { "type": "string", "description": "Level name" },
                        "color": { "type": "string", "description": "Color name (Red, Blue, Green, etc.) or hex (#FF0000)" }
                    },
                    "required": ["level_name", "color"]
                }
                """)),

            ChatTool.CreateFunctionTool("isolate_by_filter",
                "Isolate elements in the active view by combining level, category, and/or parameter filters. Only matching elements remain visible.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_name": { "type": "string", "description": "Optional: filter by level name" },
                        "category": { "type": "string", "description": "Optional: filter by category (e.g. 'Walls', 'Ducts')" },
                        "param_name": { "type": "string", "description": "Optional: parameter name to filter by" },
                        "param_value": { "type": "string", "description": "Optional: parameter value to match (requires param_name)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("override_color_by_filter",
                "Override color of elements matching level, category, and/or parameter filters in the active view.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level_name": { "type": "string", "description": "Optional: filter by level name" },
                        "category": { "type": "string", "description": "Optional: filter by category" },
                        "param_name": { "type": "string", "description": "Optional: parameter name to filter by" },
                        "param_value": { "type": "string", "description": "Optional: parameter value to match" },
                        "color": { "type": "string", "description": "Color name or hex" }
                    },
                    "required": ["color"]
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
                "override_element_color" => OverrideElementColor(doc, view, args),
                "override_category_color" => OverrideCategoryColor(doc, view, args),
                "reset_element_overrides" => ResetElementOverrides(doc, view, args),
                "set_element_transparency" => SetElementTransparency(doc, view, args),
                "zoom_to_elements" => ZoomToElements(uidoc, args),
                "get_current_selection" => GetCurrentSelection(uidoc),
                "isolate_by_level" => IsolateByLevel(doc, view, args),
                "hide_by_level" => HideByLevel(doc, view, args),
                "override_color_by_level" => OverrideColorByLevel(doc, view, args),
                "isolate_by_filter" => IsolateByFilter(doc, view, args),
                "override_color_by_filter" => OverrideColorByFilter(doc, view, args),
                _ => JsonError($"ViewControlSkill: unknown tool '{functionName}'")
            };
        }

        #region Hide / Unhide / Isolate

        private string HideElements(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var elemIds = ResolveValidIds(doc, view, ids, out var notFound);
            if (elemIds.Count == 0) return JsonError("No valid, visible elements found for the given IDs.");

            var canHide = elemIds.Where(id =>
            {
                var elem = doc.GetElement(id);
                if (elem?.Category == null) return false;
                return view.CanCategoryBeHidden(elem.Category.Id);
            }).ToList();
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

        #endregion

        #region Color / Graphics Overrides

        private string OverrideElementColor(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var colorStr = GetArg<string>(args, "color");

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(colorStr)) return JsonError("color required (e.g. 'Red', '#FF0000').");

            var color = ParseColor(colorStr);
            if (color == null) return JsonError($"Unrecognized color '{colorStr}'. Use a name (Red, Blue, Green...) or hex (#RRGGBB).");

            var solidFillId = GetSolidFillPatternId(doc);

            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            if (solidFillId != ElementId.InvalidElementId)
                ogs.SetSurfaceForegroundPatternId(solidFillId);
            ogs.SetCutLineColor(color);
            ogs.SetCutForegroundPatternColor(color);

            int applied = 0;
            var notFound = new List<long>();

            using (var trans = new Transaction(doc, "AI: Override Element Color"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elemId = new ElementId(id);
                    if (doc.GetElement(elemId) == null) { notFound.Add(id); continue; }
                    view.SetElementOverrides(elemId, ogs);
                    applied++;
                }
                if (applied > 0) trans.Commit();
                else trans.RollBack();
            }

            if (applied == 0) return JsonError("No valid elements found to override.");

            return JsonSerializer.Serialize(new
            {
                overridden = applied,
                color = colorStr,
                rgb = new { r = color.Red, g = color.Green, b = color.Blue },
                view_name = view.Name,
                not_found = notFound.Count > 0 ? notFound.Take(10).ToList() : null,
                message = $"Applied {colorStr} color override to {applied} element(s) in '{view.Name}'."
            }, JsonOpts);
        }

        private string OverrideCategoryColor(Document doc, View view, Dictionary<string, object> args)
        {
            var categoryName = GetArg<string>(args, "category");
            var colorStr = GetArg<string>(args, "color");

            if (string.IsNullOrEmpty(categoryName)) return JsonError("category required.");
            if (string.IsNullOrEmpty(colorStr)) return JsonError("color required.");

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (!bic.HasValue) return JsonError($"Category '{categoryName}' not found.");

            var color = ParseColor(colorStr);
            if (color == null) return JsonError($"Unrecognized color '{colorStr}'.");

            var solidFillId = GetSolidFillPatternId(doc);
            var categoryId = new ElementId(bic.Value);

            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            if (solidFillId != ElementId.InvalidElementId)
                ogs.SetSurfaceForegroundPatternId(solidFillId);
            ogs.SetCutLineColor(color);
            ogs.SetCutForegroundPatternColor(color);

            using (var trans = new Transaction(doc, "AI: Override Category Color"))
            {
                trans.Start();
                view.SetCategoryOverrides(categoryId, ogs);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                category = categoryName,
                color = colorStr,
                rgb = new { r = color.Red, g = color.Green, b = color.Blue },
                view_name = view.Name,
                message = $"Applied {colorStr} color override to category '{categoryName}' in '{view.Name}'."
            }, JsonOpts);
        }

        private string ResetElementOverrides(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var blank = new OverrideGraphicSettings();
            int reset = 0;

            using (var trans = new Transaction(doc, "AI: Reset Element Overrides"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elemId = new ElementId(id);
                    if (doc.GetElement(elemId) == null) continue;
                    view.SetElementOverrides(elemId, blank);
                    reset++;
                }
                if (reset > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                reset,
                view_name = view.Name,
                message = $"Reset graphics overrides for {reset} element(s) in '{view.Name}'."
            }, JsonOpts);
        }

        private string SetElementTransparency(Document doc, View view, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var transparency = GetArg(args, "transparency", 50);
            transparency = Math.Max(0, Math.Min(100, transparency));

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            int applied = 0;

            using (var trans = new Transaction(doc, "AI: Set Element Transparency"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elemId = new ElementId(id);
                    if (doc.GetElement(elemId) == null) continue;

                    var ogs = view.GetElementOverrides(elemId);
                    ogs.SetSurfaceTransparency(transparency);
                    view.SetElementOverrides(elemId, ogs);
                    applied++;
                }
                if (applied > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                applied,
                transparency,
                view_name = view.Name,
                message = $"Set transparency to {transparency}% for {applied} element(s) in '{view.Name}'."
            }, JsonOpts);
        }

        #endregion

        #region Zoom / Selection

        private string ZoomToElements(UIDocument uidoc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var doc = uidoc.Document;
            var elemIds = ids
                .Select(id => new ElementId(id))
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            if (elemIds.Count == 0) return JsonError("No valid elements found.");

            uidoc.ShowElements(elemIds);

            return JsonSerializer.Serialize(new
            {
                zoomed_to = elemIds.Count,
                message = $"Zoomed to {elemIds.Count} element(s)."
            }, JsonOpts);
        }

        private string GetCurrentSelection(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds == null || selectedIds.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    count = 0,
                    message = "No elements are currently selected."
                }, JsonOpts);

            var allElements = selectedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .ToList();

            var byCategory = allElements
                .GroupBy(e => e.Category?.Name ?? "Unknown")
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count)
                .ToList();

            var detailList = allElements
                .Take(50)
                .Select(e => new
                {
                    id = e.Id.Value,
                    category = e.Category?.Name ?? "Unknown",
                    family = GetFamilyName(doc, e),
                    type = GetElementTypeName(doc, e),
                    name = e.Name ?? "-"
                })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                count = selectedIds.Count,
                message = $"{selectedIds.Count} element(s) currently selected.",
                summary_by_category = byCategory,
                elements = detailList,
                truncated = allElements.Count > 50
            }, JsonOpts);
        }

        #endregion

        #region By-Level / By-Filter Operations

        private string IsolateByLevel(Document doc, View view, Dictionary<string, object> args)
        {
            var levelName = GetArg<string>(args, "level_name");
            if (string.IsNullOrEmpty(levelName)) return JsonError("level_name required.");

            var elemIds = CollectElementsByLevel(doc, view, levelName);
            if (elemIds == null) return JsonError($"Level '{levelName}' not found in the model.");
            if (elemIds.Count == 0) return JsonError($"No elements found on level '{levelName}' in the active view.");

            using (var trans = new Transaction(doc, "AI: Isolate by Level"))
            {
                trans.Start();
                view.IsolateElementsTemporary(elemIds);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                isolated = elemIds.Count,
                level = levelName,
                view_name = view.Name,
                message = $"Isolated {elemIds.Count} element(s) on level '{levelName}' in '{view.Name}'. Use reset_view_isolation to restore."
            }, JsonOpts);
        }

        private string HideByLevel(Document doc, View view, Dictionary<string, object> args)
        {
            var levelName = GetArg<string>(args, "level_name");
            if (string.IsNullOrEmpty(levelName)) return JsonError("level_name required.");

            var elemIds = CollectElementsByLevel(doc, view, levelName);
            if (elemIds == null) return JsonError($"Level '{levelName}' not found in the model.");
            if (elemIds.Count == 0) return JsonError($"No elements found on level '{levelName}' in the active view.");

            var canHide = elemIds
                .Where(id => {
                    var catId = doc.GetElement(id)?.Category?.Id ?? ElementId.InvalidElementId;
                    return catId != ElementId.InvalidElementId && view.CanCategoryBeHidden(catId);
                })
                .ToList();

            if (canHide.Count == 0) return JsonError("None of the elements on this level can be hidden in this view.");

            using (var trans = new Transaction(doc, "AI: Hide by Level"))
            {
                trans.Start();
                view.HideElements(canHide);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                hidden = canHide.Count,
                level = levelName,
                view_name = view.Name,
                message = $"Hidden {canHide.Count} element(s) on level '{levelName}' in '{view.Name}'."
            }, JsonOpts);
        }

        private string OverrideColorByLevel(Document doc, View view, Dictionary<string, object> args)
        {
            var levelName = GetArg<string>(args, "level_name");
            var colorStr = GetArg<string>(args, "color");

            if (string.IsNullOrEmpty(levelName)) return JsonError("level_name required.");
            if (string.IsNullOrEmpty(colorStr)) return JsonError("color required.");

            var color = ParseColor(colorStr);
            if (color == null) return JsonError($"Unrecognized color '{colorStr}'.");

            var elemIds = CollectElementsByLevel(doc, view, levelName);
            if (elemIds == null) return JsonError($"Level '{levelName}' not found.");
            if (elemIds.Count == 0) return JsonError($"No elements on level '{levelName}' in the active view.");

            var solidFillId = GetSolidFillPatternId(doc);
            var ogs = BuildColorOverride(color, solidFillId);

            using (var trans = new Transaction(doc, "AI: Color by Level"))
            {
                trans.Start();
                foreach (var id in elemIds)
                    view.SetElementOverrides(id, ogs);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                overridden = elemIds.Count,
                level = levelName,
                color = colorStr,
                view_name = view.Name,
                message = $"Applied {colorStr} override to {elemIds.Count} element(s) on level '{levelName}'."
            }, JsonOpts);
        }

        private string IsolateByFilter(Document doc, View view, Dictionary<string, object> args)
        {
            var elemIds = CollectByFilter(doc, view, args, out string desc);
            if (elemIds == null) return JsonError(desc);
            if (elemIds.Count == 0) return JsonError($"No elements match the filter ({desc}) in the active view.");

            using (var trans = new Transaction(doc, "AI: Isolate by Filter"))
            {
                trans.Start();
                view.IsolateElementsTemporary(elemIds);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                isolated = elemIds.Count,
                filter = desc,
                view_name = view.Name,
                message = $"Isolated {elemIds.Count} element(s) matching [{desc}] in '{view.Name}'. Use reset_view_isolation to restore."
            }, JsonOpts);
        }

        private string OverrideColorByFilter(Document doc, View view, Dictionary<string, object> args)
        {
            var colorStr = GetArg<string>(args, "color");
            if (string.IsNullOrEmpty(colorStr)) return JsonError("color required.");

            var color = ParseColor(colorStr);
            if (color == null) return JsonError($"Unrecognized color '{colorStr}'.");

            var elemIds = CollectByFilter(doc, view, args, out string desc);
            if (elemIds == null) return JsonError(desc);
            if (elemIds.Count == 0) return JsonError($"No elements match the filter ({desc}).");

            var solidFillId = GetSolidFillPatternId(doc);
            var ogs = BuildColorOverride(color, solidFillId);

            using (var trans = new Transaction(doc, "AI: Color by Filter"))
            {
                trans.Start();
                foreach (var id in elemIds)
                    view.SetElementOverrides(id, ogs);
                trans.Commit();
            }

            return JsonSerializer.Serialize(new
            {
                overridden = elemIds.Count,
                filter = desc,
                color = colorStr,
                view_name = view.Name,
                message = $"Applied {colorStr} override to {elemIds.Count} element(s) matching [{desc}]."
            }, JsonOpts);
        }

        private List<ElementId> CollectElementsByLevel(Document doc, View view, string levelName)
        {
            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));

            if (level == null) return null;

            var levelId = level.Id;

            return new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => GetElementLevelId(e, doc) == levelId)
                .Select(e => e.Id)
                .ToList();
        }

        private List<ElementId> CollectByFilter(Document doc, View view, Dictionary<string, object> args, out string description)
        {
            var levelName = GetArg<string>(args, "level_name");
            var categoryName = GetArg<string>(args, "category");
            var paramName = GetArg<string>(args, "param_name");
            var paramValue = GetArg<string>(args, "param_value");

            if (string.IsNullOrEmpty(levelName) && string.IsNullOrEmpty(categoryName) && string.IsNullOrEmpty(paramName))
            {
                description = "At least one filter (level_name, category, or param_name) is required.";
                return null;
            }

            var parts = new List<string>();

            var collector = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType();

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (bic.HasValue)
            {
                collector = collector.OfCategory(bic.Value);
                parts.Add($"category={categoryName}");
            }

            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                {
                    description = $"Level '{levelName}' not found.";
                    return null;
                }
                parts.Add($"level={levelName}");
            }

            if (!string.IsNullOrEmpty(paramName))
                parts.Add($"{paramName}={paramValue}");

            description = string.Join(", ", parts);

            var elements = collector.ToList();

            if (level != null)
            {
                var levelId = level.Id;
                elements = elements.Where(e => GetElementLevelId(e, doc) == levelId).ToList();
            }

            if (!string.IsNullOrEmpty(paramName))
            {
                elements = elements.Where(e =>
                {
                    var p = e.LookupParameter(paramName);
                    if (p == null || !p.HasValue) return false;
                    if (string.IsNullOrEmpty(paramValue)) return true;
                    var val = GetParameterValueAsString(doc, p);
                    return val.Equals(paramValue, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            return elements.Select(e => e.Id).ToList();
        }

        private static ElementId GetElementLevelId(Element elem, Document doc)
        {
            var lp = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                  ?? elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                  ?? elem.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                  ?? elem.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID);

            if (lp != null && lp.StorageType == StorageType.ElementId)
            {
                var id = lp.AsElementId();
                if (id != ElementId.InvalidElementId) return id;
            }

            if (elem.LevelId != null && elem.LevelId != ElementId.InvalidElementId)
                return elem.LevelId;

            return ElementId.InvalidElementId;
        }

        private static OverrideGraphicSettings BuildColorOverride(Color color, ElementId solidFillId)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            if (solidFillId != ElementId.InvalidElementId)
                ogs.SetSurfaceForegroundPatternId(solidFillId);
            ogs.SetCutLineColor(color);
            ogs.SetCutForegroundPatternColor(color);
            return ogs;
        }

        #endregion

        #region Helpers

        private static Color ParseColor(string colorStr)
        {
            if (string.IsNullOrEmpty(colorStr)) return null;

            colorStr = colorStr.Trim();

            if (NamedColors.TryGetValue(colorStr, out var named))
                return new Color(named.R, named.G, named.B);

            // Try hex format: #RRGGBB or RRGGBB
            var hex = colorStr.TrimStart('#');
            if (hex.Length == 6)
            {
                try
                {
                    byte r = Convert.ToByte(hex[..2], 16);
                    byte g = Convert.ToByte(hex[2..4], 16);
                    byte b = Convert.ToByte(hex[4..6], 16);
                    return new Color(r, g, b);
                }
                catch { }
            }

            // Try "R,G,B" format
            var parts = colorStr.Split(',');
            if (parts.Length == 3 &&
                byte.TryParse(parts[0].Trim(), out byte cr) &&
                byte.TryParse(parts[1].Trim(), out byte cg) &&
                byte.TryParse(parts[2].Trim(), out byte cb))
            {
                return new Color(cr, cg, cb);
            }

            return null;
        }

        private static ElementId GetSolidFillPatternId(Document doc)
        {
            try
            {
                var solidFill = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

                return solidFill?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
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

        #endregion
    }
}
