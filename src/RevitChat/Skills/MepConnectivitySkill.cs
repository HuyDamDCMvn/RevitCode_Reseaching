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
    public class MepConnectivitySkill : BaseRevitSkill
    {
        protected override string SkillName => "MepConnectivity";
        protected override string SkillDescription => "Inspect MEP connectors and trace connectivity paths between elements";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_connector_info", "get_system_connectivity",
            "get_mep_routing_path", "connect_mep_elements"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_connector_info",
                "Get connector details (shape, size, connected elements) for a single MEP element.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id": { "type": "integer", "description": "Element ID to inspect" }
                    },
                    "required": ["element_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_system_connectivity",
                "Trace connectivity graph starting from an element. Returns connected elements and edges.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id": { "type": "integer", "description": "Starting element ID" },
                        "system_name": { "type": "string", "description": "Optional system name filter (partial match)" },
                        "max_depth": { "type": "integer", "description": "Max traversal depth. Default 50.", "default": 50 },
                        "max_elements": { "type": "integer", "description": "Max elements in graph. Default 200.", "default": 200 }
                    },
                    "required": ["element_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_mep_routing_path",
                "Trace an ordered routing path from a start element to the end of the run. Returns elements in order with cumulative distance and elevation.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id": { "type": "integer", "description": "Starting element ID" },
                        "max_elements": { "type": "integer", "description": "Max elements in path. Default 100.", "default": 100 }
                    },
                    "required": ["element_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("connect_mep_elements",
                "Connect two nearby MEP elements that have unconnected connectors at the same location (within 10mm tolerance).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_id_1": { "type": "integer", "description": "First element ID" },
                        "element_id_2": { "type": "integer", "description": "Second element ID" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["element_id_1", "element_id_2"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_connector_info" => GetConnectorInfo(doc, args),
                "get_system_connectivity" => GetSystemConnectivity(doc, args),
                "get_mep_routing_path" => GetMepRoutingPath(doc, args),
                "connect_mep_elements" => ConnectMepElements(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetConnectorInfo(Document doc, Dictionary<string, object> args)
        {
            var elementId = GetArg<long>(args, "element_id");
            var elem = doc.GetElement(new ElementId(elementId));
            if (elem == null) return JsonError($"Element {elementId} not found.");

            if (!TryGetConnectorManager(elem, out var cm))
                return JsonError("Element does not expose connectors.");

            var connectors = new List<object>();
            int index = 0;

            foreach (Connector conn in cm.Connectors)
            {
                var connectedTo = new List<object>();
                var connectedIds = new HashSet<long>();

                if (conn.IsConnected)
                {
                    foreach (Connector refConn in conn.AllRefs)
                    {
                        var owner = refConn?.Owner as Element;
                        if (owner == null || owner.Id == elem.Id) continue;
                        if (owner.Document != doc) continue;

                        if (connectedIds.Add(owner.Id.Value))
                        {
                            connectedTo.Add(new
                            {
                                id = owner.Id.Value,
                                category = owner.Category?.Name ?? "-",
                                type = GetElementTypeName(doc, owner)
                            });
                        }
                    }
                }

                var origin = conn.Origin;
                var dir = conn.CoordinateSystem.BasisZ;
                string orientation = Math.Abs(dir.Z) > 0.9 ? (dir.Z > 0 ? "up" : "down") : "horizontal";

                connectors.Add(new
                {
                    index,
                    connector_type = conn.ConnectorType.ToString(),
                    domain = conn.Domain.ToString(),
                    shape = conn.Shape.ToString(),
                    size = BuildConnectorSize(conn),
                    position = new { x = Math.Round(origin.X * 304.8, 1), y = Math.Round(origin.Y * 304.8, 1), z = Math.Round(origin.Z * 304.8, 1) },
                    direction = new { x = Math.Round(dir.X, 3), y = Math.Round(dir.Y, 3), z = Math.Round(dir.Z, 3) },
                    orientation,
                    flow = GetFlowDirection(conn),
                    system_name = GetConnectorSystemName(conn),
                    is_connected = conn.IsConnected,
                    connected_to = connectedTo
                });

                index++;
            }

            return JsonSerializer.Serialize(new
            {
                element_id = elementId,
                category = elem.Category?.Name ?? "-",
                type = GetElementTypeName(doc, elem),
                connector_count = connectors.Count,
                connectors
            }, JsonOpts);
        }

        private string GetSystemConnectivity(Document doc, Dictionary<string, object> args)
        {
            var elementId = GetArg<long>(args, "element_id");
            var start = doc.GetElement(new ElementId(elementId));
            if (start == null) return JsonError($"Element {elementId} not found.");

            var systemFilter = GetArg<string>(args, "system_name");
            int maxDepth = GetArg(args, "max_depth", 50);
            int maxElements = GetArg(args, "max_elements", 200);
            if (maxDepth < 1) maxDepth = 1;
            if (maxElements < 10) maxElements = 10;

            var queue = new Queue<(Element elem, int depth)>();
            var visited = new HashSet<long>();
            var elements = new List<object>();
            var edges = new List<object>();
            var openEnds = new List<object>();
            var edgeKeys = new HashSet<string>();
            bool truncated = false;

            if (!MatchesSystemFilter(start, systemFilter, allowEmpty: false))
                return JsonError($"Start element {elementId} does not match system filter.");

            queue.Enqueue((start, 0));
            visited.Add(start.Id.Value);

            while (queue.Count > 0)
            {
                if (elements.Count >= maxElements) { truncated = true; break; }

                var (elem, depth) = queue.Dequeue();
                elements.Add(MapElement(doc, elem));

                if (!TryGetConnectorManager(elem, out var cm))
                    continue;

                int connectorIndex = 0;
                foreach (Connector conn in cm.Connectors)
                {
                    if (conn.ConnectorType == ConnectorType.End && !conn.IsConnected)
                    {
                        openEnds.Add(new { element_id = elem.Id.Value, connector_index = connectorIndex });
                    }

                    if (!conn.IsConnected)
                    {
                        connectorIndex++;
                        continue;
                    }

                    foreach (Connector refConn in conn.AllRefs)
                    {
                        var owner = refConn?.Owner as Element;
                        if (owner == null || owner.Id == elem.Id) continue;
                        if (owner.Document != doc) continue;
                        if (!MatchesSystemFilter(owner, systemFilter, allowEmpty: true)) continue;

                        var edgeKey = BuildEdgeKey(elem.Id.Value, owner.Id.Value);
                        if (edgeKeys.Add(edgeKey))
                        {
                            edges.Add(new { from_id = elem.Id.Value, to_id = owner.Id.Value });
                        }

                        if (depth < maxDepth && visited.Add(owner.Id.Value))
                        {
                            if (visited.Count >= maxElements) { truncated = true; break; }
                            queue.Enqueue((owner, depth + 1));
                        }
                    }

                    if (truncated) break;
                    connectorIndex++;
                }

                if (truncated) break;
            }

            return JsonSerializer.Serialize(new
            {
                start_element_id = elementId,
                system_filter = string.IsNullOrEmpty(systemFilter) ? null : systemFilter,
                element_count = elements.Count,
                connection_count = edges.Count,
                open_end_count = openEnds.Count,
                truncated,
                elements,
                connections = edges,
                open_ends = openEnds
            }, JsonOpts);
        }

        private string GetMepRoutingPath(Document doc, Dictionary<string, object> args)
        {
            var elementId = GetArg<long>(args, "element_id");
            int maxElements = GetArg(args, "max_elements", 100);
            if (maxElements < 2) maxElements = 2;

            var start = doc.GetElement(new ElementId(elementId));
            if (start == null) return JsonError($"Element {elementId} not found.");

            var path = new List<object>();
            var visited = new HashSet<long> { start.Id.Value };
            double cumulativeDistM = 0;

            var current = start;
            path.Add(BuildPathNode(doc, current, cumulativeDistM));

            while (path.Count < maxElements)
            {
                if (!TryGetConnectorManager(current, out var cm)) break;

                var currentLen = current.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
                Element next = null;

                foreach (Connector conn in cm.Connectors)
                {
                    if (!conn.IsConnected) continue;
                    foreach (Connector refConn in conn.AllRefs)
                    {
                        var owner = refConn?.Owner as Element;
                        if (owner == null || owner.Document != doc) continue;
                        if (!visited.Add(owner.Id.Value)) continue;
                        next = owner;
                        break;
                    }
                    if (next != null) break;
                }

                if (next == null) break;

                cumulativeDistM += currentLen * 0.3048;
                current = next;
                path.Add(BuildPathNode(doc, current, Math.Round(cumulativeDistM, 3)));
            }

            return JsonSerializer.Serialize(new
            {
                start_element_id = elementId,
                path_length = path.Count,
                total_distance_m = Math.Round(cumulativeDistM, 3),
                path
            }, JsonOpts);
        }

        private static object BuildPathNode(Document doc, Element elem, double cumulativeDistM)
        {
            double elevM = 0;
            if (elem.Location is LocationPoint lp) elevM = lp.Point.Z * 0.3048;
            else if (elem.Location is LocationCurve lc)
            {
                var mid = lc.Curve.Evaluate(0.5, true);
                elevM = mid.Z * 0.3048;
            }

            return new
            {
                id = elem.Id.Value,
                category = elem.Category?.Name ?? "-",
                type = GetElementTypeName(doc, elem),
                size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-",
                level = GetElementLevel(doc, elem),
                elevation_m = Math.Round(elevM, 3),
                cumulative_distance_m = cumulativeDistM
            };
        }

        private string ConnectMepElements(Document doc, Dictionary<string, object> args)
        {
            var id1 = GetArg<long>(args, "element_id_1");
            var id2 = GetArg<long>(args, "element_id_2");
            bool dryRun = GetArg(args, "dry_run", false);

            var elem1 = doc.GetElement(new ElementId(id1));
            var elem2 = doc.GetElement(new ElementId(id2));
            if (elem1 == null) return JsonError($"Element {id1} not found.");
            if (elem2 == null) return JsonError($"Element {id2} not found.");

            if (!TryGetConnectorManager(elem1, out var cm1))
                return JsonError($"Element {id1} has no connectors.");
            if (!TryGetConnectorManager(elem2, out var cm2))
                return JsonError($"Element {id2} has no connectors.");

            const double toleranceFt = 10.0 / 304.8;
            Connector bestAlignedC1 = null, bestAlignedC2 = null;
            double bestAlignedDist = double.MaxValue;
            Connector bestAnyC1 = null, bestAnyC2 = null;
            double bestAnyDist = double.MaxValue;

            foreach (Connector c1 in cm1.Connectors)
            {
                if (c1.IsConnected) continue;
                foreach (Connector c2 in cm2.Connectors)
                {
                    if (c2.IsConnected) continue;
                    if (c1.Domain != c2.Domain) continue;

                    double dist = c1.Origin.DistanceTo(c2.Origin);

                    if (dist < bestAnyDist)
                    {
                        bestAnyDist = dist;
                        bestAnyC1 = c1;
                        bestAnyC2 = c2;
                    }

                    double dot = c1.CoordinateSystem.BasisZ.DotProduct(c2.CoordinateSystem.BasisZ);
                    bool aligned = dot < -0.5;
                    if (aligned && dist < bestAlignedDist)
                    {
                        bestAlignedDist = dist;
                        bestAlignedC1 = c1;
                        bestAlignedC2 = c2;
                    }
                }
            }

            var bestC1 = bestAlignedC1 ?? bestAnyC1;
            var bestC2 = bestAlignedC2 ?? bestAnyC2;
            double bestDist = bestAlignedC1 != null ? bestAlignedDist : bestAnyDist;
            bool isAligned = bestAlignedC1 != null;

            if (bestC1 == null || bestC2 == null)
                return JsonError("No compatible unconnected connector pair found.");

            double distMm = Math.Round(bestDist * 304.8, 1);

            if (bestDist > toleranceFt)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "too_far",
                    distance_mm = distMm,
                    aligned = isAligned,
                    message = $"Closest connectors are {distMm}mm apart (tolerance: 10mm). Move elements closer first.",
                    connector_1 = new { element_id = id1, domain = bestC1.Domain.ToString(), shape = bestC1.Shape.ToString() },
                    connector_2 = new { element_id = id2, domain = bestC2.Domain.ToString(), shape = bestC2.Shape.ToString() }
                }, JsonOpts);
            }

            if (dryRun)
            {
                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    status = "can_connect",
                    distance_mm = distMm,
                    aligned = isAligned,
                    connector_1 = new { element_id = id1, domain = bestC1.Domain.ToString(), shape = bestC1.Shape.ToString() },
                    connector_2 = new { element_id = id2, domain = bestC2.Domain.ToString(), shape = bestC2.Shape.ToString() }
                }, JsonOpts);
            }

            using var trans = new Transaction(doc, "AI: Connect MEP Elements");
            trans.Start();
            try
            {
                bestC1.ConnectTo(bestC2);
                trans.Commit();
                return JsonSerializer.Serialize(new
                {
                    status = "connected",
                    element_id_1 = id1,
                    element_id_2 = id2,
                    distance_mm = distMm
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                return JsonError($"Connect failed: {ex.Message}");
            }
        }

        private static bool TryGetConnectorManager(Element elem, out ConnectorManager cm)
        {
            cm = null;
            if (elem is MEPCurve mc && mc.ConnectorManager != null)
            {
                cm = mc.ConnectorManager;
                return true;
            }

            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                cm = fi.MEPModel.ConnectorManager;
                return true;
            }

            return false;
        }

        private static bool MatchesSystemFilter(Element elem, string systemFilter, bool allowEmpty)
        {
            if (string.IsNullOrWhiteSpace(systemFilter)) return true;
            var sys = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
            if (string.IsNullOrWhiteSpace(sys)) return allowEmpty;
            return sys.IndexOf(systemFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object MapElement(Document doc, Element elem)
        {
            var size = elem.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "-";
            var system = elem.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "-";

            return new
            {
                id = elem.Id.Value,
                category = elem.Category?.Name ?? "-",
                type = GetElementTypeName(doc, elem),
                level = GetElementLevel(doc, elem),
                system,
                size
            };
        }

        private static string BuildEdgeKey(long a, long b)
        {
            return a < b ? $"{a}-{b}" : $"{b}-{a}";
        }

        private static string GetFlowDirection(Connector conn)
        {
            try { return conn.Direction.ToString(); }
            catch { return "-"; }
        }

        private static string GetConnectorSystemName(Connector conn)
        {
            try { return conn.MEPSystem?.Name ?? "-"; }
            catch { return "-"; }
        }

        private static object BuildConnectorSize(Connector conn)
        {
            const double ftToMm = 304.8;
            switch (conn.Shape)
            {
                case ConnectorProfileType.Round:
                    return new
                    {
                        diameter_mm = Math.Round(conn.Radius * 2 * ftToMm, 2)
                    };
                case ConnectorProfileType.Rectangular:
                    return new
                    {
                        width_mm = Math.Round(conn.Width * ftToMm, 2),
                        height_mm = Math.Round(conn.Height * ftToMm, 2)
                    };
                case ConnectorProfileType.Oval:
                    return new
                    {
                        width_mm = Math.Round(conn.Width * ftToMm, 2),
                        height_mm = Math.Round(conn.Height * ftToMm, 2)
                    };
                default:
                    return null;
            }
        }
    }
}
