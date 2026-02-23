using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class MepFittingSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepFitting";
        protected override string SkillDescription => "Create MEP fittings: elbow, tap/takeoff, coupling, and bloom (extend unused connectors)";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "create_elbow", "create_tap_connection",
            "bloom_connectors", "insert_coupling"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("create_elbow",
                "Create an elbow fitting between two non-collinear MEP elements. Connects their nearest unused connectors via a routed elbow.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id_1": { "type": "integer", "description": "First MEP element ID" },
                        "element_id_2": { "type": "integer", "description": "Second MEP element ID" }
                    },
                    "required": ["element_id_1", "element_id_2"]
                }
                """)),

            ChatTool.CreateFunctionTool("create_tap_connection",
                "Create a tap/takeoff from a main duct or pipe to a branch element. The main element must be a duct/pipe, and the branch is tapped into it.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "main_id": { "type": "integer", "description": "Main duct/pipe element ID (the one that gets tapped)" },
                        "branch_id": { "type": "integer", "description": "Branch element ID to connect via tap" }
                    },
                    "required": ["main_id", "branch_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("bloom_connectors",
                "Extend unused connectors on MEP elements by creating short duct/pipe extensions. Useful for capping or preparing elements for future connections.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to bloom" },
                        "extension_mm": { "type": "number", "description": "Extension length in mm. 0 = auto-calculate based on connector size. Default 0." },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("insert_coupling",
                "Insert a coupling or union fitting between two connected piping elements. Breaks the connection and inserts the fitting.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id_1": { "type": "integer", "description": "First pipe element ID" },
                        "element_id_2": { "type": "integer", "description": "Second pipe element ID" }
                    },
                    "required": ["element_id_1", "element_id_2"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "create_elbow" => CreateElbow(doc, args),
                "create_tap_connection" => CreateTapConnection(doc, args),
                "bloom_connectors" => BloomConnectors(doc, args),
                "insert_coupling" => InsertCoupling(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        #region create_elbow

        private string CreateElbow(Document doc, Dictionary<string, object> args)
        {
            var id1 = GetArg<long>(args, "element_id_1");
            var id2 = GetArg<long>(args, "element_id_2");

            var elem1 = doc.GetElement(new ElementId(id1));
            var elem2 = doc.GetElement(new ElementId(id2));
            if (elem1 == null || elem2 == null)
                return JsonError("One or both elements not found.");

            if (!TryGetConnectorManager(elem1, out var cm1) || !TryGetConnectorManager(elem2, out var cm2))
                return JsonError("One or both elements do not expose connectors.");

            var (c1, c2) = FindClosestUnusedPair(cm1, cm2);
            if (c1 == null || c2 == null)
                return JsonError("No unused connector pair found.");

            using var trans = new Transaction(doc, "AI: Create Elbow");
            trans.Start();
            try
            {
                doc.Create.NewElbowFitting(c1, c2);
                trans.Commit();
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    element_1 = id1,
                    element_2 = id2,
                    message = "Elbow fitting created."
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                return JsonError($"Failed to create elbow: {ex.Message}. Ensure elements are non-collinear and have compatible connectors.");
            }
        }

        #endregion

        #region create_tap_connection

        private string CreateTapConnection(Document doc, Dictionary<string, object> args)
        {
            var mainId = GetArg<long>(args, "main_id");
            var branchId = GetArg<long>(args, "branch_id");

            var mainElem = doc.GetElement(new ElementId(mainId));
            var branchElem = doc.GetElement(new ElementId(branchId));
            if (mainElem == null || branchElem == null)
                return JsonError("Main or branch element not found.");

            if (!TryGetConnectorManager(branchElem, out var branchCm))
                return JsonError("Branch element does not expose connectors.");

            Connector branchConn = null;
            double bestDist = double.MaxValue;

            var mainBB = mainElem.get_BoundingBox(null);
            if (mainBB == null) return JsonError("Cannot get bounding box of main element.");
            var mainCenter = (mainBB.Min + mainBB.Max) / 2.0;

            foreach (Connector c in branchCm.Connectors)
            {
                if (c.IsConnected) continue;
                double d = c.Origin.DistanceTo(mainCenter);
                if (d < bestDist) { bestDist = d; branchConn = c; }
            }

            if (branchConn == null)
                return JsonError("No unused connector on branch element.");

            using var trans = new Transaction(doc, "AI: Create Tap");
            trans.SetFailureHandlingOptions(trans.GetFailureHandlingOptions()
                .SetFailuresPreprocessor(new SilentFailureProcessor()));
            trans.Start();
            try
            {
                if (mainElem is Duct duct)
                {
                    doc.Create.NewTakeoffFitting(branchConn, duct as MEPCurve);
                }
                else if (mainElem is Pipe pipe)
                {
                    doc.Create.NewTakeoffFitting(branchConn, pipe as MEPCurve);
                }
                else
                {
                    trans.RollBack();
                    return JsonError("Main element must be a Duct or Pipe for tap connections.");
                }

                trans.Commit();
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    main = mainId,
                    branch = branchId,
                    message = "Tap/takeoff fitting created."
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                return JsonError($"Failed to create tap: {ex.Message}. Ensure the branch connector is close enough to the main element.");
            }
        }

        #endregion

        #region bloom_connectors

        private string BloomConnectors(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0)
                return JsonError("element_ids required.");

            double extMm = GetArg(args, "extension_mm", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);
            const double mmToFt = 1.0 / 304.8;

            var preview = new List<object>();
            int totalUnused = 0;

            foreach (var id in ids)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;
                if (!TryGetConnectorManager(elem, out var cm)) continue;

                foreach (Connector c in cm.Connectors)
                {
                    if (c.IsConnected) continue;
                    if (c.ConnectorType != ConnectorType.End) continue;

                    double lengthFt = extMm > 0
                        ? extMm * mmToFt
                        : GetExtensionLengthFt(c);

                    totalUnused++;
                    preview.Add(new
                    {
                        element_id = id,
                        connector_origin = new
                        {
                            x = Math.Round(c.Origin.X * 304.8, 1),
                            y = Math.Round(c.Origin.Y * 304.8, 1),
                            z = Math.Round(c.Origin.Z * 304.8, 1)
                        },
                        extension_mm = Math.Round(lengthFt * 304.8, 1),
                        domain = c.Domain.ToString(),
                        shape = c.Shape.ToString()
                    });
                }
            }

            if (totalUnused == 0)
                return JsonSerializer.Serialize(new { message = "No unused end connectors found.", count = 0 }, JsonOpts);

            if (dryRun)
                return JsonSerializer.Serialize(new { dry_run = true, unused_connectors = totalUnused, details = preview }, JsonOpts);

            int created = 0;
            var errors = new List<string>();

            // Pre-cache collectors outside loops (HIGH #6 fix)
            var fallbackLevelId = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).FirstElementId();
            if (fallbackLevelId == ElementId.InvalidElementId)
                return JsonError("No levels in project; cannot create extensions.");

            var pipeTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType)).FirstElementId();
            var pipingSysTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType)).FirstElementId();

            // Cache duct types per shape
            var ductTypeCache = new Dictionary<ConnectorProfileType, ElementId>();

            using var trans = new Transaction(doc, "AI: Bloom Connectors");
            trans.SetFailureHandlingOptions(trans.GetFailureHandlingOptions()
                .SetFailuresPreprocessor(new SilentFailureProcessor()));
            trans.Start();

            foreach (var id in ids)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;
                if (!TryGetConnectorManager(elem, out var cm)) continue;

                foreach (Connector c in cm.Connectors)
                {
                    if (c.IsConnected || c.ConnectorType != ConnectorType.End) continue;

                    double lengthFt = extMm > 0 ? extMm * mmToFt : GetExtensionLengthFt(c);
                    XYZ direction = c.CoordinateSystem.BasisZ;
                    XYZ endPoint = c.Origin + direction * lengthFt;

                    try
                    {
                        ElementId newId = ElementId.InvalidElementId;
                        var levelId = elem.LevelId != ElementId.InvalidElementId
                            ? elem.LevelId : fallbackLevelId;

                        if (c.Domain == Domain.DomainHvac)
                        {
                            if (!ductTypeCache.TryGetValue(c.Shape, out var ductTypeId))
                            {
                                ductTypeId = GetDuctTypeForShape(doc, c.Shape);
                                ductTypeCache[c.Shape] = ductTypeId;
                            }
                            if (ductTypeId == ElementId.InvalidElementId)
                            {
                                errors.Add($"No matching duct type for shape {c.Shape}");
                                continue;
                            }

                            var duct = Duct.Create(doc, ductTypeId, levelId, c, endPoint);
                            newId = duct.Id;
                        }
                        else if (c.Domain == Domain.DomainPiping)
                        {
                            if (pipeTypeId == ElementId.InvalidElementId)
                            {
                                errors.Add("No PipeType found in project.");
                                continue;
                            }
                            if (pipingSysTypeId == ElementId.InvalidElementId)
                            {
                                errors.Add("No PipingSystemType found in project.");
                                continue;
                            }

                            var pipe = Pipe.Create(doc, pipingSysTypeId, pipeTypeId,
                                levelId, c.Origin, endPoint);
                            newId = pipe.Id;

                            if (TryGetConnectorManager(pipe, out var pipeCm))
                            {
                                Connector pipeEnd = null;
                                double closest = double.MaxValue;
                                foreach (Connector pc in pipeCm.Connectors)
                                {
                                    double d = pc.Origin.DistanceTo(c.Origin);
                                    if (d < closest) { closest = d; pipeEnd = pc; }
                                }
                                if (pipeEnd != null && !pipeEnd.IsConnected)
                                    pipeEnd.ConnectTo(c);
                            }
                        }
                        else
                        {
                            errors.Add($"Domain {c.Domain} not supported for bloom.");
                            continue;
                        }

                        if (newId != ElementId.InvalidElementId)
                            created++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Element {id}: {ex.Message}");
                    }
                }
            }

            if (created > 0)
                trans.Commit();
            else
                trans.RollBack();

            return JsonSerializer.Serialize(new
            {
                status = created > 0 ? "ok" : "no_change",
                extensions_created = created,
                errors = errors.Count > 0 ? errors : null
            }, JsonOpts);
        }

        #endregion

        #region insert_coupling

        private string InsertCoupling(Document doc, Dictionary<string, object> args)
        {
            var id1 = GetArg<long>(args, "element_id_1");
            var id2 = GetArg<long>(args, "element_id_2");

            var elem1 = doc.GetElement(new ElementId(id1));
            var elem2 = doc.GetElement(new ElementId(id2));
            if (elem1 == null || elem2 == null)
                return JsonError("One or both elements not found.");

            if (!TryGetConnectorManager(elem1, out var cm1) || !TryGetConnectorManager(elem2, out var cm2))
                return JsonError("One or both elements do not expose connectors.");

            Connector connA = null, connB = null;
            foreach (Connector c1 in cm1.Connectors)
            {
                if (!c1.IsConnected) continue;
                foreach (Connector c2 in c1.AllRefs)
                {
                    if (c2?.Owner != null && c2.Owner.Id.Value == id2)
                    {
                        connA = c1;
                        connB = c2;
                        break;
                    }
                }
                if (connA != null) break;
            }

            if (connA == null || connB == null)
            {
                var (uc1, uc2) = FindClosestUnusedPair(cm1, cm2);
                if (uc1 == null || uc2 == null)
                    return JsonError("Elements are neither connected nor have nearby unused connectors.");
                connA = uc1;
                connB = uc2;
            }

            using var trans = new Transaction(doc, "AI: Insert Coupling");
            trans.SetFailureHandlingOptions(trans.GetFailureHandlingOptions()
                .SetFailuresPreprocessor(new SilentFailureProcessor()));
            trans.Start();
            try
            {
                if (connA.IsConnected)
                    connA.DisconnectFrom(connB);

                var coupling = doc.Create.NewUnionFitting(connA, connB);
                if (coupling == null)
                {
                    trans.RollBack();
                    return JsonError("Revit could not create a union/coupling fitting at this location. Ensure elements are piping and properly aligned.");
                }

                trans.Commit();
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    coupling_id = coupling.Id.Value,
                    between = new[] { id1, id2 },
                    message = "Coupling/union fitting inserted."
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                return JsonError($"Failed to insert coupling: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private static bool TryGetConnectorManager(Element elem, out ConnectorManager cm)
        {
            cm = null;
            if (elem is FamilyInstance fi) cm = fi.MEPModel?.ConnectorManager;
            else if (elem is MEPCurve mc) cm = mc.ConnectorManager;
            return cm != null;
        }

        private static (Connector, Connector) FindClosestUnusedPair(ConnectorManager cm1, ConnectorManager cm2)
        {
            Connector best1 = null, best2 = null;
            double bestDist = double.MaxValue;

            foreach (Connector c1 in cm1.Connectors)
            {
                if (c1.IsConnected) continue;
                foreach (Connector c2 in cm2.Connectors)
                {
                    if (c2.IsConnected) continue;
                    if (c1.Domain != c2.Domain) continue;
                    double d = c1.Origin.DistanceTo(c2.Origin);
                    bool aligned = AreConnectorsAligned(c1, c2);
                    double score = aligned ? d * 0.5 : d;
                    if (score < bestDist)
                    {
                        bestDist = score;
                        best1 = c1;
                        best2 = c2;
                    }
                }
            }

            return (best1, best2);
        }

        #endregion
    }
}
