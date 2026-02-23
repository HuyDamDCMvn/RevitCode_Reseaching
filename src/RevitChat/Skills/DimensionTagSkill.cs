using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class DimensionTagSkill : BaseRevitSkill
    {
        protected override string SkillName => "DimensionTag";
        protected override string SkillDescription => "Tag elements, find untagged elements, add text notes, query tag rules and types";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "tag_elements", "get_untagged_elements", "tag_all_in_view",
            "add_text_note", "get_tag_rules", "get_available_tag_types"
        };

        #region SmartTag Knowledge

        private static readonly object _knowledgeLock = new();
        private static Dictionary<string, string> _categoryTagMap;
        private static string _rulesDir;
        private static string _knowledgeDir;

        private static void EnsureKnowledgeLoaded()
        {
            if (_categoryTagMap != null) return;
            lock (_knowledgeLock)
            {
                if (_categoryTagMap != null) return;
                var asmLoc = typeof(DimensionTagSkill).Assembly.Location;
                var dllDir = !string.IsNullOrEmpty(asmLoc)
                    ? Path.GetDirectoryName(asmLoc)
                    : AppContext.BaseDirectory;
                _knowledgeDir = Path.Combine(dllDir, "Data", "ChatConfig");
                _rulesDir = Path.Combine(dllDir, "Data", "Rules", "Tagging");

                _categoryTagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var path = Path.Combine(_knowledgeDir, "smarttag_knowledge.json");
                    if (File.Exists(path))
                    {
                        var doc = JsonDocument.Parse(File.ReadAllText(path));
                        if (doc.RootElement.TryGetProperty("category_tag_mappings", out var mappings))
                        {
                            foreach (var prop in mappings.EnumerateObject())
                                _categoryTagMap[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                }
                catch { }
            }
        }

        private static BuiltInCategory? ResolveTagCategory(BuiltInCategory elementCategory)
        {
            EnsureKnowledgeLoaded();
            var key = elementCategory.ToString();
            if (_categoryTagMap.TryGetValue(key, out var tagCatStr))
            {
                if (Enum.TryParse<BuiltInCategory>(tagCatStr, out var tagCat))
                    return tagCat;
            }
            return null;
        }

        #endregion

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("tag_elements",
                "Tag specific elements with IndependentTag in the active view. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to tag" },
                        "tag_type_id": { "type": "integer", "description": "Optional: tag FamilySymbol ID from get_available_tag_types. If omitted, auto-resolves best tag type." },
                        "add_leader": { "type": "boolean", "description": "Add leader line (default: false)" },
                        "tag_orientation": { "type": "string", "enum": ["horizontal", "vertical"], "description": "Tag orientation (default: horizontal)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_untagged_elements",
                "Find elements in the active view that do not have tags. Returns element details including MEP system info.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name to check (e.g. 'Doors', 'Pipes', 'Ducts')" },
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
                        "category": { "type": "string", "description": "Category name (e.g. 'Doors', 'Rooms', 'Pipes', 'Ducts')" },
                        "tag_type_id": { "type": "integer", "description": "Optional: tag FamilySymbol ID from get_available_tag_types" },
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
                """)),

            ChatTool.CreateFunctionTool("get_tag_rules",
                "Get tagging rules from SmartTag knowledge base. Returns tag format patterns, preferred positions, and elevation references for a category/system.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category filter (e.g. 'Pipes', 'Ducts', 'CableTrays')" },
                        "system_name": { "type": "string", "description": "Optional: system name or abbreviation filter (e.g. 'SW', 'HZG', 'RLT')" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_available_tag_types",
                "List available tag families and types in the current document for a given element category. Use this before tagging to find the correct tag_type_id.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Element category name (e.g. 'Pipes', 'Ducts', 'Walls', 'Doors')" }
                    },
                    "required": ["category"]
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
                "get_tag_rules" => GetTagRules(args),
                "get_available_tag_types" => GetAvailableTagTypes(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        #region get_tag_rules

        private string GetTagRules(Dictionary<string, object> args)
        {
            EnsureKnowledgeLoaded();
            var categoryFilter = GetArg<string>(args, "category")?.ToLowerInvariant();
            var systemFilter = GetArg<string>(args, "system_name")?.ToLowerInvariant();

            if (!Directory.Exists(_rulesDir))
                return JsonError($"Rules directory not found: {_rulesDir}");

            var matchedRules = new List<object>();
            var ruleFiles = Directory.GetFiles(_rulesDir, "*.json");

            foreach (var file in ruleFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean())
                        continue;

                    bool categoryMatch = string.IsNullOrEmpty(categoryFilter);
                    bool systemMatch = string.IsNullOrEmpty(systemFilter);

                    if (!categoryMatch && root.TryGetProperty("conditions", out var conditions))
                    {
                        if (conditions.TryGetProperty("categories", out var cats))
                        {
                            foreach (var cat in cats.EnumerateArray())
                            {
                                var catStr = cat.GetString()?.ToLowerInvariant() ?? "";
                                if (catStr.Contains(categoryFilter) || CategoryNameMatches(categoryFilter, catStr))
                                {
                                    categoryMatch = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!systemMatch)
                    {
                        var ruleText = json.ToLowerInvariant();
                        if (ruleText.Contains(systemFilter))
                            systemMatch = true;
                    }

                    if (!categoryMatch || !systemMatch) continue;

                    var ruleInfo = new Dictionary<string, object>
                    {
                        ["file"] = Path.GetFileNameWithoutExtension(file),
                        ["name"] = root.TryGetProperty("name", out var n) ? n.GetString() : Path.GetFileNameWithoutExtension(file),
                        ["priority"] = root.TryGetProperty("priority", out var p) ? p.GetInt32() : 0
                    };

                    if (root.TryGetProperty("tagFormat", out var tagFormat))
                        ruleInfo["tag_format"] = JsonSerializer.Deserialize<object>(tagFormat.GetRawText());

                    if (root.TryGetProperty("actions", out var actions))
                    {
                        var actionsDict = new Dictionary<string, object>();
                        if (actions.TryGetProperty("preferredPositions", out var pos))
                            actionsDict["preferred_positions"] = JsonSerializer.Deserialize<List<string>>(pos.GetRawText());
                        if (actions.TryGetProperty("addLeader", out var leader))
                            actionsDict["add_leader"] = leader.GetBoolean();
                        if (actions.TryGetProperty("leaderStyle", out var ls))
                            actionsDict["leader_style"] = ls.GetString();
                        ruleInfo["actions"] = actionsDict;
                    }

                    if (root.TryGetProperty("elevationReference", out var elev))
                        ruleInfo["elevation_reference"] = JsonSerializer.Deserialize<object>(elev.GetRawText());

                    if (root.TryGetProperty("systemTypes", out var sysTypes))
                    {
                        var systems = new List<object>();
                        foreach (var st in sysTypes.EnumerateArray())
                        {
                            systems.Add(new
                            {
                                code = st.TryGetProperty("code", out var c) ? c.GetString() : "",
                                english = st.TryGetProperty("english", out var e) ? e.GetString() : "",
                                german = st.TryGetProperty("german", out var g) ? g.GetString() : ""
                            });
                        }
                        ruleInfo["system_types"] = systems;
                    }

                    matchedRules.Add(ruleInfo);
                }
                catch { }
            }

            matchedRules = matchedRules
                .OrderByDescending(r => r is Dictionary<string, object> d && d.ContainsKey("priority") ? (int)d["priority"] : 0)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                rules_count = matchedRules.Count,
                filters = new { category = categoryFilter ?? "(all)", system_name = systemFilter ?? "(all)" },
                rules = matchedRules
            }, JsonOpts);
        }

        private static bool CategoryNameMatches(string userInput, string ostCategory)
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["pipe"] = new[] { "ost_pipecurves", "ost_flexpipecurves", "ost_pipefitting", "ost_pipeaccessory" },
                ["pipes"] = new[] { "ost_pipecurves", "ost_flexpipecurves", "ost_pipefitting", "ost_pipeaccessory" },
                ["duct"] = new[] { "ost_ductcurves", "ost_flexductcurves", "ost_ductfitting", "ost_ductaccessory", "ost_ductterminal" },
                ["ducts"] = new[] { "ost_ductcurves", "ost_flexductcurves", "ost_ductfitting", "ost_ductaccessory", "ost_ductterminal" },
                ["cabletray"] = new[] { "ost_cabletray", "ost_cabletrayfitting" },
                ["cabletrays"] = new[] { "ost_cabletray", "ost_cabletrayfitting" },
                ["conduit"] = new[] { "ost_conduit", "ost_conduitfitting" },
                ["conduits"] = new[] { "ost_conduit", "ost_conduitfitting" },
                ["wall"] = new[] { "ost_walls" },
                ["walls"] = new[] { "ost_walls" },
                ["door"] = new[] { "ost_doors" },
                ["doors"] = new[] { "ost_doors" },
                ["window"] = new[] { "ost_windows" },
                ["windows"] = new[] { "ost_windows" },
                ["room"] = new[] { "ost_rooms" },
                ["rooms"] = new[] { "ost_rooms" }
            };

            if (map.TryGetValue(userInput, out var ostNames))
                return ostNames.Contains(ostCategory);
            return false;
        }

        #endregion

        #region get_available_tag_types

        private string GetAvailableTagTypes(Document doc, Dictionary<string, object> args)
        {
            EnsureKnowledgeLoaded();
            var catName = GetArg<string>(args, "category");
            if (string.IsNullOrEmpty(catName)) return JsonError("category is required.");

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found in document.");

            var tagBic = ResolveTagCategory(bic.Value);
            if (!tagBic.HasValue)
                return JsonError($"No tag category mapping known for '{catName}'. Element category: {bic.Value}");

            var tagTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(tagBic.Value)
                .Cast<FamilySymbol>()
                .Select(fs => new
                {
                    id = fs.Id.Value,
                    family_name = fs.Family?.Name ?? "-",
                    type_name = fs.Name,
                    is_active = fs.IsActive
                })
                .OrderBy(t => t.family_name)
                .ThenBy(t => t.type_name)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                element_category = catName,
                element_bic = bic.Value.ToString(),
                tag_category = tagBic.Value.ToString(),
                tag_types_count = tagTypes.Count,
                tag_types = tagTypes
            }, JsonOpts);
        }

        #endregion

        #region tag_elements (enhanced)

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
            ElementId resolvedTagTypeId = tagTypeId > 0 ? new ElementId(tagTypeId) : null;

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

                        var typeToApply = resolvedTagTypeId ?? AutoResolveTagType(doc, elem);
                        if (typeToApply != null && typeToApply != ElementId.InvalidElementId)
                            tag.ChangeTypeId(typeToApply);

                        success++;
                    }
                    catch (Exception ex) { errors.Add($"Element {id}: {ex.Message}"); }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { success, errors = errors.Take(10) }, JsonOpts);
        }

        private static ElementId AutoResolveTagType(Document doc, Element elem)
        {
            EnsureKnowledgeLoaded();
            var bic = (BuiltInCategory)(elem.Category?.Id.Value ?? (long)BuiltInCategory.INVALID);
            var tagBic = ResolveTagCategory(bic);
            if (!tagBic.HasValue) return null;

            var firstActive = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(tagBic.Value)
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.IsActive);

            return firstActive?.Id;
        }

        #endregion

        #region get_untagged_elements (enhanced with MEP info)

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

            bool isMep = IsMepCategory(bic.Value);

            var untagged = elements
                .Where(e => !taggedIds.Contains(e.Id.Value))
                .Take(limit)
                .Select(e =>
                {
                    var info = new Dictionary<string, object>
                    {
                        ["id"] = e.Id.Value,
                        ["name"] = e.Name,
                        ["type"] = (doc.GetElement(e.GetTypeId()) as ElementType)?.Name ?? "-"
                    };

                    if (isMep)
                    {
                        var sysParam = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                        if (sysParam != null && sysParam.HasValue)
                            info["system_name"] = sysParam.AsString();

                        var size = GetMepSizeString(e);
                        if (size != null) info["size"] = size;
                    }

                    return info;
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                category = catName,
                total_in_view = elements.Count,
                untagged_count = untagged.Count,
                untagged = untagged
            }, JsonOpts);
        }

        private static bool IsMepCategory(BuiltInCategory bic) => bic is
            BuiltInCategory.OST_PipeCurves or BuiltInCategory.OST_DuctCurves or
            BuiltInCategory.OST_CableTray or BuiltInCategory.OST_Conduit or
            BuiltInCategory.OST_FlexPipeCurves or BuiltInCategory.OST_FlexDuctCurves or
            BuiltInCategory.OST_PipeFitting or BuiltInCategory.OST_DuctFitting or
            BuiltInCategory.OST_PipeAccessory or BuiltInCategory.OST_DuctAccessory;

        private static string GetMepSizeString(Element e)
        {
            var diam = e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (diam != null && diam.HasValue)
                return $"DN{Math.Round(diam.AsDouble() * 304.8)}";

            var w = e.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var h = e.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (w != null && h != null && w.HasValue && h.HasValue)
                return $"{Math.Round(w.AsDouble() * 304.8)}x{Math.Round(h.AsDouble() * 304.8)}";

            return null;
        }

        #endregion

        #region tag_all_in_view (enhanced)

        private string TagAllInView(Document doc, View view, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            bool addLeader = GetArg(args, "add_leader", false);
            bool dryRun = GetArg(args, "dry_run", false);
            long tagTypeId = GetArg<long>(args, "tag_type_id");

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc, view.Id)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var taggedIds = CollectTaggedElementIds(doc, view);
            var untaggedElements = elements.Where(e => !taggedIds.Contains(e.Id.Value)).ToList();

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

            int success = 0;
            ElementId resolvedTagTypeId = tagTypeId > 0 ? new ElementId(tagTypeId) : null;
            bool autoResolved = false;

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
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(elem), addLeader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);

                        var typeToApply = resolvedTagTypeId ?? AutoResolveTagType(doc, elem);
                        if (typeToApply != null && typeToApply != ElementId.InvalidElementId)
                        {
                            tag.ChangeTypeId(typeToApply);
                            if (!autoResolved && resolvedTagTypeId == null) autoResolved = true;
                        }

                        success++;
                    }
                    catch { }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            var result = new Dictionary<string, object>
            {
                ["category"] = catName,
                ["total_in_view"] = elements.Count,
                ["already_tagged"] = taggedIds.Count,
                ["newly_tagged"] = success
            };
            if (autoResolved)
                result["note"] = "Tag type was auto-resolved from SmartTag knowledge base.";

            return JsonSerializer.Serialize(result, JsonOpts);
        }

        #endregion

        #region add_text_note

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

        #endregion
    }
}
