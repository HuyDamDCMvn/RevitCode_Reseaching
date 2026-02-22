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
    public class MepConnectivitySkill : IRevitSkill
    {
        public string Name => "MepConnectivity";
        public string Description => "Inspect MEP connectors and trace connectivity paths between elements";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_connector_info", "get_system_connectivity"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "get_connector_info" => GetConnectorInfo(doc, args),
                "get_system_connectivity" => GetSystemConnectivity(doc, args),
                _ => JsonError($"MepConnectivitySkill: unknown tool '{functionName}'")
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

                connectors.Add(new
                {
                    index,
                    connector_type = conn.ConnectorType.ToString(),
                    domain = conn.Domain.ToString(),
                    shape = conn.Shape.ToString(),
                    size = BuildConnectorSize(conn),
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
