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
    public class OpeningCreatorSkill : BaseRevitSkill
    {
        protected override string SkillName => "OpeningCreator";
        protected override string SkillDescription => "Detect MEP-host intersections and create openings for pipes, ducts, cable trays penetrating walls/floors";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "detect_mep_intersections", "create_openings"
        };

        private static readonly HashSet<BuiltInCategory> MepCategories = new()
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_PipeFitting
        };

        private static readonly HashSet<BuiltInCategory> HostCategories = new()
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("detect_mep_intersections",
                "Detect MEP elements (ducts, pipes, conduits, cable trays) intersecting walls, floors, or roofs. Returns intersection list with host info and suggested opening sizes.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "mep_element_ids": { "type": "array", "items": { "type": "integer" }, "description": "MEP element IDs to check. If empty, checks all MEP in active view." },
                        "host_category": { "type": "string", "enum": ["all", "walls", "floors", "roofs"], "description": "Host type filter. Default: all" },
                        "clearance_mm": { "type": "number", "description": "Clearance around MEP element in mm. Default: 50" },
                        "limit": { "type": "integer", "description": "Max results. Default: 100" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("create_openings",
                "Create rectangular openings in walls/floors where MEP elements penetrate. Requires intersection data from detect_mep_intersections. DANGEROUS: confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "intersections": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "mep_id": { "type": "integer" },
                                    "host_id": { "type": "integer" },
                                    "width_mm": { "type": "number" },
                                    "height_mm": { "type": "number" }
                                },
                                "required": ["mep_id", "host_id"]
                            },
                            "description": "Intersection list from detect_mep_intersections"
                        },
                        "clearance_mm": { "type": "number", "description": "Additional clearance. Default: 50" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default: false" }
                    },
                    "required": ["intersections"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "detect_mep_intersections" => DetectMepIntersections(doc, args),
                "create_openings" => CreateOpenings(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private const double MmPerFt = 304.8;

        private string DetectMepIntersections(Document doc, Dictionary<string, object> args)
        {
            var mepIds = GetArgLongArray(args, "mep_element_ids");
            var hostCatFilter = (GetArg(args, "host_category", "all") ?? "all").ToLower();
            double clearanceMm = GetArg(args, "clearance_mm", 50.0);
            int limit = Math.Max(1, Math.Min(GetArg(args, "limit", 100), 1000));
            double clearanceFt = clearanceMm / MmPerFt;

            IList<Element> mepElements;
            if (mepIds != null && mepIds.Count > 0)
            {
                mepElements = mepIds
                    .Select(id => doc.GetElement(new ElementId(id)))
                    .Where(e => e != null)
                    .ToList();
            }
            else
            {
                if (doc.ActiveView == null)
                    return JsonError("No active view. Open a view to auto-detect MEP elements, or provide mep_element_ids.");
                var collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
                var catFilter = new ElementMulticategoryFilter(MepCategories.ToList());
                mepElements = collector.WherePasses(catFilter).WhereElementIsNotElementType().ToElements();
            }

            if (mepElements.Count == 0)
                return JsonError("No MEP elements found.");

            var hostCats = ResolveHostCategories(hostCatFilter);
            var hostFilter = new ElementMulticategoryFilter(hostCats);
            var hostElements = new FilteredElementCollector(doc)
                .WherePasses(hostFilter)
                .WhereElementIsNotElementType()
                .ToElements();

            if (hostElements.Count == 0)
                return JsonError("No host elements (walls/floors/roofs) found.");

            var intersections = new List<object>();
            var hostSolids = new Dictionary<long, Solid>();

            foreach (var mep in mepElements)
            {
                if (intersections.Count >= limit) break;

                var mepBb = mep.get_BoundingBox(null);
                if (mepBb == null) continue;

                var expandedOutline = new Outline(
                    new XYZ(mepBb.Min.X - clearanceFt, mepBb.Min.Y - clearanceFt, mepBb.Min.Z - clearanceFt),
                    new XYZ(mepBb.Max.X + clearanceFt, mepBb.Max.Y + clearanceFt, mepBb.Max.Z + clearanceFt));

                foreach (var host in hostElements)
                {
                    if (intersections.Count >= limit) break;

                    var hostBb = host.get_BoundingBox(null);
                    if (hostBb == null) continue;

                    var hostOutline = new Outline(hostBb.Min, hostBb.Max);
                    if (!expandedOutline.Intersects(hostOutline, 0)) continue;

                    var mepSolid = GetElementSolid(mep);
                    if (mepSolid == null || mepSolid.Volume < 1e-9) continue;

                    if (!hostSolids.TryGetValue(host.Id.Value, out var hostSolid))
                    {
                        hostSolid = GetElementSolid(host);
                        hostSolids[host.Id.Value] = hostSolid;
                    }
                    if (hostSolid == null || hostSolid.Volume < 1e-9) continue;

                    Solid intersection;
                    try
                    {
                        intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            mepSolid, hostSolid, BooleanOperationsType.Intersect);
                    }
                    catch { continue; }

                    if (intersection == null || intersection.Volume < 1e-9) continue;

                    var worldBb = GetWorldBoundingBox(intersection);
                    double widthFt = worldBb.Max.X - worldBb.Min.X;
                    double heightFt = worldBb.Max.Z - worldBb.Min.Z;
                    double depthFt = worldBb.Max.Y - worldBb.Min.Y;
                    double maxSide = Math.Max(widthFt, depthFt);

                    double openingWidthMm = Math.Ceiling((maxSide + 2 * clearanceFt) * MmPerFt / 50) * 50;
                    double openingHeightMm = Math.Ceiling((heightFt + 2 * clearanceFt) * MmPerFt / 50) * 50;

                    intersections.Add(new
                    {
                        mep_id = mep.Id.Value,
                        mep_category = mep.Category?.Name ?? "-",
                        host_id = host.Id.Value,
                        host_category = host.Category?.Name ?? "-",
                        host_name = host.Name ?? "-",
                        intersection_volume_cf = Math.Round(intersection.Volume, 4),
                        suggested_width_mm = openingWidthMm,
                        suggested_height_mm = openingHeightMm
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                mep_checked = mepElements.Count,
                hosts_checked = hostElements.Count,
                intersections_found = intersections.Count,
                intersections
            }, JsonOpts);
        }

        private string CreateOpenings(Document doc, Dictionary<string, object> args)
        {
            bool dryRun = GetArg(args, "dry_run", false);
            double clearanceMm = GetArg(args, "clearance_mm", 50.0);
            double clearanceFt = clearanceMm / 304.8;

            if (!args.TryGetValue("intersections", out var intObj))
                return JsonError("intersections array required.");

            var items = new List<(long mepId, long hostId, double widthMm, double heightMm)>();
            if (intObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in je.EnumerateArray())
                {
                    long mepId = item.TryGetProperty("mep_id", out var mp) ? mp.GetInt64() : 0;
                    long hostId = item.TryGetProperty("host_id", out var hp) ? hp.GetInt64() : 0;
                    double w = item.TryGetProperty("width_mm", out var wp) ? wp.GetDouble() : 0;
                    double h = item.TryGetProperty("height_mm", out var hpp) ? hpp.GetDouble() : 0;
                    if (mepId > 0 && hostId > 0) items.Add((mepId, hostId, w, h));
                }
            }

            if (items.Count == 0) return JsonError("No valid intersections provided.");

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    openings_to_create = items.Count,
                    items = items.Select(i => new { i.mepId, i.hostId, width_mm = i.widthMm, height_mm = i.heightMm })
                }, JsonOpts);
            }

            int created = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Create Openings"))
            {
                trans.SetFailureHandlingOptions(trans.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(new SilentFailureProcessor()));
                trans.Start();

                foreach (var (mepId, hostId, widthMm, heightMm) in items)
                {
                    try
                    {
                        var mep = doc.GetElement(new ElementId(mepId));
                        var host = doc.GetElement(new ElementId(hostId));
                        if (mep == null || host == null)
                        {
                            errors.Add($"Element {mepId} or {hostId} not found");
                            continue;
                        }

                        var mepBb = mep.get_BoundingBox(null);
                        if (mepBb == null) { errors.Add($"No bounding box for MEP {mepId}"); continue; }

                        var center = new XYZ(
                            (mepBb.Min.X + mepBb.Max.X) / 2,
                            (mepBb.Min.Y + mepBb.Max.Y) / 2,
                            (mepBb.Min.Z + mepBb.Max.Z) / 2);

                        double w = widthMm > 0 ? widthMm / MmPerFt : Math.Max(mepBb.Max.X - mepBb.Min.X, mepBb.Max.Y - mepBb.Min.Y) + 2 * clearanceFt;
                        double h = heightMm > 0 ? heightMm / MmPerFt : (mepBb.Max.Z - mepBb.Min.Z) + 2 * clearanceFt;

                        if (host is Wall wall)
                        {
                            var wallDir = GetWallHorizontalDirection(wall);
                            double halfW = w / 2, halfH = h / 2;
                            var pt1 = new XYZ(
                                center.X - wallDir.X * halfW,
                                center.Y - wallDir.Y * halfW,
                                center.Z - halfH);
                            var pt2 = new XYZ(
                                center.X + wallDir.X * halfW,
                                center.Y + wallDir.Y * halfW,
                                center.Z + halfH);
                            var opening = doc.Create.NewOpening(wall, pt1, pt2);
                            if (opening != null) created++;
                            else errors.Add($"Failed to create opening in wall {hostId}");
                        }
                        else if (host is Floor floor)
                        {
                            var curveArr = new CurveArray();
                            double halfW = w / 2, halfD = w / 2;
                            var p1 = new XYZ(center.X - halfW, center.Y - halfD, center.Z);
                            var p2 = new XYZ(center.X + halfW, center.Y - halfD, center.Z);
                            var p3 = new XYZ(center.X + halfW, center.Y + halfD, center.Z);
                            var p4 = new XYZ(center.X - halfW, center.Y + halfD, center.Z);
                            curveArr.Append(Line.CreateBound(p1, p2));
                            curveArr.Append(Line.CreateBound(p2, p3));
                            curveArr.Append(Line.CreateBound(p3, p4));
                            curveArr.Append(Line.CreateBound(p4, p1));

                            var opening = doc.Create.NewOpening(floor, curveArr, true);
                            if (opening != null) created++;
                            else errors.Add($"Failed to create opening in floor {hostId}");
                        }
                        else
                        {
                            errors.Add($"Host {hostId} is not a wall or floor — skipped");
                        }
                    }
                    catch (Exception ex) { errors.Add($"Error on MEP {mepId}/Host {hostId}: {ex.Message}"); }
                }

                if (created > 0) trans.Commit(); else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { created, failed = errors.Count, errors = errors.Take(10) }, JsonOpts);
        }

        private static Solid GetElementSolid(Element elem)
        {
            try
            {
                var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Medium };
                var geom = elem.get_Geometry(opt);
                if (geom == null) return null;
                return GetSolidFromGeometry(geom);
            }
            catch { return null; }
        }

        private static BoundingBoxXYZ GetWorldBoundingBox(Solid solid)
        {
            var bb = solid.GetBoundingBox();
            if (bb == null) return null;
            var transform = bb.Transform;
            var corners = new[]
            {
                transform.OfPoint(bb.Min),
                transform.OfPoint(bb.Max),
                transform.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z)),
                transform.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
                transform.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)),
                transform.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)),
                transform.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z)),
                transform.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z))
            };
            var result = new BoundingBoxXYZ
            {
                Min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
                Max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z))
            };
            return result;
        }

        private static Solid GetSolidFromGeometry(GeometryElement geom)
        {
            if (geom == null) return null;
            Solid best = null;
            foreach (var obj in geom)
            {
                if (obj is Solid s && s.Volume > 0)
                {
                    if (best == null || s.Volume > best.Volume) best = s;
                }
                else if (obj is GeometryInstance gi)
                {
                    var inner = GetSolidFromGeometry(gi.GetInstanceGeometry());
                    if (inner != null && (best == null || inner.Volume > best.Volume)) best = inner;
                }
            }
            return best;
        }

        private static List<BuiltInCategory> ResolveHostCategories(string filter) => filter switch
        {
            "walls" => new List<BuiltInCategory> { BuiltInCategory.OST_Walls },
            "floors" => new List<BuiltInCategory> { BuiltInCategory.OST_Floors },
            "roofs" => new List<BuiltInCategory> { BuiltInCategory.OST_Roofs },
            _ => new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs }
        };

        private static XYZ GetWallHorizontalDirection(Wall wall)
        {
            var lc = wall.Location as LocationCurve;
            if (lc?.Curve is Line line)
            {
                var dir = line.Direction;
                var horiz = new XYZ(dir.X, dir.Y, 0);
                if (horiz.GetLength() > 1e-9) return horiz.Normalize();
            }
            return XYZ.BasisX;
        }
    }
}
