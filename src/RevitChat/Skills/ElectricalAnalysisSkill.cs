using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class ElectricalAnalysisSkill : BaseRevitSkill
    {
        protected override string SkillName => "ElectricalAnalysis";
        protected override string SkillDescription => "Analyze electrical panels, circuits, loads, voltage drop, and phase balance";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_panel_schedules", "get_circuit_loads", "check_panel_capacity",
            "get_voltage_drop", "get_phase_balance"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_panel_schedules",
                "List all electrical panels with load summaries.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "limit": { "type": "integer", "description": "Max panels (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_circuit_loads",
                "Get load breakdown per circuit for a panel.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "panel_id": { "type": "integer", "description": "Panel element ID" }
                    },
                    "required": ["panel_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("check_panel_capacity",
                "Check if any electrical panel exceeds its rated capacity.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_voltage_drop",
                "Estimate voltage drop for circuits based on length and load.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "panel_id": { "type": "integer", "description": "Optional panel filter" },
                        "max_drop_percent": { "type": "number", "description": "Max acceptable drop % (default 3.0)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_phase_balance",
                "Check phase balance across electrical panels.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "panel_id": { "type": "integer", "description": "Optional specific panel" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_panel_schedules" => GetPanelSchedules(doc, args),
                "get_circuit_loads" => GetCircuitLoads(doc, args),
                "check_panel_capacity" => CheckPanelCapacity(doc),
                "get_voltage_drop" => GetVoltageDrop(doc, args),
                "get_phase_balance" => GetPhaseBalance(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string GetPanelSchedules(Document doc, Dictionary<string, object> args)
        {
            int limit = GetArg(args, "limit", 50);
            var panels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType().ToList();

            var results = panels.Take(limit).Select(p =>
            {
                var totalLoad = p.LookupParameter("Total Connected Load")?.AsDouble() ?? 0;
                var totalDemand = p.LookupParameter("Total Demand Load")?.AsDouble() ?? 0;
                return new
                {
                    id = p.Id.Value, name = p.Name,
                    family = GetFamilyName(doc, p),
                    total_connected_va = Math.Round(totalLoad, 0),
                    total_demand_va = Math.Round(totalDemand, 0)
                };
            }).ToList();

            return JsonSerializer.Serialize(new { panel_count = results.Count, panels = results }, JsonOpts);
        }

        private string GetCircuitLoads(Document doc, Dictionary<string, object> args)
        {
            long panelId = GetArg<long>(args, "panel_id");
            if (panelId <= 0) return JsonError("panel_id required.");

            var panel = doc.GetElement(new ElementId(panelId));
            if (panel == null) return JsonError($"Panel {panelId} not found.");

            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .Where(c => c.BaseEquipment?.Id.Value == panelId)
                .ToList();

            var results = circuits.Select(c => new
            {
                id = c.Id.Value,
                circuit_number = c.CircuitNumber,
                load_name = c.LoadName,
                apparent_load_va = Math.Round(c.ApparentLoad, 0),
                voltage = c.Voltage,
                num_poles = c.PolesNumber
            }).ToList();

            return JsonSerializer.Serialize(new { panel_id = panelId, circuit_count = results.Count, circuits = results }, JsonOpts);
        }

        private string CheckPanelCapacity(Document doc)
        {
            var panels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType().ToList();

            var results = new List<object>();
            foreach (var p in panels)
            {
                var totalLoad = p.LookupParameter("Total Connected Load")?.AsDouble() ?? 0;
                var ratedParam = p.LookupParameter("Max #1 Pole Breakers");
                int rated = ratedParam?.AsInteger() ?? 0;

                var circuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Where(c => c.BaseEquipment?.Id.Value == p.Id.Value)
                    .ToList();

                results.Add(new
                {
                    id = p.Id.Value, name = p.Name,
                    total_load_va = Math.Round(totalLoad, 0),
                    circuit_count = circuits.Count,
                    max_breaker_slots = rated,
                    utilization_percent = rated > 0 ? Math.Round((double)circuits.Count / rated * 100, 1) : 0
                });
            }

            return JsonSerializer.Serialize(new { panel_count = results.Count, panels = results }, JsonOpts);
        }

        /// <summary>
        /// Vd = (2 × L × I × R) / V  simplified with R≈0.0175 Ω·mm²/m for copper.
        /// Revit stores Length in feet, so we convert. This is an estimate — actual
        /// drop depends on wire gauge loaded from the circuit's wire type.
        /// </summary>
        private string GetVoltageDrop(Document doc, Dictionary<string, object> args)
        {
            long panelId = GetArg<long>(args, "panel_id");
            double maxDrop = GetArg(args, "max_drop_percent", 3.0);

            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>().ToList();

            if (panelId > 0)
                circuits = circuits.Where(c => c.BaseEquipment?.Id.Value == panelId).ToList();

            var results = circuits.Select(c =>
            {
                double lengthFt = c.Length;
                double lengthM = lengthFt * 0.3048;
                double load = c.ApparentLoad;
                double voltage = c.Voltage > 0 ? c.Voltage : 220;

                double current = voltage > 0 ? load / voltage : 0;

                // Wire cross-section: try Revit param, fallback 2.5 mm² (typical residential)
                double wireSizeMm2 = 2.5;
                var wireParam = c.LookupParameter("Wire Size");
                if (wireParam != null && wireParam.HasValue)
                {
                    if (double.TryParse(
                            System.Text.RegularExpressions.Regex.Match(
                                wireParam.AsValueString() ?? "", @"[\d.]+").Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double parsed) && parsed > 0)
                        wireSizeMm2 = parsed;
                }

                // Vd = 2 × L × I × ρ / A  (ρ copper = 0.0175 Ω·mm²/m)
                const double resistivityCopper = 0.0175;
                double vDrop = wireSizeMm2 > 0
                    ? 2 * lengthM * current * resistivityCopper / wireSizeMm2
                    : 0;
                double dropPercent = voltage > 0 ? vDrop / voltage * 100 : 0;

                return new
                {
                    circuit_id = c.Id.Value,
                    circuit_number = c.CircuitNumber,
                    length_ft = Math.Round(lengthFt, 1),
                    length_m = Math.Round(lengthM, 1),
                    load_va = Math.Round(load, 0),
                    current_a = Math.Round(current, 2),
                    voltage,
                    wire_size_mm2 = wireSizeMm2,
                    estimated_drop_v = Math.Round(vDrop, 2),
                    estimated_drop_percent = Math.Round(dropPercent, 2),
                    exceeds_limit = dropPercent > maxDrop,
                    note = "Estimate using copper ρ=0.0175. Actual depends on wire material & temperature. / Ước lượng dùng đồng ρ=0.0175. Thực tế phụ thuộc vật liệu & nhiệt độ."
                };
            }).OrderByDescending(x => x.estimated_drop_percent).Take(50).ToList();

            int overLimit = results.Count(x => x.exceeds_limit);
            return JsonSerializer.Serialize(new
            {
                max_drop_percent = maxDrop,
                circuits_checked = results.Count,
                over_limit = overLimit,
                circuits = results
            }, JsonOpts);
        }

        /// <summary>
        /// Read actual phase assignment from Revit's StartSlot parameter to get
        /// real phase distribution instead of assuming all 1-pole → Phase A.
        /// </summary>
        private string GetPhaseBalance(Document doc, Dictionary<string, object> args)
        {
            long panelId = GetArg<long>(args, "panel_id");
            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>().ToList();

            if (panelId > 0)
                circuits = circuits.Where(c => c.BaseEquipment?.Id.Value == panelId).ToList();

            double phaseA = 0, phaseB = 0, phaseC = 0;
            int unresolved = 0;

            foreach (var c in circuits)
            {
                double load = c.ApparentLoad;
                int poles = c.PolesNumber;

                // Try reading the starting slot to determine actual phase assignment
                int startSlot = -1;
                var slotParam = c.LookupParameter("Start Slot");
                if (slotParam != null && slotParam.HasValue && slotParam.StorageType == StorageType.Integer)
                    startSlot = slotParam.AsInteger();

                if (poles == 1 && startSlot > 0)
                {
                    // Panels alternate phases: slot 1,4,7...=A  slot 2,5,8...=B  slot 3,6,9...=C
                    int phaseIdx = (startSlot - 1) % 3;
                    if (phaseIdx == 0) phaseA += load;
                    else if (phaseIdx == 1) phaseB += load;
                    else phaseC += load;
                }
                else if (poles == 1)
                {
                    // Fallback: round-robin distribution when slot info unavailable
                    int idx = unresolved % 3;
                    if (idx == 0) phaseA += load;
                    else if (idx == 1) phaseB += load;
                    else phaseC += load;
                    unresolved++;
                }
                else if (poles == 2)
                {
                    if (startSlot > 0)
                    {
                        int p1 = (startSlot - 1) % 3;
                        int p2 = startSlot % 3;
                        AddToPhase(ref phaseA, ref phaseB, ref phaseC, p1, load / 2);
                        AddToPhase(ref phaseA, ref phaseB, ref phaseC, p2, load / 2);
                    }
                    else
                    {
                        phaseA += load / 2; phaseB += load / 2;
                    }
                }
                else
                {
                    phaseA += load / 3; phaseB += load / 3; phaseC += load / 3;
                }
            }

            double total = phaseA + phaseB + phaseC;
            double avg = total / 3;
            double maxImbalance = total > 0
                ? Math.Max(Math.Max(Math.Abs(phaseA - avg), Math.Abs(phaseB - avg)), Math.Abs(phaseC - avg)) / avg * 100
                : 0;

            return JsonSerializer.Serialize(new
            {
                circuit_count = circuits.Count,
                phase_a_va = Math.Round(phaseA, 0),
                phase_b_va = Math.Round(phaseB, 0),
                phase_c_va = Math.Round(phaseC, 0),
                total_va = Math.Round(total, 0),
                max_imbalance_percent = Math.Round(maxImbalance, 1),
                unresolved_slot_circuits = unresolved,
                note = unresolved > 0
                    ? $"{unresolved} circuit(s) missing slot info — used round-robin. / {unresolved} mạch thiếu thông tin slot — phân bổ luân phiên."
                    : "Phase assignment read from Start Slot. / Phân pha đọc từ Start Slot."
            }, JsonOpts);
        }

        private static void AddToPhase(ref double a, ref double b, ref double c, int phaseIdx, double load)
        {
            if (phaseIdx == 0) a += load;
            else if (phaseIdx == 1) b += load;
            else c += load;
        }
    }
}
