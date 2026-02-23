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
    public class MepDistanceSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepDistance";
        protected override string SkillDescription => "Measure distances from MEP elements to floors/slabs/ceilings using raycasting";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "measure_distance_to_slab"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("measure_distance_to_slab",
                "Measure vertical distance from MEP elements to the nearest floor/slab above or below. Uses raycasting for accurate measurement.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "MEP element IDs to measure" },
                        "direction": { "type": "string", "enum": ["up", "down", "both"], "description": "Ray direction. Default: both" },
                        "target_category": { "type": "string", "enum": ["floors", "ceilings", "both"], "description": "Target to measure to. Default: floors" },
                        "write_parameter": { "type": "string", "description": "Optional: parameter name to write the distance value (in mm)" }
                    },
                    "required": ["element_ids"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "measure_distance_to_slab" => MeasureDistanceToSlab(uidoc, doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string MeasureDistanceToSlab(UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var direction = (GetArg(args, "direction", "both") ?? "both").ToLower();
            var targetCat = (GetArg(args, "target_category", "floors") ?? "floors").ToLower();
            var writeParam = GetArg<string>(args, "write_parameter");

            var view3d = Get3DView(doc);
            if (view3d == null)
                return JsonError("A 3D view is required for raycasting. No 3D view found in the document.");

            var targetCategories = new List<BuiltInCategory>();
            if (targetCat is "floors" or "both") targetCategories.Add(BuiltInCategory.OST_Floors);
            if (targetCat is "ceilings" or "both") targetCategories.Add(BuiltInCategory.OST_Ceilings);

            if (targetCategories.Count == 0)
                return JsonError("target_category must be 'floors', 'ceilings', or 'both'.");

            var catFilter = new ElementMulticategoryFilter(targetCategories);
            var intersector = new ReferenceIntersector(catFilter, FindReferenceTarget.Face, view3d)
            {
                FindReferencesInRevitLinks = true
            };

            var results = new List<object>();
            var errors = new List<string>();
            bool doWrite = !string.IsNullOrEmpty(writeParam);
            Transaction trans = null;

            if (doWrite)
            {
                trans = new Transaction(doc, "AI: Write Slab Distance");
                trans.Start();
            }

            try
            {
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found"); continue; }

                    var bb = elem.get_BoundingBox(null);
                    if (bb == null) { errors.Add($"No bounding box for {id}"); continue; }

                    double centerX = (bb.Min.X + bb.Max.X) / 2;
                    double centerY = (bb.Min.Y + bb.Max.Y) / 2;
                    double topZ = bb.Max.Z;
                    double bottomZ = bb.Min.Z;

                    double? distUp = null, distDown = null;
                    string hitUpName = null, hitDownName = null;

                    if (direction is "up" or "both")
                    {
                        var rayStart = new XYZ(centerX, centerY, topZ);
                        var hit = intersector.FindNearest(rayStart, XYZ.BasisZ);
                        if (hit != null)
                        {
                            distUp = hit.Proximity * 304.8;
                            var hitElem = doc.GetElement(hit.GetReference().ElementId);
                            hitUpName = hitElem?.Name ?? "-";
                        }
                    }

                    if (direction is "down" or "both")
                    {
                        var rayStart = new XYZ(centerX, centerY, bottomZ);
                        var hit = intersector.FindNearest(rayStart, -XYZ.BasisZ);
                        if (hit != null)
                        {
                            distDown = hit.Proximity * 304.8;
                            var hitElem = doc.GetElement(hit.GetReference().ElementId);
                            hitDownName = hitElem?.Name ?? "-";
                        }
                    }

                    if (doWrite && (distUp.HasValue || distDown.HasValue))
                    {
                        var param = elem.LookupParameter(writeParam);
                        if (param != null && !param.IsReadOnly)
                        {
                            double val = distDown ?? distUp ?? 0;
                            if (param.StorageType == StorageType.Double)
                                param.Set(val / 304.8);
                            else if (param.StorageType == StorageType.Integer)
                                param.Set((int)Math.Round(val));
                            else if (param.StorageType == StorageType.String)
                                param.Set($"{Math.Round(val, 1)} mm");
                        }
                    }

                    results.Add(new
                    {
                        element_id = id,
                        category = elem.Category?.Name ?? "-",
                        distance_up_mm = distUp.HasValue ? Math.Round(distUp.Value, 1) : (double?)null,
                        hit_above = hitUpName,
                        distance_down_mm = distDown.HasValue ? Math.Round(distDown.Value, 1) : (double?)null,
                        hit_below = hitDownName
                    });
                }

                if (trans != null)
                {
                    if (results.Count > 0) trans.Commit(); else trans.RollBack();
                }
            }
            catch
            {
                if (trans != null && trans.HasStarted()) trans.RollBack();
                throw;
            }
            finally
            {
                trans?.Dispose();
            }

            return JsonSerializer.Serialize(new
            {
                measured = results.Count,
                parameter_written = doWrite ? writeParam : null,
                results,
                errors = errors.Take(10)
            }, JsonOpts);
        }

        private static View3D Get3DView(Document doc)
        {
            if (doc.ActiveView is View3D active3d && !active3d.IsTemplate)
                return active3d;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);
        }
    }
}
