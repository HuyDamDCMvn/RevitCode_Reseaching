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
    public class WorksetPhaseSkill : IRevitSkill
    {
        public string Name => "WorksetPhase";
        public string Description => "Manage worksets, phases, and phase filters";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_worksets", "move_to_workset", "get_phases", "get_elements_by_phase", "set_phase"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_worksets",
                "List all user worksets in the document with element counts.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "include_counts": { "type": "boolean", "description": "Include element count per workset (default: true)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("move_to_workset",
                "Move elements to a specified workset. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to move" },
                        "workset_name": { "type": "string", "description": "Target workset name" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "workset_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_phases",
                "List all phases in the document with creation dates and element counts.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_elements_by_phase",
                "Get elements created or demolished in a specific phase.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "phase_name": { "type": "string", "description": "Phase name" },
                        "phase_status": { "type": "string", "enum": ["created", "demolished"], "description": "Filter by created or demolished phase (default: created)" },
                        "category": { "type": "string", "description": "Optional: filter by category" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": ["phase_name"]
                }
                """)),

            ChatTool.CreateFunctionTool("set_phase",
                "Set the Created Phase or Demolished Phase of elements. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs" },
                        "phase_name": { "type": "string", "description": "Phase name to assign" },
                        "phase_type": { "type": "string", "enum": ["created", "demolished"], "description": "Set 'created' or 'demolished' phase (default: created)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "phase_name"]
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
                "get_worksets" => GetWorksets(doc, args),
                "move_to_workset" => MoveToWorkset(doc, args),
                "get_phases" => GetPhases(doc),
                "get_elements_by_phase" => GetElementsByPhase(doc, args),
                "set_phase" => SetPhase(doc, args),
                _ => JsonError($"WorksetPhaseSkill: unknown tool '{functionName}'")
            };
        }

        private string GetWorksets(Document doc, Dictionary<string, object> args)
        {
            bool includeCounts = GetArg(args, "include_counts", true);

            if (!doc.IsWorkshared)
                return JsonSerializer.Serialize(new { workshared = false, message = "Document is not workshared. No worksets available." }, JsonOpts);

            var worksets = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToList();

            var items = worksets.Select(ws =>
            {
                int count = 0;
                if (includeCounts)
                {
                    var filter = new ElementWorksetFilter(ws.Id);
                    count = new FilteredElementCollector(doc)
                        .WherePasses(filter)
                        .GetElementCount();
                }

                return new
                {
                    id = ws.Id.IntegerValue,
                    name = ws.Name,
                    is_open = ws.IsOpen,
                    owner = ws.Owner,
                    element_count = includeCounts ? count : -1
                };
            }).ToList();

            return JsonSerializer.Serialize(new { workshared = true, workset_count = items.Count, worksets = items }, JsonOpts);
        }

        private string MoveToWorkset(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var wsName = GetArg<string>(args, "workset_name");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(wsName)) return JsonError("workset_name required.");

            if (!doc.IsWorkshared) return JsonError("Document is not workshared.");

            var workset = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(ws => ws.Name.Equals(wsName, StringComparison.OrdinalIgnoreCase));

            if (workset == null) return JsonError($"Workset '{wsName}' not found.");

            if (dryRun)
            {
                int wouldMove = 0, failed = 0;
                var previewErrors = new List<string>();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; previewErrors.Add($"Element {id} not found."); continue; }

                    var wsParam = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (wsParam == null || wsParam.IsReadOnly)
                    {
                        failed++; previewErrors.Add($"Element {id}: cannot change workset.");
                        continue;
                    }

                    wouldMove++;
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_move = wouldMove,
                    workset = wsName,
                    failed,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Move to Workset"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found."); continue; }

                    var wsParam = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (wsParam == null || wsParam.IsReadOnly)
                    {
                        errors.Add($"Element {id}: cannot change workset.");
                        continue;
                    }

                    wsParam.Set(workset.Id.IntegerValue);
                    success++;
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { success, workset = wsName, errors = errors.Take(10) }, JsonOpts);
        }

        private string GetPhases(Document doc)
        {
            var phases = new FilteredElementCollector(doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .ToList();

            var items = phases.Select(p => new
            {
                id = p.Id.Value,
                name = p.Name
            }).ToList();

            return JsonSerializer.Serialize(new { phase_count = items.Count, phases = items }, JsonOpts);
        }

        private string GetElementsByPhase(Document doc, Dictionary<string, object> args)
        {
            var phaseName = GetArg<string>(args, "phase_name");
            var phaseStatus = GetArg(args, "phase_status", "created");
            var catName = GetArg<string>(args, "category");
            int limit = GetArg(args, "limit", 50);

            var phase = new FilteredElementCollector(doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .FirstOrDefault(p => p.Name.IndexOf(phaseName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (phase == null) return JsonError($"Phase '{phaseName}' not found.");

            var bipParam = phaseStatus == "demolished"
                ? BuiltInParameter.PHASE_DEMOLISHED
                : BuiltInParameter.PHASE_CREATED;

            var pvp = new FilteredElementCollector(doc)
                .WherePasses(new ElementParameterFilter(
                    ParameterFilterRuleFactory.CreateEqualsRule(
                        new ElementId(bipParam), phase.Id)));

            if (!string.IsNullOrEmpty(catName))
            {
                var bic = ResolveCategoryFilter(doc, catName);
                if (bic.HasValue)
                    pvp = pvp.OfCategory(bic.Value);
            }

            var elements = pvp.WhereElementIsNotElementType().ToList();

            var items = elements.Take(limit).Select(e => new
            {
                id = e.Id.Value,
                name = e.Name,
                category = e.Category?.Name ?? "-"
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                phase = phase.Name,
                status = phaseStatus,
                total = elements.Count,
                returned = items.Count,
                elements = items
            }, JsonOpts);
        }

        private string SetPhase(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var phaseName = GetArg<string>(args, "phase_name");
            var phaseType = GetArg(args, "phase_type", "created");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(phaseName)) return JsonError("phase_name required.");

            var phase = new FilteredElementCollector(doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .FirstOrDefault(p => p.Name.IndexOf(phaseName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (phase == null) return JsonError($"Phase '{phaseName}' not found.");

            if (dryRun)
            {
                int wouldSet = 0, failed = 0;
                var previewErrors = new List<string>();
                var previewBipParam = phaseType == "demolished"
                    ? BuiltInParameter.PHASE_DEMOLISHED
                    : BuiltInParameter.PHASE_CREATED;

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; previewErrors.Add($"Element {id} not found."); continue; }

                    var param = elem.get_Parameter(previewBipParam);
                    if (param == null || param.IsReadOnly)
                    {
                        failed++; previewErrors.Add($"Element {id}: cannot set phase.");
                        continue;
                    }

                    wouldSet++;
                }

                return JsonSerializer.Serialize(new
                {
                    dry_run = true,
                    would_set = wouldSet,
                    phase = phase.Name,
                    phase_type = phaseType,
                    failed,
                    errors = previewErrors.Take(10)
                }, JsonOpts);
            }

            var bipParam = phaseType == "demolished"
                ? BuiltInParameter.PHASE_DEMOLISHED
                : BuiltInParameter.PHASE_CREATED;

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Set Phase"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { errors.Add($"Element {id} not found."); continue; }

                    var param = elem.get_Parameter(bipParam);
                    if (param == null || param.IsReadOnly)
                    {
                        errors.Add($"Element {id}: cannot set phase.");
                        continue;
                    }

                    param.Set(phase.Id);
                    success++;
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                success,
                phase = phase.Name,
                phase_type = phaseType,
                errors = errors.Take(10)
            }, JsonOpts);
        }
    }
}
