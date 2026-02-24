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
    public class StructuralAnalysisSkill : BaseRevitSkill
    {
        protected override string SkillName => "StructuralAnalysis";
        protected override string SkillDescription => "Analyze structural elements, rebar, and foundations";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_structural_model", "check_rebar_coverage", "get_rebar_schedule", "check_foundation_loads"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_structural_model",
                "Get summary of structural elements: columns, beams, foundations.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("check_rebar_coverage",
                "Check rebar spacing and coverage in structural elements.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Specific element IDs (optional)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_rebar_schedule",
                "Generate rebar schedule / bill of quantities.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "level": { "type": "string", "description": "Optional level filter" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("check_foundation_loads",
                "Summarize foundation elements and their associated loads.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }"""))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_structural_model" => GetStructuralModel(doc),
                "check_rebar_coverage" => CheckRebarCoverage(doc, args),
                "get_rebar_schedule" => GetRebarSchedule(doc, args),
                "check_foundation_loads" => CheckFoundationLoads(doc),
                _ => UnknownTool(functionName)
            };
        }

        private string GetStructuralModel(Document doc)
        {
            int columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount();
            int beams = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().GetElementCount();
            int foundations = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType().GetElementCount();
            int floors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().GetElementCount();
            int walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType()
                .Cast<Wall>().Count(w => w.StructuralUsage != StructuralWallUsage.NonBearing);
            int rebar = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType().GetElementCount();

            return JsonSerializer.Serialize(new
            {
                structural_columns = columns, beams, foundations, structural_floors = floors,
                structural_walls = walls, rebar_elements = rebar,
                total = columns + beams + foundations + floors + walls
            }, JsonOpts);
        }

        private string CheckRebarCoverage(Document doc, Dictionary<string, object> args)
        {
            var specificIds = GetArgLongArray(args, "element_ids");
            var rebars = specificIds != null && specificIds.Count > 0
                ? specificIds.Select(id => doc.GetElement(new ElementId(id))).Where(e => e is Rebar).Cast<Rebar>().ToList()
                : new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();

            var results = rebars.Take(100).Select(r =>
            {
                var host = doc.GetElement(r.GetHostId());
                var coverParam = r.LookupParameter("Cover");
                return new
                {
                    rebar_id = r.Id.Value,
                    host_id = host?.Id.Value ?? -1,
                    host_category = host?.Category?.Name ?? "-",
                    bar_type = (doc.GetElement(r.GetTypeId()) as RebarBarType)?.Name ?? "-",
                    quantity = r.Quantity,
                    cover = coverParam?.AsValueString() ?? "-"
                };
            }).ToList();

            return JsonSerializer.Serialize(new { rebar_count = results.Count, rebars = results }, JsonOpts);
        }

        private string GetRebarSchedule(Document doc, Dictionary<string, object> args)
        {
            var levelFilter = GetArg<string>(args, "level");
            var resolvedLevel = !string.IsNullOrWhiteSpace(levelFilter)
                ? ResolveLevelName(doc, levelFilter)
                : null;

            var rebars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();

            if (resolvedLevel != null)
            {
                rebars = rebars.Where(r =>
                {
                    var hostId = r.GetHostId();
                    if (hostId == ElementId.InvalidElementId) return false;
                    var host = doc.GetElement(hostId);
                    return host != null && GetElementLevel(doc, host)
                        .Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            var grouped = rebars.GroupBy(r =>
            {
                var bt = doc.GetElement(r.GetTypeId()) as RebarBarType;
                return bt?.Name ?? "Unknown";
            }).Select(g =>
            {
                double totalLength = g.Sum(r => { var lp = r.LookupParameter("Total Bar Length"); return lp?.AsDouble() ?? 0; });
                double totalWeight = g.Sum(r => { var wp = r.LookupParameter("Total Weight"); return wp?.AsDouble() ?? 0; });
                return new
                {
                    bar_type = g.Key,
                    count = g.Count(),
                    total_quantity = g.Sum(r => r.Quantity),
                    total_length_ft = Math.Round(totalLength, 1),
                    total_weight_lb = Math.Round(totalWeight, 1)
                };
            }).OrderBy(x => x.bar_type).ToList();

            var result = new Dictionary<string, object>
            {
                ["bar_type_count"] = grouped.Count,
                ["schedule"] = grouped
            };
            if (resolvedLevel != null)
                result["level_filter"] = resolvedLevel;

            return JsonSerializer.Serialize(result, JsonOpts);
        }

        private string CheckFoundationLoads(Document doc)
        {
            var foundations = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType().ToList();

            var results = foundations.Take(50).Select(f => new
            {
                id = f.Id.Value,
                name = f.Name,
                family = GetFamilyName(doc, f),
                type = GetElementTypeName(doc, f),
                level = GetElementLevel(doc, f)
            }).ToList();

            return JsonSerializer.Serialize(new { foundation_count = results.Count, foundations = results }, JsonOpts);
        }
    }
}
