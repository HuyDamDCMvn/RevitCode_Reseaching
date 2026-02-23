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
                var addinFiles = Directory.GetFiles(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Autodesk", "Revit", "Addins"), "*.addin", SearchOption.AllDirectories);

                var addins = addinFiles.Take(50).Select(f => new
                {
                    file = Path.GetFileName(f),
                    path = f,
                    size_kb = new FileInfo(f).Length / 1024
                }).ToList();

                return JsonSerializer.Serialize(new { addin_count = addins.Count, addins }, JsonOpts);
            }
            catch (Exception ex) { return JsonError($"Cannot enumerate addins: {ex.Message}"); }
        }

        private string GetAddinLoadTimes()
        {
            return JsonSerializer.Serialize(new
            {
                message = "Add-in load time tracking requires the AddInsManagerSettings API (Revit 2025.3). " +
                          "Use 'get_loaded_addins' to list registered add-ins.",
                tip = "For slow startup diagnosis, check the Revit journal file in %LOCALAPPDATA%/Autodesk/Revit."
            }, JsonOpts);
        }
    }
}
