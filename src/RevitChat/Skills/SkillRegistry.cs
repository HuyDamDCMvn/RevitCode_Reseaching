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
            return registry;
        }
    }
}
