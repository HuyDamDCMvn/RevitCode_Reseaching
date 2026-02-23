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
            "change_mep_system_type", "batch_set_offset", "add_change_insulation"
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
                "Split duct/pipe/conduit curves into equal-length segments. The last segment may be shorter. Confirm with user first.",
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

                    var cat = elem.Category?.BuiltInCategory ?? BuiltInCategory.INVALID;
                    bool isDuct = cat is BuiltInCategory.OST_DuctCurves or BuiltInCategory.OST_FlexDuctCurves;
                    bool isPipe = cat is BuiltInCategory.OST_PipeCurves or BuiltInCategory.OST_FlexPipeCurves;
                    bool isConduit = cat == BuiltInCategory.OST_Conduit;
                    bool isCableTray = cat == BuiltInCategory.OST_CableTray;

                    if (!isDuct && !isPipe && !isConduit && !isCableTray)
                    {
                        results.Add(new { id, error = $"Unsupported category: {elem.Category?.Name ?? "unknown"}" });
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
                    var currentId = elem.Id;
                    foreach (var pt in breakPoints)
                    {
                        try
                        {
                            ElementId newId = ElementId.InvalidElementId;
                            if (isDuct)
                                newId = MechanicalUtils.BreakCurve(doc, currentId, pt);
                            else if (isPipe)
                                newId = PlumbingUtils.BreakCurve(doc, currentId, pt);

                            if (newId != null && newId != ElementId.InvalidElementId)
                                created++;
                        }
                        catch { }
                    }

                    totalCreated += created;
                    results.Add(new { id, total_length_mm = totalMm,
                        segments_created = created + 1, segment_length_mm = segLenMm });
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
    }
}
