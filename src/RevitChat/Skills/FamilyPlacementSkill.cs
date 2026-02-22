using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class FamilyPlacementSkill : IRevitSkill
    {
        public string Name => "FamilyPlacement";
        public string Description => "Place family instances, swap types, list available types, load families";

        private static readonly HashSet<string> HandledTools = new()
        {
            "place_family_instance", "swap_family_type", "get_family_types", "load_family"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_family_types",
                "List all loaded family types (symbols) in the model, optionally filtered by category or family name.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Optional: filter by category name" },
                        "family_name": { "type": "string", "description": "Optional: filter by family name (partial match)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("place_family_instance",
                "Place a family instance at a specific location. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "family_type_id": { "type": "integer", "description": "FamilySymbol (type) ID to place" },
                        "x": { "type": "number", "description": "X coordinate in feet" },
                        "y": { "type": "number", "description": "Y coordinate in feet" },
                        "z": { "type": "number", "description": "Z coordinate in feet (default 0)" },
                        "level_name": { "type": "string", "description": "Optional: level name to place on" },
                        "structural_type": { "type": "string", "enum": ["non_structural", "beam", "brace", "column", "footing"], "description": "Structural type (default: non_structural)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["family_type_id", "x", "y"]
                }
                """)),

            ChatTool.CreateFunctionTool("swap_family_type",
                "Change the family type of one or more elements. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to change type" },
                        "new_type_id": { "type": "integer", "description": "New FamilySymbol (type) ID" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "new_type_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("load_family",
                "Load a family from an .rfa file path into the current document.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "file_path": { "type": "string", "description": "Full path to the .rfa file" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["file_path"]
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
                "get_family_types" => GetFamilyTypes(doc, args),
                "place_family_instance" => PlaceFamilyInstance(doc, args),
                "swap_family_type" => SwapFamilyType(doc, args),
                "load_family" => LoadFamily(doc, args),
                _ => JsonError($"FamilyPlacementSkill: unknown tool '{functionName}'")
            };
        }

        private string GetFamilyTypes(Document doc, Dictionary<string, object> args)
        {
            var categoryFilter = GetArg<string>(args, "category");
            var familyFilter = GetArg<string>(args, "family_name");
            int limit = GetArg(args, "limit", 50);

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol));

            var bic = ResolveCategoryFilter(doc, categoryFilter);
            if (bic.HasValue)
                collector = collector.OfCategory(bic.Value);

            var symbols = collector.Cast<FamilySymbol>().ToList();

            if (!string.IsNullOrEmpty(familyFilter))
                symbols = symbols.Where(s => s.Family?.Name?.IndexOf(familyFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var items = symbols.Take(limit).Select(s => new
            {
                type_id = s.Id.Value,
                family = s.Family?.Name ?? "-",
                type = s.Name,
                category = s.Category?.Name ?? "-",
                is_active = s.IsActive
            }).ToList();

            return JsonSerializer.Serialize(new { total = symbols.Count, returned = items.Count, types = items }, JsonOpts);
        }

        private string PlaceFamilyInstance(Document doc, Dictionary<string, object> args)
        {
            long typeId = GetArg<long>(args, "family_type_id");
            double x = GetArg(args, "x", 0.0);
            double y = GetArg(args, "y", 0.0);
            double z = GetArg(args, "z", 0.0);
            var levelName = GetArg<string>(args, "level_name");
            var structStr = GetArg(args, "structural_type", "non_structural");
            bool dryRun = GetArg(args, "dry_run", false);

            var symbol = doc.GetElement(new ElementId(typeId)) as FamilySymbol;
            if (symbol == null) return JsonError($"FamilySymbol with ID {typeId} not found.");

            var structType = structStr switch
            {
                "beam" => StructuralType.Beam,
                "brace" => StructuralType.Brace,
                "column" => StructuralType.Column,
                "footing" => StructuralType.Footing,
                _ => StructuralType.NonStructural
            };

            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.IndexOf(levelName, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            level ??= new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            if (level == null) return JsonError("No levels found in the model.");

            var point = new XYZ(x, y, z);

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_place = true,
                    family = symbol.Family?.Name,
                    type = symbol.Name,
                    location = new { x, y, z },
                    level = level.Name
                }, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Place Family Instance"))
            {
                trans.Start();
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    var instance = doc.Create.NewFamilyInstance(point, symbol, level, structType);
                    trans.Commit();

                    return JsonSerializer.Serialize(new
                    {
                        placed = true,
                        element_id = instance.Id.Value,
                        family = symbol.Family?.Name,
                        type = symbol.Name,
                        location = new { x, y, z },
                        level = level.Name
                    }, JsonOpts);
                }
                catch (Exception ex)
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    return JsonError($"PlaceFamilyInstance failed: {ex.Message}");
                }
            }
        }

        private string SwapFamilyType(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            long newTypeId = GetArg<long>(args, "new_type_id");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var newSymbol = doc.GetElement(new ElementId(newTypeId)) as FamilySymbol;
            if (newSymbol == null) return JsonError($"FamilySymbol with ID {newTypeId} not found.");

            if (dryRun)
            {
                int wouldSwap = 0, failed = 0;
                var previewErrors = new List<string>();
                foreach (var id in ids)
                {
                    var fi = doc.GetElement(new ElementId(id)) as FamilyInstance;
                    if (fi == null) { failed++; previewErrors.Add($"Element {id} is not a FamilyInstance"); continue; }
                    wouldSwap++;
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_swap = wouldSwap,
                    new_type = newSymbol.Name,
                    new_family = newSymbol.Family?.Name,
                    failed,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Swap Family Type"))
            {
                trans.Start();
                if (!newSymbol.IsActive) newSymbol.Activate();

                foreach (var id in ids)
                {
                    var fi = doc.GetElement(new ElementId(id)) as FamilyInstance;
                    if (fi == null) { errors.Add($"Element {id} is not a FamilyInstance"); continue; }

                    try { fi.Symbol = newSymbol; success++; }
                    catch (Exception ex) { errors.Add($"Failed on {id}: {ex.Message}"); }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                success,
                new_type = newSymbol.Name,
                new_family = newSymbol.Family?.Name,
                errors = errors.Take(10)
            }, JsonOpts);
        }

        private string LoadFamily(Document doc, Dictionary<string, object> args)
        {
            var filePath = GetArg<string>(args, "file_path");
            bool dryRun = GetArg(args, "dry_run", false);
            if (string.IsNullOrEmpty(filePath)) return JsonError("file_path required.");

            filePath = System.IO.Path.GetFullPath(filePath);
            if (!filePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                return JsonError("file_path must be a .rfa file.");

            if (!System.IO.File.Exists(filePath))
                return JsonError($"File not found: {filePath}");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_load = true,
                    file_path = filePath
                }, JsonOpts);
            }

            bool loaded = doc.LoadFamily(filePath, out Family family);

            if (!loaded || family == null)
                return JsonSerializer.Serialize(new { loaded = false, message = "Family may already be loaded or failed to load." }, JsonOpts);

            var typeNames = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id)?.Name)
                .Where(n => n != null)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                loaded = true,
                family_name = family.Name,
                category = family.FamilyCategory?.Name ?? "-",
                types = typeNames
            }, JsonOpts);
        }
    }
}
