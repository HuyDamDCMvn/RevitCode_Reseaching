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
    public class ExportSkill : IRevitSkill
    {
        public string Name => "Export";
        public string Description => "Export Revit element data to CSV files";

        public bool CanHandle(string functionName) => functionName == "export_to_csv";

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
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
                        "file_path": { "type": "string", "description": "Output CSV path. Default: Desktop." }
                    },
                    "required": ["category", "param_names"]
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;
            return ExportToCsv(doc, args);
        }

        private string ExportToCsv(Document doc, Dictionary<string, object> args)
        {
            var category = GetArg<string>(args, "category");
            var paramNames = GetArgStringArray(args, "param_names");
            var level = GetArg<string>(args, "level");
            var filePath = GetArg<string>(args, "file_path");

            if (string.IsNullOrEmpty(category)) return JsonError("category required.");
            if (paramNames == null || paramNames.Count == 0) return JsonError("param_names required.");
            paramNames = paramNames.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paramNames.Count == 0) return JsonError("param_names must contain valid parameter names.");

            if (string.IsNullOrEmpty(filePath))
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Revit_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            filePath = Path.GetFullPath(filePath);
            if (!filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return JsonError("file_path must end with .csv");

            var collector = BuildCollector(doc, category);
            bool needsCategoryFallback = !string.IsNullOrEmpty(category) && ResolveCategoryFilter(doc, category) == null;

            var sb = new StringBuilder();
            sb.AppendLine("ElementId," + string.Join(",", paramNames.Select(EscapeCsv)));

            int rowCount = 0;
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                if (needsCategoryFallback &&
                    !elem.Category.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(level) &&
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

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);

            return JsonSerializer.Serialize(new
            {
                file_path = filePath,
                row_count = rowCount,
                columns = paramNames
            }, JsonOpts);
        }

    }
}
