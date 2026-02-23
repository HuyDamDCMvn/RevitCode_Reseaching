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
    public class MaterialSkill : BaseRevitSkill
    {
        protected override string SkillName => "Material";
        protected override string SkillDescription => "Query and manage materials, element materials, and material quantities";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_materials", "get_element_material", "set_element_material", "get_material_quantities"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_materials",
                "List all materials in the document with their appearance properties.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter": { "type": "string", "description": "Optional: filter by material name (partial match)" },
                        "material_class": { "type": "string", "description": "Optional: filter by material class (e.g. 'Concrete', 'Metal', 'Glass')" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_element_material",
                "Get the materials assigned to specific elements.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("set_element_material",
                "Set the material of elements via the Structural Material parameter. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to change" },
                        "material_id": { "type": "integer", "description": "Material ID to assign" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "material_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_material_quantities",
                "Calculate total material area and volume for elements in a category.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category name (e.g. 'Walls', 'Floors')" },
                        "limit": { "type": "integer", "description": "Max material types to return (default 30)" }
                    },
                    "required": ["category"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_materials" => GetMaterials(doc, args),
                "get_element_material" => GetElementMaterial(doc, args),
                "set_element_material" => SetElementMaterial(doc, args),
                "get_material_quantities" => GetMaterialQuantities(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetMaterials(Document doc, Dictionary<string, object> args)
        {
            var filter = GetArg<string>(args, "filter");
            var matClass = GetArg<string>(args, "material_class");
            int limit = GetArg(args, "limit", 50);

            var materials = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();

            if (!string.IsNullOrEmpty(filter))
                materials = materials.Where(m => m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (!string.IsNullOrEmpty(matClass))
                materials = materials.Where(m => m.MaterialClass?.IndexOf(matClass, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var items = materials.OrderBy(m => m.Name).Take(limit).Select(m => new
            {
                id = m.Id.Value,
                name = m.Name,
                material_class = m.MaterialClass ?? "-",
                material_category = m.MaterialCategory.ToString(),
                color = m.Color?.IsValid == true ? $"#{m.Color.Red:X2}{m.Color.Green:X2}{m.Color.Blue:X2}" : "-",
                transparency = m.Transparency
            }).ToList();

            return JsonSerializer.Serialize(new { total = materials.Count, returned = items.Count, materials = items }, JsonOpts);
        }

        private string GetElementMaterial(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var results = new List<object>();

            foreach (var id in ids.Take(50))
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) { results.Add(new { id, error = "not found" }); continue; }

                var materialIds = new HashSet<long>();
                var materialNames = new List<object>();

                foreach (var matId in elem.GetMaterialIds(false))
                {
                    if (materialIds.Contains(matId.Value)) continue;
                    materialIds.Add(matId.Value);

                    var mat = doc.GetElement(matId) as Material;
                    double area = 0, volume = 0;
                    try { area = elem.GetMaterialArea(matId, false); } catch { }
                    try { volume = elem.GetMaterialVolume(matId); } catch { }

                    materialNames.Add(new
                    {
                        material_id = matId.Value,
                        name = mat?.Name ?? "-",
                        material_class = mat?.MaterialClass ?? "-",
                        area_sqft = Math.Round(area, 4),
                        volume_cuft = Math.Round(volume, 4)
                    });
                }

                var structMat = elem.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                string structMatName = null;
                if (structMat != null && structMat.AsElementId() != ElementId.InvalidElementId)
                {
                    var sm = doc.GetElement(structMat.AsElementId()) as Material;
                    structMatName = sm?.Name;
                }

                results.Add(new
                {
                    id,
                    name = elem.Name,
                    category = elem.Category?.Name ?? "-",
                    structural_material = structMatName,
                    materials = materialNames
                });
            }

            return JsonSerializer.Serialize(new { elements = results }, JsonOpts);
        }

        private string SetElementMaterial(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            long materialId = GetArg<long>(args, "material_id");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var mat = doc.GetElement(new ElementId(materialId)) as Material;
            if (mat == null) return JsonError($"Material {materialId} not found.");

            if (dryRun)
            {
                int wouldSet = 0, failed = 0;
                var previewErrors = new List<string>();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; previewErrors.Add($"Element {id} not found."); continue; }

                    var param = elem.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                    if (param == null || param.IsReadOnly)
                    {
                        failed++;
                        previewErrors.Add($"Element {id}: no writable Structural Material parameter.");
                        continue;
                    }

                    wouldSet++;
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_set = wouldSet,
                    material = mat.Name,
                    failed,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Set Material"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found."); continue; }

                    var param = elem.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                    if (param == null || param.IsReadOnly)
                    {
                        errors.Add($"Element {id}: no writable Structural Material parameter.");
                        continue;
                    }

                    param.Set(mat.Id);
                    success++;
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                success,
                material = mat.Name,
                errors = errors.Take(10)
            }, JsonOpts);
        }

        private string GetMaterialQuantities(Document doc, Dictionary<string, object> args)
        {
            var catName = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 30);

            var bic = ResolveCategoryFilter(doc, catName);
            if (!bic.HasValue) return JsonError($"Category '{catName}' not found.");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var matTotals = new Dictionary<long, (string name, string cls, double area, double volume)>();

            foreach (var elem in elements)
            {
                foreach (var matId in elem.GetMaterialIds(false))
                {
                    double area = 0, volume = 0;
                    try { area = elem.GetMaterialArea(matId, false); } catch { }
                    try { volume = elem.GetMaterialVolume(matId); } catch { }

                    if (!matTotals.ContainsKey(matId.Value))
                    {
                        var mat = doc.GetElement(matId) as Material;
                        matTotals[matId.Value] = (mat?.Name ?? "-", mat?.MaterialClass ?? "-", 0, 0);
                    }

                    var cur = matTotals[matId.Value];
                    matTotals[matId.Value] = (cur.name, cur.cls, cur.area + area, cur.volume + volume);
                }
            }

            var items = matTotals
                .OrderByDescending(kv => kv.Value.volume)
                .Take(limit)
                .Select(kv => new
                {
                    material_id = kv.Key,
                    name = kv.Value.name,
                    material_class = kv.Value.cls,
                    total_area_sqft = Math.Round(kv.Value.area, 2),
                    total_volume_cuft = Math.Round(kv.Value.volume, 2)
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                category = catName,
                element_count = elements.Count,
                material_count = items.Count,
                materials = items
            }, JsonOpts);
        }
    }
}
