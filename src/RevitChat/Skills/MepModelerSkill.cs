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
    public class MepModelerSkill : IRevitSkill
    {
        public string Name => "MepModeler";
        public string Description => "Modify MEP element sizes safely (ducts and pipes)";

        private static readonly HashSet<string> HandledTools = new()
        {
            "resize_mep_elements"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "resize_mep_elements" => ResizeMepElements(doc, args),
                _ => JsonError($"MepModelerSkill: unknown tool '{functionName}'")
            };
        }

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

            Transaction trans = null;
            if (!dryRun)
            {
                trans = new Transaction(doc, "AI: Resize MEP Elements");
                trans.Start();
            }

            foreach (var id in ids)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null)
                {
                    failed++;
                    errors.Add($"Element {id} not found.");
                    continue;
                }

                if (elem is Pipe pipe)
                {
                    if (diameterFt == null)
                    {
                        failed++;
                        errors.Add($"Element {id}: diameter_mm required for pipes.");
                        continue;
                    }

                    if (!TrySetParam(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, diameterFt.Value, !dryRun,
                        out var beforePipe, out var afterPipe, out var errPipe))
                    {
                        failed++;
                        errors.Add($"Element {id}: {errPipe}");
                        continue;
                    }

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
                        if (diameterFt == null)
                        {
                            failed++;
                            errors.Add($"Element {id}: diameter_mm required for round ducts.");
                            continue;
                        }

                        if (!TrySetParam(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, diameterFt.Value, !dryRun,
                                out var beforeDuct, out var afterDuct, out var errDuct))
                        {
                            failed++;
                            errors.Add($"Element {id}: {errDuct}");
                            continue;
                        }

                        if (beforeDuct == afterDuct) { skipped++; continue; }
                        if (!dryRun) success++;
                        changes.Add(new { id, category = elem.Category?.Name ?? "-", from = beforeDuct, to = afterDuct });
                        continue;
                    }

                    if (widthFt == null || heightFt == null)
                    {
                        failed++;
                        errors.Add($"Element {id}: width_mm and height_mm required for rectangular ducts.");
                        continue;
                    }

                    var widthOk = TryGetWritableParam(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, out var widthParam, out var errW);
                    var heightOk = TryGetWritableParam(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, out var heightParam, out var errH);
                    if (!widthOk || !heightOk)
                    {
                        failed++;
                        errors.Add($"Element {id}: {errW ?? errH}");
                        continue;
                    }

                    var beforeWidth = widthParam.AsDouble();
                    var beforeHeight = heightParam.AsDouble();
                    var beforeSize = $"{ToMmString(beforeWidth)}x{ToMmString(beforeHeight)}";
                    var afterSize = $"{ToMmString(widthFt.Value)}x{ToMmString(heightFt.Value)}";

                    if (Math.Abs(beforeWidth - widthFt.Value) < 1e-6 &&
                        Math.Abs(beforeHeight - heightFt.Value) < 1e-6)
                    {
                        skipped++;
                        continue;
                    }

                    if (!dryRun)
                    {
                        try
                        {
                            widthParam.Set(widthFt.Value);
                            heightParam.Set(heightFt.Value);
                            success++;
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                widthParam.Set(beforeWidth);
                                heightParam.Set(beforeHeight);
                            }
                            catch { }
                            failed++;
                            errors.Add($"Element {id}: {ex.Message}");
                            continue;
                        }
                    }

                    changes.Add(new { id, category = elem.Category?.Name ?? "-", from = beforeSize, to = afterSize });
                    continue;
                }

                failed++;
                errors.Add($"Element {id}: unsupported element type for resize.");
            }

            if (!dryRun)
            {
                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                dry_run = dryRun,
                success,
                failed,
                skipped,
                changes = changes.Take(100),
                errors = errors.Take(10)
            }, JsonOpts);
        }

        private static bool TrySetParam(Element elem, BuiltInParameter bip, double valueFt, bool apply,
            out string before, out string after, out string error)
        {
            before = "-";
            after = "-";
            error = null;

            if (!TryGetWritableParam(elem, bip, out var param, out error)) return false;

            var current = param.AsDouble();
            before = ToMmString(current);
            after = ToMmString(valueFt);

            if (Math.Abs(current - valueFt) < 1e-6)
                return true;

            if (!apply)
                return true;

            try
            {
                param.Set(valueFt);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ToMmString(double valueFt)
        {
            var mm = valueFt * 304.8;
            return $"{Math.Round(mm, 1)}mm";
        }

        private static bool TryGetWritableParam(Element elem, BuiltInParameter bip, out Parameter param, out string error)
        {
            param = elem.get_Parameter(bip);
            error = null;
            if (param == null) { error = "Parameter not found."; return false; }
            if (param.IsReadOnly) { error = "Parameter is read-only."; return false; }
            return true;
        }
    }
}
