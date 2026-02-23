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
    public class MepModelerSkill : BaseRevitSkill
    {
        protected override string SkillName => "MepModeler";
        protected override string SkillDescription => "Modify MEP elements: resize, split, set slope, change system type, set offset, add insulation";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "resize_mep_elements", "split_mep_elements", "set_pipe_slope",
            "change_mep_system_type", "batch_set_offset", "add_change_insulation",
            "flip_mep_elements", "auto_size_mep", "route_mep_between"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("resize_mep_elements",
                "Resize ducts or pipes by setting width/height/diameter (mm). Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to resize" },
                        "width_mm": { "type": "number", "description": "Target width in mm (rectangular ducts)" },
                        "height_mm": { "type": "number", "description": "Target height in mm (rectangular ducts)" },
                        "diameter_mm": { "type": "number", "description": "Target diameter in mm (round ducts or pipes)" },
                        "dry_run": { "type": "boolean", "description": "Preview changes only (no transaction). Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("split_mep_elements",
                "Split ducts, pipes, conduits, or cable trays into equal-length segments. The last segment may be shorter. Does not support flex elements. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs of ducts/pipes/conduits to split" },
                        "segment_length_mm": { "type": "number", "description": "Target length per segment in mm (e.g. 1350)" },
                        "dry_run": { "type": "boolean", "description": "Preview only (no transaction). Default false." }
                    },
                    "required": ["element_ids", "segment_length_mm"]
                }
                """)),

            ChatTool.CreateFunctionTool("set_pipe_slope",
                "Set slope (percent) for pipes. Use check_pipe_slope first to see current values.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Pipe element IDs" },
                        "slope_pct": { "type": "number", "description": "Target slope in percent (e.g. 2.0 = 2%)" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["element_ids", "slope_pct"]
                }
                """)),

            ChatTool.CreateFunctionTool("change_mep_system_type",
                "Change duct/pipe system type (e.g. Supply Air, Return Air, Sanitary). Use list_types=true to see available types first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "list_types": { "type": "boolean", "description": "If true, list available system types instead of changing" },
                        "category": { "type": "string", "description": "'duct', 'pipe', or 'all'. For list_types or filtering. Default 'all'." },
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to change" },
                        "system_type_name": { "type": "string", "description": "Target system type name (exact match)" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("batch_set_offset",
                "Set elevation offset from level (mm) for ducts/pipes/conduits. Common batch operation.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "MEP element IDs" },
                        "offset_mm": { "type": "number", "description": "Target offset from reference level in mm (e.g. 3000 = 3m above level)" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["element_ids", "offset_mm"]
                }
                """)),

            ChatTool.CreateFunctionTool("add_change_insulation",
                "Add or change insulation on pipes/ducts. Use list_types=true to see available insulation types.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "list_types": { "type": "boolean", "description": "If true, list available insulation types" },
                        "category": { "type": "string", "description": "'pipe' or 'duct'. Required when listing types or applying." },
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to insulate" },
                        "insulation_type_name": { "type": "string", "description": "Insulation type name (partial match)" },
                        "thickness_mm": { "type": "number", "description": "Insulation thickness in mm" },
                        "remove": { "type": "boolean", "description": "If true, remove insulation instead of adding" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("flip_mep_elements",
                "Flip MEP family instances (valves, fittings, equipment). Supports flip_facing, flip_hand, and flip_workplane.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Family instance element IDs to flip" },
                        "flip_type": { "type": "string", "enum": ["facing", "hand", "workplane"], "description": "Type of flip: 'facing' (flow direction), 'hand' (mirror), 'workplane' (vertical). Default 'facing'." },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("auto_size_mep",
                "Auto-size pipes/ducts based on flow rate and target velocity.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Elements to resize" },
                        "target_velocity_ms": { "type": "number", "description": "Target velocity in m/s (default 2.0 for pipes, 6.0 for ducts)" },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default true." }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("route_mep_between",
                "Auto-route a pipe or duct between two connector points using L-shaped routing. Supports obstacle avoidance for structural elements in current and linked models.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "start_element_id": { "type": "integer", "description": "Start equipment/fitting ID" },
                        "end_element_id": { "type": "integer", "description": "End equipment/fitting ID" },
                        "mep_type": { "type": "string", "enum": ["Pipe", "Duct"], "description": "Type of MEP element" },
                        "elevation_ft": { "type": "number", "description": "Routing elevation in feet (optional)" },
                        "avoid_categories": { "type": "string", "description": "Comma-separated categories to avoid, e.g. 'Structural Columns,Structural Framing,Walls'. Use 'structural' or 'kết cấu' for all structural categories." },
                        "include_links": { "type": "boolean", "description": "Also check obstacles from linked models. Default false." },
                        "clearance_mm": { "type": "number", "description": "Minimum clearance from obstacles in mm. Default 100." },
                        "dry_run": { "type": "boolean", "description": "Preview only. Default false." }
                    },
                    "required": ["start_element_id", "end_element_id", "mep_type"]
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "resize_mep_elements" => ResizeMepElements(doc, args),
                "split_mep_elements" => SplitMepElements(doc, args),
                "set_pipe_slope" => SetPipeSlope(doc, args),
                "change_mep_system_type" => ChangeMepSystemType(doc, args),
                "batch_set_offset" => BatchSetOffset(doc, args),
                "add_change_insulation" => AddChangeInsulation(doc, args),
                "flip_mep_elements" => FlipMepElements(doc, args),
                "auto_size_mep" => AutoSizeMep(doc, args),
                "route_mep_between" => RouteMepBetween(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        #region resize_mep_elements

        private string ResizeMepElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            double widthMm = GetArg(args, "width_mm", 0.0);
            double heightMm = GetArg(args, "height_mm", 0.0);
            double diameterMm = GetArg(args, "diameter_mm", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (widthMm <= 0 && heightMm <= 0 && diameterMm <= 0)
                return JsonError("Provide width_mm/height_mm for rectangular ducts or diameter_mm for round ducts/pipes.");

            const double mmToFt = 1.0 / 304.8;
            double? widthFt = widthMm > 0 ? widthMm * mmToFt : null;
            double? heightFt = heightMm > 0 ? heightMm * mmToFt : null;
            double? diameterFt = diameterMm > 0 ? diameterMm * mmToFt : null;

            int success = 0, failed = 0, skipped = 0;
            var errors = new List<string>();
            var changes = new List<object>();

            using var trans = dryRun ? null : new Transaction(doc, "AI: Resize MEP Elements");
            try
            {
                if (!dryRun) trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found."); continue; }

                    if (elem is Pipe pipe)
                    {
                        if (diameterFt == null) { failed++; errors.Add($"Element {id}: diameter_mm required for pipes."); continue; }
                        if (!TrySetParam(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, diameterFt.Value, !dryRun,
                            out var beforePipe, out var afterPipe, out var errPipe))
                        { failed++; errors.Add($"Element {id}: {errPipe}"); continue; }
                        if (beforePipe == afterPipe) { skipped++; continue; }
                        if (!dryRun) success++;
                        changes.Add(new { id, category = elem.Category?.Name ?? "-", from = beforePipe, to = afterPipe });
                        continue;
                    }

                    if (elem is Duct duct)
                    {
                        var shape = duct.DuctType?.Shape ?? ConnectorProfileType.Rectangular;
                        if (shape == ConnectorProfileType.Round)
                        {
                            if (diameterFt == null) { failed++; errors.Add($"Element {id}: diameter_mm required for round ducts."); continue; }
                            if (!TrySetParam(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, diameterFt.Value, !dryRun,
                                    out var beforeDuct, out var afterDuct, out var errDuct))
                            { failed++; errors.Add($"Element {id}: {errDuct}"); continue; }
                            if (beforeDuct == afterDuct) { skipped++; continue; }
                            if (!dryRun) success++;
                            changes.Add(new { id, category = elem.Category?.Name ?? "-", from = beforeDuct, to = afterDuct });
                            continue;
                        }

                        if (widthFt == null || heightFt == null)
                        { failed++; errors.Add($"Element {id}: width_mm and height_mm required for rectangular ducts."); continue; }

                        var widthOk = TryGetWritableParam(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, out var widthParam, out var errW);
                        var heightOk = TryGetWritableParam(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, out var heightParam, out var errH);
                        if (!widthOk || !heightOk) { failed++; errors.Add($"Element {id}: {errW ?? errH}"); continue; }

                        var beforeWidth = widthParam.AsDouble();
                        var beforeHeight = heightParam.AsDouble();
                        var beforeSize = $"{ToMmString(beforeWidth)}x{ToMmString(beforeHeight)}";
                        var afterSize = $"{ToMmString(widthFt.Value)}x{ToMmString(heightFt.Value)}";

                        if (Math.Abs(beforeWidth - widthFt.Value) < 1e-6 && Math.Abs(beforeHeight - heightFt.Value) < 1e-6)
                        { skipped++; continue; }

                        if (!dryRun)
                        {
                            try { widthParam.Set(widthFt.Value); heightParam.Set(heightFt.Value); success++; }
                            catch (Exception ex)
                            {
                                try { widthParam.Set(beforeWidth); heightParam.Set(beforeHeight); } catch { }
                                failed++; errors.Add($"Element {id}: {ex.Message}"); continue;
                            }
                        }
                        changes.Add(new { id, category = elem.Category?.Name ?? "-", from = beforeSize, to = afterSize });
                        continue;
                    }

                    failed++; errors.Add($"Element {id}: unsupported element type for resize.");
                }

                if (!dryRun) { if (success > 0) trans.Commit(); else trans.RollBack(); }
            }
            catch
            {
                if (trans?.GetStatus() == TransactionStatus.Started) trans.RollBack();
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun, success, failed, skipped,
                changes = changes.Take(100), errors = errors.Take(10)
            }, JsonOpts);
        }

        #endregion

        #region split_mep_elements

        private string SplitMepElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            double segLenMm = GetArg(args, "segment_length_mm", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (segLenMm <= 0) return JsonError("segment_length_mm must be positive.");
            if (segLenMm < 10) return JsonError("segment_length_mm must be >= 10mm.");

            const double mmToFt = 1.0 / 304.8;
            double segLenFt = segLenMm * mmToFt;

            var results = new List<object>();
            int totalCreated = 0;

            using var trans = dryRun ? null : new Transaction(doc, "AI: Split MEP Elements");
            try
            {
                if (!dryRun) trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { results.Add(new { id, error = "Element not found" }); continue; }

                    var locCurve = elem.Location as LocationCurve;
                    if (locCurve == null)
                    {
                        results.Add(new { id, error = "Not a linear MEP element (no curve)" });
                        continue;
                    }

                    var curve = locCurve.Curve;
                    double totalLen = curve.Length;
                    double totalMm = Math.Round(totalLen / mmToFt, 1);

                    if (totalLen <= segLenFt)
                    {
                        results.Add(new { id, total_length_mm = totalMm, status = "skipped",
                            reason = $"Length ({totalMm}mm) <= segment ({segLenMm}mm)" });
                        continue;
                    }

                    var cat = elem.Category?.BuiltInCategory ?? BuiltInCategory.INVALID;
                    bool isDuct = cat == BuiltInCategory.OST_DuctCurves;
                    bool isPipe = cat == BuiltInCategory.OST_PipeCurves;
                    bool isConduit = cat == BuiltInCategory.OST_Conduit;
                    bool isCableTray = cat == BuiltInCategory.OST_CableTray;

                    if (!isDuct && !isPipe && !isConduit && !isCableTray)
                    {
                        var reason = cat is BuiltInCategory.OST_FlexDuctCurves or BuiltInCategory.OST_FlexPipeCurves
                            ? "Flex ducts/pipes cannot be split (BreakCurve not supported)"
                            : $"Unsupported category: {elem.Category?.Name ?? "unknown"}";
                        results.Add(new { id, error = reason });
                        continue;
                    }

                    int numCuts = (int)Math.Floor(totalLen / segLenFt);
                    if (Math.Abs(totalLen - numCuts * segLenFt) < 1e-6)
                        numCuts--;

                    if (dryRun)
                    {
                        int segCount = numCuts + 1;
                        double lastMm = Math.Round((totalLen - numCuts * segLenFt) / mmToFt, 1);
                        var segs = new List<object>();
                        for (int i = 0; i < segCount; i++)
                        {
                            double len = i < numCuts ? segLenMm : lastMm;
                            segs.Add(new { index = i + 1, length_mm = len });
                        }
                        results.Add(new { id, total_length_mm = totalMm,
                            segment_count = segCount, segments = segs });
                        continue;
                    }

                    var breakPoints = new List<XYZ>();
                    for (int i = numCuts; i >= 1; i--)
                    {
                        double dist = i * segLenFt;
                        double param = dist / totalLen;
                        breakPoints.Add(curve.Evaluate(param, true));
                    }

                    int created = 0;
                    var errors = new List<string>();
                    var currentId = elem.Id;
                    foreach (var pt in breakPoints)
                    {
                        try
                        {
                            ElementId newId;
                            if (isDuct)
                                newId = MechanicalUtils.BreakCurve(doc, currentId, pt);
                            else if (isPipe)
                                newId = PlumbingUtils.BreakCurve(doc, currentId, pt);
                            else
                                newId = BreakCurveGeneric(doc, currentId, pt);

                            if (newId != ElementId.InvalidElementId)
                                created++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }

                    totalCreated += created;
                    var result = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["total_length_mm"] = totalMm,
                        ["segments_created"] = created + 1,
                        ["segment_length_mm"] = segLenMm
                    };
                    if (errors.Count > 0)
                        result["errors"] = errors.Take(5).ToList();
                    results.Add(result);
                }

                if (!dryRun)
                {
                    if (totalCreated > 0) trans.Commit();
                    else trans.RollBack();
                }
            }
            catch
            {
                if (trans?.GetStatus() == TransactionStatus.Started) trans.RollBack();
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun, segment_length_mm = segLenMm,
                total_new_elements = totalCreated,
                results = results.Take(50)
            }, JsonOpts);
        }

        #endregion

        #region set_pipe_slope

        private string SetPipeSlope(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            double slopePct = GetArg(args, "slope_pct", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (slopePct < 0) return JsonError("slope_pct must be >= 0.");

            double slopeRatio = slopePct / 100.0;
            int success = 0, failed = 0, skipped = 0;
            var errors = new List<string>();
            var changes = new List<object>();

            using var trans = dryRun ? null : new Transaction(doc, "AI: Set Pipe Slope");
            try
            {
                if (!dryRun) trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found."); continue; }
                    if (!(elem is Pipe)) { failed++; errors.Add($"Element {id} is not a pipe."); continue; }

                    var slopeParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE);
                    if (slopeParam == null || slopeParam.IsReadOnly)
                    { failed++; errors.Add($"Element {id}: slope parameter not writable."); continue; }

                    double current = slopeParam.AsDouble();
                    double currentPct = Math.Round(current * 100.0, 4);

                    if (Math.Abs(current - slopeRatio) < 1e-8) { skipped++; continue; }

                    if (!dryRun)
                    {
                        try { slopeParam.Set(slopeRatio); success++; }
                        catch (Exception ex) { failed++; errors.Add($"Element {id}: {ex.Message}"); continue; }
                    }

                    changes.Add(new { id, from_pct = currentPct, to_pct = slopePct });
                }

                if (!dryRun) { if (success > 0) trans.Commit(); else trans.RollBack(); }
            }
            catch
            {
                if (trans?.GetStatus() == TransactionStatus.Started) trans.RollBack();
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun, target_slope_pct = slopePct,
                success, failed, skipped, changes = changes.Take(100), errors = errors.Take(10)
            }, JsonOpts);
        }

        #endregion

        #region change_mep_system_type

        private string ChangeMepSystemType(Document doc, Dictionary<string, object> args)
        {
            bool listOnly = GetArg(args, "list_types", false);
            var category = GetArg<string>(args, "category")?.ToLower() ?? "all";

            if (listOnly)
            {
                var types = new List<object>();
                if (category is "all" or "duct")
                {
                    foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>())
                        types.Add(new { name = t.Name, id = t.Id.Value, category = "Duct", classification = t.SystemClassification.ToString() });
                }
                if (category is "all" or "pipe")
                {
                    foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>())
                        types.Add(new { name = t.Name, id = t.Id.Value, category = "Pipe", classification = t.SystemClassification.ToString() });
                }
                return JsonSerializer.Serialize(new { system_types = types }, JsonOpts);
            }

            var ids = GetArgLongArray(args, "element_ids");
            var targetName = GetArg<string>(args, "system_type_name");
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (string.IsNullOrEmpty(targetName)) return JsonError("system_type_name required. Use list_types=true to see options.");

            var ductSysType = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>().FirstOrDefault(t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            var pipeSysType = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>().FirstOrDefault(t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

            if (ductSysType == null && pipeSysType == null)
                return JsonError($"System type '{targetName}' not found. Use list_types=true.");

            int success = 0, failed = 0, skipped = 0;
            var errors = new List<string>();
            var changes = new List<object>();

            using var trans = dryRun ? null : new Transaction(doc, "AI: Change System Type");
            try
            {
                if (!dryRun) trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found."); continue; }

                    Parameter sysParam = null;
                    ElementId targetTypeId = ElementId.InvalidElementId;

                    if (elem is Duct && ductSysType != null)
                    {
                        sysParam = elem.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                        targetTypeId = ductSysType.Id;
                    }
                    else if (elem is Pipe && pipeSysType != null)
                    {
                        sysParam = elem.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                        targetTypeId = pipeSysType.Id;
                    }
                    else
                    { failed++; errors.Add($"Element {id}: not a duct/pipe or no matching system type."); continue; }

                    if (sysParam == null || sysParam.IsReadOnly)
                    { failed++; errors.Add($"Element {id}: system type parameter not writable."); continue; }

                    var currentId = sysParam.AsElementId();
                    var currentName = doc.GetElement(currentId)?.Name ?? "-";
                    if (currentId == targetTypeId) { skipped++; continue; }

                    if (!dryRun)
                    {
                        try { sysParam.Set(targetTypeId); success++; }
                        catch (Exception ex) { failed++; errors.Add($"Element {id}: {ex.Message}"); continue; }
                    }

                    changes.Add(new { id, from = currentName, to = targetName });
                }

                if (!dryRun) { if (success > 0) trans.Commit(); else trans.RollBack(); }
            }
            catch
            {
                if (trans?.GetStatus() == TransactionStatus.Started) trans.RollBack();
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun, target_type = targetName,
                success, failed, skipped, changes = changes.Take(100), errors = errors.Take(10)
            }, JsonOpts);
        }

        #endregion

        #region batch_set_offset

        private string BatchSetOffset(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            double offsetMm = GetArg(args, "offset_mm", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            double offsetFt = offsetMm / 304.8;
            int success = 0, failed = 0, skipped = 0;
            var errors = new List<string>();
            var changes = new List<object>();

            using var trans = dryRun ? null : new Transaction(doc, "AI: Set MEP Offset");
            try
            {
                if (!dryRun) trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found."); continue; }

                    var offsetParam = elem.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM)
                        ?? elem.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);

                    if (offsetParam == null || offsetParam.IsReadOnly)
                    { failed++; errors.Add($"Element {id}: offset parameter not found or read-only."); continue; }

                    double currentFt = offsetParam.AsDouble();
                    double currentMm = Math.Round(currentFt * 304.8, 1);

                    if (Math.Abs(currentFt - offsetFt) < 1e-6) { skipped++; continue; }

                    if (!dryRun)
                    {
                        try { offsetParam.Set(offsetFt); success++; }
                        catch (Exception ex) { failed++; errors.Add($"Element {id}: {ex.Message}"); continue; }
                    }

                    changes.Add(new { id, category = elem.Category?.Name ?? "-", from_mm = currentMm, to_mm = offsetMm });
                }

                if (!dryRun) { if (success > 0) trans.Commit(); else trans.RollBack(); }
            }
            catch
            {
                if (trans?.GetStatus() == TransactionStatus.Started) trans.RollBack();
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun, target_offset_mm = offsetMm,
                success, failed, skipped, changes = changes.Take(100), errors = errors.Take(10)
            }, JsonOpts);
        }

        #endregion

        #region add_change_insulation

        private string AddChangeInsulation(Document doc, Dictionary<string, object> args)
        {
            bool listOnly = GetArg(args, "list_types", false);
            var category = GetArg<string>(args, "category")?.ToLower() ?? "";
            bool remove = GetArg(args, "remove", false);

            if (listOnly)
            {
                var types = new List<object>();
                if (category is "" or "pipe")
                {
                    foreach (var t in new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeInsulations).WhereElementIsElementType())
                        types.Add(new { name = t.Name, id = t.Id.Value, category = "Pipe" });
                }
                if (category is "" or "duct")
                {
                    foreach (var t in new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_DuctInsulations).WhereElementIsElementType())
                        types.Add(new { name = t.Name, id = t.Id.Value, category = "Duct" });
                }
                return JsonSerializer.Serialize(new { insulation_types = types }, JsonOpts);
            }

            var ids = GetArgLongArray(args, "element_ids");
            var typeName = GetArg<string>(args, "insulation_type_name");
            double thicknessMm = GetArg(args, "thickness_mm", 0.0);
            bool dryRun = GetArg(args, "dry_run", false);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");
            if (!remove && string.IsNullOrEmpty(typeName)) return JsonError("insulation_type_name required. Use list_types=true.");
            if (!remove && thicknessMm <= 0) return JsonError("thickness_mm must be > 0.");

            double thickFt = thicknessMm / 304.8;

            ElementId insTypeId = ElementId.InvalidElementId;
            if (!remove)
            {
                var allInsTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeInsulations).WhereElementIsElementType().ToList();
                allInsTypes.AddRange(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DuctInsulations).WhereElementIsElementType().ToList());

                var match = allInsTypes.FirstOrDefault(t =>
                    t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null) return JsonError($"Insulation type '{typeName}' not found. Use list_types=true.");
                insTypeId = match.Id;
            }

            int success = 0, failed = 0, skipped = 0;
            var errors = new List<string>();
            var changes = new List<object>();

            using var trans = dryRun ? null : new Transaction(doc, "AI: Insulation");
            try
            {
                if (!dryRun) trans.Start();

                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) { failed++; errors.Add($"Element {id} not found."); continue; }

                    var existingInsIds = InsulationLiningBase.GetInsulationIds(doc, elem.Id);

                    if (remove)
                    {
                        if (existingInsIds.Count == 0) { skipped++; continue; }
                        if (!dryRun)
                        {
                            try { doc.Delete(existingInsIds); success++; }
                            catch (Exception ex) { failed++; errors.Add($"Element {id}: {ex.Message}"); continue; }
                        }
                        changes.Add(new { id, action = "removed", removed_count = existingInsIds.Count });
                        continue;
                    }

                    if (existingInsIds.Count > 0 && !dryRun)
                    {
                        try { doc.Delete(existingInsIds); }
                        catch (Exception ex) { failed++; errors.Add($"Element {id}: cannot remove old insulation: {ex.Message}"); continue; }
                    }

                    if (!dryRun)
                    {
                        try
                        {
                            if (elem is Pipe)
                                PipeInsulation.Create(doc, elem.Id, insTypeId, thickFt);
                            else if (elem is Duct)
                                DuctInsulation.Create(doc, elem.Id, insTypeId, thickFt);
                            else
                            { failed++; errors.Add($"Element {id}: not a pipe or duct."); continue; }
                            success++;
                        }
                        catch (Exception ex) { failed++; errors.Add($"Element {id}: {ex.Message}"); continue; }
                    }

                    changes.Add(new
                    {
                        id, action = existingInsIds.Count > 0 ? "replaced" : "added",
                        thickness_mm = thicknessMm
                    });
                }

                if (!dryRun) { if (success > 0) trans.Commit(); else trans.RollBack(); }
            }
            catch
            {
                if (trans?.GetStatus() == TransactionStatus.Started) trans.RollBack();
                throw;
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun, success, failed, skipped,
                changes = changes.Take(100), errors = errors.Take(10)
            }, JsonOpts);
        }

        #endregion

        #region Helpers

        private static bool TrySetParam(Element elem, BuiltInParameter bip, double valueFt, bool apply,
            out string before, out string after, out string error)
        {
            before = "-"; after = "-"; error = null;
            if (!TryGetWritableParam(elem, bip, out var param, out error)) return false;
            var current = param.AsDouble();
            before = ToMmString(current); after = ToMmString(valueFt);
            if (Math.Abs(current - valueFt) < 1e-6) return true;
            if (!apply) return true;
            try { param.Set(valueFt); return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string ToMmString(double valueFt) => $"{Math.Round(valueFt * 304.8, 1)}mm";

        private static bool TryGetWritableParam(Element elem, BuiltInParameter bip, out Parameter param, out string error)
        {
            param = elem.get_Parameter(bip); error = null;
            if (param == null) { error = "Parameter not found."; return false; }
            if (param.IsReadOnly) { error = "Parameter is read-only."; return false; }
            return true;
        }

        #endregion

        #region Generic Break

        /// <summary>
        /// Generic BreakCurve for Conduit/CableTray using LocationCurve manipulation.
        /// Creates a copy and adjusts both curves' endpoints at the break point.
        /// </summary>
        private static ElementId BreakCurveGeneric(Document doc, ElementId elemId, XYZ breakPoint)
        {
            var elem = doc.GetElement(elemId);
            if (elem?.Location is not LocationCurve lc) return ElementId.InvalidElementId;

            var curve = lc.Curve;
            var start = curve.GetEndPoint(0);
            var end = curve.GetEndPoint(1);

            var copiedIds = ElementTransformUtils.CopyElement(doc, elemId, XYZ.Zero);
            if (copiedIds == null || copiedIds.Count == 0) return ElementId.InvalidElementId;

            var newId = copiedIds.First();
            var newElem = doc.GetElement(newId);
            if (newElem?.Location is not LocationCurve newLc) return ElementId.InvalidElementId;

            try
            {
                lc.Curve = Line.CreateBound(start, breakPoint);
                newLc.Curve = Line.CreateBound(breakPoint, end);
                return newId;
            }
            catch
            {
                try { doc.Delete(newId); } catch { }
                return ElementId.InvalidElementId;
            }
        }

        #endregion

        #region flip_mep_elements

        private string FlipMepElements(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            if (ids == null || ids.Count == 0)
                return JsonError("element_ids required.");

            string flipType = (GetArg<string>(args, "flip_type") ?? "facing").ToLower();
            bool dryRun = GetArg(args, "dry_run", false);

            var results = new List<object>();
            int flipped = 0, skipped = 0;

            foreach (var id in ids)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem is not FamilyInstance fi)
                {
                    results.Add(new { id, status = "skipped", reason = "not a FamilyInstance" });
                    skipped++;
                    continue;
                }

                bool canFlip = flipType switch
                {
                    "facing" => fi.CanFlipFacing,
                    "hand" => fi.CanFlipHand,
                    "workplane" => fi.CanFlipWorkPlane,
                    _ => false
                };

                if (!canFlip)
                {
                    results.Add(new { id, flip_type = flipType, status = "skipped", reason = $"cannot flip {flipType}" });
                    skipped++;
                    continue;
                }

                if (dryRun)
                {
                    results.Add(new { id, flip_type = flipType, status = "can_flip" });
                    flipped++;
                    continue;
                }

                results.Add(new { id, flip_type = flipType, status = "flipped" });
                flipped++;
            }

            if (dryRun)
                return JsonSerializer.Serialize(new { dry_run = true, would_flip = flipped, skipped, details = results }, JsonOpts);

            var err = RunInTransaction(doc, "Flip MEP Elements", () =>
            {
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem is not FamilyInstance fi) continue;

                    switch (flipType)
                    {
                        case "facing" when fi.CanFlipFacing: fi.flipFacing(); break;
                        case "hand" when fi.CanFlipHand: fi.flipHand(); break;
                        case "workplane" when fi.CanFlipWorkPlane: fi.IsWorkPlaneFlipped = !fi.IsWorkPlaneFlipped; break;
                    }
                }
            });

            return err != null
                ? JsonError(err)
                : JsonSerializer.Serialize(new { status = "ok", flipped, skipped, details = results }, JsonOpts);
        }

        #endregion

        #region auto_size_mep

        private string AutoSizeMep(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            double targetVelocity = GetArg(args, "target_velocity_ms", 0.0);
            bool dryRun = GetArg(args, "dry_run", true);

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var preview = new List<object>();
            foreach (var id in ids.Take(50))
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;

                bool isPipe = elem.Category?.Id.Value == (long)BuiltInCategory.OST_PipeCurves;
                double defaultVel = isPipe ? 2.0 : 6.0;
                double vel = targetVelocity > 0 ? targetVelocity : defaultVel;
                double velFps = vel * 3.28084;

                var flowParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)
                             ?? elem.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM);
                double flow = flowParam?.AsDouble() ?? 0;
                if (flow <= 0) { preview.Add(new { id, error = "no flow data" }); continue; }

                bool isDuct = (elem.Category?.Name ?? "").IndexOf("Duct", StringComparison.OrdinalIgnoreCase) >= 0;
                double flowForArea = isDuct ? flow / 60.0 : flow;
                double requiredArea = flowForArea / velFps;
                double requiredDiameter = 2 * Math.Sqrt(requiredArea / Math.PI);
                double requiredDiaMm = requiredDiameter * 304.8;

                double[] standardSizes = isPipe
                    ? new[] { 15.0, 20.0, 25.0, 32.0, 40.0, 50.0, 65.0, 80.0, 100.0, 125.0, 150.0, 200.0, 250.0, 300.0 }
                    : new[] { 100.0, 125.0, 150.0, 200.0, 250.0, 300.0, 350.0, 400.0, 450.0, 500.0, 600.0, 700.0, 800.0 };

                double selectedMm = standardSizes.FirstOrDefault(s => s >= requiredDiaMm);
                if (selectedMm == 0) selectedMm = standardSizes.Last();

                var currentDia = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                              ?? elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                double currentMm = (currentDia?.AsDouble() ?? 0) * 304.8;

                preview.Add(new { id, flow_cfs = Math.Round(flow, 4),
                    current_dia_mm = Math.Round(currentMm, 0), recommended_dia_mm = selectedMm,
                    would_change = Math.Abs(currentMm - selectedMm) > 1 });
            }

            if (dryRun)
                return JsonSerializer.Serialize(new { dry_run = true, elements = preview }, JsonOpts);

            int changed = 0;
            using (var trans = new Transaction(doc, "AI: Auto Size MEP"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) continue;
                    bool isPipe2 = elem.Category?.Id.Value == (long)BuiltInCategory.OST_PipeCurves;
                    var diaParam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                                ?? elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                    if (diaParam == null || diaParam.IsReadOnly) continue;

                    var flowParam2 = elem.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_PARAM)
                                  ?? elem.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM);
                    double flow2 = flowParam2?.AsDouble() ?? 0;
                    if (flow2 <= 0) continue;

                    bool isDuct2 = (elem.Category?.Name ?? "").IndexOf("Duct", StringComparison.OrdinalIgnoreCase) >= 0;
                    double flowForArea2 = isDuct2 ? flow2 / 60.0 : flow2;
                    double vel2 = targetVelocity > 0 ? targetVelocity : (isPipe2 ? 2.0 : 6.0);
                    double reqArea = flowForArea2 / (vel2 * 3.28084);
                    double reqDia = 2 * Math.Sqrt(reqArea / Math.PI);
                    double reqMm = reqDia * 304.8;

                    double[] sizes = isPipe2
                        ? new[] { 15.0, 20.0, 25.0, 32.0, 40.0, 50.0, 65.0, 80.0, 100.0, 125.0, 150.0, 200.0, 250.0, 300.0 }
                        : new[] { 100.0, 125.0, 150.0, 200.0, 250.0, 300.0, 350.0, 400.0, 450.0, 500.0, 600.0, 700.0, 800.0 };
                    double selMm = sizes.FirstOrDefault(s => s >= reqMm);
                    if (selMm == 0) selMm = sizes.Last();

                    diaParam.Set(selMm / 304.8);
                    changed++;
                }
                if (changed > 0) trans.Commit(); else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { resized = changed, total = ids.Count }, JsonOpts);
        }

        #endregion

        #region route_mep_between

        private static readonly Dictionary<string, BuiltInCategory> ObstacleCategoryMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["Structural Columns"] = BuiltInCategory.OST_StructuralColumns,
            ["Structural Framing"] = BuiltInCategory.OST_StructuralFraming,
            ["Structural Foundations"] = BuiltInCategory.OST_StructuralFoundation,
            ["Columns"] = BuiltInCategory.OST_Columns,
            ["Walls"] = BuiltInCategory.OST_Walls,
            ["Floors"] = BuiltInCategory.OST_Floors,
        };

        private string RouteMepBetween(Document doc, Dictionary<string, object> args)
        {
            long startId = GetArg<long>(args, "start_element_id");
            long endId = GetArg<long>(args, "end_element_id");
            var mepType = GetArg(args, "mep_type", "Pipe");
            double elevation = GetArg(args, "elevation_ft", double.NaN);
            bool dryRun = GetArg(args, "dry_run", false);
            var avoidCats = GetArg<string>(args, "avoid_categories");
            bool includeLinks = GetArg(args, "include_links", false);
            double clearanceMm = GetArg(args, "clearance_mm", 100.0);

            if (startId <= 0 || endId <= 0) return JsonError("Both start_element_id and end_element_id required.");
            var startElem = doc.GetElement(new ElementId(startId));
            var endElem = doc.GetElement(new ElementId(endId));
            if (startElem == null) return JsonError($"Start element {startId} not found.");
            if (endElem == null) return JsonError($"End element {endId} not found.");

            Connector startConn = FindOpenConnector(startElem);
            Connector endConn = FindOpenConnector(endElem);
            if (startConn == null) return JsonError($"No open connector on start element {startId}.");
            if (endConn == null) return JsonError($"No open connector on end element {endId}.");

            XYZ startPt = startConn.Origin;
            XYZ endPt = endConn.Origin;

            if (!double.IsNaN(elevation))
            {
                startPt = new XYZ(startPt.X, startPt.Y, elevation);
                endPt = new XYZ(endPt.X, endPt.Y, elevation);
            }

            var variants = new (string Name, XYZ Mid)[]
            {
                ("L-shape(X→Y)", new XYZ(endPt.X, startPt.Y, startPt.Z)),
                ("L-shape(Y→X)", new XYZ(startPt.X, endPt.Y, startPt.Z)),
            };

            string bestRouteName = variants[0].Name;
            XYZ bestMidPt = variants[0].Mid;
            int bestClashCount = 0;
            int obstacleCount = 0;
            var clashWarnings = new List<string>();

            if (!string.IsNullOrEmpty(avoidCats))
            {
                double clearanceFt = clearanceMm / 304.8;
                var obstacles = CollectObstacleBounds(doc, avoidCats, includeLinks, clearanceFt);
                obstacleCount = obstacles.Count;

                if (obstacles.Count > 0)
                {
                    double halfWidth = 0.5;
                    int minClashes = int.MaxValue;

                    foreach (var (name, mid) in variants)
                    {
                        int clashes = CountSegmentClashes(startPt, mid, obstacles, halfWidth)
                                    + CountSegmentClashes(mid, endPt, obstacles, halfWidth);
                        if (clashes < minClashes)
                        {
                            minClashes = clashes;
                            bestMidPt = mid;
                            bestRouteName = name;
                        }
                        if (clashes == 0) break;
                    }

                    bestClashCount = minClashes;
                    if (bestClashCount > 0)
                        clashWarnings.Add($"Best route '{bestRouteName}' still intersects {bestClashCount} obstacle(s). Manual adjustment may be needed.");
                }
            }

            if (dryRun)
            {
                var result = new Dictionary<string, object>
                {
                    ["dry_run"] = true,
                    ["mep_type"] = mepType,
                    ["route"] = bestRouteName,
                    ["start_element"] = startId,
                    ["end_element"] = endId,
                    ["start_point"] = new { x = Math.Round(startPt.X, 3), y = Math.Round(startPt.Y, 3), z = Math.Round(startPt.Z, 3) },
                    ["mid_point"] = new { x = Math.Round(bestMidPt.X, 3), y = Math.Round(bestMidPt.Y, 3), z = Math.Round(bestMidPt.Z, 3) },
                    ["end_point"] = new { x = Math.Round(endPt.X, 3), y = Math.Round(endPt.Y, 3), z = Math.Round(endPt.Z, 3) },
                    ["obstacles_checked"] = obstacleCount,
                    ["remaining_clashes"] = bestClashCount,
                    ["message"] = bestClashCount > 0
                        ? $"Would route {mepType} via {bestRouteName}, but {bestClashCount} obstacle clash(es) remain."
                        : $"Would route {mepType} via {bestRouteName}. Path clear of obstacles."
                };
                if (clashWarnings.Count > 0) result["warnings"] = clashWarnings;
                return JsonSerializer.Serialize(result, JsonOpts);
            }

            using (var trans = new Transaction(doc, "AI: Route MEP"))
            {
                trans.Start();
                try
                {
                    if (mepType == "Pipe")
                    {
                        var pipeType = new FilteredElementCollector(doc)
                            .OfClass(typeof(PipeType)).FirstOrDefault();
                        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault() as Level;
                        if (pipeType == null || level == null) { trans.RollBack(); return JsonError("No pipe type or level found."); }

                        Pipe.Create(doc, pipeType.Id, level.Id, startConn, bestMidPt);
                        Pipe.Create(doc, pipeType.Id, level.Id, endConn, bestMidPt);
                    }
                    else
                    {
                        var ductType = new FilteredElementCollector(doc)
                            .OfClass(typeof(DuctType)).FirstOrDefault();
                        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstOrDefault() as Level;
                        if (ductType == null || level == null) { trans.RollBack(); return JsonError("No duct type or level found."); }

                        Duct.Create(doc, ductType.Id, level.Id, startConn, bestMidPt);
                        Duct.Create(doc, ductType.Id, level.Id, endConn, bestMidPt);
                    }
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted()) trans.RollBack();
                    return JsonError($"Routing failed: {ex.Message}");
                }
            }

            var execResult = new Dictionary<string, object>
            {
                ["routed"] = true,
                ["mep_type"] = mepType,
                ["route"] = bestRouteName,
                ["start_element"] = startId,
                ["end_element"] = endId,
                ["obstacles_checked"] = obstacleCount,
                ["remaining_clashes"] = bestClashCount,
                ["message"] = bestClashCount > 0
                    ? $"Routed {mepType} via {bestRouteName}, but {bestClashCount} obstacle clash(es) detected. Consider manual adjustment."
                    : $"Routed {mepType} via {bestRouteName}. Path clear of obstacles."
            };
            if (clashWarnings.Count > 0) execResult["warnings"] = clashWarnings;
            return JsonSerializer.Serialize(execResult, JsonOpts);
        }

        private List<(XYZ Min, XYZ Max)> CollectObstacleBounds(
            Document doc, string avoidCategories, bool includeLinks, double clearanceFt)
        {
            var obstacles = new List<(XYZ, XYZ)>();

            var bics = new HashSet<BuiltInCategory>();
            foreach (var name in avoidCategories.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0))
            {
                if (ObstacleCategoryMap.TryGetValue(name, out var bic))
                    bics.Add(bic);
                if (name.IndexOf("structural", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("kết cấu", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bics.Add(BuiltInCategory.OST_StructuralColumns);
                    bics.Add(BuiltInCategory.OST_StructuralFraming);
                    bics.Add(BuiltInCategory.OST_StructuralFoundation);
                }
            }

            if (bics.Count == 0) return obstacles;

            void Collect(Document d, Transform xform)
            {
                foreach (var cat in bics)
                {
                    foreach (var elem in new FilteredElementCollector(d)
                                 .OfCategory(cat).WhereElementIsNotElementType())
                    {
                        var bb = elem.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ min = bb.Min, max = bb.Max;
                        if (xform != null)
                        {
                            min = xform.OfPoint(min);
                            max = xform.OfPoint(max);
                        }
                        obstacles.Add((
                            new XYZ(Math.Min(min.X, max.X) - clearanceFt,
                                    Math.Min(min.Y, max.Y) - clearanceFt,
                                    Math.Min(min.Z, max.Z) - clearanceFt),
                            new XYZ(Math.Max(min.X, max.X) + clearanceFt,
                                    Math.Max(min.Y, max.Y) + clearanceFt,
                                    Math.Max(min.Z, max.Z) + clearanceFt)));
                    }
                }
            }

            Collect(doc, null);

            if (includeLinks)
            {
                foreach (var linkInst in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;
                    Collect(linkDoc, linkInst.GetTotalTransform());
                }
            }

            return obstacles;
        }

        private static int CountSegmentClashes(
            XYZ p1, XYZ p2, List<(XYZ Min, XYZ Max)> obstacles, double halfWidth)
        {
            double segMinX = Math.Min(p1.X, p2.X) - halfWidth;
            double segMinY = Math.Min(p1.Y, p2.Y) - halfWidth;
            double segMinZ = Math.Min(p1.Z, p2.Z) - halfWidth;
            double segMaxX = Math.Max(p1.X, p2.X) + halfWidth;
            double segMaxY = Math.Max(p1.Y, p2.Y) + halfWidth;
            double segMaxZ = Math.Max(p1.Z, p2.Z) + halfWidth;

            int count = 0;
            foreach (var (boxMin, boxMax) in obstacles)
            {
                if (segMaxX >= boxMin.X && segMinX <= boxMax.X &&
                    segMaxY >= boxMin.Y && segMinY <= boxMax.Y &&
                    segMaxZ >= boxMin.Z && segMinZ <= boxMax.Z)
                    count++;
            }
            return count;
        }

        private static Connector FindOpenConnector(Element elem)
        {
            ConnectorManager cm = null;
            if (elem is MEPCurve mep) cm = mep.ConnectorManager;
            else if (elem is FamilyInstance fi) cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                if (!c.IsConnected) return c;
            }
            foreach (Connector c in cm.Connectors) return c;
            return null;
        }

        #endregion
    }
}
