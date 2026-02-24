using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class AddInManagementSkill : BaseRevitSkill
    {
        protected override string SkillName => "AddInManagement";
        protected override string SkillDescription => "Manage Revit add-ins and check load times";

        protected override HashSet<string> HandledFunctions { get; } = new()
        {
            "get_loaded_addins", "get_addin_load_times"
        };

        public override IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_loaded_addins",
                "List all registered/loaded Revit add-ins.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }""")),

            ChatTool.CreateFunctionTool("get_addin_load_times",
                "Get information about add-in registrations for diagnostics.",
                BinaryData.FromString("""{ "type": "object", "properties": {}, "required": [] }"""))
        };

        protected override string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args)
        {
            return functionName switch
            {
                "get_loaded_addins" => GetLoadedAddins(),
                "get_addin_load_times" => GetAddinLoadTimes(),
                _ => UnknownTool(functionName)
            };
        }

        private string GetLoadedAddins()
        {
            try
            {
                var searchPaths = new List<string>();

                // System-level addins
                var commonPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Autodesk", "Revit", "Addins");
                if (Directory.Exists(commonPath)) searchPaths.Add(commonPath);

                // User-level addins
                var userPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins");
                if (Directory.Exists(userPath)) searchPaths.Add(userPath);

                var addinFiles = searchPaths
                    .SelectMany(p =>
                    {
                        try { return Directory.GetFiles(p, "*.addin", SearchOption.AllDirectories); }
                        catch { return Array.Empty<string>(); }
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var addins = addinFiles.Take(100).Select(f => new
                {
                    file = Path.GetFileName(f),
                    path = f,
                    size_kb = new FileInfo(f).Length / 1024,
                    scope = f.StartsWith(commonPath, StringComparison.OrdinalIgnoreCase)
                        ? "machine" : "user"
                }).ToList();

                return JsonSerializer.Serialize(new
                {
                    addin_count = addins.Count,
                    addins,
                    search_paths = searchPaths
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                return JsonError($"Cannot enumerate addins: {ex.Message} / Không thể liệt kê addins: {ex.Message}");
            }
        }

        private string GetAddinLoadTimes()
        {
            return JsonSerializer.Serialize(new
            {
                message = "Add-in load time tracking is not available via API. " +
                          "Use 'get_loaded_addins' to list registered add-ins. / " +
                          "Theo dõi thời gian load add-in không khả dụng qua API. " +
                          "Dùng 'get_loaded_addins' để xem danh sách add-ins.",
                tip = "For slow startup diagnosis, check the Revit journal file in %LOCALAPPDATA%/Autodesk/Revit. / " +
                      "Để chẩn đoán khởi động chậm, kiểm tra Revit journal file tại %LOCALAPPDATA%/Autodesk/Revit."
            }, JsonOpts);
        }
    }
}
