using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using OpenAI.Chat;

namespace RevitChat.Skills
{
    /// <summary>
    /// Central registry of all Revit skills.
    /// Aggregates tool definitions from all registered skills
    /// and routes tool calls to the correct skill for execution.
    /// </summary>
    public class SkillRegistry
    {
        private readonly List<IRevitSkill> _skills = new();
        private IReadOnlyList<ChatTool> _cachedTools;

        public static readonly Dictionary<string, string[]> PackToSkills = new()
        {
            ["Core"] = new[] { "Query", "ProjectInfo", "Modify", "Export" },
            ["ViewControl"] = new[] { "ViewControl", "SelectionFilter" },
            ["MEP"] = new[] { "MepSystemAnalysis", "MepEquipment", "MepSpace", "MepQuantityTakeoff", "MepValidation", "MepConnectivity", "MepModeler", "MepFitting", "OpeningCreator", "MepDistance" },
            ["Modeler"] = new[] {
                "FamilyPlacement", "SheetManagement", "FilterTemplate",
                "DimensionTag", "WorksetPhase", "Group", "Material",
                "RoomArea", "GridLevel", "SharedParameter", "RevisionMarkup", "Schedule"
            },
            ["BIMCoordinator"] = new[] {
                "ModelHealth", "NamingAudit", "PurgeAudit",
                "CoordinationReport", "ClashDetection"
            },
            ["LinkedModels"] = new[] { "RevitLink" }
        };

        public IReadOnlyList<IRevitSkill> Skills => _skills;

        public void Register(IRevitSkill skill)
        {
            if (_skills.Any(s => s.Name == skill.Name)) return;
            _skills.Add(skill);
            _cachedTools = null;
        }

        public void Unregister(string skillName)
        {
            _skills.RemoveAll(s => s.Name == skillName);
            _cachedTools = null;
        }

        public IReadOnlyList<ChatTool> GetAllToolDefinitions()
        {
            return _cachedTools ??= _skills.SelectMany(s => s.GetToolDefinitions()).ToList();
        }

        public IReadOnlyList<ChatTool> GetToolDefinitionsByPacks(IEnumerable<string> enabledPacks)
        {
            var allowedSkills = new HashSet<string>();
            foreach (var pack in enabledPacks)
            {
                if (PackToSkills.TryGetValue(pack, out var skillNames))
                    foreach (var s in skillNames)
                        allowedSkills.Add(s);
            }

            return _skills
                .Where(s => allowedSkills.Contains(s.Name))
                .SelectMany(s => s.GetToolDefinitions())
                .ToList();
        }

        public string ExecuteTool(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var skill = _skills.FirstOrDefault(s => s.CanHandle(functionName));
            if (skill == null)
                return $"{{\"error\":\"No skill registered for tool '{functionName}'\"}}";

            return skill.Execute(functionName, app, args);
        }

        /// <summary>
        /// Create a registry with all built-in skills pre-registered.
        /// </summary>
        public static SkillRegistry CreateDefault()
        {
            var registry = new SkillRegistry();
            registry.Register(new QuerySkill());
            registry.Register(new ProjectInfoSkill());
            registry.Register(new ModifySkill());
            registry.Register(new ExportSkill());
            registry.Register(new MepSystemAnalysisSkill());
            registry.Register(new MepEquipmentSkill());
            registry.Register(new MepSpaceSkill());
            registry.Register(new MepQuantityTakeoffSkill());
            registry.Register(new MepValidationSkill());
            registry.Register(new MepConnectivitySkill());
            registry.Register(new MepModelerSkill());
            registry.Register(new ViewControlSkill());
            registry.Register(new RevitLinkSkill());
            registry.Register(new FamilyPlacementSkill());
            registry.Register(new SheetManagementSkill());
            registry.Register(new FilterTemplateSkill());
            registry.Register(new DimensionTagSkill());
            registry.Register(new WorksetPhaseSkill());
            registry.Register(new GroupSkill());
            registry.Register(new ClashDetectionSkill());
            registry.Register(new MaterialSkill());
            registry.Register(new RoomAreaSkill());
            registry.Register(new ModelHealthSkill());
            registry.Register(new NamingAuditSkill());
            registry.Register(new SharedParameterSkill());
            registry.Register(new GridLevelSkill());
            registry.Register(new PurgeAuditSkill());
            registry.Register(new CoordinationReportSkill());
            registry.Register(new RevisionMarkupSkill());
            registry.Register(new SelectionFilterSkill());
            registry.Register(new ScheduleSkill());
            registry.Register(new OpeningCreatorSkill());
            registry.Register(new MepDistanceSkill());
            registry.Register(new MepFittingSkill());
            return registry;
        }
    }
}
