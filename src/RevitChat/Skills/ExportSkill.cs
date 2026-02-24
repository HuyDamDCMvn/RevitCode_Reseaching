using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class ExportSkill : BaseRevitSkill
    {
        protected override string SkillName => "Export";
        protected override string SkillDescription => "Export/import Revit data in CSV, JSON, PDF, IFC formats";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "export_to_csv", "export_to_json", "import_from_csv",
            "export_pdf", "export_ifc", "get_ifc_mappings"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("export_to_csv",
                "Export element data to a CSV file. Query elements first, then export.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category of elements to export" },
                        "param_names": { "type": "array", "items": { "type": "string" }, "description": "Parameter names to include as columns" },
                        "level": { "type": "string", "description": "Optional level filter" },
                        "file_path": { "type": "string", "description": "Output file path. Default: Desktop." },
                        "format": { "type": "string", "enum": ["csv", "json", "txt"], "description": "Export format: csv (default), json, txt (ASCII table)" }
                    },
                    "required": ["category", "param_names"]
                }
                """)),

            ChatTool.CreateFunctionTool("export_to_json",
                "Export element data as structured JSON file.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Category of elements to export" },
                        "param_names": { "type": "array", "items": { "type": "string" }, "description": "Parameter names to include" },
                        "level": { "type": "string", "description": "Optional level filter" },
                        "file_path": { "type": "string", "description": "Output JSON path. Default: Desktop." },
                        "pretty_print": { "type": "boolean", "description": "Pretty-print JSON. Default true." }
                    },
                    "required": ["category", "param_names"]
                }
                """)),

            ChatTool.CreateFunctionTool("import_from_csv",
                "Import a CSV file and batch-update element parameters. First column must be Element ID.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "file_path": { "type": "string", "description": "Path to CSV file" },
                        "id_column": { "type": "string", "description": "Column name for Element ID (default: 'ElementId')" },
                        "dry_run": { "type": "boolean", "description": "Preview only, no changes. Default true." }
                    },
                    "required": ["file_path"]
                }
                """)),

            ChatTool.CreateFunctionTool("export_pdf",
                "Export views or sheets to PDF.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "view_ids": { "type": "array", "items": { "type": "integer" }, "description": "View/sheet IDs to export" },
                        "output_folder": { "type": "string", "description": "Output folder path. Default: Desktop." },
                        "combine": { "type": "boolean", "description": "Combine into single PDF. Default false." }
                    },
                    "required": ["view_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("export_ifc",
                "Export model to IFC format.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "file_path": { "type": "string", "description": "Output IFC path. Default: Desktop." },
                        "view_id": { "type": "integer", "description": "Optional view to export (default: active view)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_ifc_mappings",
                "Get current IFC category export mappings.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "category": { "type": "string", "description": "Optional: specific category to check" }
                    },
                    "required": []
                }
                """))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "export_to_csv" => ExportToCsv(doc, args),
                "export_to_json" => ExportToJson(doc, args),
                "import_from_csv" => ImportFromCsv(doc, args),
                "export_pdf" => ExportPdf(doc, args),
                "export_ifc" => ExportIfc(doc, uidoc, args),
                "get_ifc_mappings" => GetIfcMappings(doc, args),
                _ => UnknownTool(functionName)
            };
        }

        private string ExportToCsv(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var paramNames = GetArgStringArray(args, "param_names");
            var levelInput = GetArg<string>(args, "level");
            var filePath = GetArg<string>(args, "file_path");

            if (string.IsNullOrEmpty(category)) return JsonError("category required.");
            var level = ResolveLevelName(doc, levelInput);
            if (paramNames == null || paramNames.Count == 0) return JsonError("param_names required.");
            paramNames = paramNames.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paramNames.Count == 0) return JsonError("param_names must contain valid parameter names.");

            var format = GetArg(args, "format", "csv");
            if (format == "json") return ExportToJson(doc, args);
            if (format == "txt") return ExportToTxt(doc, category, paramNames, level);

            if (string.IsNullOrEmpty(filePath))
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Revit_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            filePath = Path.GetFullPath(filePath);
            if (!filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return JsonError("file_path must end with .csv");

            var pathErr = ValidateOutputPath(filePath);
            if (pathErr != null) return JsonError(pathErr);

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;

            var sb = new StringBuilder();
            sb.AppendLine("ElementId," + string.Join(",", paramNames.Select(EscapeCsv)));

            int rowCount = 0;
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !MatchesCategoryName(elem.Category.Name, category))
                    continue;

                if (level != null &&
                    !GetElementLevel(doc, elem).Equals(level, StringComparison.OrdinalIgnoreCase))
                    continue;

                var values = new List<string> { elem.Id.Value.ToString() };
                foreach (var pn in paramNames)
                {
                    var param = elem.LookupParameter(pn);
                    values.Add(EscapeCsv(param != null ? GetParameterValueAsString(doc, param) : "-"));
                }
                sb.AppendLine(string.Join(",", values));
                rowCount++;
            }

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to write CSV: {ex.Message}. Path: {filePath}");
            }

            return JsonSerializer.Serialize(new
            {
                file_path = filePath,
                row_count = rowCount,
                columns = paramNames
            }, JsonOpts);
        }

        private string ExportToTxt(Document doc, string category, List<string> paramNames, string level)
        {
            var resolvedLevel = ResolveLevelName(doc, level);
            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;

            var rows = new List<List<string>>();
            var headers = new List<string> { "ElementId" };
            headers.AddRange(paramNames);

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                if (needsCategoryFallback && !MatchesCategoryName(elem.Category.Name, category)) continue;
                if (resolvedLevel != null && !GetElementLevel(doc, elem).Equals(resolvedLevel, StringComparison.OrdinalIgnoreCase)) continue;

                var row = new List<string> { elem.Id.Value.ToString() };
                foreach (var pn in paramNames)
                {
                    var param = elem.LookupParameter(pn);
                    row.Add(param != null ? GetParameterValueAsString(doc, param) : "-");
                }
                rows.Add(row);
            }

            var widths = headers.Select((h, i) =>
                Math.Max(h.Length, rows.Count > 0 ? rows.Max(r => i < r.Count ? r[i].Length : 0) : 0)).ToArray();

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(" | ", headers.Select((h, i) => h.PadRight(widths[i]))));
            sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
            foreach (var row in rows)
                sb.AppendLine(string.Join(" | ", row.Select((c, i) => c.PadRight(widths[i]))));

            var filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"Revit_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);

            return JsonSerializer.Serialize(new { file_path = filePath, format = "txt", row_count = rows.Count, columns = headers }, JsonOpts);
        }

        private string ExportToJson(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var paramNames = GetArgStringArray(args, "param_names");
            var levelInput = GetArg<string>(args, "level");
            var filePath = GetArg<string>(args, "file_path");
            bool prettyPrint = GetArg(args, "pretty_print", true);

            if (string.IsNullOrEmpty(category)) return JsonError("category required.");
            if (paramNames == null || paramNames.Count == 0) return JsonError("param_names required.");

            var level = ResolveLevelName(doc, levelInput);

            if (string.IsNullOrEmpty(filePath))
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Revit_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

            filePath = Path.GetFullPath(filePath);
            var pathErr = ValidateOutputPath(filePath);
            if (pathErr != null) return JsonError(pathErr);

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;

            var elements = new List<Dictionary<string, object>>();
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                if (needsCategoryFallback && !MatchesCategoryName(elem.Category.Name, category)) continue;
                if (level != null && !GetElementLevel(doc, elem).Equals(level, StringComparison.OrdinalIgnoreCase)) continue;

                var obj = new Dictionary<string, object> { ["ElementId"] = elem.Id.Value };
                foreach (var pn in paramNames)
                {
                    var param = elem.LookupParameter(pn);
                    obj[pn] = param != null ? GetParameterValueAsString(doc, param) : null;
                }
                elements.Add(obj);
            }

            var opts = new JsonSerializerOptions { WriteIndented = prettyPrint };
            var json = JsonSerializer.Serialize(new { category, exported_at = DateTime.Now.ToString("o"), count = elements.Count, elements }, opts);

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, json, Encoding.UTF8);
            }
            catch (Exception ex) { return JsonError($"Failed to write JSON: {ex.Message}"); }

            return JsonSerializer.Serialize(new { file_path = filePath, format = "json", row_count = elements.Count }, JsonOpts);
        }

        private string ImportFromCsv(Document doc, Dictionary<string, object> args)
        {
            var filePath = GetArg<string>(args, "file_path");
            var idColumn = GetArg(args, "id_column", "ElementId");
            bool dryRun = GetArg(args, "dry_run", true);

            if (string.IsNullOrEmpty(filePath)) return JsonError("file_path required. / file_path là bắt buộc.");
            filePath = Path.GetFullPath(filePath);
            var readPathErr = ValidateOutputPath(filePath);
            if (readPathErr != null) return JsonError(readPathErr);
            if (!File.Exists(filePath)) return JsonError($"File not found: {filePath} / Không tìm thấy file: {filePath}");

            const long maxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > maxFileSizeBytes)
                return JsonError($"CSV file too large ({fileInfo.Length / 1024 / 1024}MB). Max 10MB. / File CSV quá lớn ({fileInfo.Length / 1024 / 1024}MB). Tối đa 10MB.");

            string[] lines;
            try { lines = File.ReadAllLines(filePath, Encoding.UTF8); }
            catch (Exception ex) { return JsonError($"Cannot read file: {ex.Message} / Không đọc được file: {ex.Message}"); }

            if (lines.Length > 10001) return JsonError($"CSV too large ({lines.Length} rows). Max 10,000 data rows. / CSV quá lớn ({lines.Length} dòng). Tối đa 10.000 dòng dữ liệu.");
            if (lines.Length < 2) return JsonError("CSV must have a header row and at least one data row. / CSV cần có dòng tiêu đề và ít nhất 1 dòng dữ liệu.");

            var headers = ParseCsvLine(lines[0]);
            int idIdx = headers.FindIndex(h => h.Equals(idColumn, StringComparison.OrdinalIgnoreCase));
            if (idIdx < 0) return JsonError($"Column '{idColumn}' not found in CSV header. Found: {string.Join(", ", headers)}");

            var paramColumns = headers.Where((h, i) => i != idIdx).ToList();
            var preview = new List<object>();
            var errors = new List<string>();
            int wouldUpdate = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = ParseCsvLine(lines[i]);
                if (cols.Count <= idIdx) continue;
                if (!long.TryParse(cols[idIdx], out long elemId)) { errors.Add($"Row {i + 1}: invalid ID '{cols[idIdx]}'"); continue; }

                var elem = doc.GetElement(new ElementId(elemId));
                if (elem == null) { errors.Add($"Row {i + 1}: element {elemId} not found"); continue; }

                for (int j = 0; j < headers.Count; j++)
                {
                    if (j == idIdx) continue;
                    if (j >= cols.Count) break;
                    var paramName = headers[j];
                    var value = cols[j];
                    if (string.IsNullOrEmpty(value)) continue;

                    var param = elem.LookupParameter(paramName);
                    if (param == null) { errors.Add($"Row {i + 1}: param '{paramName}' not found on {elemId}"); continue; }
                    if (param.IsReadOnly) { errors.Add($"Row {i + 1}: param '{paramName}' is read-only on {elemId}"); continue; }

                    wouldUpdate++;
                    if (preview.Count < 20)
                        preview.Add(new { element_id = elemId, param_name = paramName, new_value = value,
                            current = GetParameterValueAsString(doc, param) });
                }
            }

            if (dryRun)
                return JsonSerializer.Serialize(new { dry_run = true, rows = lines.Length - 1, would_update = wouldUpdate, errors = errors.Take(20), preview }, JsonOpts);

            int success = 0;
            errors.Clear();
            using (var trans = new Transaction(doc, "AI: Import CSV"))
            {
                trans.Start();
                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = ParseCsvLine(lines[i]);
                    if (cols.Count <= idIdx || !long.TryParse(cols[idIdx], out long elemId)) continue;
                    var elem = doc.GetElement(new ElementId(elemId));
                    if (elem == null) continue;

                    for (int j = 0; j < headers.Count; j++)
                    {
                        if (j == idIdx) continue;
                        if (j >= cols.Count) break;
                        var paramName = headers[j];
                        var value = cols[j];
                        if (string.IsNullOrEmpty(value)) continue;

                        var param = elem.LookupParameter(paramName);
                        if (param == null || param.IsReadOnly) continue;
                        if (SetParamValue(param, value)) success++;
                        else errors.Add($"Row {i + 1}: failed {elemId}.{paramName}");
                    }
                }
                if (success > 0) trans.Commit(); else trans.RollBack();
            }
            return JsonSerializer.Serialize(new { imported = success, errors = errors.Take(20) }, JsonOpts);
        }

        private string ExportPdf(Document doc, Dictionary<string, object> args)
        {
            var viewIds = GetArgLongArray(args, "view_ids");
            if (viewIds == null || viewIds.Count == 0) return JsonError("view_ids required.");
            var outputFolder = GetArg(args, "output_folder",
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            bool combine = GetArg(args, "combine", false);

            outputFolder = Path.GetFullPath(outputFolder);
            var pathErr = ValidateOutputPath(outputFolder);
            if (pathErr != null) return JsonError(pathErr);

            Directory.CreateDirectory(outputFolder);
            var validViews = viewIds.Select(id => doc.GetElement(new ElementId(id)) as View)
                .Where(v => v != null).ToList();
            if (validViews.Count == 0) return JsonError("No valid views found.");

            try
            {
                var options = new PDFExportOptions
                {
                    FileName = $"Revit_Export_{DateTime.Now:yyyyMMdd_HHmmss}",
                    Combine = combine
                };
                var viewSet = validViews.Select(v => v.Id).ToList();
                doc.Export(outputFolder, viewSet, options);

                return JsonSerializer.Serialize(new
                {
                    exported = validViews.Count,
                    output_folder = outputFolder,
                    message = $"Exported {validViews.Count} view(s) to PDF."
                }, JsonOpts);
            }
            catch (Exception ex) { return JsonError($"PDF export failed: {ex.Message}"); }
        }

        private string ExportIfc(Document doc, UIDocument uidoc, Dictionary<string, object> args)
        {
            var filePath = GetArg<string>(args, "file_path");
            long viewIdVal = GetArg<long>(args, "view_id");

            if (string.IsNullOrEmpty(filePath))
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Revit_Export_{DateTime.Now:yyyyMMdd_HHmmss}.ifc");

            filePath = Path.GetFullPath(filePath);
            var pathErr = ValidateOutputPath(filePath);
            if (pathErr != null) return JsonError(pathErr);

            try
            {
                var options = new IFCExportOptions();
                if (viewIdVal > 0) options.FilterViewId = new ElementId(viewIdVal);

                doc.Export(Path.GetDirectoryName(filePath), Path.GetFileName(filePath), options);

                return JsonSerializer.Serialize(new { file_path = filePath, message = "IFC export completed." }, JsonOpts);
            }
            catch (Exception ex) { return JsonError($"IFC export failed: {ex.Message}"); }
        }

        private string GetIfcMappings(Document doc, Dictionary<string, object> args)
        {
            var categoryFilter = GetArg<string>(args, "category");
            var mappings = new List<object>();

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || cat.Id.Value < 0) continue;
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !MatchesCategoryName(cat.Name, categoryFilter)) continue;

                try
                {
                    var subCats = cat.SubCategories;
                    mappings.Add(new
                    {
                        category = cat.Name,
                        id = cat.Id.Value,
                        subcategory_count = subCats?.Size ?? 0
                    });
                }
                catch { }
                if (mappings.Count >= 200) break;
            }

            return JsonSerializer.Serialize(new { count = mappings.Count, mappings }, JsonOpts);
        }

        // Delegates to RevitHelpers.SetParamValue (single shared implementation)
        private static bool SetParamValue(Parameter param, string value) => RevitHelpers.SetParamValue(param, value);

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuote = false;
            var current = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                            inQuote = false;
                    }
                    else
                        current.Append(c);
                }
                else
                {
                    if (c == '"') inQuote = true;
                    else if (c == ',') { result.Add(current.ToString().Trim()); current.Clear(); }
                    else current.Append(c);
                }
            }
            result.Add(current.ToString().Trim());
            return result;
        }

    }
}
